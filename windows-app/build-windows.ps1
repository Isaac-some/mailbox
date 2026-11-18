[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "build\\邮箱助手-Windows-x64")
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = Split-Path -Parent $PSScriptRoot
$serverOutput = Join-Path $OutputDirectory "server"
$wrapperProject = Join-Path $PSScriptRoot "MailAssistant.Windows.csproj"
$serverProject = Join-Path $projectRoot "MailArchiver.csproj"
$makensisCommand = Get-Command makensis -ErrorAction SilentlyContinue
$makensis = if ($makensisCommand) { $makensisCommand.Source } else { Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe" }
if (-not (Test-Path $makensis)) {
    throw "请先安装 NSIS（https://nsis.sourceforge.io/），再生成完整 EXE 安装包。"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$serverOutput = Join-Path $OutputDirectory "server"

if (Test-Path $OutputDirectory) {
    Remove-Item -Recurse -Force $OutputDirectory
}
New-Item -ItemType Directory -Path $OutputDirectory, $serverOutput | Out-Null

dotnet publish $serverProject `
    --configuration Release --runtime win-x64 --self-contained true `
    --output $serverOutput -p:PublishSingleFile=false -p:PublishTrimmed=false

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


Rename-Item -Path $launcher -NewName "邮箱助手.exe"
dotnet publish (Join-Path $projectRoot "desktop-updater/MailAssistant.Updater.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    --output (Join-Path $serverOutput "updater") `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $OutputDirectory "README.md")
$version = ([xml](Get-Content $wrapperProject)).Project.PropertyGroup.Version
$installerOutput = Join-Path (Split-Path -Parent $OutputDirectory) "MailAssistant-Windows-x64-v$version-Setup.exe"
$uninstallList = [IO.Path]::GetTempFileName()
try {
    $commands = @(Get-ChildItem $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName).Replace('$', '$$').Replace('"', '$\"')
        'Delete "$INSTDIR\' + $relative + '"'
    })
    $commands += @(Get-ChildItem $OutputDirectory -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName).Replace('$', '$$').Replace('"', '$\"')
        'RMDir "$INSTDIR\' + $relative + '"'
    })
    [IO.File]::WriteAllLines($uninstallList, $commands, [Text.UTF8Encoding]::new($false))
    & $makensis /V3 "/DPACKAGE_DIR=$OutputDirectory" "/DOUTPUT_FILE=$installerOutput" `
        "/DAPP_VERSION=$version" "/DUNINSTALL_FILES=$uninstallList" (Join-Path $PSScriptRoot "installer.nsi")
    if ($LASTEXITCODE -ne 0) { throw "EXE 安装包构建失败。" }
} finally {
    Remove-Item $uninstallList -ErrorAction SilentlyContinue
}
Write-Host "完整 EXE 安装包已生成：$installerOutput"
Write-Host "Windows 发布包已生成：$OutputDirectory"
