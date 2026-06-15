[CmdletBinding()]
param(
    # Repo root. Defaults to the directory containing this script.
    [string]$RepoRoot = $PSScriptRoot,

    # Optional. Used to infer Version/AppName for .NET projects.
    [string]$ProjectPath,

    # Optional. If omitted, inferred from ProjectPath or the repo directory name.
    [string]$AppName,

    # Optional. If omitted, read from ProjectPath, package.json, VERSION, or version.txt.
    [string]$Version,

    # stable -> vX.Y.Z, beta -> vX.Y.Z-beta.N, rc -> vX.Y.Z-rc.N
    [ValidateSet('stable', 'beta', 'rc')]
    [string]$Channel = 'stable',

    [int]$PreReleaseNumber = 1,

    # Override the generated tag when needed.
    [string]$Tag,

    # Labels used by publish scripts and asset names.
    [string]$Runtime = 'win-x64',
    [string]$Mode = 'framework-dependent',
    [string]$Configuration = 'Release',

    # Optional publish/build script. Defaults to ./publish.ps1 when present.
    [string]$PublishScriptPath,

    # Override publish script arguments completely.
    [string[]]$PublishArguments,

    # Convenience switches passed to the default publish.ps1 argument set.
    [switch]$IncludeTools,
    [switch]$Clean,

    # Use existing release assets. Supports zip/msi/exe/7z/etc.
    [string[]]$AssetPath,

    # Find assets by glob after publishing, or instead of AssetPath.
    [string[]]$AssetGlob,

    # Work directory for renamed assets, release notes, and tag messages.
    # Defaults to a temp path so gh asset parsing is not confused by '#'
    # characters in repository paths (for example a parent folder named "C#").
    [string]$ReleaseWorkRoot,

    # Asset name template. Tokens:
    # {AppName} {Version} {Tag} {TagVersion} {Channel} {Runtime} {Arch} {Mode} {BaseName} {Ext} {Index}
    [string]$AssetNameTemplate = '{AppName}_v{TagVersion}_{Runtime}{Ext}',

    [string]$Title,
    [string]$ChangelogPath = 'CHANGELOG.md',

    # Create as draft. Pre-releases are still marked as pre-release automatically.
    [switch]$Draft,

    # Edit an existing release and replace uploaded assets.
    [switch]$UpdateExisting,

    # Allow creating a release while the working tree is dirty.
    [switch]$AllowDirty,

    # Print actions without creating tags, publishing, or touching GitHub.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    if (![string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $RepoRoot = $PSScriptRoot
    }
    else {
        $RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
}

function Resolve-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $RepoRoot $Path
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )

    Write-Host "> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    if ($DryRun) {
        return
    }

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Get-DefaultProjectPath {
    $projects = Get-ChildItem -LiteralPath $RepoRoot -Recurse -Filter '*.csproj' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts)\\' }

    if ($projects.Count -eq 1) {
        return $projects[0].FullName
    }

    return $null
}

