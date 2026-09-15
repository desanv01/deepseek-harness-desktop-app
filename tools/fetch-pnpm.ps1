#Requires -Version 5.1
<#
.SYNOPSIS
    Fetches the pnpm runtime the desktop app carries.

.DESCRIPTION
    `dsh plugin add` forwards to pnpm, and a user's machine may not have it, so
    the app ships one. The runtime is two files (pnpm.mjs and worker.js, about
    12 MB) and is fetched here rather than committed: assets\pnpm\ is
    git-ignored, and the project file embeds whatever is present at build time.

    A build that runs without network still works. The app then fetches the
    runtime with npm the first time someone installs a plugin.

.PARAMETER Version
    pnpm version to fetch. Defaults to the version the app was written against.

.PARAMETER Force
    Re-fetch even when the runtime is already there.

.EXAMPLE
    .\tools\fetch-pnpm.ps1
    .\tools\fetch-pnpm.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$Version = '11.7.0',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root 'assets\pnpm'
$target = Join-Path $assets 'pnpm.mjs'

if ((Test-Path -LiteralPath $target) -and -not $Force) {
    $mb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
    Write-Output ("pnpm runtime present ({0} MB)" -f $mb)
    exit 0
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    # Not fatal: the app fetches the runtime on demand when a build ships without it.
    Write-Output 'npm was not found; the build will ship without the pnpm runtime'
    exit 0
}

Write-Output ("fetching the pnpm {0} runtime" -f $Version)
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$cache = Join-Path $root '.pnpm-fetch-cache'

& npm install --prefix $assets "pnpm@$Version" --no-save --no-audit --no-fund --loglevel=error --cache $cache 2>&1 |
    Select-Object -Last 3 | ForEach-Object { Write-Output ("  " + $_) }

$dist = Join-Path $assets 'node_modules\pnpm\dist'
foreach ($file in @('pnpm.mjs', 'worker.js')) {
    Copy-Item -LiteralPath (Join-Path $dist $file) -Destination (Join-Path $assets $file) -Force
}
Remove-Item -LiteralPath (Join-Path $assets 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $assets 'package.json') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $assets 'package-lock.json') -Force -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath $target)) {
    Write-Output 'the pnpm runtime could not be fetched; the app will download it on demand'
    exit 0
}
Write-Output ("pnpm runtime ready: {0}" -f $assets)
exit 0
