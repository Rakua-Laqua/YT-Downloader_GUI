[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('self-contained', 'framework-dependent', 'both')]
    [string]$Mode = 'framework-dependent',

    # 出力フォルダ名・ZIP名に "-with-tools" を付ける（手動でツールを配置する想定）
    [switch]$IncludeTools,

    # 成果物フォルダを事前に掃除する
    [switch]$Clean,

    # publish 出力を zip 化する（配布用）
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'YouTubeDownloader\YouTubeDownloader.csproj'

if (!(Test-Path $ProjectPath)) {
    throw "Project not found: $ProjectPath"
}

# バージョン取得
function Get-AppVersion {
    param([Parameter(Mandatory = $true)][string]$CsprojPath)
    try {
        [xml]$xml = Get-Content -LiteralPath $CsprojPath -Raw
        $v = $xml.Project.PropertyGroup.Version | Select-Object -First 1
        if (![string]::IsNullOrWhiteSpace($v)) { return $v.Trim() }
    }
    catch { }
    try {
        $git = Get-Command git -ErrorAction Stop
        $hash = (& $git.Source -C $RepoRoot rev-parse --short HEAD) 2>$null
        if (![string]::IsNullOrWhiteSpace($hash)) { return $hash.Trim() }
    }
    catch { }
    return ""
}

# 実行中プロセスを確認して終了を促す
function Stop-RunningApp {
    param([string]$ExePath)
    if (!(Test-Path $ExePath)) { return $true }

    $exeName = [System.IO.Path]::GetFileNameWithoutExtension($ExePath)
    $procs = Get-Process -Name $exeName -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -eq $ExePath
    }
    if ($procs) {
        Write-Host "WARNING: $exeName is running. Attempting to stop..." -ForegroundColor Yellow
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
    return $true
}

$AppName = "YouTubeDownloader"
$AppVersion = Get-AppVersion -CsprojPath $ProjectPath
$DistDir = Join-Path $RepoRoot ("artifacts\dist\$Runtime")

function Publish-One {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('self-contained', 'framework-dependent')]
        [string]$OneMode
    )

    $toolsSuffix = if ($IncludeTools) { '-with-tools' } else { '' }
    $OutDir = Join-Path $RepoRoot ("artifacts\publish\$Runtime\$OneMode$toolsSuffix")
    $exePath = Join-Path $OutDir "$AppName.exe"

    # 実行中のアプリを停止
    Stop-RunningApp -ExePath $exePath

    if ($Clean -and (Test-Path $OutDir)) {
        Remove-Item $OutDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    Write-Host "`nPublishing..." -ForegroundColor Cyan
    Write-Host "  Configuration: $Configuration"
    Write-Host "  Runtime      : $Runtime"
    Write-Host "  Mode         : $OneMode"
    Write-Host "  WithTools    : $IncludeTools"
    Write-Host "  Output       : $OutDir"

    if ($OneMode -eq 'self-contained') {
        dotnet publish $ProjectPath `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -o $OutDir `
            /p:PublishSingleFile=true `
            /p:IncludeNativeLibrariesForSelfExtract=true
    }
    else {
        dotnet publish $ProjectPath `
            -c $Configuration `
            -r $Runtime `
            --self-contained false `
            -o $OutDir
    }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Host "Done: $OutDir" -ForegroundColor Green

    if ($Zip) {
        # ビルド直後のファイルロック解除を待つ
        Start-Sleep -Seconds 2

        New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

        $verPart = if (![string]::IsNullOrWhiteSpace($AppVersion)) { "-v$AppVersion" } else { '' }
        $zipName = "$AppName$verPart-$Runtime-$OneMode$toolsSuffix.zip"
        $zipPath = Join-Path $DistDir $zipName

        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

        Write-Host "Zipping -> $zipPath" -ForegroundColor Cyan
        Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath -Force
        Write-Host "Zip done." -ForegroundColor Green
    }
}

if ($Mode -eq 'both') {
    Publish-One -OneMode 'self-contained'
    Publish-One -OneMode 'framework-dependent'
}
else {
    Publish-One -OneMode $Mode
}
