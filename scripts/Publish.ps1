#requires -Version 7.0
param([string]$Dotnet = 'dotnet', [string]$Go = 'go')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 7) { throw '必须使用 PowerShell 7' }
$workspacePath = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspacePath
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.artifacts/cli'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$releasePath = Join-Path $workspacePath ".artifacts/releases/FileSync-win-x64-$stamp"
& $Dotnet publish 'src/FileSync/FileSync.csproj' -c Release -r win-x64 --self-contained true --nologo --verbosity minimal -o $releasePath "-p:GoExecutable=$Go" -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw '发布失败' }
Copy-Item -LiteralPath (Join-Path $workspacePath 'README.md') -Destination (Join-Path $releasePath '使用说明.md')
Copy-Item -LiteralPath (Join-Path $workspacePath 'docs') -Destination (Join-Path $releasePath 'docs') -Recurse
$noticePath = Join-Path $releasePath '第三方许可'
New-Item -ItemType Directory -Path $noticePath | Out-Null
$goRoot = Split-Path -Parent (Split-Path -Parent (Get-Command $Go).Source)
$dotnetRoot = Split-Path -Parent (Get-Command $Dotnet).Source
Copy-Item -LiteralPath (Join-Path $goRoot 'LICENSE') -Destination (Join-Path $noticePath 'Go-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $noticePath 'DotNET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $noticePath 'DotNET-ThirdPartyNotices.txt')
$archivePath = "$releasePath.zip"
Compress-Archive -Path (Join-Path $releasePath '*') -DestinationPath $archivePath
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Select-Object Hash, Path
Write-Output "运行程序：$(Join-Path $releasePath 'FileSync.exe')"
