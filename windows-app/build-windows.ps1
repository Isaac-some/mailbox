[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "build\\邮箱助手-Windows-x64")
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
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

# Keep generated authorization outside both source control and the package.
$tokenDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("mailbox-token-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tokenDirectory | Out-Null
$tokenSource = Join-Path $tokenDirectory "PlatformAuthorization.g.cs"
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:MAIL_ASSISTANT_UPSTREAM_TOKEN.Trim()))
$source = @"
namespace MailArchiver.Services;
internal static partial class BundledPlatformAuthorization
{
    static partial void ApplyReleaseToken(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["UpstreamMailboxSync:BearerToken"]))
            configuration["UpstreamMailboxSync:BearerToken"] =
                System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("$encoded"));
    }
}
"@
try {
    [System.IO.File]::WriteAllText($tokenSource, $source)
    dotnet publish $serverProject `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $serverOutput `
        "-p:BundledPlatformTokenSource=$tokenSource" `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false
} finally {
    Remove-Item -Recurse -Force $tokenDirectory
    $encoded = $null
    $source = $null
}

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

if ($env:MAIL_ASSISTANT_UPSTREAM_ENDPOINT) {
    $settings = Get-Content $localSettings -Raw | ConvertFrom-Json
    if ($env:MAIL_ASSISTANT_UPSTREAM_ENDPOINT) {
        $settings.UpstreamMailboxSync.Endpoint = $env:MAIL_ASSISTANT_UPSTREAM_ENDPOINT
    }
    $settings | ConvertTo-Json -Depth 20 | Set-Content $localSettings -Encoding UTF8
}

Rename-Item -Path $launcher -NewName "邮箱助手.exe"
Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $OutputDirectory "README.md")
Write-Host "Windows 发布包已生成：$OutputDirectory"
