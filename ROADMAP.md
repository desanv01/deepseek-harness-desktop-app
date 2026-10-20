# Roadmap

This is a record of what the app set out to do and how each part was verified — not a schedule. Everything
in the core promise is built, shipped and documented in the [README](README.md).

| Area | State | Notes |
| --- | --- | --- |
| 1. GitHub Actions | **done** | `ci.yml` builds and smoke-tests every push and pull request; `release.yml` publishes a tagged build with `SHA256SUMS`. A plugin-publish workflow existed and was retired when the plugins moved to their own repositories |
| 2. Conformance checks | **done, behaviourally** | Two suites run in CI against a real built executable: `tools/smoke-test.ps1` (CLI discovery, repair, install safety, the plugin pipeline, the newer-plugin rule, safe-mode recovery, the served boot graph, and the page bridge's message shapes — 63 assertions) and `tools/client-plugin-smoke.mjs`, which renders the updates plugin's browser half under a minimal React and a fake shell that enforces the real slot rules, so its UI is checked where WebView2 cannot start (53 assertions). Pure-logic unit tests were not built — see §2 |
| 3. Signed self-update | **done at verification level 1** | Detect, download, checksum-verify, stage, apply, roll back and notify all ship. Authenticode signing and `WinVerifyTrust` were not built — see §3 |

The three sections below keep the engineering that was worked out before those two items were set aside. They
are notes for anyone who picks them up, written so the thinking does not have to be repeated.

## Milestones

| Milestone | Contents | State |
| --- | --- | --- |
| A | CI workflow + `SHA256SUMS` published with every release | done 2026-09-15 |
| B | `DSH_DESKTOP_HOME` override for a testable data root | done |
| C | Update check, download, checksum verification, notify-only UI, cached checks | done 2026-09-15 |
| D | Staged apply with rollback | done 2026-09-15 — every apply is an explicit click, stricter than the `AutoApplyUpdates` opt-in originally designed |
| E | Authenticode signing in the release workflow and `WinVerifyTrust` at apply time | not built — the largest remaining trust gap, see §3 |

## What shipped after the first release (2026-09-15, later the same day)

| Area | What landed |
| --- | --- |
| Startup | The per-launch `dsh web --help` verification (7–8 s) replaced by a 0.2 s probe, with the deep check kept as diagnosis; WebView2 warmed in parallel with the server boot; the two update checks moved after first paint. Measured 25.5 s → 14.6 s to a ready window |
| UI as plugins | The Updates UI moved out of a native window into a DSH plugin (`plugins/dsh-plugin-desktop-updates`): a sidebar entry beside Settings and a Settings section, driven by a versioned page bridge (`window.__dshDesktop`) over WebView2 messages. The plugin publishes a marker of what it registered, the app logs it after every page load, and a slot the shell refuses is reported there and in the section — the shell itself drops a rejected entry without a word |
| Plugin management | Install, remove, enable and disable plugins — ours and third-party — from the app or from flags, with the pnpm runtime carried by the build (12.4 MB, two files) and enable/disable going through the profile patch layer |
| Safe mode | A plugin the loader cannot apply is detected from its own message, disabled, and the boot retried once; `--safe-mode` boots with the base bundles only |
| Lifetime | The harness keeps running after the window closes; the next launch adopts it (attach instead of boot), the tray and `--stop` end it, `--no-keep-alive` restores the old contract |

---

## 1. GitHub Actions — shipped 2026-09-15

**Goal.** Every push and pull request builds and tests the app on a clean Windows runner, and every version
tag produces a release whose assets match the tagged source.

**What exists.**

- `ci.yml` — push to `main` and pull requests. Restores, builds `Release`, refuses a `<Version>` that is not
  a `yyyy.MM.dd` date, publishes the framework-dependent single-file build, and smoke-tests it: `--self-test`
  must exit `0`; an out-of-range `--port` and an unknown flag must exit `2`. The checks go through
  `Start-Process -Wait -PassThru`, because `&` does not reliably wait for a GUI-subsystem executable.
- `release.yml` — tag `v*`. Refuses a tag that does not equal `v<Version>`, publishes the self-contained
  single-file build, writes `SHA256SUMS` over the exe and the zip, and creates the release with
  `gh release create` (re-running uploads with `--clobber` instead of failing).
- `dependabot.yml` — weekly NuGet and GitHub Actions updates.

**Retired.** `publish-plugin.yml` used to stage a copy of the bundled plugin and publish it to npm from this
repository, through trusted publishing (OIDC) or an `NPM_TOKEN` bypass. It was removed once the plugins moved
to repositories of their own, which publish themselves; leaving it would have meant a workflow publishing from
a copy that had already drifted from the plugin's real source. `tools/stage-plugin-package.mjs`, which existed
only to serve it, was removed with it.

**Notes.** `windows-latest` is required: the project targets `net8.0-windows` and uses WinForms. The
workflows' only credential is the automatic `GITHUB_TOKEN`.

Branch protection requiring the `ci` check is enabled on `main`; force-pushes and deletion are blocked.

---

## 2. Pure-logic unit tests — not built

The conformance guarantee comes from the behavioural suites described in the table at the top: they take a
real built executable and drive it end to end, which is what actually caught the defects this app shipped
fixes for. A separate unit-test project was designed and not built. The design is kept here because it is
still the right shape if the suite is ever wanted.

**Prerequisite — a testable data root.** `AppPaths.Root` is a static, read-only property computed from
`%LOCALAPPDATA%`. Tests must never touch real app data, so the design called for an environment override:

```
DSH_DESKTOP_HOME   # when set, AppPaths.Root resolves here
```

This is worth shipping on its own: it gives users a portable data directory and makes every test hermetic.

**Project layout that was designed.**

```text
tests/DeepSeekHarness.Tests/
  DeepSeekHarness.Tests.csproj     # xUnit, net8.0-windows, ProjectReference to the app
  OptionsTests.cs
  SettingsTests.cs
  AppPathsTests.cs
  NetProbeTests.cs
  ProcTests.cs
  UpdateVerificationTests.cs
```

Referencing a `WinExe` project from a test project works; the tests must not construct Forms.

**What it would cover, and why.**

| Area | Cases | Why |
| --- | --- | --- |
| `Options.Parse` | every flag; `--port 0`; out-of-range port; non-numeric values; unknown flag; missing `--project` directory; `--flag=value` rejection | Argument handling decides which server is started and where |
| Settings precedence | CLI beats settings; settings beat defaults; a deleted remembered folder falls through to the picker | Wrong precedence silently opens the wrong project |
| `AppSettings` | round-trip; corrupt JSON; recent-list de-duplication and cap; atomic save | A corrupt settings file must not break startup |
| `AppPaths` | home key stability across path spellings; log pruning by age and count; `DSH_DESKTOP_HOME` override | These names key the mutex, the lease, and the focus event |
| `NetProbe.Probe` | free port; listener that is not DSH; DSH marker present; connection refused mid-probe | The classification is the only thing standing between the app and an arbitrary local service |
| `Proc` | `KillTreeIfStartTime` refuses a reused PID; `Run` timeout and cancellation codes | Wrong kills terminate unrelated processes |
| Log rotation | rotates at the threshold; keeps `desktop.log.1`; never throws on a locked file | Unbounded logs fill the disk |
| Update verification | checksum match and mismatch; version-tag comparison; asset selection | The updater must refuse a bad download |

**Refactors the design required.** Extract the ready-line parse and the version-tag comparison into small
pure helpers so tests do not need a live harness or a network; then make those helpers `internal` with
`InternalsVisibleTo("DeepSeekHarness.Tests")`, or keep them `public` if genuinely useful.

**Acceptance criteria that were written for it.**
`dotnet test` green with no network, no `dsh` install and no WebView2 runtime; tests writing only under a
temporary `DSH_DESKTOP_HOME`; and a deliberately broken `Options.Parse`, settings round-trip, endpoint
classification or log rotation failing the suite.

**Note.** Target the pure logic. Do not chase UI coverage; the WinForms and WebView2 paths are verified by the
behavioural suites instead.

---

## 3. Signed self-update — shipped at level 1; signing was not built

**Goal.** Tell the user when a newer build exists, and — only if they opt in — download, verify, and apply it
without ever leaving the machine with a broken or unverified executable.

### The threat this was designed against

The upstream project this app derives from downloads a release asset and overwrites its own executable after
checking only the file size. That turns a compromised GitHub account into code execution on every user's
machine. Any updater here must **verify before applying** and **never replace a running binary in place**.

### The verification ladder

| Level | Mechanism | Protects against | Cost |
| --- | --- | --- | --- |
| 0 | Notify only, user downloads manually | Everything (no auto-apply at all) | Free |
| 1 | Published `SHA-256` in `SHA256SUMS` | Truncated or corrupted downloads | Free — **this is what ships** |
| 2 | Authenticode signature checked with `WinVerifyTrust` | Tampered releases, wrong publisher | Code-signing certificate |

Level 1 alone does **not** protect against a compromised release, because the checksum comes from the same
place as the binary. The README and the app both say so honestly. Level 2 is the real fix and also removes
the SmartScreen "unknown publisher" warning.

### What shipped (2026-09-15)

- **Detection** — `AppUpdate` asks `github.com/<repo>/releases/latest` where it redirects (free, not rate
  limited) and only enriches with the REST API when the anonymous limit allows it; the answer is cached for
  six hours in `update-check.json`, keyed by the installed version and the feed, and a check never blocks
  startup. `--no-update-check` and a settings toggle keep it quiet; `--update-feed` points the whole thing at
  a `file://` fixture.
- **Download** — into `updates\<tag>\<asset>` through a `.part` file, with a 256 MB ceiling, a floor that
  rejects an implausibly small file, and an `MZ` check for an exe. `UpdateHttp` uses the in-process TLS stack
  first and Node second, because a machine whose TLS stack refuses credentials silently disables every update
  path.
- **Verify** — the SHA-256 is compared with the release's `SHA256SUMS`. A mismatch deletes the download and
  refuses to continue. A release with no `SHA256SUMS` can still be installed, but only after an explicit
  warning that says it is unverified.
- **Apply** — `pending.json` plus a helper copy of the *running* build started with `--apply-update`. The app
  exits (the job object stops the harness server), the helper waits for the pid **and start time**,
  re-verifies the staged file, keeps the old exe as `<exe>.old`, swaps, relaunches, and rolls back if the new
  build dies within 12 seconds.
- **Notify** — tray balloon, tray menu line, window title suffix, and a pill injected into the harness page;
  all four open the same updates window.

Verified with a local fixture feed: staging, a tampered `SHA256SUMS` refusal, a successful swap-and-restart,
and a rollback from a build that exits immediately.

### What level 2 would need

**Design.** Fetch `SHA256SUMS`, compute the digest, compare, refuse on mismatch (level 1). When the release is
signed, `WinVerifyTrust` with the expected subject, refusing an unsigned or wrongly-signed binary (level 2).
Refuse to apply if the staged file is not a PE image or is smaller than a floor. The staged-apply sequence
already in place does not change: write `pending.json`, copy the running exe to a helper, exit, wait for the
pid and start time, re-verify, swap with `.old` retained, relaunch, restore `.old` if the new build dies.

**Risks.**

- **Certificate cost and identity.** An OV certificate requires a legal entity or a verified individual; Azure
  Trusted Signing is the cheaper route but still needs identity verification. Decide before building.
- **Antivirus false positives.** A self-replacing executable is exactly the pattern heuristic scanners flag;
  signing is the mitigation.
- **Rollback complexity.** Keep the helper small and log every step; never delete the previous binary until
  the new one has started.
- **Rate limits and offline machines.** Cache the check and fail soft; an update check must never block
  startup.

### Decisions that were already settled

1. **Code-signing route** — buy an OV certificate, use Azure Trusted Signing, or ship notify-only at level 1?
   *Open. The shipped updater is level 1 (published SHA-256) with an explicit warning when even that is
   missing.*
2. **Default for `AutoApplyUpdates`** — off. *Not implemented as a setting: every apply is an explicit click,
   which is stricter than the recommendation.*
3. **Test framework** — xUnit, if §2 is ever built.
4. **CI runners** — GitHub-hosted `windows-latest`. *Done.*
