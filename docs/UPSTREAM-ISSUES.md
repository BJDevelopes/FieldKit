# Lessons from Vorssaint's bug reports

[← Docs](README.md)

Fieldkit ports Vorssaint's features, so its bug tracker is a map of where this kind of app goes wrong.
All 487 issues labelled `bug` (166 open, 321 closed, October 2026) were read. Most are specific to
macOS (Spaces, the Dock, Stage Manager, the notch hardware, Apple Music, AirPods, Accessibility
timeouts). The ones whose cause could exist in Fieldkit's design were checked against the code.

## Fixed in Fieldkit

| Upstream | Problem | What Fieldkit does |
|---|---|---|
| [#308](https://github.com/vorssaint/vorssaint-utils/issues/308) | Shortcut recorder triggers existing shortcuts instead of capturing them | Every recorder suspends all Fieldkit hotkeys while it records |
| [#1075](https://github.com/vorssaint/vorssaint-utils/issues/1075) | Scroll wheel dead after fast user switching until restart | Input hooks are reinstalled after unlock, resume and session switches (Windows silently drops slow hooks) |
| [#401](https://github.com/vorssaint/vorssaint-utils/issues/401) | Mouse button shortcuts cannot trigger the app's own shortcuts | Remapped keys carry their own tag, so the Alt+Tab switcher and other hook features react to them |
| [#2212](https://github.com/vorssaint/vorssaint-utils/issues/2212), [#240](https://github.com/vorssaint/vorssaint-utils/issues/240) | Shelf opens while dragging Dock items or windows | Shelf shake ignores drags that start on the taskbar, window moves and resizes, and full-screen apps |
| [#1851](https://github.com/vorssaint/vorssaint-utils/issues/1851) | Smooth scroll carries into another window | The glide stops as soon as the pointer leaves the window it started in |
| [#2436](https://github.com/vorssaint/vorssaint-utils/issues/2436) | Dynamic Island blocks other content | Clicks pass through everything but the pill; the island hides over full-screen apps by default on Windows |
| [#512](https://github.com/vorssaint/vorssaint-utils/issues/512), [#807](https://github.com/vorssaint/vorssaint-utils/issues/807) | Switcher shows closed windows | Windows are listed live on every open; remembered window-less apps are checked by process id and path, so a reused id cannot bring a quit app back |
| [#89](https://github.com/vorssaint/vorssaint-utils/issues/89), [#2411](https://github.com/vorssaint/vorssaint-utils/issues/2411) | Clipboard history wrong for Excel; copied text re-encoded | Text wins over the picture Office apps add (no forced rendering), and copied text is stored exactly, without trimming |
| [#877](https://github.com/vorssaint/vorssaint-utils/issues/877) | Scanning triggers mass cloud downloads | Cloud-only (OneDrive) files are never read for hashing; previews keep placeholders |

## Already handled by the design

| Upstream | Problem | Why Fieldkit is not affected |
|---|---|---|
| [#148](https://github.com/vorssaint/vorssaint-utils/issues/148), [#1721](https://github.com/vorssaint/vorssaint-utils/issues/1721) | Quit on close quits apps when switching desktops or going full screen | Windows on other virtual desktops still count, and a 1.5 s grace covers window swaps |
| [#2391](https://github.com/vorssaint/vorssaint-utils/issues/2391), [#77](https://github.com/vorssaint/vorssaint-utils/issues/77) | Switcher switches on hover; activates the wrong window | Only a click or key release commits, and the exact window is activated |
| [#2485](https://github.com/vorssaint/vorssaint-utils/issues/2485) | Now Playing follows a paused player instead of the one playing | Playing music, then playing video, then the last followed player, then paused music |
| [#916](https://github.com/vorssaint/vorssaint-utils/issues/916) | Multi-line snippets lose line breaks | Multi-line expansions paste through a temporary clipboard instead of typing Enter |
| [#1581](https://github.com/vorssaint/vorssaint-utils/issues/1581), [#1522](https://github.com/vorssaint/vorssaint-utils/issues/1522) | Cleaner removes files apps need (Spicetify) | Only known cache folders are cleaned; Spotify's folders are protected; everything goes to the Recycle Bin |
| [#1913](https://github.com/vorssaint/vorssaint-utils/issues/1913), [#1465](https://github.com/vorssaint/vorssaint-utils/issues/1465) | False "update available" | Versions are normalised (leading `v`, trailing `.0`, build suffixes) before comparing |
| [#1351](https://github.com/vorssaint/vorssaint-utils/issues/1351) | Colour picker copies the wrong pixel | Pointer and screenshot both use physical pixels on every display scale |
| [#1131](https://github.com/vorssaint/vorssaint-utils/issues/1131), [#747](https://github.com/vorssaint/vorssaint-utils/issues/747) | Freeze when a display is switched off and on | Monitor control runs on its own worker; display changes are debounced and never block the UI thread |
| [#3047](https://github.com/vorssaint/vorssaint-utils/issues/3047), [#157](https://github.com/vorssaint/vorssaint-utils/issues/157) | Battery drain | Measured idle cost: about 0.3% of one core with the Essentials, 0.5% with 31 features installed |
| [#369](https://github.com/vorssaint/vorssaint-utils/issues/369), [#1447](https://github.com/vorssaint/vorssaint-utils/issues/1447) | Menu bar icon disappears | The tray icon re-registers when Explorer restarts, and the taskbar button re-attaches to the new taskbar |

## Not applicable on Windows

Spaces ordering and Mission Control, Stage Manager, the hardware notch, Dock auto-hide and magnification,
Apple Music and AirPods behaviour, macOS Accessibility and Screen Recording permission prompts, and
Apple Silicon sensor readings have no Windows counterpart in Fieldkit's design.
