param(
    [switch]$SelfContained,   # legacy alias of -SingleFile
    [switch]$SingleFile,      # lone self-contained exe (BunkrDownloader-style) + zip
    [switch]$Portable,        # self-contained exe+DLLs folder + zip (CyberleekViewer-style)
    [switch]$RefreshIcons,
    [string]$RestoreSource,   # NuGet source to restore from (folder or URL) instead of nuget.org
    [switch]$SkipPnpm         # do not fetch the pnpm runtime used to install plugins
)
<#
.SYNOPSIS
    Builds the DeepSeek Harness desktop app.

.DESCRIPTION
    Default (no switch): framework-dependent single-file exe into dist\ -
    small, for local development; needs the .NET 8 runtime installed.

    -Portable    Self-contained folder (exe + DLLs, .NET runtime bundled) plus
                 a release zip. Runs on any 64-bit Windows 10/11 without a
                 .NET install. Folder layout keeps startup fast and triggers
                 fewer AV false positives (CyberleekViewer-style).
    -SingleFile  One self-contained exe (runtime + native libs bundled,
                 extracted to %TEMP% on start) plus a release zip
                 (BunkrDownloader-style).
    -RefreshIcons  First re-fetches the official DeepSeek icons
                 (tools\update-icons.ps1) and regenerates the .ico assets.
    -RestoreSource  Restore NuGet packages from this source instead of the
                 machine's configured feeds. Use a folder of .nupkg files to
                 build offline, or an alternate feed URL. The restore runs
                 once up front and the publish step then skips its own restore.

    Artifacts land in release\ (git-ignored, ready for GitHub Releases):
      release\DeepSeekHarness-portable-win-x64-<ver>\        (folder; -Portable)
      release\DeepSeekHarness-portable-win-x64-<ver>.zip     (-Portable)
      release\DeepSeekHarness-win-x64-<ver>.exe              (-SingleFile)
      release\DeepSeekHarness-win-x64-<ver>.zip              (-SingleFile)

.EXAMPLE
    .\build.ps1                 # dev exe (framework-dependent) into dist\
    .\build.ps1 -Portable       # portable folder + zip
    .\build.ps1 -SingleFile     # lone portable exe + zip
    .\build.ps1 -Portable -RefreshIcons
    .\build.ps1 -SingleFile -RestoreSource C:\offline-nuget   # build without nuget.org
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\DeepSeekHarness\DeepSeekHarness.csproj'
$dist = Join-Path $root 'dist'
$release = Join-Path $root 'release'

if ($RestoreSource -and $RestoreSource -notmatch '^[a-z][a-z0-9+.-]*://' -and -not (Test-Path -LiteralPath $RestoreSource)) {
    # A terminating Write-Error would exit 1 before the documented code.
    [Console]::Error.WriteLine(("error: restore source not found: {0}" -f $RestoreSource))
    exit 2
}

if ($RefreshIcons) {
    pwsh -NoProfile -File (Join-Path $root 'tools\update-icons.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Get-PnpmRuntime {
    <#
      Fetches the pnpm runtime the app uses to install third-party harness
      plugins. It is fetched here rather than committed because it is a 12 MB
      vendored binary: assets\pnpm\ is git-ignored, and a build without network
      still works - the app then downloads the runtime the first time someone
      installs a plugin.
    #>
    param([switch]$Skip)
    if ($Skip) { Write-Output 'skipping the pnpm runtime (-SkipPnpm)'; return }
    $assets = Join-Path $root 'assets\pnpm'
    $target = Join-Path $assets 'pnpm.mjs'
    if (Test-Path -LiteralPath $target) {
        $mb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
        Write-Output ("pnpm runtime present ({0} MB)" -f $mb)
        return
    }
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        Write-Output 'npm was not found; the build will ship without the pnpm runtime'
        return
    }
    $version = '11.7.0'
    Write-Output ("fetching the pnpm {0} runtime" -f $version)
    New-Item -ItemType Directory -Force -Path $assets | Out-Null
    $cache = Join-Path $root '.pnpm-fetch-cache'
    try {
        & npm install --prefix $assets "pnpm@$version" --no-save --no-audit --no-fund --loglevel=error --cache $cache 2>&1 |
            Select-Object -Last 3 | ForEach-Object { Write-Output ("  " + $_) }
        $dist = Join-Path $assets 'node_modules\pnpm\dist'
        foreach ($file in @('pnpm.mjs', 'worker.js')) {
            Copy-Item -LiteralPath (Join-Path $dist $file) -Destination (Join-Path $assets $file) -Force
        }
        Remove-Item -LiteralPath (Join-Path $assets 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $assets 'package.json') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $assets 'package-lock.json') -Force -ErrorAction SilentlyContinue
        Write-Output ("pnpm runtime ready: {0}" -f $assets)
    }
    catch {
        Write-Output ("could not fetch the pnpm runtime ({0}); the app will download it on demand" -f $_.Exception.Message)
    }
}
function Get-Version {
    $xml = Get-Content -LiteralPath $proj -Raw
    $m = [regex]::Match($xml, '<Version>([^<]+)</Version>')
    if ($m.Success) { return $m.Groups[1].Value }
    return '0.0.0'
}

