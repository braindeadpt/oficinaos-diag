# fetch-tools.ps1 — downloads the device tools the exe bundles.
# Run once before build/publish. Binaries are git-ignored.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root "tools"
$pt = Join-Path $tools "platform-tools"

if (Test-Path "$pt\adb.exe") {
    Write-Output "platform-tools already present - nothing to do"
    exit 0
}

Write-Output "Downloading Android platform-tools..."
$zip = Join-Path $env:TEMP "platform-tools.zip"
Invoke-WebRequest "https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $tools -Force
Remove-Item $zip
Write-Output "OK: $pt\adb.exe"
Write-Output "iPhone: imobiledevice-net ships via NuGet (nothing to download)."
