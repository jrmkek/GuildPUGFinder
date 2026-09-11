# build.ps1
# Builds a self-contained, single-file GuildPUGFinderApp.exe into dist/,
# ready to hand to the GM. Works regardless of where you run it from.
#
# Usage:
#   .\build.ps1

$ErrorActionPreference = "Stop"

# $PSScriptRoot is this script's own folder (repo root), so this works no
# matter what directory you're in when you run it.
$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot "app\GuildPUGFinderApp\GuildPUGFinderApp.csproj"
$distPath = Join-Path $repoRoot "dist"

if (-not (Test-Path $projectPath)) {
    Write-Error "Couldn't find project file at: $projectPath"
    exit 1
}

Write-Host "Cleaning old dist folder..." -ForegroundColor Cyan
if (Test-Path $distPath) {
    Remove-Item -Recurse -Force $distPath
}

Write-Host "Publishing release build..." -ForegroundColor Cyan
dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $distPath

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed - see errors above."
    exit 1
}

# config.json (real credentials) is deliberately NOT copied here - only the
# example template should ever ship. The GM needs to create their own
# config.json in dist/ from config.example.json before running the app.
$examplePath = Join-Path $repoRoot "app\GuildPUGFinderApp\config.example.json"
if (Test-Path $examplePath) {
    Copy-Item $examplePath -Destination $distPath -Force
}

# Remove a real config.json if the csproj's CopyToOutputDirectory rule
# happened to pull one in - dist/ is meant to be shareable, so a live
# secret should never end up in there.
$realConfigInDist = Join-Path $distPath "config.json"
if (Test-Path $realConfigInDist) {
    Remove-Item $realConfigInDist -Force
    Write-Host "Removed config.json from dist/ (only config.example.json ships)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done. Distributable build is in: $distPath" -ForegroundColor Green
Write-Host "Before handing this to the GM: copy config.example.json to config.json in that folder and fill in real credentials." -ForegroundColor Green