function Get-VersionFromCsproj {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        [xml]$xml = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
        $value = $xml.Project.PropertyGroup.Version | Select-Object -First 1
        if (![string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim().TrimStart('v')
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-VersionFromJsonFile {
    param([string]$Path)

    if (!(Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if (![string]::IsNullOrWhiteSpace($json.version)) {
            return ([string]$json.version).Trim().TrimStart('v')
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-VersionFromTextFile {
    param([string]$Path)

    if (!(Test-Path -LiteralPath $Path)) {
        return $null
    }

    $line = Get-Content -LiteralPath $Path -Encoding UTF8 | Select-Object -First 1
    if (![string]::IsNullOrWhiteSpace($line)) {
        return $line.Trim().TrimStart('v')
    }

    return $null
}

function Get-DefaultVersion {
    param([string]$ResolvedProjectPath)

    $fromProject = Get-VersionFromCsproj -Path $ResolvedProjectPath
    if ($fromProject) { return $fromProject }

    $fromPackageJson = Get-VersionFromJsonFile -Path (Join-Path $RepoRoot 'package.json')
    if ($fromPackageJson) { return $fromPackageJson }

    foreach ($name in @('VERSION', 'version.txt')) {
        $fromText = Get-VersionFromTextFile -Path (Join-Path $RepoRoot $name)
        if ($fromText) { return $fromText }
    }

    throw "Version was not specified and could not be inferred."
}

function Get-DefaultAppName {
    param([string]$ResolvedProjectPath)

    if (![string]::IsNullOrWhiteSpace($ResolvedProjectPath) -and (Test-Path -LiteralPath $ResolvedProjectPath)) {
        try {
            [xml]$xml = Get-Content -LiteralPath $ResolvedProjectPath -Raw -Encoding UTF8
            $assemblyName = $xml.Project.PropertyGroup.AssemblyName | Select-Object -First 1
            if (![string]::IsNullOrWhiteSpace($assemblyName)) {
                return $assemblyName.Trim()
            }
        }
        catch { }

        return [System.IO.Path]::GetFileNameWithoutExtension($ResolvedProjectPath)
    }

    return Split-Path -Leaf (Resolve-Path -LiteralPath $RepoRoot)
}

function New-ReleaseTag {
    param(
        [Parameter(Mandatory = $true)][string]$BaseVersion,
        [Parameter(Mandatory = $true)][string]$ReleaseChannel,
        [Parameter(Mandatory = $true)][int]$Number
    )

    $normalized = $BaseVersion.Trim().TrimStart('v')
    if ($ReleaseChannel -eq 'stable') {
        return "v$normalized"
    }

    if ($Number -lt 1) {
        throw "PreReleaseNumber must be 1 or greater."
    }

    return "v$normalized-$ReleaseChannel.$Number"
}

function Get-ArchFromRuntime {
    param([string]$RuntimeValue)

    if ($RuntimeValue -match 'arm64') { return 'arm64' }
    if ($RuntimeValue -match 'x64') { return 'x64' }
    if ($RuntimeValue -match 'x86') { return 'x86' }
    return $RuntimeValue
}

function Test-GitHasRef {
    param([Parameter(Mandatory = $true)][string]$RefName)

    if ($DryRun) {
        return $false
    }

    & git rev-parse --verify --quiet $RefName *> $null
    return $LASTEXITCODE -eq 0
}

function Get-ChangelogSection {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BaseVersion
    )

    if (!(Test-Path -LiteralPath $Path)) {
        return "Updated to v$BaseVersion"
    }

    $escaped = [regex]::Escape($BaseVersion.Trim().TrimStart('v'))
    $headingPattern = "^\s*##\s+\[?v?$escaped(\]|\s|-|$)"
    $lines = Get-Content -LiteralPath $Path -Encoding UTF8
    $startIndex = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $headingPattern) {
            $startIndex = $i
            break
        }
    }

    if ($startIndex -lt 0) {
        return "Updated to v$BaseVersion"
    }

    $section = New-Object System.Collections.Generic.List[string]
    for ($i = $startIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i].StartsWith('## ')) {
            break
        }

        $section.Add($lines[$i])
    }

    return ($section -join [Environment]::NewLine).Trim()
}

function Resolve-AssetPaths {
    param(
        [string[]]$Paths,
        [string[]]$Globs
    )

    $resolved = New-Object System.Collections.Generic.List[string]

    foreach ($path in @($Paths)) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $resolvedPath = (Resolve-Path -LiteralPath (Resolve-RepoPath $path) -ErrorAction Stop).Path
        $resolved.Add($resolvedPath)
    }

    foreach ($glob in @($Globs)) {
        if ([string]::IsNullOrWhiteSpace($glob)) { continue }
        $fullGlob = Resolve-RepoPath $glob
        $dir = Split-Path -Parent $fullGlob
        $filter = Split-Path -Leaf $fullGlob
        if ([string]::IsNullOrWhiteSpace($dir)) { $dir = $RepoRoot }
        if (!(Test-Path -LiteralPath $dir)) { continue }

        Get-ChildItem -LiteralPath $dir -Filter $filter -File |
            Sort-Object LastWriteTimeUtc -Descending |
            ForEach-Object { $resolved.Add($_.FullName) }
    }

    return @($resolved | Select-Object -Unique)
}

function Get-DefaultAssetGlob {
    return @("artifacts\dist\$Runtime\*.zip", "artifacts\dist\$Runtime\*.msi", "artifacts\dist\$Runtime\*.exe", "artifacts\dist\$Runtime\*.7z")
}

function Format-AssetName {
    param(
        [Parameter(Mandatory = $true)][string]$Template,
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][int]$Index,
        [Parameter(Mandatory = $true)][string]$ResolvedAppName,
        [Parameter(Mandatory = $true)][string]$BaseVersion,
        [Parameter(Mandatory = $true)][string]$ReleaseTag
    )

    $extension = [System.IO.Path]::GetExtension($SourcePath)
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($SourcePath)
    $tagVersion = $ReleaseTag.TrimStart('v')
    $arch = Get-ArchFromRuntime -RuntimeValue $Runtime

    $name = $Template
    $name = $name.Replace('{AppName}', $ResolvedAppName)
    $name = $name.Replace('{Version}', $BaseVersion)
    $name = $name.Replace('{Tag}', $ReleaseTag)
    $name = $name.Replace('{TagVersion}', $tagVersion)
    $name = $name.Replace('{Channel}', $Channel)
    $name = $name.Replace('{Runtime}', $Runtime)
    $name = $name.Replace('{Arch}', $arch)
    $name = $name.Replace('{Mode}', $Mode)
    $name = $name.Replace('{BaseName}', $baseName)
    $name = $name.Replace('{Ext}', $extension)
    $name = $name.Replace('{Index}', $Index.ToString())

    return $name
}

