# install.ps1 - install BepInEx + the mod into the game directory
# Principle: copy-only, never modify/delete any original game files; existing BepInEx is preserved without overwriting.
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
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir "Gamble With Your Friends.exe"))) {
    Write-Host "Game directory not found. Please pass -GameDir." -ForegroundColor Red
    exit 1
}

$root = $PSScriptRoot
$bepinexSrc = Join-Path $root "vendor\bepinex"
$dll = Join-Path $root "build\GwfLoanMod.dll"
if (-not (Test-Path $dll)) {
    Write-Host "Build output not found; please run build.ps1 first." -ForegroundColor Red
    exit 1
}

Write-Host "Game directory: $GameDir"
Write-Host "Installing BepInEx (copy only, will not touch any game original files)..."

# ---- Copy the BepInEx runtime files to the game root directory (winhttp.dll / doorstop_config.ini / .doorstop_version / changelog.txt) ----
foreach ($f in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")) {
    $src = Join-Path $bepinexSrc $f
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination (Join-Path $GameDir $f) -Force
    }
}

# ---- Copy the BepInEx directory (the whole core) ----
$srcBep = Join-Path $bepinexSrc "BepInEx"
$dstBep = Join-Path $GameDir "BepInEx"
if (-not (Test-Path $dstBep)) { New-Item -ItemType Directory -Force -Path $dstBep | Out-Null }
foreach ($sub in @("core")) {
    $s = Join-Path $srcBep $sub
    if (Test-Path $s) {
        Copy-Item -Path $s -Destination $dstBep -Recurse -Force
    }
}

# ---- Install the mod DLL ----
$pluginsDir = Join-Path $dstBep "plugins"
New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
Copy-Item -Path $dll -Destination (Join-Path $pluginsDir "GwfLoanMod.dll") -Force

Write-Host ""
Write-Host "Installation complete:"
Write-Host "  - BepInEx 5.4.23.5 (win-x64) injected loader"
Write-Host "  - $pluginsDir\GwfLoanMod.dll"
Write-Host ""
Write-Host "Launch the game (via Steam). After joining a room press F8 to open the loan panel."
Write-Host "Everyone in the room must have it installed for results to stay consistent."
exit 0
