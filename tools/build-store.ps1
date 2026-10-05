# build-store.ps1 — builds the MSIX package for Microsoft Store submission.
#
# Prereqs: .NET 8 SDK, Windows SDK (makeappx.exe — found automatically below),
# tools\fetch-tools.ps1 already run (platform-tools present), and the two
# REPLACE_ values in packaging\store\AppxManifest.xml filled from Partner
# Center (see docs\microsoft-store.md).
#
# Output: out-store\OficinaOSDiag_<version>_x64.msix  (+ .msixupload zip)

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$layout = Join-Path $root "out-store\layout"
$outDir = Join-Path $root "out-store"
$manifest = Join-Path $root "packaging\store\AppxManifest.xml"
$assets = Join-Path $root "packaging\store\Assets"
$publishDir = Join-Path $root "publish"

if ((Get-Content $manifest -Raw) -match "REPLACE_") {
    throw "AppxManifest.xml still has REPLACE_ placeholders — fill Package/Identity values from Partner Center first (docs\microsoft-store.md)."
}

# --- 1. publish (self-contained single file + platform-tools alongside) -----
Write-Host "==> dotnet publish" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\OficinaDiag\OficinaDiag.csproj") -r win-x64 --self-contained `
    -c $Configuration -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- 2. version: MSIX needs four parts — derive from the csproj --------------
[xml]$csproj = Get-Content (Join-Path $root "src\OficinaDiag\OficinaDiag.csproj")
$csVersion = $csproj.Project.PropertyGroup.Version
$msixVersion = if (($csVersion -split "\.").Count -ge 4) { $csVersion } else { "$csVersion.0" }
Write-Host "==> version $csVersion -> MSIX $msixVersion" -ForegroundColor Cyan

# --- 3. stage the layout: app payload + manifest + tile assets ---------------
Write-Host "==> staging layout" -ForegroundColor Cyan
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout | Out-Null
Copy-Item "$publishDir\*" $layout -Recurse -Force
Remove-Item "$layout\*.pdb" -ErrorAction SilentlyContinue
Copy-Item $manifest (Join-Path $layout "AppxManifest.xml") -Force
Copy-Item $assets (Join-Path $layout "Assets") -Recurse -Force

# --- 4. resources.pri — maps the scale-200 tile assets -------------------------
$sdkBin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64" -Directory |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$makeappx = Join-Path $sdkBin "makeappx.exe"
$makepri = Join-Path $sdkBin "makepri.exe"
if (-not (Test-Path $makeappx)) { throw "makeappx.exe not found — install the Windows SDK" }
if (-not (Test-Path $makepri)) { throw "makepri.exe not found — install the Windows SDK" }

Write-Host "==> makepri: resource index" -ForegroundColor Cyan
$priConfig = Join-Path $outDir "priconfig.xml"
& $makepri createconfig /cf $priConfig /dq pt-PT_en-US /o | Out-Null
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout "AppxManifest.xml") `
    /of (Join-Path $layout "resources.pri") /o
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

# --- 5. makeappx pack ---------------------------------------------------------
Write-Host "==> makeappx: $makeappx" -ForegroundColor Cyan
$msix = Join-Path $outDir "OficinaOSDiag_${msixVersion}_x64.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

# --- 6. wrap as .msixupload (plain zip containing the .msix) ------------------
$upload = Join-Path $outDir "OficinaOSDiag_${msixVersion}_x64.msixupload"
$zip = "$upload.zip"
if (Test-Path $upload) { Remove-Item $upload -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
# Compress-Archive only accepts a .zip destination — zip then rename.
Compress-Archive -Path $msix -DestinationPath $zip -CompressionLevel Optimal
Rename-Item $zip $upload

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Write-Host "  $msix"
Write-Host "  $upload  <- upload this one to Partner Center"
