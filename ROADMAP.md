# Roadmap

The app is feature-complete for its core promise: launch, project-scoped window, verified endpoint, guaranteed shutdown, settings, picker, tray, focus handoff. Three items remain, all of them about **trust and verification** rather than features.

| Item | Why it matters | Rough effort |
| --- | --- | --- |
| 1. GitHub Actions | Makes every later change verifiable and ships reproducible artifacts | 0.5 day |
| 2. Unit tests | Protects the pure logic that the updater will depend on | 1–2 days |
| 3. Signed self-update | Lets users get new builds without hand-downloading, safely | 2–4 days |

Recommended order: **1 → 2 → 3**. CI is cheap and unblocks the rest; tests de-risk the updater; the updater depends on both the release pipeline and verified logic.

---

## 1. GitHub Actions

### Goal

Every push and pull request builds and tests the app on a clean Windows runner, and every version tag produces a release whose assets match the tagged source.

### Design

**`ci.yml` — push to `main`, pull requests**

1. `actions/checkout`
2. `actions/setup-dotnet` with .NET 8
3. `dotnet restore` (cache `~/.nuget/packages` keyed on the csproj)
4. `dotnet build -c Release --no-restore`
5. `dotnet test -c Release --no-build` (once item 2 lands; before that, build-only)
6. Fail the job on any warning-free-build regression

**`release.yml` — tag `v*`**

1. Read `<Version>` from `DeepSeekHarness.csproj`.
2. **Refuse to release if the tag does not equal `v<Version>`.** This is the guard against the version drift that made the earlier build ambiguous.
3. `dotnet publish` the self-contained single-file build.
4. Compute `SHA256SUMS` over the exe and zip.
5. `gh release create "$tag"` with the exe, the zip, and `SHA256SUMS`; `permissions: contents: write`.

**`dependabot.yml`** — weekly NuGet and GitHub Actions updates, so the WebView2 package and the actions stay current.

### Acceptance criteria

- A pull request with a failing test cannot merge (branch protection requires the CI check).
- Pushing `v2026.09.11` with `<Version>2026.09.11</Version>` publishes a release containing the exe, the zip, and `SHA256SUMS`; a mismatched tag fails the workflow before publishing.
- CI finishes in under ~5 minutes and needs no repository secrets.

### Notes

- `windows-latest` is required: the project targets `net8.0-windows` and uses WinForms.
- The release workflow's only credential is the automatic `GITHUB_TOKEN`.

---

## 2. Unit tests

### Goal

Automated coverage of the logic that can silently break the app, runnable offline on any machine with the .NET 8 SDK.

### Prerequisite: a testable data root

`AppPaths.Root` is a static, read-only property computed from `%LOCALAPPDATA%`. Tests must never touch the real app data. Add an environment override:

```
DSH_DESKTOP_HOME   # when set, AppPaths.Root resolves here
```

This is worth shipping on its own: it gives users a portable data directory and makes every test hermetic.

### Project layout

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

### What to cover, and why

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

### Refactors required first

- Extract the ready-line parse and the version-tag comparison into small pure helpers so tests do not need a live harness or a network.
- Make those helpers `internal` and add `InternalsVisibleTo("DeepSeekHarness.Tests")`, or keep them `public` if they are genuinely useful.

### Acceptance criteria

- `dotnet test` is green with no network, no `dsh` install, and no WebView2 runtime present.
- Tests write only under a temporary `DSH_DESKTOP_HOME`.
- A deliberately broken `Options.Parse`, settings round-trip, endpoint classification, or log rotation fails the suite.

### Notes

- Target the pure logic. Do not chase UI coverage; the WinForms and WebView2 paths are verified by the manual smoke checklist instead.
- A later end-to-end test can drive `--no-window` against a stub harness server if it becomes worth the maintenance.

---

## 3. Signed self-update

### Goal

Tell the user when a newer build exists, and — only if they opt in — download, verify, and apply it without ever leaving the machine with a broken or unverified executable.

### The threat to design against

The upstream project this app derives from downloads a release asset and overwrites its own executable after checking only the file size. That turns a compromised GitHub account into code execution on every user's machine. Any updater here must **verify before applying** and **never replace a running binary in place**.

