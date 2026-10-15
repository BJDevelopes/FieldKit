# Changelog

All notable changes are listed here. Versions follow [semantic versioning](https://semver.org);
0.x releases are previews.

## 0.1.0 (2026-10-14)

First preview: the feature set of [Vorssaint](https://github.com/vorssaint/vorssaint-utils) for macOS,
mapped to Windows 10 and 11 behind one notification-area icon.

- **Panel and Settings** like the original: section tabs, Controls, Utilities and Quick toggles, a
  searchable Settings window, the Features hub (install only what you use, bundles, permissions),
  light and dark themes.
- **Sound:** per-app volume mixer with per-app output, output switcher, device priority, microphone
  mute, music app blocker.
- **System monitor:** CPU, GPU, memory, network (with speed test), disk, power and connected devices,
  tray readouts, alerts, optional fan control.
- **Windows and taskbar:** app switcher with live previews (optional Alt+Tab takeover), window layouts
  with shortcuts and modifier-drag, taskbar previews and clicks, maximize behaviour, quit on close.
- **Mouse and keyboard:** scroll direction, sideways and linear scrolling, smooth scrolling, focus
  follows mouse, pointer acceleration, button shortcuts and gestures, click and key debounce, super
  key, quit and close protection.
- **Clipboard and files:** clipboard history, paste as plain text, Clean URL, shelf, Explorer image
  paste, installer helper, text snippets.
- **Energy and display:** keep awake (timers, automations, lid closed), display brightness (DDC/CI,
  laptop panels, software dimming), HDR extra brightness, Bluetooth on sleep.
- **Tools:** quick panel, quick toggles, colour picker, copy text from screen (OCR and QR), screenshot
  editor, screen recording, camera preview, media tools, cleaner, uninstaller, winget packages, app
  updates, Command Bar, radial menu, scratchpad, wallpaper, cleaning mode, processes and ports.
- **Dynamic Island:** a simulated island at the top of the screen with music, timers, calendar feeds,
  downloads, AI agent usage, lyrics, live equalizer, screen watch and a companion.
- **Taskbar button:** Fieldkit docks a small button on the taskbar next to the notification area
  (Windows 11 hides new tray icons by default). Settings → General → Show on the taskbar.
- **Ideas from the original's pull requests and requests:** a sleep / shut down / restart timer under
  Keep awake, break reminders (eyes and moving), "unplug at 80 %" battery reminder, mute the
  microphone while typing, monitor speaker volume over DDC/CI, window "make larger / smaller"
  shortcuts, clipboard items kept for a chosen number of days, network totals since startup,
  brightness shortcuts for every display, focus follows mouse only between displays, Shift to flip
  OCR line breaks once, npx / Bun / pnpm caches in the cleaner.
- **Command line:** `Fieldkit.exe --list-actions` and `Fieldkit.exe --action <id>` run any tool or
  shortcut action from scripts, Task Scheduler or a Stream Deck; a running Fieldkit handles them.
- **Lessons from the original's bug tracker** built in from the start: shortcut recording, hooks that
  survive sleep and unlock, remapped mouse buttons reaching Fieldkit's own shortcuts, shelf false
  triggers, smooth scrolling that stays in its window, Office clipboard copies kept as text, no cloud
  file downloads while scanning. See [docs/UPSTREAM-ISSUES.md](docs/UPSTREAM-ISSUES.md).

See [the parity map](docs/PARITY.md) for exactly how each feature compares with the original.
