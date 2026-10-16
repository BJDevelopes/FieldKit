# Privacy

[← Docs](README.md)

> [!IMPORTANT]
> Fieldkit has **no account, no telemetry, no analytics and no crash reporting**. Nothing you do in the app
> is sent anywhere. It only uses the network for a feature you invoke, listed [below](#when-fieldkit-uses-the-network).

## What stays on your PC

Everything Fieldkit stores lives in `%APPDATA%\Fieldkit`:

| What | Where |
|---|---|
| Preferences | `settings.json` |
| Clipboard history, pinned items and copied images | the clipboard folder, only while Clipboard history is on |
| Text snippets, scratchpad notes, shelf items | their own files |
| Quick notes from the Dynamic Island | `Notes\notes.json` (never part of the settings export) |
| Diagnostics | `fieldkit.log` (rotated at 1 MB; contains no clipboard or typed text) |

Settings export (Settings → General → Backup) contains preferences only. Clipboard history, snippets,
notes and machine-specific state are left out.

Clipboard history skips anything password managers mark as private
(`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`), and you can exclude
apps and clear the history at any time.

Features that listen to the keyboard or mouse (switcher, snippets, debounce, super key, scrolling
features, quit protection, and so on) only install their hook while they are switched on. Text
snippets keep a short buffer of recent keystrokes in memory to match triggers; it is never written to
disk.

The AI agents extension of the Dynamic Island reads usage logs that Claude Code, Codex, OpenCode and
GitHub Copilot already keep in your user profile. It only reads them, on this PC.

## When Fieldkit uses the network

Only when you use a feature that needs it:

| Feature | Contacts | When |
|---|---|---|
| Network speed test | `speed.cloudflare.com` | when you click the test button |
| Dynamic Island lyrics | `lrclib.net` (song title, artist, album, duration) | only if online lyrics are turned on (off by default) |
| Dynamic Island calendar | the calendar feed URLs you add | while the extension is on |
| Radial menu link icons | the website of a link you add (`/favicon.ico`) | when the link's icon is fetched |
| Packages and App updates | Microsoft's winget and Store sources, through `winget` | when you search, install, update or check for updates |
| Command Bar web search | your browser opens Bing with your query | when you choose "Search the web" |

Links from outside data (for example calendar events) only open if they are web or email links.

## Permissions

> [!NOTE]
> Fieldkit runs as a normal user and only asks for more when an optional extra needs it.

Fieldkit runs as a normal user. Administrator rights are only needed for optional extras (CPU
temperatures and fan control through the PawnIO driver, system-wide cleaning, machine-wide
uninstall leftovers), and the app asks before restarting elevated. Settings → Features → Permissions
lists which features use the microphone, camera, notifications and input hooks.
