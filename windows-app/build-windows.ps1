[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "build\\邮箱助手-Windows-x64")
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$serverOutput = Join-Path $OutputDirectory "server"
$wrapperProject = Join-Path $PSScriptRoot "KouziMailAssistant.Windows.csproj"
$serverProject = Join-Path $projectRoot "MailArchiver.csproj"

if (Test-Path $OutputDirectory) {
    Remove-Item -Recurse -Force $OutputDirectory
}
New-Item -ItemType Directory -Path $OutputDirectory, $serverOutput | Out-Null

dotnet publish $serverProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $serverOutput `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false

dotnet publish $wrapperProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $OutputDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false

$launcher = Join-Path $OutputDirectory "KouziMailAssistant.exe"
$userLauncher = Join-Path $OutputDirectory "邮箱助手.exe"
$serverExecutable = Join-Path $serverOutput "MailArchiver.exe"
$localSettings = Join-Path $serverOutput "appsettings.Local.json"

foreach ($requiredFile in @($launcher, $serverExecutable, $localSettings)) {
    if (-not (Test-Path $requiredFile)) {
        throw "打包失败，缺少文件：$requiredFile"
    }
}

Rename-Item -Path $launcher -NewName "邮箱助手.exe"
Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $OutputDirectory "README.md")
Write-Host "Windows 发布包已生成：$OutputDirectory"
