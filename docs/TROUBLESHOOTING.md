# Troubleshooting

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
administrator (Settings → Features → Permissions) if you need its input features there.

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

## Resetting Fieldkit

Quit Fieldkit, then delete or rename `%APPDATA%\Fieldkit`. The next start behaves like a first run.
To stop it starting with Windows, turn off Settings → General → Start with Windows.

## Reporting a problem

Open an issue with your Windows version, what you did, what you expected and what happened. Attach
the relevant lines of `%APPDATA%\Fieldkit\fieldkit.log` (Settings → About → Open log folder). Report
security problems privately, as described in [SECURITY.md](../SECURITY.md).
