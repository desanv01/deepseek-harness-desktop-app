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
dsh plugin --profile web add dsh-plugin-desktop-updates
```

The same install from the app is **Settings → Updates → the plugin manager**,
which takes the package name, a git spec, or a local folder.

The app leaves an installed version alone when it is newer than the copy in the
executable, so a newer npm release is not undone by the next launch;
`DeepSeekHarness.exe --install-plugin` restores the bundled copy deliberately.

## Releases

This folder is the copy the executable embeds. The plugin is developed and
published from its own repository,
[`desanv01/dsh-plugin-desktop-updates`](https://github.com/desanv01/dsh-plugin-desktop-updates),
which is where versions are bumped, tagged and released. Keep the two in step:
a plugin change that the app should ship has to be copied back here.

## What it contributes

| Slot | Entry | What it renders |
| --- | --- | --- |
| `sidebar.footer.action` | id `desktop-updates` | A labelled row beside Settings (an icon in the 56px rail) that opens the Updates panel; a dot appears when a newer app build exists |
| `sidebar.panellist` | id `desktop-updates` | The sidebar row for the Updates panel, addressed by the same id as the body below |
| `main` | key `desktop-updates` | The Updates panel itself: the full view, in the app's own window |
| `settings.section` | id `desktop-updates` | The concise view inside Settings, with the same facts and actions |

Opening updates is a **layout action, not a bridge call**: the plugin selects its
own panel through `ctx.layout.selectPanel`, so the view appears in the window the
user is already looking at, and opening it cannot fail because the app's bridge
is unavailable. When the shell has no panel slot to select, the plugin falls back
to the desktop app's native updates window — the same window the tray menu opens.

The three list slots require an `id` and the keyed `main` slot requires a `key`;
the shell rejects a registration that omits either.

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
