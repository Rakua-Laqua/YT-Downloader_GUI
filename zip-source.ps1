[CmdletBinding()]
param(
    [string]$OutPath = $null
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$OutputRoot = (Get-Location).ProviderPath
$ProjectName = Split-Path -Leaf $OutputRoot

if ([string]::IsNullOrEmpty($OutPath)) {
    $OutPath = Join-Path $OutputRoot "$($ProjectName)_source.zip"
}

$TempDir = Join-Path $RepoRoot "temp_source_archive"

# Cleanup temp directory if it exists
if (Test-Path $TempDir) {
    Remove-Item $TempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TempDir | Out-Null

Write-Host "Collecting source files and documents to temp directory..." -ForegroundColor Cyan

# 1. Copy CHANGELOG.md and README.md
foreach ($file in @("CHANGELOG.md", "README.md")) {
    $src = Join-Path $RepoRoot $file
    if (Test-Path $src) {
        Copy-Item -LiteralPath $src -Destination $TempDir
        Write-Host "Copied: $file"
    }
}

# 2. Copy solution files dynamically to avoid encoding issues with Japanese file names
$slnFiles = Get-ChildItem -Path $RepoRoot -Filter "*.sln" -File
foreach ($sln in $slnFiles) {
    Copy-Item -LiteralPath $sln.FullName -Destination $TempDir
    Write-Host "Copied: $($sln.Name)"
}

# 3. Copy YouTubeDownloader folder (excluding bin, obj, artifacts)
$srcSrc = Join-Path $RepoRoot "YouTubeDownloader"
$destSrc = Join-Path $TempDir "YouTubeDownloader"

if (Test-Path $srcSrc) {
    Get-ChildItem -Path $srcSrc -Recurse | Where-Object {
        # Exclude 'bin', 'obj', and 'artifacts' folders and their contents
        $_.FullName -notmatch '\\(bin|obj|artifacts)(\\|$)'
    } | ForEach-Object {
        $relative = $_.FullName.Substring($srcSrc.Length + 1)
        $target = Join-Path $destSrc $relative
        
        if ($_.PsIsContainer) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
        } else {
            $parent = Split-Path -Parent $target
            if (!(Test-Path $parent)) {
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
            }
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }
    Write-Host "Copied: YouTubeDownloader (excluding bin, obj, artifacts)"
}

# 4. Create ZIP archive
if (Test-Path $OutPath) {
    Remove-Item $OutPath -Force
}

Write-Host "Creating ZIP archive: $OutPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $TempDir "*") -DestinationPath $OutPath -Force

# Cleanup temp directory
Remove-Item $TempDir -Recurse -Force

Write-Host "Successfully completed." -ForegroundColor Green
