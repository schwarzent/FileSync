#requires -Version 7.0
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 7) { throw '必须使用 PowerShell 7' }
$workspacePath = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspacePath
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.artifacts/cli'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$testProject = Join-Path $workspacePath 'tests/FileSync.Tests/FileSync.Tests.csproj'
& $Dotnet build $testProject --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw '测试项目编译失败' }
$outputPath = Join-Path $workspacePath ('.test-runs/logs-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$testAssembly = Join-Path $workspacePath 'tests/FileSync.Tests/bin/Debug/net10.0/FileSync.Tests.dll'
$process = Start-Process -FilePath (Get-Command $Dotnet).Source -ArgumentList @('"' + $testAssembly + '"', '"' + $workspacePath + '"') -WorkingDirectory $workspacePath -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputPath 'stdout.log') -RedirectStandardError (Join-Path $outputPath 'stderr.log')
if (-not $process.WaitForExit(60000)) {
    $process.Kill($true)
    throw "后端验证超过 60 秒，已终止本次测试进程。日志：$outputPath"
}
Get-Content -LiteralPath (Join-Path $outputPath 'stdout.log') -Encoding utf8
Get-Content -LiteralPath (Join-Path $outputPath 'stderr.log') -Encoding utf8
if ($process.ExitCode -ne 0) { throw "验证失败：退出码 $($process.ExitCode)" }
