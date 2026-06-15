param(
    [string]$Version = "0.1.0",
    [string]$RuntimeIdentifier = "win-x64",
    [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\MegaDownloaderNext.App\MegaDownloaderNext.App.csproj"
$artifactsRoot = Join-Path $repoRoot "artifacts\installer"
$publishDir = Join-Path $artifactsRoot "publish"
$stagingDir = Join-Path $artifactsRoot "staging"
$outputDir = Join-Path $artifactsRoot "output"
$installerName = "MegaDownloaderPlus-Setup-v$Version.exe"
$zipName = "MegaDownloaderPlus-v$Version-portable.zip"
$installerPath = Join-Path $outputDir $installerName
$zipPath = Join-Path $outputDir $zipName
$sfxArchivePath = Join-Path $artifactsRoot "MegaDownloaderPlus.7z"
$sfxConfigPath = Join-Path $artifactsRoot "MegaDownloaderPlus.sfx.config"

if (-not $env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE ".nuget\packages"
}

foreach ($path in @($publishDir, $stagingDir, $outputDir)) {
    if (Test-Path $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $path | Out-Null
}

$publishArgs = @(
    "publish",
    $appProject,
    "--configuration",
    "Release",
    "--runtime",
    $RuntimeIdentifier,
    "--self-contained",
    $SelfContained.ToString().ToLowerInvariant(),
    "--output",
    $publishDir,
    "-p:PublishSingleFile=false",
    "-p:RestoreIgnoreFailedSources=true"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -Path (Join-Path $publishDir "*") -Destination $stagingDir -Recurse -Force

$installScript = @'
$ErrorActionPreference = "Stop"

$appName = "MegaDownloader+"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\MegaDownloaderPlus"
$previousInstallDir = Join-Path $env:LOCALAPPDATA "Programs\MegaDownloadManager"
$legacyInstallDir = Join-Path $env:LOCALAPPDATA "Programs\MegaDownloaderNext"
$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\MegaDownloader+.lnk"
$previousStartMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\MegaDownloadManager (MDM).lnk"
$legacyStartMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\MegaDownloader Next.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "MegaDownloader+.lnk"
$previousDesktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "MegaDownloadManager (MDM).lnk"
$legacyDesktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "MegaDownloader Next.lnk"
$sourceDir = $PSScriptRoot
$exeName = "MegaDownloaderPlus.exe"
$exePath = Join-Path $installDir $exeName

foreach ($path in @($installDir, $previousInstallDir, $legacyInstallDir)) {
    if (Test-Path $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

foreach ($shortcut in @($previousStartMenuShortcut, $legacyStartMenuShortcut, $previousDesktopShortcut, $legacyDesktopShortcut)) {
    if (Test-Path $shortcut) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}

$previousUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MegaDownloadManager"
$legacyUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MegaDownloaderNext"
foreach ($key in @($previousUninstallKey, $legacyUninstallKey)) {
    if (Test-Path $key) {
        Remove-Item -LiteralPath $key -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $installDir | Out-Null

Get-ChildItem -LiteralPath $sourceDir |
    Where-Object { $_.Name -notin @("install.cmd", "Install.ps1") } |
    Copy-Item -Destination $installDir -Recurse -Force

$uninstallScript = @"
`$ErrorActionPreference = "Stop"
`$installDir = "$installDir"
`$previousInstallDir = "$previousInstallDir"
`$legacyInstallDir = "$legacyInstallDir"
`$startMenuShortcut = "$startMenuShortcut"
`$previousStartMenuShortcut = "$previousStartMenuShortcut"
`$legacyStartMenuShortcut = "$legacyStartMenuShortcut"
`$desktopShortcut = "$desktopShortcut"
`$previousDesktopShortcut = "$previousDesktopShortcut"
`$legacyDesktopShortcut = "$legacyDesktopShortcut"
`$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MegaDownloaderPlus"
`$previousUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MegaDownloadManager"
`$legacyUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MegaDownloaderNext"

foreach (`$shortcut in @(`$startMenuShortcut, `$desktopShortcut, `$previousStartMenuShortcut, `$legacyStartMenuShortcut, `$previousDesktopShortcut, `$legacyDesktopShortcut)) {
    if (Test-Path `$shortcut) {
        Remove-Item -LiteralPath `$shortcut -Force
    }
}

foreach (`$key in @(`$uninstallKey, `$previousUninstallKey, `$legacyUninstallKey)) {
    if (Test-Path `$key) {
        Remove-Item -LiteralPath `$key -Recurse -Force
    }
}

foreach (`$path in @(`$installDir, `$previousInstallDir, `$legacyInstallDir)) {
    if (Test-Path `$path) {
        Remove-Item -LiteralPath `$path -Recurse -Force
    }
}
"@

$uninstallPath = Join-Path $installDir "Uninstall.ps1"
Set-Content -LiteralPath $uninstallPath -Value $uninstallScript -Encoding UTF8

$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @($startMenuShortcut, $desktopShortcut)) {
    $shortcutDirectory = Split-Path -Parent $shortcutPath
    if (-not (Test-Path $shortcutDirectory)) {
        New-Item -ItemType Directory -Path $shortcutDirectory | Out-Null
    }

    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = $appName
    $shortcut.Save()
}

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MegaDownloaderPlus"
if (-not (Test-Path $uninstallKey)) {
    New-Item -Path $uninstallKey | Out-Null
}

Set-ItemProperty -Path $uninstallKey -Name DisplayName -Value $appName
Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value "__VERSION__"
Set-ItemProperty -Path $uninstallKey -Name Publisher -Value "MegaDownloader+"
Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDir
Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value $exePath
Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallPath`""
Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord

Start-Process -FilePath $exePath
'@ -replace "__VERSION__", $Version

$installCmd = @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
exit /b %ERRORLEVEL%
'@

Set-Content -LiteralPath (Join-Path $stagingDir "Install.ps1") -Value $installScript -Encoding UTF8
Set-Content -LiteralPath (Join-Path $stagingDir "install.cmd") -Value $installCmd -Encoding ASCII

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

$sevenZipCommand = Get-Command 7z.exe -ErrorAction SilentlyContinue
$sevenZip = if ($sevenZipCommand) {
    $sevenZipCommand.Source
}
else {
    @(
        (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
        (Join-Path $env:USERPROFILE "scoop\apps\7zip\current\7z.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $sevenZip) {
    throw "7-Zip executable was not found."
}

$sfxCandidates = @(
    (Join-Path (Split-Path -Parent $sevenZip) "7z.sfx"),
    (Join-Path $env:ProgramFiles "7-Zip\7z.sfx"),
    (Join-Path $env:USERPROFILE "scoop\apps\7zip\current\7z.sfx")
)

$sfxModule = $sfxCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sfxModule) {
    throw "7-Zip SFX module was not found."
}

$sfxConfig = @"
;!@Install@!UTF-8!
Title="MegaDownloader+ Setup"
BeginPrompt="MegaDownloader+를 설치할까요?"
RunProgram="install.cmd"
;!@InstallEnd@!
"@

[System.IO.File]::WriteAllText($sfxConfigPath, $sfxConfig, [System.Text.UTF8Encoding]::new($false))

if (Test-Path $sfxArchivePath) {
    Remove-Item -LiteralPath $sfxArchivePath -Force
}

Push-Location $stagingDir
try {
    & $sevenZip a -t7z $sfxArchivePath "*" -mx=9 | Out-Host
}
finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0) {
    throw "7-Zip archive creation failed with exit code $LASTEXITCODE."
}

if (Test-Path $installerPath) {
    Remove-Item -LiteralPath $installerPath -Force
}

$output = [System.IO.File]::Create($installerPath)
try {
    foreach ($part in @($sfxModule, $sfxConfigPath, $sfxArchivePath)) {
        $input = [System.IO.File]::OpenRead($part)
        try {
            $input.CopyTo($output)
        }
        finally {
            $input.Dispose()
        }
    }
}
finally {
    $output.Dispose()
}

if (-not (Test-Path $installerPath)) {
    throw "Installer was not created: $installerPath"
}

[pscustomobject]@{
    Installer = $installerPath
    PortableZip = $zipPath
    PublishDirectory = $publishDir
}
