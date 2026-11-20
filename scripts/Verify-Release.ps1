#requires -Version 7.0
param([Parameter(Mandatory)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 7) { throw '必须使用 PowerShell 7' }
$workspacePath = Split-Path -Parent $PSScriptRoot
$archive = [IO.Path]::GetFullPath($ArchivePath)
$releaseRoot = Join-Path $workspacePath '.artifacts/releases'
if (-not $archive.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '压缩包必须位于当前项目的发布目录内'
}
$published = $archive.Substring(0, $archive.Length - '.zip'.Length)
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $path = [IO.Path]::GetFullPath((Join-Path $published $entry.FullName))
        if (-not $path.StartsWith($published + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "压缩包包含越界路径：$($entry.FullName)"
        }
        $stream = $entry.Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($actual -ne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) {
            throw "压缩内容与本次发布输出不一致：$($entry.FullName)"
        }
        if ($entry.FullName -match '(^|[/\\])\.filesync([/\\]|$)') { throw '压缩包混入用户数据' }
    }
    Write-Output "ZIP 逐文件哈希校验通过：$($zip.Entries.Count) 个条目"
}
finally { $zip.Dispose() }
function Get-OpenPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}
$runPath = Join-Path $workspacePath ('.test-runs/release-' + [guid]::NewGuid().ToString('N'))
$appPath = Join-Path $runPath 'app'
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $appPath)
foreach ($name in @('FileSync.exe', 'FileSync.dll', 'coreclr.dll', 'README.md', 'ThirdPartyLicenses/Go-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appPath $name) -PathType Leaf)) { throw "运行包缺少文件：$name" }
}
$uiPort = Get-OpenPort
$peerPort = Get-OpenPort
$arguments = @('--data', (Join-Path $runPath 'state'), '--ui-port', "$uiPort", '--port', "$peerPort", '--no-discovery', '--no-browser', '--loopback-only')
$process = Start-Process -FilePath (Join-Path $appPath 'FileSync.exe') -ArgumentList $arguments -WorkingDirectory $appPath -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runPath 'stdout.log') -RedirectStandardError (Join-Path $runPath 'stderr.log')
try {
    $base = "http://127.0.0.1:$uiPort"
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    while ($deadline.Elapsed.TotalSeconds -lt 15) {
        if ($process.HasExited) { throw "解压后的程序提前退出：$(Get-Content -LiteralPath (Join-Path $runPath 'stderr.log') -Raw)" }
        $connection = [Net.Sockets.TcpClient]::new()
        try { $connection.Connect([Net.IPAddress]::Loopback, $uiPort); $ready = $true }
        catch [Net.Sockets.SocketException] { Start-Sleep -Milliseconds 100 }
        finally { $connection.Dispose() }
        if ($ready) { break }
    }
    if (-not $ready) { throw '解压后的程序未在 15 秒内启动' }
    $html = (Invoke-WebRequest -Uri $base -NoProxy -TimeoutSec 5).Content
    $token = [regex]::Match($html, 'name="filesync-session" content="([^"]+)"').Groups[1].Value
    if ([string]::IsNullOrEmpty($token)) { throw '管理页面缺少会话令牌' }
    $status = Invoke-RestMethod -Uri "$base/api/status" -NoProxy -Headers @{ 'X-FileSync-Session' = $token } -TimeoutSec 5
    if ($status.busy) { throw '隔离启动的程序状态异常' }
    $bindings = @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -In @($uiPort, $peerPort))
    if ($bindings.Count -ne 2 -or @($bindings | Where-Object { -not [Net.IPAddress]::IsLoopback($_.Address) }).Count -ne 0 -or $status.discoveryEnabled) {
        throw '发布验证未保持仅本机回环模式'
    }
    foreach ($name in @('app.js', 'ui.js', 'forms.js', 'views.js', 'preview.js', 'conflicts.js', 'onedrive.js', 'repository.js', 'backup.js', 'records.js', 'record-form.js', 'record-plans.js', 'record-selection.js', 'style.css')) {
        $content = (Invoke-WebRequest -Uri "$base/$name" -NoProxy -TimeoutSec 5).Content
        $expected = [IO.File]::ReadAllText((Join-Path $workspacePath "src/FileSync/wwwroot/$name"))
        if ($content -cne $expected) { throw "运行包的界面资源与最新源码不一致：$name" }
    }
    Write-Output '解压运行验证通过：FileSync.exe 正常启动，全部界面资源与最新源码一致'
    Write-Output "验证目录：$runPath"
}
finally {
    if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
    $process.Dispose()
}
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List Hash,Path
