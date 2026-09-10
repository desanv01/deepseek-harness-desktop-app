#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the portable single-file DeepSeek Harness executable.

.DESCRIPTION
    Produces a fully portable single .exe - no DLLs next to it and no .NET
    installation needed on the target machine:

      release\DeepSeekHarness-win-x64-<version>.exe
      release\DeepSeekHarness-win-x64-<version>.zip

    The .NET runtime and the native WebView2 loader are bundled inside the
    exe (extracted to %TEMP% on first start). Only OS dependency on the
    target: the WebView2 Runtime (preinstalled on Windows 11 / most
    Windows 10 machines).

    This is a thin alias for `build.ps1 -SingleFile`; that script owns the
    publish flags and the release layout.

    Usage:
      .\build_portable.ps1                # build single-file exe + zip
      .\build_portable.ps1 -RefreshIcons  # re-fetch the official DeepSeek
                                          # icons from the websites first
      .\build_portable.ps1 -RestoreSource C:\offline-nuget   # build offline
.EXAMPLE
    pwsh -NoProfile -File .\build_portable.ps1
#>
param(
    [switch]$RefreshIcons,
    [string]$RestoreSource
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $root 'build.ps1') -SingleFile -RefreshIcons:$RefreshIcons -RestoreSource $RestoreSource
exit $LASTEXITCODE
