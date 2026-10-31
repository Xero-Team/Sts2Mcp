<#
.SYNOPSIS
    Builds the Sts2Mcp mod DLL.

.DESCRIPTION
    Compiles Sts2Mcp.dll against the game's assemblies. Does NOT install
    the mod — copy the output files to the game's mods/ directory yourself.

.PARAMETER GameDir
    Path to the Slay the Spire 2 installation directory.
    Falls back to the STS2_GAME_DIR environment variable, then to build.local.props.

.PARAMETER Configuration
    Build configuration (default: Release).

.EXAMPLE
    .\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
    .\build.ps1  # uses $env:STS2_GAME_DIR
#>
param(
    [string]$GameDir,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# --- Resolve game directory ---
if (-not $GameDir) {
    $GameDir = $env:STS2_GAME_DIR
}
if (-not $GameDir) {
    # Same resolution order as Directory.Build.props: the gitignored local file.
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $resolved = & dotnet msbuild (Join-Path $PSScriptRoot "Sts2Mcp.csproj") -nologo -getProperty:STS2GameDir 2>$null
        if ($LASTEXITCODE -eq 0 -and $resolved) { $GameDir = "$resolved".Trim() }
    }
}
if (-not $GameDir) {
    Write-Host @"
ERROR: Game directory not specified.

Provide it via parameter, environment variable, or build.local.props:
  .\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"

Or set it once in your PowerShell profile:
  `$env:STS2_GAME_DIR = "D:\SteamLibrary\steamapps\common\Slay the Spire 2"

Or copy build.local.props.example to build.local.props and set STS2GameDir there.
"@ -ForegroundColor Red
    exit 1
}

$dllDir = Join-Path $GameDir "data_sts2_windows_x86_64"
if (-not (Test-Path (Join-Path $dllDir "sts2.dll"))) {
    Write-Host "ERROR: Could not find sts2.dll in '$dllDir'." -ForegroundColor Red
    Write-Host "Make sure -GameDir points to the Slay the Spire 2 installation root." -ForegroundColor Red
    exit 1
}

# --- Check prerequisites ---
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host @"
ERROR: 'dotnet' not found.

Install the .NET 9 SDK from:
  https://dotnet.microsoft.com/download/dotnet/9.0
"@ -ForegroundColor Red
    exit 1
}

# --- Build ---
$scriptDir = $PSScriptRoot
$project = Join-Path $scriptDir "Sts2Mcp.csproj"
$outDir = Join-Path (Join-Path $scriptDir "out") "Sts2Mcp"

Write-Host "=== Building Sts2Mcp ($Configuration) ===" -ForegroundColor Cyan
Write-Host "Game directory : $GameDir"
Write-Host "Output         : $outDir"
Write-Host ""

dotnet build $project -c $Configuration -o $outDir -p:STS2GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "=== Build succeeded ===" -ForegroundColor Green
Write-Host "The output folder is ready to install. Copy these files to <game_install>/mods/:"
Write-Host "  $outDir\Sts2Mcp.dll"
Write-Host "  $outDir\Sts2Mcp.json"
