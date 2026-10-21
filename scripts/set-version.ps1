[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string[]]$Projects = @("FunscriptToolbox", "FunscriptToolbox.Core", "FunscriptToolbox.UI", "AudioSynchronization")
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# Normalize version (strip leading 'v' or 'V')
$cleanVersion = $Version.Trim() -replace '^[vV]', ''

# Format 4-part numeric version for AssemblyVersion and AssemblyFileVersion
$parts = $cleanVersion.Split('.')
$major = if ($parts.Count -gt 0 -and $parts[0] -match '^\d+$') { [int]$parts[0] } else { 1 }
$minor = if ($parts.Count -gt 1 -and $parts[1] -match '^\d+$') { [int]$parts[1] } else { 0 }
$build = if ($parts.Count -gt 2 -and $parts[2] -match '^\d+$') { [int]$parts[2] } else { 0 }
$revision = if ($parts.Count -gt 3 -and $parts[3] -match '^\d+$') { [int]$parts[3] } else { 0 }

$quadVersion = "$major.$minor.$build.$revision"

Write-Host "Updating assembly versions to '$quadVersion' (Informational: '$cleanVersion')..." -ForegroundColor Cyan

foreach ($proj in $Projects) {
    $assemblyInfoPath = Join-Path $repoRoot "src\$proj\Properties\AssemblyInfo.cs"
    if (Test-Path $assemblyInfoPath) {
        $content = [System.IO.File]::ReadAllText($assemblyInfoPath)

        # Replace non-commented AssemblyVersion
        $content = [System.Text.RegularExpressions.Regex]::Replace(
            $content,
            '(?m)^(\s*\[assembly:\s*AssemblyVersion\(")[^"]+("\)\])',
            "`${1}$quadVersion`${2}"
        )

        # Replace non-commented AssemblyFileVersion
        $content = [System.Text.RegularExpressions.Regex]::Replace(
            $content,
            '(?m)^(\s*\[assembly:\s*AssemblyFileVersion\(")[^"]+("\)\])',
            "`${1}$quadVersion`${2}"
        )

        # Replace or append AssemblyInformationalVersion
        if ($content -match '(?m)^\s*\[assembly:\s*AssemblyInformationalVersion\(') {
            $content = [System.Text.RegularExpressions.Regex]::Replace(
                $content,
                '(?m)^(\s*\[assembly:\s*AssemblyInformationalVersion\(")[^"]+("\)\])',
                "`${1}$cleanVersion`${2}"
            )
        } else {
            $content += "`r`n[assembly: System.Reflection.AssemblyInformationalVersion(`"$cleanVersion`")]`r`n"
        }

        [System.IO.File]::WriteAllText($assemblyInfoPath, $content)
        Write-Host "  Updated $proj -> $quadVersion ($cleanVersion)" -ForegroundColor Green
    }
}
