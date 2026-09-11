<#
.SYNOPSIS
    Builds and publishes the GuildPUGFinder companion app, and stages the
    addon folder for copying into AddOns/.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Configuration Debug
    ./build.ps1 -Run
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Produce a single .exe that does not need .NET installed on the machine.
    [switch]$SelfContained,

    # Launch the app once the publish succeeds.
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'app/GuildPUGFinderApp/GuildPUGFinderApp.csproj'
$dist = Join-Path $root 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found on PATH. Install .NET 8 from https://dotnet.microsoft.com/download/dotnet/8.0"
}

Write-Host "Publishing $Configuration -> $dist" -ForegroundColor Cyan

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '-o', $dist,
    '--nologo'
)
if ($SelfContained) {
    $publishArgs += @('--self-contained', 'true', '-p:PublishSingleFile=true')
} else {
    $publishArgs += @('--self-contained', 'false')
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# A user's real config.json is gitignored and lives next to the sources, so
# carry it over to the publish folder; otherwise seed the template so a first
# run has something to edit rather than an error.
$srcConfig = Join-Path $root 'app/GuildPUGFinderApp/config.json'
$distConfig = Join-Path $dist 'config.json'
if ((Test-Path $srcConfig) -and -not (Test-Path $distConfig)) {
    Copy-Item $srcConfig $distConfig
} elseif (-not (Test-Path $distConfig)) {
    Copy-Item (Join-Path $root 'app/GuildPUGFinderApp/config.example.json') $distConfig
    Write-Warning "No config.json found. Seeded dist/config.json from the template - fill in your WarcraftLogs credentials before running."
}

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  App:   $dist\GuildPUGFinderApp.exe"
Write-Host "  Addon: copy 'addon\GuildPUGFinder' into World of Warcraft\_anniversary_\Interface\AddOns\"

if ($Run) {
    Start-Process (Join-Path $dist 'GuildPUGFinderApp.exe')
}
