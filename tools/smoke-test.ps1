#Requires -Version 5.1
<#
.SYNOPSIS
    Behavioural smoke test for how the desktop app finds and repairs the harness CLI.

.DESCRIPTION
    Runs DeepSeekHarness.exe against throwaway fixtures instead of the real
    machine: a fake npm global prefix (shim plus package) and a fake npm that can
    succeed, fail, or leave a half-written package behind - the state an
    interrupted `npm install -g` produces, and the state that used to end a
    launch with "@deepseek-ai/dsh is not installed globally".

    Nothing here needs a network, a real harness install, or a real npm. Each
    scenario asserts what the app reports and what it does to the installation.

.PARAMETER Exe
    The built DeepSeekHarness.exe to exercise. Defaults to dist\DeepSeekHarness.exe
    next to the repository root.

.PARAMETER WorkRoot
    Where the fixtures are created. Defaults to a folder under the temp directory.

.PARAMETER KeepFixtures
    Leave the fixtures on disk for inspection.

.EXAMPLE
    .\tools\smoke-test.ps1
    .\tools\smoke-test.ps1 -Exe .\dist\DeepSeekHarness.exe -KeepFixtures
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$WorkRoot,
    [switch]$KeepFixtures
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Exe) { $Exe = Join-Path $root 'dist\DeepSeekHarness.exe' }
if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host ("error: {0} was not found; build it first (build.ps1) or pass -Exe" -f $Exe) -ForegroundColor Red
    exit 2
}
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if (-not $WorkRoot) { $WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('dsh-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

$script:Passed = 0
$script:Failed = 0
$originalPath = $env:PATH
$originalAppData = $env:APPDATA
$originalDesktopHome = $env:DSH_DESKTOP_HOME
$originalHarnessHome = $env:DSH_HOME

function Write-Step { param([string]$Text) Write-Host ("==> {0}" -f $Text) -ForegroundColor Cyan }
function Write-Ok { param([string]$Text) Write-Host ("    PASS  {0}" -f $Text) -ForegroundColor Green; $script:Passed++ }
function Write-Bad { param([string]$Text) Write-Host ("    FAIL  {0}" -f $Text) -ForegroundColor Red; $script:Failed++ }

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$What)
    if ($Text -like ('*' + $Needle + '*')) { Write-Ok $What } else { Write-Bad ("{0} (expected to find '{1}')" -f $What, $Needle) }
}

function Assert-True {
    param([bool]$Condition, [string]$What)
    if ($Condition) { Write-Ok $What } else { Write-Bad $What }
}

# Runs the app and captures stdout/stderr plus the exit code. The .NET process
# API is used rather than `&` or Start-Process: `&` does not reliably wait for a
# GUI-subsystem executable, and Start-Process redirection needs a temp file.
function Invoke-App {
    param([string[]]$AppArguments, [string]$Name)
    $quoted = ($AppArguments | ForEach-Object {
        if ($_ -match '[\s]') { '"' + $_ + '"' } else { $_ }
    }) -join ' '

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = $quoted
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    Set-Content -LiteralPath (Join-Path $WorkRoot ($Name + '.out.txt')) -Value $stdout -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $WorkRoot ($Name + '.err.txt')) -Value $stderr -Encoding UTF8
    return @{ Code = $process.ExitCode; Out = $stdout; Err = $stderr }
}

# A fake npm global prefix: shim, package, and a fake npm that installs,
# fails, or leaves the package without the file the CLI needs.
function New-Prefix {
    param(
        [string]$Path,
        [string]$Version = '1.0.0',
        [switch]$WithoutEntry,
        [string]$ShimTarget,
        [string]$EntryFile = 'lib/bin.js',
        [string]$DeclaredBin
    )

    $packageDir = Join-Path $Path 'node_modules\@deepseek-ai\dsh'
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

    $binProperty = if ($DeclaredBin) { '"{0}"' -f $DeclaredBin } else { $null }
    if ($binProperty) {
        $manifest = '{{ "name": "@deepseek-ai/dsh", "version": "{0}", "bin": {{ "dsh": {1} }} }}' -f $Version, $binProperty
    } else {
        $manifest = '{{ "name": "@deepseek-ai/dsh", "version": "{0}", "bin": {{ "dsh": "lib/bin.js" }} }}' -f $Version
    }
    Set-Content -LiteralPath (Join-Path $packageDir 'package.json') -Value $manifest -Encoding ASCII

    if (-not $WithoutEntry) {
        $entryPath = Join-Path $packageDir $EntryFile
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $entryPath) | Out-Null
        # A stand-in for the CLI: it answers the --help probe the app runs.
        Set-Content -LiteralPath $entryPath -Encoding ASCII -Value @'
// fixture stand-in for the harness CLI
const args = process.argv.slice(2);
if (args.includes('--help')) { console.log('dsh web (fixture)'); process.exit(0); }
console.log('dsh fixture ' + args.join(' '));
process.exit(0);
'@
    }

    $target = if ($ShimTarget) { $ShimTarget } else { '%dp0%\node_modules\@deepseek-ai\dsh\lib\bin.js' }
    Set-Content -LiteralPath (Join-Path $Path 'dsh.cmd') -Encoding ASCII -Value @"