function Invoke-Restore {
    if (-not $RestoreSource) { return }
    Write-Output ("restoring NuGet packages from {0}" -f $RestoreSource)
    dotnet restore $proj -r win-x64 --source $RestoreSource
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Invoke-Publish {
    param([string[]]$ExtraArgs, [string]$OutDir, [string]$SelfContainedMode = 'true')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutDir) | Out-Null
    $publishArgs = @('publish', $proj, '-c', 'Release', '-r', 'win-x64',
                     '--self-contained', $SelfContainedMode)
    if ($RestoreSource) { $publishArgs += '--no-restore' }
    $publishArgs += $ExtraArgs
    $publishArgs += @('-o', $OutDir)
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function New-Zip {
    param([string]$SourcePath, [string]$ZipPath)
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Compress-Archive -Path $SourcePath -DestinationPath $ZipPath -CompressionLevel Optimal -Force
    Write-Output ("zip: {0} ({1:N1} MB)" -f $ZipPath, ((Get-Item -LiteralPath $ZipPath).Length / 1MB))
}

$version = Get-Version
Write-Output ("building DeepSeek Harness desktop v{0}" -f $version)

Get-PnpmRuntime -Skip:$SkipPnpm

if ($SelfContained) { $SingleFile = $true }

if ($Portable -and $SingleFile) {
    Write-Output 'use either -Portable or -SingleFile, not both; defaulting to -Portable'
    $SingleFile = $false
}

Invoke-Restore

# --- default: local dev exe (framework-dependent) ----------------------------
if (-not $Portable -and -not $SingleFile) {
    Write-Output 'publishing dev exe (framework-dependent single file) -> dist\'
    Invoke-Publish -ExtraArgs @('-p:PublishSingleFile=true') -OutDir $dist -SelfContainedMode 'false'
    Write-Output ("published: {0}" -f (Join-Path $dist 'DeepSeekHarness.exe'))
    exit 0
}

# --- -Portable: self-contained folder + zip ----------------------------------
if ($Portable) {
    $name = "DeepSeekHarness-portable-win-x64-$version"
    $folder = Join-Path $release $name
    Write-Output "publishing self-contained portable folder -> $folder"
    Invoke-Publish -ExtraArgs @('-p:PublishSingleFile=false', '-p:DebugType=None') -OutDir $folder
    New-Zip -SourcePath (Join-Path $folder '*') -ZipPath (Join-Path $release ($name + '.zip'))
    # stable mirror so a shortcut/run path never breaks across version bumps
    $current = Join-Path $release 'portable-current'
    if (Test-Path -LiteralPath $current) { Remove-Item -LiteralPath $current -Recurse -Force }
    Copy-Item -LiteralPath $folder -Destination $current -Recurse
    Write-Output ("portable exe: {0}" -f (Join-Path $folder 'DeepSeekHarness.exe'))
    Write-Output ("current mirror: {0}" -f (Join-Path $current 'DeepSeekHarness.exe'))
    exit 0
}

# --- -SingleFile: one self-contained exe + zip -------------------------------
if ($SingleFile) {
    $name = "DeepSeekHarness-win-x64-$version"
    $out = Join-Path $release $name
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    Write-Output "publishing self-contained single-file exe -> $out"
    Invoke-Publish -ExtraArgs @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None'
    ) -OutDir $out
    # keep the staging folder to just the exe (drop NuGet XML doc files)
    Get-ChildItem -LiteralPath $out -File -Filter '*.xml' | Remove-Item -Force
    $exe = Join-Path $out 'DeepSeekHarness.exe'
    $finalExe = Join-Path $release ($name + '.exe')
    Copy-Item -LiteralPath $exe -Destination $finalExe -Force
    New-Zip -SourcePath $finalExe -ZipPath (Join-Path $release ($name + '.zip'))
    Write-Output ("portable exe: {0}" -f $finalExe)
    exit 0
}
