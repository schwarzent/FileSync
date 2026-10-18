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
& $Dotnet build 'src/FileSync/FileSync.csproj' --nologo --verbosity minimal "-p:GoExecutable=$Go"
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
& $Dotnet 'src/FileSync/bin/Debug/net10.0/FileSync.dll' --data (Join-Path $workspacePath '.filesync')
exit $LASTEXITCODE
