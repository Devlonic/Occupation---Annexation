<#
.SYNOPSIS
    Builds the mod and assembles a clean copy for the Steam Workshop in Release\OccupationAnnexation.

.DESCRIPTION
    RimWorld uploads the whole mod folder, so the Workshop copy must not contain the source code,
    .git or build output. This script copies only what the game loads.

    -LinkForUpload points the game's Mods\OccupationAnnexation junction at the release copy, so the
    in-game "Upload to Steam Workshop" button uploads it. -LinkForDevelopment points it back at the
    repository. Only a junction is ever replaced, never a real folder.

    The game writes About\PublishedFileId.txt into the uploaded copy after the first upload. The
    script copies it back into the repository (commit it), so later uploads update the same item.

.EXAMPLE
    .\Tools\Build-Release.ps1 -LinkForUpload
    .\Tools\Build-Release.ps1 -LinkForDevelopment
#>
param(
    [string]$GameModsDir = "D:\Games\Steam\steamapps\common\RimWorld\Mods",
    [switch]$LinkForUpload,
    [switch]$LinkForDevelopment,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo "Release\OccupationAnnexation"
$junction = Join-Path $GameModsDir "OccupationAnnexation"
$content = @("About", "1.5", "Defs", "Languages", "Patches", "Textures", "LoadFolders.xml", "LICENSE")

function Save-PublishedFileId {
    $released = Join-Path $release "About\PublishedFileId.txt"
    $kept = Join-Path $repo "About\PublishedFileId.txt"
    if ((Test-Path $released) -and -not (Test-Path $kept)) {
        Copy-Item $released $kept
        Write-Host "Saved About\PublishedFileId.txt from the upload. Commit it." -ForegroundColor Yellow
    }
}

function Set-ModJunction([string]$target) {
    if (Test-Path $junction) {
        $item = Get-Item $junction -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "$junction is a real folder, not a junction. Move it away yourself; the script will not touch it."
        }
        # A non-recursive delete of a junction removes only the link, never the folder it points to.
        [IO.Directory]::Delete($junction)
    }
    New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
    Write-Host "Mods\OccupationAnnexation -> $target"
}

if ((Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) -and (-not $SkipBuild -or $LinkForUpload -or $LinkForDevelopment)) {
    throw "Close RimWorld first: it locks the DLL and reads the mod folder. (-SkipBuild alone only copies files.)"
}

Save-PublishedFileId
if ($LinkForDevelopment) {
    Set-ModJunction $repo
    return
}

if (-not $SkipBuild) {
    dotnet build (Join-Path $repo "Source\OccupationAnnexation\OccupationAnnexation.csproj") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

foreach ($required in "About\About.xml", "About\Preview.png", "1.5\Assemblies\OccupationAnnexation.dll") {
    if (-not (Test-Path (Join-Path $repo $required))) { throw "Missing $required." }
}

if (Test-Path $release) {
    Remove-Item $release -Recurse -Force
}
New-Item -ItemType Directory -Force $release | Out-Null
foreach ($name in $content) {
    $source = Join-Path $repo $name
    if (-not (Test-Path $source)) { continue }
    # Git does not keep empty folders; neither does the release.
    if ((Get-Item $source).PSIsContainer -and -not (Get-ChildItem $source -Recurse -File)) { continue }
    Copy-Item $source (Join-Path $release $name) -Recurse
}

$files = Get-ChildItem $release -Recurse -File
$size = ($files | Measure-Object Length -Sum).Sum / 1KB
Write-Host ("Release: {0} files, {1:N0} KB in {2}" -f $files.Count, $size, $release)
$files | ForEach-Object { "  " + $_.FullName.Substring($release.Length + 1) }

if ($LinkForUpload) {
    Set-ModJunction $release
    Write-Host "Start RimWorld with Dev mode on, open Mods, right-click Occupation & Annexation and choose 'Upload to Steam Workshop'."
    Write-Host "Afterwards close the game and run: .\Tools\Build-Release.ps1 -LinkForDevelopment"
}
