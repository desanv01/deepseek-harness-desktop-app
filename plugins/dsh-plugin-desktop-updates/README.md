# dsh-plugin-desktop-updates

The Updates UI of the [DeepSeek Harness desktop app](https://github.com/desanv01/deepseek-harness-desktop-app):
a row beside Settings in the sidebar and an Updates section inside Settings,
showing the desktop app's version, the installed and published harness versions,
and the plugins this home runs — with the actions to check, download, install,
restart, repair the harness, and enable, disable, install or remove plugins.

Everything it shows comes from the desktop app through `window.__dshDesktop`, the
versioned page bridge the app injects into every page it loads. In a plain
browser tab that bridge is absent and the plugin says so instead of pretending:
the app is what performs updates, and a page that cannot reach it has nothing to
offer.

## Installing it

The app installs this plugin by itself, from the copy inside its executable, into
the harness home it owns. Installing from npm replaces that copy with the
published one:

```sh
dsh plugin add dsh-plugin-desktop-updates
```

The app leaves an installed version alone when it is newer than the copy in the
executable, so a newer npm release is not undone by the next launch;
`DeepSeekHarness.exe --install-plugin` restores the bundled copy deliberately.

## What it contributes

| Slot | Entry id | What it renders |
| --- | --- | --- |
| `sidebar.footer.action` | `desktop-updates` | A labelled row beside Settings (an icon in the 56px rail) that opens the updates window; a dot appears when a newer app build exists |
| `settings.section` | `desktop-updates` | The Updates section: app, harness, plugin list, and their actions |

Both slots are lists, so both registrations carry an `id`; the shell rejects a
list entry without one.

## Saying what happened

The shell drops a slot registration it refuses without reporting it, so a plugin
whose UI never appears looks exactly like one that never loaded. This plugin
writes `window.__dshDesktopUpdates` as it applies — the slots it registered and
every contribution the shell refused — and reports the same thing to the app's
log. The app reads that marker after each page load and logs it; the Updates
section repeats any refusal, because that is the only place a user would see it.

## Files

- `index.js` — the host half: an empty apply, so the row exists in the profile's
  bundle stack and the loader keeps it.
- `client.js` — the browser half, a hand-written module-face bundle
  (`window.__ModuleLoader__.load({ id, factory })`).
- `cordis.patch.yml` — the loader row the bundle contributes.

## License

MIT
