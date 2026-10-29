[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$OutputDir = "dist",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# Repository root is the parent directory of this script's directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

$buildDir = Join-Path $repoRoot "src\FunscriptToolbox\bin\$Configuration"
if (-not (Test-Path $buildDir)) {
    throw "Build directory not found at '$buildDir'. Please build the solution in '$Configuration' configuration first."
}

$mainExe = Join-Path $buildDir "FunscriptToolbox.exe"
if (-not (Test-Path $mainExe)) {
    throw "FunscriptToolbox.exe not found at '$mainExe'. Build may have failed or not completed."
}


# Extract version from AssemblyInfo.cs if not provided
if ([string]::IsNullOrWhiteSpace($Version)) {
    $assemblyInfoPath = Join-Path $repoRoot "src\FunscriptToolbox\Properties\AssemblyInfo.cs"
    if (Test-Path $assemblyInfoPath) {
        $content = Get-Content $assemblyInfoPath -Raw
        if ($content -match '(?m)^\s*\[assembly:\s*AssemblyVersion\("([^"*]+)"\)\]') {
            $rawVer = $matches[1]
            # Convert 2.2.0.0 -> 2.2.0 if revision is 0
            $parts = $rawVer.Split('.')
            if ($parts.Count -eq 4 -and $parts[3] -eq "0") {
                $Version = "$($parts[0]).$($parts[1]).$($parts[2])"
            } else {
                $Version = $rawVer
            }
        }
    }
}

Write-Host "Packaging FunscriptToolbox (Version: $(if ($Version) { $Version } else { 'Unknown' })) from '$buildDir'..." -ForegroundColor Cyan

$distPath = Join-Path $repoRoot $OutputDir
if (-not (Test-Path $distPath)) {
    New-Item -ItemType Directory -Path $distPath -Force | Out-Null
}

$stagingDir = Join-Path $distPath "staging"
if (Test-Path $stagingDir) {
    Remove-Item -Path $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# 1. Copy main executable, config, batch scripts, and library DLLs
Get-ChildItem -Path $buildDir -Filter "*.dll" -File | Copy-Item -Destination $stagingDir -Force
Get-ChildItem -Path $buildDir -Filter "*.exe" -File | Copy-Item -Destination $stagingDir -Force
Get-ChildItem -Path $buildDir -Filter "*.bat" -File | Copy-Item -Destination $stagingDir -Force

$mainConfig = Join-Path $buildDir "FunscriptToolbox.exe.config"
if (Test-Path $mainConfig) {
    Copy-Item -Path $mainConfig -Destination $stagingDir -Force
}

# 2. Copy PythonSource directory
$pythonSourceDir = Join-Path $buildDir "PythonSource"
if (Test-Path $pythonSourceDir) {
    $destPython = Join-Path $stagingDir "PythonSource"
    New-Item -ItemType Directory -Path $destPython -Force | Out-Null
    Copy-Item -Path "$pythonSourceDir\*" -Destination $destPython -Recurse -Force
}


# 4. Copy runtimes directory if present (Whisper native runtimes)
$runtimesDir = Join-Path $buildDir "runtimes"
if (Test-Path $runtimesDir) {
    $destRuntimes = Join-Path $stagingDir "runtimes"
    New-Item -ItemType Directory -Path $destRuntimes -Force | Out-Null
    Copy-Item -Path "$runtimesDir\*" -Destination $destRuntimes -Recurse -Force
}

# 5. Compress staging directory to a single zip
$cleanVer = if ($Version) { $Version.Trim() -replace '^[vV]', '' } else { "" }
$zipName = if ($cleanVer) { "FunscriptToolbox-$cleanVer.zip" } else { "FunscriptToolbox.zip" }
$mainZip = Join-Path $distPath $zipName

# Clean previous zips in dist to ensure only one release zip exists
Get-ChildItem -Path $distPath -Filter "*.zip" -File | Remove-Item -Force

Write-Host "Creating zip package at '$mainZip'..." -ForegroundColor Cyan
Compress-Archive -Path "$stagingDir\*" -DestinationPath $mainZip -Force

# Clean staging directory
Remove-Item -Path $stagingDir -Recurse -Force

$zipItem = Get-Item $mainZip
$sizeMB = [math]::Round($zipItem.Length / 1MB, 2)
Write-Host "Successfully generated FunscriptToolbox.zip ($sizeMB MB)" -ForegroundColor Green