function Copy-ReleaseAssets {
    param(
        [Parameter(Mandatory = $true)][string[]]$SourcePaths,
        [Parameter(Mandatory = $true)][string]$ReleaseTag,
        [Parameter(Mandatory = $true)][string]$ReleaseDir,
        [Parameter(Mandatory = $true)][string]$ResolvedAppName,
        [Parameter(Mandatory = $true)][string]$BaseVersion
    )

    $result = New-Object System.Collections.Generic.List[string]
    $usedNames = @{}
    $index = 1

    foreach ($source in $SourcePaths) {
        $assetName = Format-AssetName -Template $AssetNameTemplate -SourcePath $source -Index $index -ResolvedAppName $ResolvedAppName -BaseVersion $BaseVersion -ReleaseTag $ReleaseTag
        if ($usedNames.ContainsKey($assetName)) {
            $nameNoExt = [System.IO.Path]::GetFileNameWithoutExtension($assetName)
            $ext = [System.IO.Path]::GetExtension($assetName)
            $assetName = "$nameNoExt-$index$ext"
        }
        $usedNames[$assetName] = $true

        $destPath = Join-Path $releaseDir $assetName
        if ($DryRun) {
            Write-Host "> Copy-Item $source $destPath" -ForegroundColor DarkGray
        }
        else {
            New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
            Copy-Item -LiteralPath $source -Destination $destPath -Force
        }

        $result.Add($destPath)
        $index++
    }

    return @($result)
}

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$safeRepoName = (Split-Path -Leaf $RepoRoot) -replace '[^A-Za-z0-9_.-]', '_'
$ReleaseWorkRoot = if ([string]::IsNullOrWhiteSpace($ReleaseWorkRoot)) {
    Join-Path ([System.IO.Path]::GetTempPath()) "github-release\$safeRepoName"
}
else {
    Resolve-RepoPath $ReleaseWorkRoot
}

$resolvedProjectPath = if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    Get-DefaultProjectPath
}
else {
    Resolve-RepoPath $ProjectPath
}

$defaultPublishScript = Join-Path $RepoRoot 'publish.ps1'
$resolvedPublishScriptPath = if ([string]::IsNullOrWhiteSpace($PublishScriptPath)) {
    if (Test-Path -LiteralPath $defaultPublishScript) { $defaultPublishScript } else { $null }
}
else {
    Resolve-RepoPath $PublishScriptPath
}

$baseVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
    Get-DefaultVersion -ResolvedProjectPath $resolvedProjectPath
}
else {
    $Version.Trim().TrimStart('v')
}

$resolvedAppName = if ([string]::IsNullOrWhiteSpace($AppName)) {
    Get-DefaultAppName -ResolvedProjectPath $resolvedProjectPath
}
else {
    $AppName.Trim()
}

$releaseTag = if ([string]::IsNullOrWhiteSpace($Tag)) {
    New-ReleaseTag -BaseVersion $baseVersion -ReleaseChannel $Channel -Number $PreReleaseNumber
}
else {
    $Tag.Trim()
}

$isPrerelease = $releaseTag -match '-(alpha|beta|rc)\.'
$releaseTitle = if ([string]::IsNullOrWhiteSpace($Title)) {
    "version $($releaseTag.TrimStart('v'))"
}
else {
    $Title
}

Write-Host "Release tag : $releaseTag" -ForegroundColor Cyan
Write-Host "Title       : $releaseTitle"
Write-Host "AppName     : $resolvedAppName"
Write-Host "Runtime     : $Runtime"
Write-Host "Channel     : $(if ($isPrerelease) { 'pre-release' } else { 'stable' })"

if (!$AllowDirty) {
    $status = (& git -C $RepoRoot status --porcelain) -join [Environment]::NewLine
    if (![string]::IsNullOrWhiteSpace($status)) {
        throw "Working tree is dirty. Commit/stash changes first, or rerun with -AllowDirty."
    }
}

if (!$DryRun) {
    Invoke-Checked -FilePath 'gh' -Arguments @('--version')
    Invoke-Checked -FilePath 'gh' -Arguments @('auth', 'status')
}

