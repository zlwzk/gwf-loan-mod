# build.ps1 - compile the GWF Loan Mod plugin DLL (using dotnet build)
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1 -GameDir "<game directory>"
#   Or set the environment variable GWF_GAME_DIR first. When neither is set, the Steam default location is auto-detected.
param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

# ---- Automatically detect the game directory (explicit parameter > environment variable > auto-detect, no hardcoded drive letters/paths) ----
if (-not $GameDir) { $GameDir = $env:GWF_GAME_DIR }
if (-not $GameDir) {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends",
        "D:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends"
    )
    # Also try auto-detecting the Steam library from the registry
    try {
        $libs = Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
        if ($libs -and $libs.InstallPath) {
            $vdf = Join-Path $libs.InstallPath "steamapps\libraryfolders.vdf"
            if (Test-Path $vdf) {
                $found = Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches
                foreach ($m in $found) {
                    foreach ($mm in $m.Matches) {
                        $p = ($mm.Groups[1].Value -replace '\\\\', '\') + "\steamapps\common\Gamble With Your Friends"
                        $candidates += $p
                    }
                }
            }
        }
    } catch { }
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "Gamble With Your Friends.exe")) { $GameDir = $c; break }
    }
}

if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir "Gamble With Your Friends.exe"))) {
    Write-Host "Game directory not found. Please pass -GameDir or set GWF_GAME_DIR." -ForegroundColor Red
    exit 1
}
Write-Host "Game directory: $GameDir"

$root = $PSScriptRoot
$out = Join-Path $root "build"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# ---- dotnet build (requires .NET SDK; targeting pack automatically via NuGet ReferenceAssemblies) ----
$env:GWF_GAME_DIR = $GameDir
& dotnet build (Join-Path $root "src\GwfLoanMod.csproj") -c Release -o $out
if ($LASTEXITCODE -ne 0) {
    Write-Host "Compilation failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

$dll = Join-Path $out "GwfLoanMod.dll"
if (-not (Test-Path $dll)) {
    Write-Host "Output DLL not found" -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "Build OK: $dll"
Write-Host ("Size: {0:N0} bytes" -f (Get-Item $dll).Length)
exit 0
