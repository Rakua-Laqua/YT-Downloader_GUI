param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # If specified, publish as framework-dependent (requires .NET Desktop Runtime on target PC)
    [switch]$FrameworkDependent,

    # Skip ZIP creation
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'YouTubeDownloader\YouTubeDownloader.csproj'

$mode = if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }
$outputDir = Join-Path $repoRoot ("artifacts\publish\$Runtime\$mode")

Write-Host "Publishing: runtime=$Runtime, mode=$mode" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$publishArgs = @(
    'publish',
    $projectPath,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $outputDir,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true'
)

if ($FrameworkDependent) {
    $publishArgs += @('--self-contained', 'false')
} else {
    $publishArgs += @('--self-contained', 'true')
}

dotnet @publishArgs

Write-Host "Output: $outputDir" -ForegroundColor Green

if (-not $NoZip) {
    $distDir = Join-Path $repoRoot 'artifacts\dist'
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null

    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $zipName = "YouTubeDownloader_${Runtime}_${mode}_${timestamp}.zip"
    $zipPath = Join-Path $distDir $zipName

    if (Test-Path $zipPath) {
        Remove-Item -Force $zipPath
    }

    Compress-Archive -Path (Join-Path $outputDir '*') -DestinationPath $zipPath -Force
    Write-Host "ZIP: $zipPath" -ForegroundColor Green
}