@ECHO off
node "$target" %*
"@
    Set-Content -LiteralPath (Join-Path $Path 'dsh') -Encoding ASCII -Value @"
#!/bin/sh
exec node "`$basedir/node_modules/@deepseek-ai/dsh/lib/bin.js" "`$@"
"@
    return $packageDir
}

# A fake npm: `install -g <spec>` either writes a fresh fixture package (ok),
# writes one without its entry file (broken), or does nothing (fail).
function New-FakeNpm {
    param([string]$Path, [ValidateSet('ok', 'broken', 'fail')][string]$Mode = 'ok', [string]$NewVersion = '9.9.9')

    $npmDir = Join-Path $Path 'node_modules\npm\bin'
    New-Item -ItemType Directory -Force -Path $npmDir | Out-Null
    Set-Content -LiteralPath (Join-Path $Path 'npm.cmd') -Encoding ASCII -Value "@ECHO off`r`nnode `"%~dp0node_modules\npm\bin\npm-cli.js`" %*`r`n"
    Set-Content -LiteralPath (Join-Path $Path 'npm-mode.txt') -Encoding ASCII -Value $Mode

    Set-Content -LiteralPath (Join-Path $npmDir 'npm-cli.js') -Encoding ASCII -Value @"
// fixture stand-in for npm: performs the global install the app asks for
const fs = require('fs');
const path = require('path');
const prefix = __dirname.split(path.sep + 'node_modules' + path.sep)[0];
const mode = fs.readFileSync(path.join(prefix, 'npm-mode.txt'), 'utf8').trim();
const version = '$NewVersion';
const pkg = path.join(prefix, 'node_modules', '@deepseek-ai', 'dsh');

if (mode === 'fail') {
  console.error('fixture npm: refusing to install');
  process.exit(1);
}
console.error('fixture npm: installing @deepseek-ai/dsh@' + version);
fs.mkdirSync(path.join(pkg, 'lib'), { recursive: true });
fs.writeFileSync(path.join(pkg, 'package.json'),
  JSON.stringify({ name: '@deepseek-ai/dsh', version: version, bin: { dsh: 'lib/bin.js' } }));
if (mode === 'broken') {
  // exactly what an interrupted install leaves behind: no entry file
  process.exit(1);
}
fs.writeFileSync(path.join(pkg, 'lib', 'bin.js'),
  "const a=process.argv.slice(2);if(a.includes('--help')){console.log('dsh web (fixture)');process.exit(0);}process.exit(0);\n");
process.exit(0);
"@
    return $npmDir
}

function Set-ScenarioPath {
    param([string]$Prefix, [switch]$RealHarness)

    $env:PATH = $Prefix + ';' + $originalPath
    $env:DSH_DESKTOP_HOME = Join-Path $WorkRoot 'data'
    $env:DSH_HOME = Join-Path $WorkRoot 'harness-home'

    if ($RealHarness) {
        # Plugin scenarios need the machine's own harness CLI, so leave APPDATA
        # alone: that is where the app looks for the global npm install.
        $env:APPDATA = $originalAppData
    }
    else {
        # CLI-discovery scenarios must not see the real machine's install, and
        # the app always probes %APPDATA%\npm. An empty APPDATA per fixture keeps
        # the fixture the only CLI in sight.
        $empty = Join-Path $WorkRoot 'appdata'
        New-Item -ItemType Directory -Force -Path $empty | Out-Null
        $env:APPDATA = $empty
    }
}

function Reset-Environment {
    $env:PATH = $originalPath
    $env:DSH_DESKTOP_HOME = $originalDesktopHome
    $env:DSH_HOME = $originalHarnessHome
    $env:APPDATA = $originalAppData
}

Write-Host ("smoke test: {0}" -f $Exe) -ForegroundColor Cyan
Write-Host ("fixtures  : {0}" -f $WorkRoot) -ForegroundColor Cyan

try {
    # Whether this machine has a usable harness CLI: several scenarios below only
    # make sense when one exists, and a runner without one skips them. The app is
    # asked rather than guessed at, because it searches PATH as well as the npm
    # global folder - and a runner may keep the global prefix elsewhere (GitHub's
    # Windows runners use C:\npm\global, not %APPDATA%\npm).
    New-Item -ItemType Directory -Force -Path (Join-Path $WorkRoot 'probe') | Out-Null
    Set-ScenarioPath (Join-Path $WorkRoot 'probe') -RealHarness
    $probe = Invoke-App @('--self-test') 'probe'
    $haveHarness = ($probe.Out -match 'dsh state\s+:\s+FOUND') -and [bool](Get-Command node -ErrorAction SilentlyContinue)
    # The entry file the app found, for scenarios that drive the CLI themselves.
    $harnessEntry = if ($probe.Out -match 'dsh entry\s+:\s+([^\r\n]+)') { $Matches[1].Trim() } else { $null }
    Write-Host ("    harness CLI on this machine: {0}" -f $(if ($haveHarness) { 'yes' } else { 'no' }))

    # The credential rule, exercised by the app itself. This is the one check
    # that needs no harness and no network, so it runs first and never skips: a
    # token that reaches a log is the artefact a user pastes into a bug report.
    Assert-Contains $probe.Out 'redaction   : ok' 'the token redaction rules hold'

    # A CLI that answers --version is not automatically one that can boot: a
    # release whose dependencies float can install a tree that fails to resolve
    # its own plugin rows, which is a broken harness, not a broken app. The
    # scenarios that need a server boot are gated on this probe.
    $harnessBoots = $false
    $harnessBootError = ''
    if ($haveHarness -and $harnessEntry) {
        $probeHome = Join-Path $WorkRoot 'boot-probe-home'
        New-Item -ItemType Directory -Force -Path $probeHome | Out-Null
        $env:DSH_HOME = $probeHome
        $probeOut = Join-Path $WorkRoot 'boot-probe.out.txt'
        $probeErr = Join-Path $WorkRoot 'boot-probe.err.txt'
        $server = $null
        try {
            $server = Start-Process -FilePath (Get-Command node).Source `
                -ArgumentList @($harnessEntry, 'web', '--no-open', '--host', '127.0.0.1', '--port', '0') `
                -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr `
                -NoNewWindow -PassThru
            for ($i = 0; $i -lt 60 -and -not $server.HasExited; $i++) {
                Start-Sleep -Seconds 1
                if ((Test-Path -LiteralPath $probeOut) -and
                    (Get-Content -LiteralPath $probeOut -Raw) -match 'dsh web:\s+http://') {
                    $harnessBoots = $true
                    break
                }
            }
            if (-not $harnessBoots) {
                $harnessBootError = ((Get-Content -LiteralPath $probeErr -Tail 12 -ErrorAction SilentlyContinue) -join ' ') -replace '\s+', ' '
            }
        }
        finally {
            if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
        }
    }
    Write-Host ("    harness can boot a server: {0}" -f $(if ($harnessBoots) { 'yes' } else { 'no' }))
    if ($haveHarness -and -not $harnessBoots) {
        Write-Host '    the harness CLI on this machine cannot start a server; scenarios that need one will skip'
        if ($harnessBootError) { Write-Host ('    harness error: ' + $harnessBootError) -ForegroundColor Yellow }
    }

    # ---------------------------------------------------------------- scenario 1
    Write-Step '1. a shim on PATH names the CLI, so the app finds it'
    $f1 = Join-Path $WorkRoot 's1'
    New-Item -ItemType Directory -Force -Path $f1 | Out-Null
    New-Prefix -Path $f1 -Version '1.2.3' | Out-Null
    Set-ScenarioPath $f1
    $r = Invoke-App @('--self-test') 's1'
    Assert-Contains $r.Out 'dsh state   : FOUND' 'the CLI is reported as found'
    Assert-Contains $r.Out 'dsh version : 1.2.3' 'the version comes from the installed package'
    Assert-Contains $r.Out 'dsh health  : CLI VALID' 'the CLI passes the health probe'

    # ---------------------------------------------------------------- scenario 2
    Write-Step '2. a package whose layout is not lib/bin.js still resolves'
    $f2 = Join-Path $WorkRoot 's2'
    New-Item -ItemType Directory -Force -Path $f2 | Out-Null
    New-Prefix -Path $f2 -Version '2.0.0' -EntryFile 'dist/cli.js' -DeclaredBin 'dist/cli.js' | Out-Null
    Set-ScenarioPath $f2
    $r = Invoke-App @('--self-test') 's2'
    Assert-Contains $r.Out 'dsh state   : FOUND' 'the CLI is found through package.json "bin"'
    Assert-Contains $r.Out 'dsh version : 2.0.0' 'that package reports its own version'

    # ---------------------------------------------------------------- scenario 3
    Write-Step '3. an interrupted install is reported as incomplete, not as missing'
    $f3 = Join-Path $WorkRoot 's3'
    New-Item -ItemType Directory -Force -Path $f3 | Out-Null
    New-Prefix -Path $f3 -Version '3.0.0' -WithoutEntry | Out-Null
    Set-ScenarioPath $f3
    # Pointed at explicitly: a half-installed package is reported as incomplete,
    # not as missing. (Without --dsh-cli the app rightly prefers a working CLI
    # found elsewhere over a broken one, so this is the deterministic form.)
    $r = Invoke-App @('--self-test', '--dsh-cli', (Join-Path $f3 'node_modules\@deepseek-ai\dsh\lib\bin.js')) 's3'
    Assert-Contains $r.Out 'dsh state   : INCOMPLETE' 'the broken install is named as such'
    Assert-Contains $r.Out 'dsh problem :' 'the reason is reported'
    Assert-Contains $r.Out 'does not exist' 'the missing entry file is named'
    if ($haveHarness) {
        $r = Invoke-App @('--self-test') 's3b'
        Assert-Contains $r.Out 'dsh state   : FOUND' 'a broken one on PATH does not hide a working CLI'
    }
    else {
        Write-Host '    SKIP  nothing to prefer: this machine has no working harness CLI'
    }

    # ---------------------------------------------------------------- scenario 4
    Write-Step '4. --repair-harness installs a missing CLI'
    $f4 = Join-Path $WorkRoot 's4'
    New-Item -ItemType Directory -Force -Path $f4 | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $f4 'node_modules\@deepseek-ai\dsh') | Out-Null
    Set-Content -LiteralPath (Join-Path $f4 'node_modules\@deepseek-ai\dsh\package.json') -Encoding ASCII `
        -Value '{ "name": "@deepseek-ai/dsh", "version": "0.0.1" }'
    New-FakeNpm -Path $f4 -Mode 'ok' | Out-Null
    Set-ScenarioPath $f4
    $r = Invoke-App @('--repair-harness') 's4'
    Assert-True ($r.Code -eq 0) 'the repair exits 0'
    Assert-Contains $r.Out 'state after  : usable' 'the CLI is usable afterwards'
    Assert-True (Test-Path (Join-Path $f4 'node_modules\@deepseek-ai\dsh\lib\bin.js')) 'the entry file is on disk'

    # ---------------------------------------------------------------- scenario 5
    Write-Step '5. a failed install puts the previous CLI back'
    $f5 = Join-Path $WorkRoot 's5'
    New-Item -ItemType Directory -Force -Path $f5 | Out-Null
    New-Prefix -Path $f5 -Version '5.0.0' | Out-Null
    New-FakeNpm -Path $f5 -Mode 'broken' | Out-Null
    Set-ScenarioPath $f5
    $env:DSH_DESKTOP_HOME = Join-Path $WorkRoot 's5-data'
    $project = Join-Path $WorkRoot 's5-project'
    New-Item -ItemType Directory -Force -Path $project | Out-Null
    # --update runs the install; the boot then fails on the fixture CLI (it is
    # not a server), but the installation itself must have been restored.
    $fixtureCli = Join-Path $f5 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    $r = Invoke-App @('--update', '--no-window', '--project', $project, '--ready-timeout', '20', '--dsh-cli', $fixtureCli) 's5'
    $manifest = Get-Content -LiteralPath (Join-Path $f5 'node_modules\@deepseek-ai\dsh\package.json') -Raw
    Assert-True ($manifest -like '*5.0.0*') 'the previous version is back on disk'
    Assert-True (Test-Path (Join-Path $f5 'node_modules\@deepseek-ai\dsh\lib\bin.js')) 'its entry file is back'
    $leftovers = @(Get-ChildItem -Path (Join-Path $f5 'node_modules\@deepseek-ai') -Directory -Filter 'dsh.backup-*' -ErrorAction SilentlyContinue)
    Assert-True ($leftovers.Count -eq 0) 'no kept copy is left behind after a restore'

    # ---------------------------------------------------------------- scenario 6
    Write-Step '6. a successful install replaces the CLI and drops the kept copy'
    $f6 = Join-Path $WorkRoot 's6'
    New-Item -ItemType Directory -Force -Path $f6 | Out-Null
    New-Prefix -Path $f6 -Version '6.0.0' | Out-Null
    New-FakeNpm -Path $f6 -Mode 'ok' -NewVersion '9.9.9' | Out-Null
    Set-ScenarioPath $f6
    $env:DSH_DESKTOP_HOME = Join-Path $WorkRoot 's6-data'
    $project = Join-Path $WorkRoot 's6-project'
    New-Item -ItemType Directory -Force -Path $project | Out-Null
    $fixtureCli = Join-Path $f6 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    $r = Invoke-App @('--update', '--no-window', '--project', $project, '--ready-timeout', '20', '--dsh-cli', $fixtureCli) 's6'
    $manifest = Get-Content -LiteralPath (Join-Path $f6 'node_modules\@deepseek-ai\dsh\package.json') -Raw
    Assert-True ($manifest -like '*9.9.9*') 'the new version is installed'
    $leftovers = @(Get-ChildItem -Path (Join-Path $f6 'node_modules\@deepseek-ai') -Directory -Filter 'dsh.backup-*' -ErrorAction SilentlyContinue)
    Assert-True ($leftovers.Count -eq 0) 'the kept copy is removed once the new one works'

    # ---------------------------------------------------------------- scenario 7
    # The plugin scenarios need a harness CLI on this machine; a machine without
    # one skips them rather than failing the suite.
    if (-not $haveHarness) {
        Write-Step '7-9. plugin scenarios'
        Write-Host '    SKIP  no harness CLI on this machine (set up node + @deepseek-ai/dsh)'
    }
    else {
        Write-Step '7. the app installs its own UI plugin into the home it owns'
        $f7 = Join-Path $WorkRoot 's7-home'
        New-Item -ItemType Directory -Force -Path $f7 | Out-Null
        Set-ScenarioPath $f7 -RealHarness
        $env:DSH_DESKTOP_HOME = Join-Path $WorkRoot 's7-data'
        $r = Invoke-App @('--install-plugin', '--dsh-home', $f7) 's7'
        Assert-True ($r.Code -eq 0) 'the install exits 0'
        Assert-Contains $r.Out 'dsh-plugin-desktop-updates' 'the updates plugin is named'
        $manifest7 = Get-Content -LiteralPath (Join-Path $f7 'profiles\web\package.json') -Raw
        Assert-True ($manifest7 -like '*dsh-plugin-desktop-updates*') 'it is in the bundle stack'
        Assert-True (Test-Path (Join-Path $f7 'profiles\web\node_modules\dsh-plugin-desktop-updates\client.js')) `
            'its browser half is on disk'
        $r = Invoke-App @('--install-plugin', '--dsh-home', $f7) 's7b'
        Assert-Contains $r.Out 'already current' 'a second run is a no-op'

        # The plugin is also installable from npm, so a boot must not undo a
        # newer install with the copy inside the executable. The explicit
        # command forces the bundled copy back, because that is what it is for.
        $pluginManifest = Join-Path $f7 'profiles\web\node_modules\dsh-plugin-desktop-updates\package.json'
        $newer = Get-Content -LiteralPath $pluginManifest -Raw | ConvertFrom-Json
        $bundledVersion = $newer.version
        $newer.version = '9.9.9'
        $newer | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $pluginManifest -Encoding UTF8

        $project7 = Join-Path $WorkRoot 's7-project'
        New-Item -ItemType Directory -Force -Path $project7 | Out-Null
        $r = Invoke-App @('--no-window', '--dsh-home', $f7, '--project', $project7, '--ready-timeout', '120') 's7-newer'
        Assert-Contains $r.Out 'newer than the bundled' 'a newer plugin is reported at boot'
        $keptVersion = (Get-Content -LiteralPath $pluginManifest -Raw | ConvertFrom-Json).version
        Assert-True ($keptVersion -eq '9.9.9') 'a plugin newer than the bundled one is left in place'

        $r = Invoke-App @('--install-plugin', '--dsh-home', $f7) 's7-force'
        Assert-True ($r.Code -eq 0) 'the explicit install exits 0'
        $restoredVersion = (Get-Content -LiteralPath $pluginManifest -Raw | ConvertFrom-Json).version
        Assert-True ($restoredVersion -eq $bundledVersion) `
            ("the explicit install puts the bundled {0} back" -f $bundledVersion)

        Write-Step '8. a plugin can be added, switched off and on, and removed'
        $plugin = Join-Path $WorkRoot 's8-plugin'
        New-Item -ItemType Directory -Force -Path $plugin | Out-Null
        Set-Content -LiteralPath (Join-Path $plugin 'package.json') -Encoding ASCII -Value @'
{
  "name": "dsh-plugin-smoke",
  "version": "0.0.2",
  "private": true,
  "type": "module",
  "main": "index.js",
  "dsh": { "bundle": { "patch": "./cordis.patch.yml" } }
}
'@
        Set-Content -LiteralPath (Join-Path $plugin 'cordis.patch.yml') -Encoding ASCII -Value @'
- insert:
    - id: smoke
      name: 'dsh-plugin-smoke'
'@
        Set-Content -LiteralPath (Join-Path $plugin 'index.js') -Encoding ASCII `
            -Value "export const name = 'dsh-plugin-smoke'`nexport function apply() {}"

        $r = Invoke-App @('--add-plugin', $plugin, '--dsh-home', $f7) 's8-add'
        Assert-True ($r.Code -eq 0) 'adding a local plugin exits 0'
        Assert-Contains $r.Out 'dsh-plugin-smoke' 'the bundle stack names it'

        $r = Invoke-App @('--plugin-list', '--dsh-home', $f7) 's8-list'
        Assert-Contains $r.Out 'dsh-plugin-smoke' 'the list shows it'
        Assert-True ($r.Out -match 'on\s+dsh-plugin-smoke') 'it starts enabled'

        $r = Invoke-App @('--disable-plugin', 'dsh-plugin-smoke', '--dsh-home', $f7) 's8-off'
        Assert-True ($r.Code -eq 0) 'disabling exits 0'
        $r = Invoke-App @('--plugin-list', '--dsh-home', $f7) 's8-list2'
        Assert-True ($r.Out -match 'off\s+dsh-plugin-smoke') 'the list shows it disabled'
        Assert-Contains $r.Out 'disabled rows: smoke' 'the patch layer carries the row'

        $r = Invoke-App @('--enable-plugin', 'dsh-plugin-smoke', '--dsh-home', $f7) 's8-on'
        Assert-True ($r.Code -eq 0) 're-enabling exits 0'
        $r = Invoke-App @('--plugin-list', '--dsh-home', $f7) 's8-list3'
        Assert-True ($r.Out -match 'on\s+dsh-plugin-smoke') 'the list shows it enabled again'

        $r = Invoke-App @('--disable-plugin', '@deepseek-ai/dsh-base', '--dsh-home', $f7) 's8-base'
        Assert-True ($r.Code -eq 1) 'a base bundle cannot be switched off'

        $r = Invoke-App @('--remove-plugin', 'dsh-plugin-smoke', '--dsh-home', $f7) 's8-rm'
        Assert-True ($r.Code -eq 0) 'removing it exits 0'
        $manifest8 = Get-Content -LiteralPath (Join-Path $f7 'profiles\web\package.json') -Raw
        Assert-True ($manifest8 -notlike '*dsh-plugin-smoke*') 'it is gone from the bundle stack'

        Write-Step '9. a plugin that cannot load is disabled automatically'
        # The recovery is the app's answer to a server that refuses to boot, so
        # it means nothing on a machine whose harness cannot boot at all.
        if (-not $harnessBoots) {
            Write-Host '    SKIP  this machine''s harness CLI cannot start a server'
        }
        else {
        $f9 = Join-Path $WorkRoot 's9-home'
        $thrower = Join-Path $f9 'profiles\web\node_modules\dsh-plugin-thrower'
        New-Item -ItemType Directory -Force -Path $thrower | Out-Null
        Set-Content -LiteralPath (Join-Path $thrower 'package.json') -Encoding ASCII -Value @'
{
  "name": "dsh-plugin-thrower",
  "version": "0.0.1",
  "private": true,
  "type": "module",
  "main": "index.js",
  "dsh": { "bundle": { "patch": "./cordis.patch.yml" } }
}
'@
        Set-Content -LiteralPath (Join-Path $thrower 'cordis.patch.yml') -Encoding ASCII -Value @'
- insert:
    - id: thrower
      name: 'dsh-plugin-thrower'
'@
        Set-Content -LiteralPath (Join-Path $thrower 'index.js') -Encoding ASCII `
            -Value "throw new Error('smoke test: this plugin cannot load')"
        $throwerManifest = @'
{
  "name": "dsh-profile-web",
  "private": true,
  "dependencies": { "dsh-plugin-thrower": "file:./node_modules/dsh-plugin-thrower" },
  "dsh": { "profile": { "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dsh-plugin-thrower"], "patchReload": "live" } }
}
'@
        Set-Content -LiteralPath (Join-Path $f9 'profiles\web\package.json') -Encoding ASCII -Value $throwerManifest
        Set-Content -LiteralPath (Join-Path $f9 'profiles\web\cordis.patch.yml') -Encoding ASCII -Value '[]'
        Set-ScenarioPath $f9 -RealHarness
        $env:DSH_DESKTOP_HOME = Join-Path $WorkRoot 's9-data'
        $project = Join-Path $WorkRoot 's9-project'
        New-Item -ItemType Directory -Force -Path $project | Out-Null

        $r = Invoke-App @('--no-window', '--dsh-home', $f9, '--project', $project, '--ready-timeout', '240') 's9'
        Assert-True ($r.Code -eq 0) 'the boot recovers and exits 0'
        Assert-Contains $r.Out 'safe mode' 'the recovery is reported'
        Assert-Contains $r.Out 'dsh-plugin-thrower' 'the culprit is named'
        $patch = Get-Content -LiteralPath (Join-Path $f9 'profiles\web\cordis.patch.yml') -Raw
        Assert-True ($patch -like '*thrower*' -and $patch -like '*disabled: true*') 'its row is disabled in the patch layer'
        if ($r.Code -ne 0) {
            # A recovery that did not happen is worth the app's own account of
            # why, printed where CI can show it instead of in a fixture file.
            Write-Host '    --- app output ---' -ForegroundColor Yellow
            ($r.Out -split "`n" | Select-Object -Last 25) | ForEach-Object { Write-Host ('    ' + $_.TrimEnd()) }
            if ($r.Err) { ($r.Err -split "`n" | Select-Object -Last 10) | ForEach-Object { Write-Host ('    ' + $_.TrimEnd()) } }
            Write-Host '    --- server log ---' -ForegroundColor Yellow
            Get-ChildItem (Join-Path (Join-Path $WorkRoot 's9-data') 'logs') -Filter 'server-*.out.log' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
                ForEach-Object { Get-Content $_.FullName -Tail 25 | ForEach-Object { Write-Host ('    ' + $_.TrimEnd()) } }
        }
        }
    }

    # --------------------------------------------------------------- scenario 10
    Write-Step '10. the updates UI renders without a browser'
    # The plugin's browser half, rendered headlessly: WebView2 cannot start in
    # every build environment, so its UI code is checked here instead of by eye.
    if (Get-Command node -ErrorAction SilentlyContinue) {
        $clientSmoke = Join-Path $root 'tools\client-plugin-smoke.mjs'
        $clientOut = Join-Path $WorkRoot 'client-smoke.txt'
        & node $clientSmoke > $clientOut 2>&1
        $clientCode = $LASTEXITCODE
        $clientText = if (Test-Path $clientOut) { Get-Content -LiteralPath $clientOut -Raw } else { '' }
        $clientPassed = if ($clientText -match '(\d+) passed') { [int]$Matches[1] } else { 0 }
        Assert-True ($clientCode -eq 0) ("the client smoke exits 0 ({0} assertions)" -f $clientPassed)
        Assert-Contains $clientText 'apply() contributes the sidebar entry beside Settings' 'the sidebar entry is registered'
        Assert-Contains $clientText 'the sidebar entry carries the list slot id the registry requires' `
            'the sidebar entry carries the id a list slot requires'
        Assert-Contains $clientText 'the section shows the installed app version' 'the settings section renders live state'
        Assert-Contains $clientText 'clicking it selects the updates panel in this window' `
            'updates open as a panel in this window, not in a second one'
        Assert-Contains $clientText 'without a layout service it falls back to the app window' `
            'a shell without the panel slot falls back to the native window'
        Assert-Contains $clientText 'without the bridge the section says the app is not connected' `
            'it says so when the desktop app is not attached'
    }
    else {
        Write-Host '    SKIP  node is not on PATH'
    }

    # --------------------------------------------------------------- scenario 11
    Write-Step '11. the served boot graph carries the plugin'
    # Everything between "the plugin is installed in this home" and "the plugin
    # runs in the page" happens in the graph the server injects into the page.
    # A row the loader never emits, or a bundle route that answers 404, is
    # invisible to the app - the UI simply has nothing to show. So this boots a
    # real `dsh web` on a fixture home and asks the server what it serves.
    if ($haveHarness -and $harnessEntry -and $harnessBoots) {
        $f11 = Join-Path $WorkRoot 's11'
        New-Item -ItemType Directory -Force -Path $f11 | Out-Null
        Set-ScenarioPath (Join-Path $WorkRoot 'probe') -RealHarness
        $install = Invoke-App @('--install-plugin', '--dsh-home', $f11) 's11-install'
        Assert-True ($install.Code -eq 0) 'the fixture home gets the bundled plugin'

        $env:DSH_HOME = $f11
        $serverOut = Join-Path $WorkRoot 's11-server.out.txt'
        $serverErr = Join-Path $WorkRoot 's11-server.err.txt'
        $server = $null
        try {
            $server = Start-Process -FilePath (Get-Command node).Source `
                -ArgumentList @($harnessEntry, 'web', '--no-open', '--host', '127.0.0.1', '--port', '0') `
                -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr `
                -NoNewWindow -PassThru

            $ready = $null
            for ($i = 0; $i -lt 180 -and -not $ready -and -not $server.HasExited; $i++) {
                Start-Sleep -Seconds 1
                if (Test-Path -LiteralPath $serverOut) {
                    $serverText = Get-Content -LiteralPath $serverOut -Raw
                    if ($serverText -match 'dsh web:\s+(http://\S+)') { $ready = $Matches[1] }
                }
            }

            Assert-True ([bool]$ready) 'the harness server prints its ready line'
            if (-not $ready) {
                # Say what the server said instead of leaving a bare failure: a
                # boot that never printed its line is the server's own story.
                Write-Host '    --- server stdout ---' -ForegroundColor Yellow
                Get-Content -LiteralPath $serverOut -Tail 25 -ErrorAction SilentlyContinue |
                    ForEach-Object { Write-Host ('    ' + $_.TrimEnd()) }
                Write-Host '    --- server stderr ---' -ForegroundColor Yellow
                Get-Content -LiteralPath $serverErr -Tail 25 -ErrorAction SilentlyContinue |
                    ForEach-Object { Write-Host ('    ' + $_.TrimEnd()) }
            }
            if ($ready) {
                $base = ($ready -split '\?')[0].TrimEnd('/')
                $token = if ($ready -match 'token=([^&\s]+)') { $Matches[1] } else { '' }
                $probeOut = Join-Path $WorkRoot 's11-probe.txt'
                & node (Join-Path $root 'tools\boot-graph-probe.mjs') $base $token > $probeOut 2>&1
                $probeCode = $LASTEXITCODE
                $probeText = if (Test-Path -LiteralPath $probeOut) { Get-Content -LiteralPath $probeOut -Raw } else { '' }
                $probePassed = if ($probeText -match '(\d+) passed') { [int]$Matches[1] } else { 0 }
                Assert-True ($probeCode -eq 0) ("the served plugin is intact ({0} checks)" -f $probePassed)
                Assert-Contains $probeText 'the graph has a row for dsh-plugin-desktop-updates' `
                    'the boot graph names the plugin'
                Assert-Contains $probeText 'the bundle route answers 200' 'the server serves the plugin bundle'
                Assert-Contains $probeText 'the served bundle carries the id a list slot requires' `
                    'the served bundle carries the id the sidebar entry needs'
                Assert-Contains $probeText 'the served bundle carries the sidebar panel row' `
                    'the served bundle carries the panel row'
                Assert-Contains $probeText 'the served bundle carries the layout call that opens the panel' `
                    'the served bundle opens the panel in this window'
            }
        }
        finally {
            if ($server -and -not $server.HasExited) {
                Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    else {
        Write-Host $(if ($haveHarness) { '    SKIP  this machine''s harness CLI cannot start a server' }
                      else { '    SKIP  no harness CLI on this machine' })
    }

    # --------------------------------------------------------------- scenario 12
    Write-Step '12. the page bridge reads what the page actually sends'
    # The page posts JSON.stringify(...), so the app is handed a JSON string that
    # contains the request. A bridge that only understood the object form ignored
    # every real message while its own tests stayed green, so both shapes are
    # asserted here, in the suite CI runs.
    $bridge = Invoke-App @('--bridge-selftest') 'bridge'
    $bridgeText = $bridge.Out
    $bridgePassed = if ($bridgeText -match '(\d+) passed') { [int]$Matches[1] } else { 0 }
    Assert-True ($bridge.Code -eq 0) ("the bridge self test exits 0 ({0} checks)" -f $bridgePassed)
    Assert-Contains $bridgeText 'a request sent the way the page sends it - stringified - parses' `
        'a request in the shape the page sends is understood'
    Assert-Contains $bridgeText 'it reaches the host like any other request' `
        'that request reaches the host'
    Assert-Contains $bridgeText 'the badge''s stringified click is recognized' `
        'the badge message is understood'
    Assert-Contains $bridgeText 'a plugin install naming the package is not mistaken for the badge' `
        'a request is not mistaken for the badge'
    Assert-Contains $bridgeText 'the payload is unwrapped from a stringified message' `
        'a stringified message is unwrapped'
}
finally {
    Reset-Environment
    if (-not $KeepFixtures) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:Passed, $script:Failed) -ForegroundColor ($(if ($script:Failed -eq 0) { 'Green' } else { 'Red' }))
if ($script:Failed -gt 0) { exit 1 }
exit 0
