[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "build\\邮箱助手-Windows-x64")
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($env:MAIL_ASSISTANT_UPSTREAM_TOKEN)) {
    throw "缺少租户 Token，不能生成交付包。请设置 MAIL_ASSISTANT_UPSTREAM_TOKEN。"
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$serverOutput = Join-Path $OutputDirectory "server"
$wrapperProject = Join-Path $PSScriptRoot "MailAssistant.Windows.csproj"
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

$launcher = Join-Path $OutputDirectory "MailAssistant.exe"
$userLauncher = Join-Path $OutputDirectory "邮箱助手.exe"
$serverExecutable = Join-Path $serverOutput "MailArchiver.exe"
$localSettings = Join-Path $serverOutput "appsettings.Local.json"
Copy-Item (Join-Path $projectRoot "appsettings.Local.template.json") $localSettings -Force

foreach ($requiredFile in @($launcher, $serverExecutable, $localSettings)) {
    if (-not (Test-Path $requiredFile)) {
        throw "打包失败，缺少文件：$requiredFile"
    }
}

if ($env:MAIL_ASSISTANT_UPSTREAM_TOKEN -or $env:MAIL_ASSISTANT_UPSTREAM_ENDPOINT) {
    $settings = Get-Content $localSettings -Raw | ConvertFrom-Json
    if ($env:MAIL_ASSISTANT_UPSTREAM_TOKEN) {
        $settings.UpstreamMailboxSync.BearerToken = $env:MAIL_ASSISTANT_UPSTREAM_TOKEN
    }
    if ($env:MAIL_ASSISTANT_UPSTREAM_ENDPOINT) {
        $settings.UpstreamMailboxSync.Endpoint = $env:MAIL_ASSISTANT_UPSTREAM_ENDPOINT
    }
    $settings | ConvertTo-Json -Depth 20 | Set-Content $localSettings -Encoding UTF8
}

Rename-Item -Path $launcher -NewName "邮箱助手.exe"
Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $OutputDirectory "README.md")
Write-Host "Windows 发布包已生成：$OutputDirectory"
