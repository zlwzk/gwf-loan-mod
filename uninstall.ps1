# uninstall.ps1 - uninstall the mod (remove only what we copied, never touch original game files)
param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

if (-not $GameDir) { $GameDir = $env:GWF_GAME_DIR }
if (-not $GameDir) {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends",
        "D:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "Gamble With Your Friends.exe")) { $GameDir = $c; break }
    }
}
if (-not $GameDir) { Write-Host "Game directory not found. Please pass -GameDir."; exit 1 }

$remove = @(
    (Join-Path $GameDir "winhttp.dll"),
    (Join-Path $GameDir "doorstop_config.ini"),
    (Join-Path $GameDir ".doorstop_version"),
    (Join-Path $GameDir "changelog.txt"),
    (Join-Path $GameDir "BepInEx")
)
foreach ($p in $remove) {
    if (Test-Path $p) {
        Remove-Item -Path $p -Recurse -Force
        Write-Host "Removed: $p"
    }
}
Write-Host "Uninstall complete (original game files untouched)."