### Verification ladder

| Level | Mechanism | Protects against | Cost |
| --- | --- | --- | --- |
| 0 | Notify only, user downloads manually | Everything (no auto-apply at all) | Free — this is the default |
| 1 | Published `SHA-256` in `SHA256SUMS` | Truncated or corrupted downloads | Free |
| 2 | Authenticode signature checked with `WinVerifyTrust` | Tampered releases, wrong publisher | Code-signing certificate |

Level 1 alone does **not** protect against a compromised release, because the checksum comes from the same place as the binary. The UI must say so honestly. Level 2 is the real fix and also removes the SmartScreen "unknown publisher" warning.

### Design

**Detection**
- Query `https://api.github.com/repos/desanv01/deepseek-harness-desktop-app/releases/latest`.
- Compare the tag with the assembly version rendered as `vYYYY.MM.DD`.
- Cache the result for 24 hours: the unauthenticated API allows 60 requests per hour per IP, and a launch-per-check would burn that.

**Download**
- Stage into `%LOCALAPPDATA%\DeepSeekHarness\updates\<tag>\`.
- Download to a `.part` file, then rename; resume or retry once on failure.
- Never download more than the asset's advertised size plus a small margin.

**Verify**
- Level 1: fetch `SHA256SUMS`, compute the digest, compare; refuse on mismatch.
- Level 2 when the release is signed: `WinVerifyTrust` with the expected subject; refuse an unsigned or wrongly-signed binary.
- Refuse to apply if the staged file is not a PE image or is smaller than a floor.

**Apply (staged, never in place)**
1. Write `pending.json` (staged path, destination path, expected hash, relaunch flag).
2. Copy the running exe to `updater-helper.exe` and start it with `--apply-update <marker>`.
3. The main app exits; its job object stops the harness server cleanly.
4. The helper waits for the app's PID **and start time** to disappear, re-verifies the staged file, then swaps: move the old exe to `.old`, move the new one into place.
5. Relaunch. On success, delete `.old`. If the new exe fails to start (the helper probes the process for a few seconds), restore `.old` and relaunch it.

**Settings**
- `CheckForUpdates` (default **on**) — notify only.
- `AutoApplyUpdates` (default **off**) — allow the staged apply.
- `--no-update-check` for a one-off launch without a network call.

### Acceptance criteria

- Against a local `file://` fixture feed, the app detects a newer tag, downloads, verifies, and stages.
- A modified asset fails the checksum and the app refuses to apply, leaving the running exe untouched.
- A failed apply leaves a runnable original exe and a readable log entry.
- Applying while a server runs stops the server through the normal job-object path; the relaunched app reopens the same project from `settings.json`.
- No update is applied without either a valid signature or an explicit `AutoApplyUpdates` opt-in.

### Risks

- **Certificate cost and identity.** An OV certificate requires a legal entity or a verified individual; Azure Trusted Signing is the cheaper route but still needs identity verification. Decide before building level 2.
- **Antivirus false positives.** A self-replacing executable is exactly the pattern heuristic scanners flag; signing is the mitigation.
- **Rollback complexity.** Keep the helper small and log every step; never delete the previous binary until the new one has started.
- **Rate limits and offline machines.** Cache the check and fail soft; an update check must never block startup.

---

## Decisions needed before starting

1. **Code-signing route** — buy an OV certificate, use Azure Trusted Signing, or ship notify-only at level 1 for now?
2. **Default for `AutoApplyUpdates`** — off is recommended.
3. **Test framework** — xUnit is recommended.
4. **CI runners** — GitHub-hosted `windows-latest` is recommended; self-hosted only if the build time becomes a problem.

## Milestones

| Milestone | Contents |
| --- | --- |
| A | CI workflow + `SHA256SUMS` published with every release |
| B | `DSH_DESKTOP_HOME` override + unit tests for options, settings, paths, probe, proc, rotation |
| C | Update check, download, checksum verification, notify-only UI, cached checks |
| D | Staged apply with rollback, behind the `AutoApplyUpdates` opt-in |
| E | Authenticode signing in the release workflow and `WinVerifyTrust` at apply time |