if ($AssetPath.Count -eq 0 -and $AssetGlob.Count -eq 0) {
    if ([string]::IsNullOrWhiteSpace($resolvedPublishScriptPath)) {
        throw "No assets or publish script were specified. Use -AssetPath, -AssetGlob, or -PublishScriptPath."
    }

    $publishArgsToUse = if ($PublishArguments.Count -gt 0) {
        $PublishArguments
    }
    else {
        $args = @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $resolvedPublishScriptPath,
            '-Configuration', $Configuration,
            '-Runtime', $Runtime,
            '-Mode', $Mode,
            '-Zip'
        )
        if ($IncludeTools) { $args += '-IncludeTools' }
        if ($Clean) { $args += '-Clean' }
        $args
    }

    Invoke-Checked -FilePath 'powershell' -Arguments $publishArgsToUse
    $AssetGlob = Get-DefaultAssetGlob
}

$assetSources = Resolve-AssetPaths -Paths $AssetPath -Globs $AssetGlob
if ($assetSources.Count -eq 0 -and $DryRun) {
    $fakeAssetName = "$resolvedAppName-v$baseVersion-$Runtime-$Mode.zip"
    $assetSources = @(Join-Path $RepoRoot "artifacts\dist\$Runtime\$fakeAssetName")
}
elseif ($assetSources.Count -eq 0) {
    throw "No release assets were found."
}

$releaseWorkDir = Join-Path $ReleaseWorkRoot $releaseTag
$assetsForUpload = Copy-ReleaseAssets -SourcePaths $assetSources -ReleaseTag $releaseTag -ReleaseDir $releaseWorkDir -ResolvedAppName $resolvedAppName -BaseVersion $baseVersion
$resolvedChangelogPath = Resolve-RepoPath $ChangelogPath
$notes = Get-ChangelogSection -Path $resolvedChangelogPath -BaseVersion $baseVersion
$notesPath = Join-Path $releaseWorkDir 'release-notes.md'

if ($DryRun) {
    Write-Host "> Write release notes: $notesPath" -ForegroundColor DarkGray
}
else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $notesPath) | Out-Null
    [System.IO.File]::WriteAllText($notesPath, $notes, [System.Text.UTF8Encoding]::new($false))
}

$tagExists = Test-GitHasRef -RefName "refs/tags/$releaseTag"
if (!$tagExists) {
    $tagMessagePath = Join-Path $releaseWorkDir 'tag-message.txt'
    if (!$DryRun) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $tagMessagePath) | Out-Null
        [System.IO.File]::WriteAllText($tagMessagePath, $releaseTitle, [System.Text.UTF8Encoding]::new($false))
    }

    Invoke-Checked -FilePath 'git' -Arguments @('-C', $RepoRoot, 'tag', '-a', $releaseTag, '-F', $tagMessagePath)
}
else {
    Write-Host "Tag already exists locally: $releaseTag" -ForegroundColor Yellow
}

Invoke-Checked -FilePath 'git' -Arguments @('-C', $RepoRoot, 'push', 'origin', $releaseTag)

$releaseExists = $false
if (!$DryRun) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & gh release view $releaseTag *> $null
        $releaseExists = $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

if ($releaseExists) {
    if (!$UpdateExisting) {
        throw "Release already exists: $releaseTag. Rerun with -UpdateExisting to edit it and replace assets."
    }

    $editArgs = @('release', 'edit', $releaseTag, '--title', $releaseTitle, '--notes-file', $notesPath)
    if ($isPrerelease) {
        $editArgs += '--prerelease'
    }
    elseif ($Channel -eq 'stable') {
        $editArgs += '--latest'
    }

    Invoke-Checked -FilePath 'gh' -Arguments $editArgs
    foreach ($asset in $assetsForUpload) {
        Invoke-Checked -FilePath 'gh' -Arguments @('release', 'upload', $releaseTag, $asset, '--clobber')
    }
}
else {
    $createArgs = @('release', 'create', $releaseTag)
    $createArgs += $assetsForUpload
    $createArgs += @('--title', $releaseTitle, '--notes-file', $notesPath)

    if ($Draft) {
        $createArgs += '--draft'
    }
    if ($isPrerelease) {
        $createArgs += '--prerelease'
    }
    elseif ($Channel -eq 'stable') {
        $createArgs += '--latest'
    }

    Invoke-Checked -FilePath 'gh' -Arguments $createArgs
}

Write-Host "Release complete: $releaseTag" -ForegroundColor Green
Write-Host "Assets:"
$assetsForUpload | ForEach-Object { Write-Host "  $_" }
