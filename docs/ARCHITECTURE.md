# Architecture

[← Docs](README.md)

Fieldkit is a .NET 8 WPF tray app (`src/Fieldkit`). It mirrors the feature set and UI of
[Vorssaint](https://github.com/vorssaint/vorssaint-utils) for macOS, mapped to Windows.

## Layout

| Folder | What lives there |
|---|---|
| `Core/` | Settings store, feature catalog, module contracts, runtime, notifier, parity report |
| `Interop/` | Shared Win32: `Native` P/Invokes, `InputHooks` (low-level kb/mouse), `Hotkeys`/`Shortcut`, `InputSender`, `WindowHelper` |
| `UI/` | Theme (`Themes/*.xaml`), `Ui` code-first builders, `Glyphs`, `Sparkline`, `AppIcons`, tray icon, panel window, settings window |
| `Features/<Area>/` | One folder per feature area. Each contains an `IFeatureModule` plus its services and views |

## Feature modules (the only extension point)

Every non-abstract class implementing `IFeatureModule` is discovered by reflection. A module's
`Register(ModuleRegistry r)` declares everything; it must not start work.

```csharp
public sealed class KeepAwakeModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements("keepAwake", Parity.Partial, "No lid-closed mode yet.");     // parity claim (required)
        r.Service("keepAwake", () => new KeepAwakeService());                    // background behaviour
        r.PanelSection(new PanelSectionDef("keepAwake", "Keep awake", Glyphs.Moon, 0, ["keepAwake"], KeepAwakeView.Build));
        r.SettingsPage(new SettingsPageDef("energy", SettingsCategory.Essentials, "Energy", Glyphs.Bolt, 2, ["keepAwake"], EnergyPage.Build));
        r.Action(new ToolAction("keepAwake.toggle", "keepAwake", "Toggle keep awake", Glyphs.Moon, Toggle, "keepAwake.shortcut", "Ctrl+Alt+Win+A"));
    }
}
```

* **Feature ids** are the upstream `AppFeature` raw values, listed in `Core/FeatureCatalog.cs`
  together with how Windows implements each one. Do not rename ids.
* **Parity claims**: `Full` = behaves like upstream, `Partial` = say what is missing in the note,
  `Native` = Windows already does it (point at the system setting), `Planned` = nothing yet (the
  default when a module never claims a feature). `Fieldkit.exe --write-parity docs/PARITY.md`
  regenerates the parity map; the Features hub flags "Not built yet" and "Built into Windows" features, and every other note appears in the
  feature's info tooltip.
* **Services** (`FeatureService`) run while the feature is installed in the Features hub AND
  `ShouldRun()` is true (default: any `EnableKeys` setting is true). `OnStart`/`OnStop` run on the
  UI thread; stop must release every hook, timer, thread and handle. Call
  `FeatureRuntime.Reevaluate(id)` if the run condition depends on something else.
  Get a running instance from UI code with `FeatureRuntime.Service<T>()`.
* **Panel sections** are the icon tabs of the tray panel. `Build` runs every time the tab is shown;
  use `Ui.Every(element, interval, tick)` / `Ui.Track(element, subscribe)` so timers and
  subscriptions stop when the panel closes.
* **Controls / Utilities / Quick toggles** entries are aggregated by `Features/Shell/CoreModule.cs`
  into the Controls, Utilities and Quick toggles tabs, matching the upstream panel.
* **Actions** with a `ShortcutKey` appear on Settings → Keyboard shortcuts, the Command Bar and the
  quick panel. Give the default in `ToolAction.DefaultShortcut` (empty = off until recorded) and bind
  it in your service with `Hotkeys.Bind(shortcutKey, sameDefault, callback)`. Never write defaults
  into settings. A test keeps every default unique and AltGr-safe: no Ctrl+Alt+letter/digit (that is
  AltGr typing on many layouts) and no bare Win+key (Windows owns those).
* **Tray status**: `TrayStatus.Set(featureId, new TrayBadge(text, 0xRRGGBB, priority))` puts a dot and a
  tooltip line on the tray icon while something is active; pass null to clear it (and do so in `OnStop`).
* **Opening things**: open URLs from outside data (feeds, logs, web responses) only after checking they
  are absolute http/https/mailto; move files only through `RecycleBin.Send`, which refuses anything the
  Recycle Bin cannot keep instead of letting the shell delete it for good.

## Settings

`Settings.Get/Set` is a flat JSON key/value store at `%APPDATA%\Fieldkit\settings.json`
(override the folder with `FIELDKIT_DATA_DIR`). Keys are `<featureId>.<name>`, e.g.
`mixer.hideInactive`, `switcher.enabled`. `Settings.Observe(callback, keys...)` calls back on the
UI thread. Bound controls (`Ui.Toggle`, `Ui.SliderRow`, `Ui.ChoiceRow`, `Ui.ShortcutRow`, ...)
read and write a key directly.

## Input

* `InputHooks.AddKeyboardHandler/AddMouseHandler` share ONE low-level hook each, on a dedicated
  thread. Handlers run on that thread: be fast, never block, marshal UI work with `Ui.OnUi`.
  Return true to swallow the event. Ignore `e.IsOwn` events (injected by us) unless you mean it.
* `InputSender` injects input tagged as ours. `Hotkeys` uses `RegisterHotKey` (callbacks on the UI
  thread); for combinations Windows reserves (Alt+Tab, Win+…) use the keyboard hook.

## UI conventions

* Build UI in C# with `UI/Ui.cs`. Colours are theme resource keys (`TextPrimary`, `TextSecondary`,
  `TextTertiary`, `Accent`, `AccentSubtle`, `CardBackground`, `CardBorder`, `ControlBackground`,
  `Green`, `Orange`, `Red`, `Cyan`, `Mint`, `Yellow`, `Pink`, `Purple`, ...); use
  `SetResourceReference`, never hard-coded colours, so light/dark both work.
