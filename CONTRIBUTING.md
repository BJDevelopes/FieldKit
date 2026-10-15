# Contributing to Fieldkit

Thanks for helping. Fieldkit aims to stay native, small per feature and readable. Contributions are
accepted under GPL-3.0-or-later.

## Getting started

You need Windows 10 2004 or newer and the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/BJDevelopes/FieldKit.git
cd FieldKit
dotnet run --project src/Fieldkit
```

Read [the architecture notes](docs/ARCHITECTURE.md) before writing code: every feature is a
self-contained module under `src/Fieldkit/Features/`, discovered automatically, with its own
settings, services and UI.

## Before a big change

Open an issue first for anything substantial (a new feature, a new dependency, a change to how a
feature behaves). Explain the user need, which permissions or system changes it involves, and how it
maps to the original macOS feature if it is a port.

## Rules of thumb

- Keep each pull request to one independently useful change.
- Put decision logic (parsers, matchers, layout maths, timers) in plain static classes and unit-test it.
- Everything a feature starts must stop when it is switched off or uninstalled: hooks, timers,
  threads, windows, COM objects. Anything that changes a system setting must restore it.
- Default shortcuts go in `ToolAction.DefaultShortcut`. They must be unique and must not be
  Ctrl+Alt+letter/digit (that is AltGr typing on many keyboard layouts) or a bare Win+key.
- Open URLs from outside data only when they are absolute http/https/mailto, and move files only
  through `RecycleBin.Send` (it refuses anything the Recycle Bin cannot keep).
- Claim parity honestly with `r.Implements(...)` and regenerate the parity map
  (`Fieldkit.exe --write-parity docs/PARITY.md`) when a claim changes. CI fails if it is stale.
- Never commit real personal data in fixtures or previews (app lists, device names, paths, logs).

## Checking your work

```sh
./build.ps1
```

This builds in Release with warnings as errors, runs every test and the app's selftest. For UI work,
render the surfaces you changed and look at them in both themes:

```sh
set FIELDKIT_DATA_DIR=%TEMP%\fieldkit-dev
src/Fieldkit/bin/Debug/net8.0-windows10.0.19041.0/Fieldkit.exe --render-ui out --all-features --only <sectionOrPageId>
src/Fieldkit/bin/Debug/net8.0-windows10.0.19041.0/Fieldkit.exe --render-ui out --all-features --theme light --only <sectionOrPageId>
```

Builds, tests and renders do not prove that a hook, a hardware control or a window interaction works.
In the pull request, say what you actually tried on a real PC (Windows version, displays, DPI,
keyboard layout where relevant) and what remains untested.

## Pull requests

Use a title like `type(scope): short imperative phrase`, for example `fix(mixer): keep pinned apps
first`. Describe the behaviour before and after, and how you verified it. Leave version numbers and
the changelog to the maintainer.

Report security problems privately, as described in [SECURITY.md](SECURITY.md).
