# Allsio Push — Release Build Script
# Run from repo root in PowerShell
# Usage: .\build-release.ps1
#
# WinUI 3 note: the app cannot use PublishSingleFile=true; publish produces a
# folder of DLLs with AllsioPush.exe as the entry point. Velopack packs the
# whole folder and resolves the main exe via --mainExe.

$ErrorActionPreference = "Stop"
$version = "2026.6.20"
$project = "AllsioPush.App"
$rid = "win-x64"
$publishDir = "publish\$rid"
$releaseDir = "releases"

Write-Host "Building Allsio Push $version..." -ForegroundColor Cyan

# Clean previous publish
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }

# Publish self-contained WinUI 3 app (folder output, not single-file)
dotnet publish $project `
    -c Release `
    -r $rid `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Publish succeeded. Running vpk pack..." -ForegroundColor Cyan

# Package with velopack
vpk pack `
    --packId AllsioPush `
    --packVersion $version `
    --packDir $publishDir `
    --mainExe AllsioPush.exe `
    --outputDir $releaseDir `
    --icon "AllsioPush.App\Resources\icon-on.ico" `
    --packTitle "Allsio Push" `
    --packAuthors "Charleston Telecom Solutions"

if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "Release build complete. Output in .\$releaseDir" -ForegroundColor Green
Write-Host "Files:"
Get-ChildItem $releaseDir | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "To publish: create a GitHub release tagged v$version"
Write-Host "and upload all files from .\$releaseDir to the release assets."