* Icons are Segoe Fluent Icons code points in `UI/Glyphs.cs`. Check new ones with
  `Fieldkit.exe --render-glyphs out.png` or `--render-glyph-range E700 E7FF out.png`.
* Panel content is ~330 px wide. Section headers are drawn by the panel; start with content.
* Tool windows (clipboard history, switcher, pickers, …) are separate WPF windows. Borderless
  overlays should set `WS_EX_TOOLWINDOW` (out of Alt+Tab) and, when they must not take focus,
  `WS_EX_NOACTIVATE`.

## Checking your work

```sh
dotnet build src/Fieldkit -v q
set FIELDKIT_DATA_DIR=%TEMP%\fieldkit-dev
src/Fieldkit/bin/Debug/net8.0-windows10.0.19041.0/Fieldkit.exe --render-ui out --all-features [--theme light] [--only <sectionOrPageId>]
src/Fieldkit/bin/Debug/net8.0-windows10.0.19041.0/Fieldkit.exe --selftest
```

`--render-ui` renders every panel section and settings page to PNG for review without clicking.
`--selftest` builds every surface and exits non-zero on failure. Neither exercises hooks or
hardware: say what you actually verified interactively.

## Tests

`tests/Fieldkit.Tests` (xUnit) references the app and sees its internals. Put an area's tests under
`tests/Fieldkit.Tests/<Area>/`. WPF allows one `Application` per process: anything that renders UI
in a test must run inside `WpfTestHost.Run`/`RunAsync` (one shared STA thread with the theme loaded),
never create its own `Application`. Keep decision logic (parsers, matchers, layout maths, filters)
in plain static classes so it can be tested without hooks or windows.

```sh
set FIELDKIT_DATA_DIR=%TEMP%\fieldkit-test
dotnet test Fieldkit.sln
```
