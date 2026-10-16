# Troubleshooting

[← Docs](README.md)

| If you see… | Go to |
|---|---|
| No Fieldkit icon next to the clock | [The tray icon is missing](#the-tray-icon-is-missing) |
| A shortcut that does nothing, or ⚠ in Settings | [A shortcut does nothing](#a-shortcut-does-nothing-or-shows--in-settings) |
| Input features dead in some windows | [Keyboard or mouse features stop working](#keyboard-or-mouse-features-stop-working-in-some-windows) |
| Scrolling that goes too far | [Smooth scrolling](#smooth-scrolling-scrolls-too-far-in-an-app) |
| No CPU temperature or fan control | [Sensors](#no-cpu-temperature-or-fan-control) |
| Recording effects or GIF export greyed out | [ffmpeg](#recording-effects-or-gif-export-are-unavailable) |
| Empty Packages or App updates | [winget](#packages-or-app-updates-show-nothing) |
| An update that does not install, or none offered | [Updates](#updates) |
| Something badly wrong | [Resetting Fieldkit](#resetting-fieldkit) |

## The tray icon is missing

Windows may hide new icons behind the **^** arrow on the taskbar. To keep it visible, open
**Settings → Personalization → Taskbar → Other system tray icons** and turn on Fieldkit.

## A shortcut does nothing, or shows ⚠ in Settings

Another app already owns that combination. Settings → Keyboard shortcuts marks taken shortcuts with
⚠; record a different one. Some combinations belong to Windows itself (Win+letter, Win+arrows) and
cannot be used.

Ctrl+Alt+letter shortcuts are the same keys as AltGr+letter on many keyboard layouts, so Fieldkit's
defaults avoid them. If you record one yourself, typing characters such as € or @ may stop working.

## Keyboard or mouse features stop working in some windows

Windows does not let a normal app see input aimed at apps running as administrator. Run Fieldkit as
administrator if you need its input features there: **Settings → General → Always run as administrator** makes
Fieldkit ask for permission every time it starts (including when it starts with Windows), or use Settings →
Features → Permissions → Restart as administrator for one session.

If you decline the permission prompt, Fieldkit starts as a normal user, and turning the switch off stops the prompt at
the next start.

## Smooth scrolling scrolls too far in an app

Some older apps treat every wheel message as a full notch. Add the app to **Apps to leave alone** in
Settings → Mouse & touchpad.

## No CPU temperature or fan control

Both need administrator rights and the [PawnIO](https://pawnio.eu) driver, and are off until you turn
on hardware sensors in Settings → Monitor. GPU temperature works without them.

## Recording effects or GIF export are unavailable

Zooms, blurs, speed changes and GIFs use `ffmpeg`. Install it (for example `winget install ffmpeg`)
and restart Fieldkit; plain trims and cuts work without it.

## Packages or App updates show nothing

They use `winget`. Install or update **App Installer** from the Microsoft Store, then try again.

## Updates

Fieldkit checks for a new version a few seconds after it starts and asks before doing anything. If something is off:

- **No update is offered.** Settings → About → **Check for updates** asks right now and says why if it can't (offline,
  or GitHub is limiting requests: try again later). The start-up check can be switched off in the same place, and "Skip
  this version" hides one version until a newer one arrives; Check for updates offers a skipped version again.
- **"Open download page" instead of "Update now".** You are running the portable copy, which can't replace the file it
  runs from. Quit Fieldkit and unzip the new `Fieldkit.exe` over the old one, or install with the installer.
- **"does not match the checksum" or "no checksum".** Fieldkit only runs an installer whose SHA-256 matches the digest
  GitHub published for it, and it threw the download away. Try again; if it keeps happening, download the installer
  from the [releases page](https://github.com/BJDevelopes/FieldKit/releases/latest) by hand.
- **The update stops at a permission prompt.** An install made for all users (in Program Files) needs your approval to
  be replaced. Declining leaves Fieldkit as it was.
- **Fieldkit started as administrator.** An update installs for the account it runs from. Run the installer by hand if
  you want it elsewhere.

## Resetting Fieldkit

Quit Fieldkit, then delete or rename `%APPDATA%\Fieldkit`. The next start behaves like a first run, with every
feature installed again. To stop it starting with Windows, turn off Settings → General → Start with Windows.

> [!TIP]
> Uninstalling with the installer keeps `%APPDATA%\Fieldkit` so a reinstall finds your settings. Delete that
> folder yourself if you want them gone too.

## Reporting a problem

Open an issue with your Windows version, what you did, what you expected and what happened. Attach
the relevant lines of `%APPDATA%\Fieldkit\fieldkit.log` (Settings → About → Open log folder). Report
security problems privately, as described in [SECURITY.md](../SECURITY.md).
