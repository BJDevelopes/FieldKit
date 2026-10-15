# Changelog

All notable changes are listed here. Versions follow [semantic versioning](https://semver.org);
0.x releases are previews.

## 0.4.0 (2026-10-15)

- **The Dynamic Island is now part of Essentials and lives in the taskbar by default,** in place of the square
  Fieldkit button. Choose "Top of the screen" in Settings → Dynamic Island → Where, or turn it off there.
  Its settings page and feature description now say so too. While the island is open, its pill stays in the
  taskbar; click it to close the island.
- **A clean install starts with every feature installed** (except the experimental ones and Fieldkit's six
  extras, which stay one click away in Settings → Features), instead of only the Essentials bundle. Existing
  installs keep the features they chose.
- **Installing features shows progress.** Install, Uninstall, "Install all" and the bundles work through the
  features one at a time with a progress bar ("Installing Screenshot… 12 of 83") and a note when they are done,
  and the button answers at once with "Installing…". Installed features carry a green "Installed" badge; the
  yellow "Partial" badge is gone (it only said the macOS app has options Fieldkit lacks, and read like an
  install state; that detail stays in each feature's info tooltip).
- **The island opens on Controls** when nothing is going on (no music, timer or agents), instead of the last
  page you looked at. Change it in Settings → Dynamic Island → Reopen on.
- **A Screenshot tile in Controls:** the Dynamic Island's Controls section and the panel's Quick toggles gain a
  Screenshot button that starts the capture selector. Hide or reorder it in Settings → Quick toggles.
- **An installer** next to the portable zip in each release (`Fieldkit-<version>-setup-x64.exe`, and arm64):
  per user without an administrator prompt, a Start menu shortcut, updates in place, and an uninstaller that
  turns off Start with Windows and removes the browser picker's registration (settings stay).
- Settings → Features fits narrow windows: the bundles stack and each feature's badges wrap under its name.
- A project website with a live demo desktop: https://bjdevelopes.github.io/FieldKit/
- Settings and the larger tool windows (Media, the screenshot and recording editors, Disk usage, Port Manager)
  never open bigger than the display: at 150 % scaling or on a small screen they shrink to fit.

## 0.3.0 (2026-10-15)

New Fieldkit extras (they start uninstalled; add them in Settings → Features):

- **Block apps:** listed apps are closed as soon as they open, always or on a schedule (overnight schedules
  work). When a block starts, open ones are asked to close normally. Windows' own programs can't be blocked.
- **Browser picker:** register Fieldkit as a browser (your account only), choose it as the default in Windows'
  Default apps, and every link asks which browser to use, or opens straight in the one you chose for that site.

Also:

- **Dynamic Island in the taskbar:** Settings → Dynamic Island → Where, or right-click the island. It takes
  the Fieldkit button's spot next to the notification area and opens upward, so it never covers browser tabs.
- **AI agents uses far less memory:** session logs are read line by line instead of whole (a heavy Claude
  Code user went from about 1.2 GB to about 120 MB), and live updates are spaced out while an agent writes.
- AI agents no longer shows months-old "working" timers right after Fieldkit starts.
- The island's companion only redraws when it blinks or reacts, instead of keeping the island drawing at
  60 frames a second.

## 0.2.0 (2026-10-15)

- **Join meetings from the Dynamic Island:** calendar events with a Teams, Zoom, Google Meet, Webex, GoTo,
  Whereby or Jitsi link get a Join button, and the island says so a minute before the meeting starts.
- **Keep awake while AI agents work:** a new automation keeps the PC awake while Claude Code, Codex,
  OpenCode or Copilot is working (it follows the island's AI agents section).
- **Screenshot editor:** a Spotlight tool dims everything outside the boxes you draw, and a Copy text tool
  copies the text in the box you drag (blurred areas stay hidden).

New Fieldkit extras (they start uninstalled; add them in Settings → Features):

- **Duplicate files:** finds copies of the same file in the folders you choose and moves the extra
  copies to the Recycle Bin. One copy of every file always stays; repositories, package folders and
  online-only OneDrive files are left alone.
- **Disk usage:** a sunburst chart of a drive or folder with its largest items listed. Click a ring to
  go into a folder; anything can go to the Recycle Bin from the list (Windows folders are not offered).
- **Key remapping:** make a key or shortcut act as another (Caps Lock as Esc, Ctrl+Alt+K as
  Ctrl+Shift+Esc), everywhere or in one app. Settings → Key remapping.

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

- **Light on memory:** about 40 MB idle, and it starts in under a second. Downloads are zips with a
  single `Fieldkit.exe` inside.

See [the parity map](docs/PARITY.md) for exactly how each feature compares with the original.
