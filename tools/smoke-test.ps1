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
    # Whether this machine has a usable harness CLI: several assertions below
    # only make sense when one exists, and a runner without one skips them.
    $cli = Join-Path $originalAppData 'npm\node_modules\@deepseek-ai\dsh\lib\bin.js'
    $haveHarness = (Test-Path -LiteralPath $cli) -and [bool](Get-Command node -ErrorAction SilentlyContinue)
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

        $r = Invoke-App @('--no-window', '--dsh-home', $f9, '--project', $project, '--ready-timeout', '120') 's9'
        Assert-True ($r.Code -eq 0) 'the boot recovers and exits 0'
        Assert-Contains $r.Out 'safe mode' 'the recovery is reported'
        Assert-Contains $r.Out 'dsh-plugin-thrower' 'the culprit is named'
        $patch = Get-Content -LiteralPath (Join-Path $f9 'profiles\web\cordis.patch.yml') -Raw
        Assert-True ($patch -like '*thrower*' -and $patch -like '*disabled: true*') 'its row is disabled in the patch layer'
    }
}
finally {
    Reset-Environment
    if (-not $KeepFixtures) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:Passed, $script:Failed) -ForegroundColor ($(if ($script:Failed -eq 0) { 'Green' } else { 'Red' }))
if ($script:Failed -gt 0) { exit 1 }
exit 0
