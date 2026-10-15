// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Builds everything the bar can offer right now (port of upstream CommandBarCatalog): every
/// registered <see cref="ToolAction"/> and Utilities entry of installed features, the on/off
/// switches of the Controls tab and Quick toggles, power, Wi-Fi and volume, the folders everyone has,
/// facts about this PC, Fieldkit's Settings pages, Windows Settings pages, saved shortcuts, emoji,
/// apps, windows and found files. Rebuilt on every open, so availability and states are current.
/// </summary>
public static class CommandBarCatalog
{
    /// <summary>Features whose first action is worth suggesting before any habit forms (upstream's curated list).</summary>
    private static readonly string[] CuratedFeatures =
        ["screenshot", "keepAwake", "screenOCR", "clipboardHistory", "colorPicker", "quickToggles", "textSnippets"];

    /// <summary>Runs after a short beat, so the bar is gone and the previous app has focus again.</summary>
    public static void AfterBeat(Action action, int milliseconds = 150)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Ui.Guard(action);
        };
        timer.Start();
    }

    private static string Area(string featureId, string title)
    {
        if (!FeatureCatalog.TryGet(featureId, out var feature)) return string.Empty;
        return string.Equals(feature.Title, title, StringComparison.CurrentCultureIgnoreCase)
            ? FeatureCatalog.GroupTitle(feature.Group)
            : feature.Title;
    }

    public static List<CommandBarEntry> Build(CommandBarController controller, ICommandBarHost host)
    {
        var entries = new List<CommandBarEntry>();
        var registry = FeatureRuntime.Registry;

        // Every action other modules registered (clipboard history, snippets, screenshot, ...).
        var actionTitles = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var action in registry.Actions)
        {
            if (action.FeatureId == CommandBarKeys.FeatureId || !FeatureCatalog.IsAvailable(action.FeatureId)) continue;
            actionTitles.Add(action.Title);
            var shortcut = action.ShortcutKey is null ? null : Fieldkit.Features.Shell.CoreModule.EffectiveShortcut(action.ShortcutKey);
            var run = action.Run;
            entries.Add(new CommandBarEntry
            {
                Id = "action." + action.Id,
                Title = action.Title,
                Subtitle = Area(action.FeatureId, action.Title),
                Keywords = FeatureCatalog.TryGet(action.FeatureId, out var f) ? f.Title : string.Empty,
                Glyph = action.Glyph,
                ShortcutText = string.IsNullOrEmpty(shortcut) ? null : shortcut,
                Run = _ => AfterBeat(run),
            });
        }

        // Tools that only registered a Utilities entry.
        foreach (var utility in registry.Utilities)
        {
            if (utility.FeatureId == CommandBarKeys.FeatureId || !FeatureCatalog.IsAvailable(utility.FeatureId) || !actionTitles.Add(utility.Title)) continue;
            var shortcut = utility.ShortcutKey is null ? null : Fieldkit.Features.Shell.CoreModule.EffectiveShortcut(utility.ShortcutKey);
            var open = utility.Open;
            entries.Add(new CommandBarEntry
            {
                Id = "action.utility." + utility.FeatureId + "." + CommandBarSearch.Normalize(utility.Title).Replace(' ', '-'),
                Title = utility.Title,
                Subtitle = Area(utility.FeatureId, utility.Title),
                Keywords = utility.Description,
                Glyph = utility.Glyph,
                ShortcutText = string.IsNullOrEmpty(shortcut) ? null : shortcut,
                Run = _ => AfterBeat(open),
            });
        }

        // Every on/off switch of the Controls tab becomes "Turn on X" / "Turn off X".
        foreach (var control in registry.Controls)
        {
            if (!FeatureCatalog.IsAvailable(control.FeatureId)) continue;
            var group = FeatureCatalog.GroupTitle(FeatureCatalog.Get(control.FeatureId).Group);
            entries.Add(Toggle(control.SettingKey, control.DefaultValue, control.Title, control.Description, control.Glyph, group));
            foreach (var sub in control.SubToggles ?? [])
                entries.Add(Toggle(sub.SettingKey, sub.DefaultValue, sub.Title, sub.Description, control.Glyph, group));
        }

        if (FeatureCatalog.IsAvailable("quickToggles"))
        {
            foreach (var quick in registry.QuickToggles)
            {
                var on = SafeIsOn(quick);
                var toggle = quick.Toggle;
                entries.Add(new CommandBarEntry
                {
                    Id = "toggle.quick." + quick.Id,
                    Title = on is null ? quick.Title : string.Format(on.Value ? CommandBarStrings.TurnOffFormat : CommandBarStrings.TurnOnFormat, quick.Title),
                    Subtitle = CommandBarStrings.QuickTogglesTitle,
                    Keywords = quick.Title,
                    Glyph = quick.Glyph,
                    IsActive = on == true,
                    Run = _ => AfterBeat(toggle),
                });
            }
        }

        entries.Add(new CommandBarEntry
        {
            Id = CommandBarPreferences.EmojiBrowserRowId,
            Title = CommandBarStrings.SourceEmoji,
            Subtitle = CommandBarStrings.PageTitle,
            Keywords = CommandBarStrings.KindEmoji,
            Glyph = CommandBarGlyphs.Smile,
            KeepsBarOpen = true,
            CountsUsage = true,
            Run = _ =>
            {
                controller.Prefill(string.Empty);
                controller.SetCategory(CommandBarSource.Emoji);
            },
        });

        entries.AddRange(PowerEntries());
        entries.AddRange(SoundEntries());

        foreach (var (name, path) in CommandBarSystem.StandardFolders())
        {
            entries.Add(new CommandBarEntry
            {
                Id = "folder." + path,
                Title = name,
                Subtitle = CommandBarStrings.KindFolder,
                Keywords = CommandBarStrings.KindFolder,
                IconKind = CommandBarIconKind.Shell,
                IconPath = path,
                Glyph = Glyphs.Folder,
                RevealPath = path,
                Run = _ => CommandBarSystem.Open(path),
            });
        }

        entries.AddRange(SystemAnswers());
        entries.AddRange(DateAnswers(CultureInfo.CurrentCulture, DateTime.Now));

        entries.Add(new CommandBarEntry
        {
            Id = "action.openSettings",
            Title = CommandBarStrings.ActionOpenSettings,
            Subtitle = CommandBarStrings.SettingsTitle,
            Glyph = Glyphs.Settings,
            Run = _ => host.OpenSettings("general"),
        });

        // Fieldkit's own Settings pages ("general" is what Open Settings opens).
        foreach (var page in registry.SettingsPages)
        {
            if (page.Id == "general" || (page.FeatureGate.Length > 0 && !page.FeatureGate.Any(FeatureCatalog.IsAvailable))) continue;
            var featureWords = page.FeatureGate.Select(id => FeatureCatalog.TryGet(id, out var info) ? info.Title : string.Empty);
            var id = page.Id;
            entries.Add(new CommandBarEntry
            {
                Id = "settings." + page.Id,
                Title = page.Title,
                Subtitle = CommandBarStrings.SettingsTitle,
                Keywords = string.Join(' ', (page.Keywords ?? []).Concat(featureWords)),
                Glyph = page.Glyph,
                Run = _ => host.OpenSettings(id),
            });
        }

        entries.AddRange(LinkEntries(controller, host));
        controller.Curated = CuratedIds(entries);
        return entries;
    }

    private static bool? SafeIsOn(QuickToggleDef quick)
    {
        try { return quick.IsOn?.Invoke(); }
        catch (Exception) { return null; }
    }

    private static CommandBarEntry Toggle(string key, bool defaultValue, string title, string description, string glyph, string group)
    {
        var on = Settings.GetBool(key, defaultValue);
        return new CommandBarEntry
        {
            Id = "toggle." + key,
            Title = string.Format(on ? CommandBarStrings.TurnOffFormat : CommandBarStrings.TurnOnFormat, title),
            Subtitle = group,
            Keywords = title + " " + description,
            Glyph = glyph,
            IsActive = on,
            Run = _ => Settings.Set(key, !on),
        };
    }

    /// <summary>The first action of each curated feature, then the first few actions at all.</summary>
    public static List<string> CuratedIds(IReadOnlyList<CommandBarEntry> entries)
    {
        var actions = FeatureRuntime.Registry.Actions;
        var ids = new List<string>();
        foreach (var feature in CuratedFeatures)
        {
            var first = actions.FirstOrDefault(a => a.FeatureId == feature);
            if (first is not null) ids.Add("action." + first.Id);
        }
        ids.AddRange(entries.Where(e => e.Id.StartsWith("action.", StringComparison.Ordinal)).Select(e => e.Id).Take(7));
        return ids.Distinct().ToList();
    }

    // ------------------------------------------------------------------ power and sound

    private static IEnumerable<CommandBarEntry> PowerEntries()
    {
        yield return Power(CommandBarSystem.PowerAction.Sleep, CommandBarStrings.PowerSleep, null, CommandBarGlyphs.Moon, "suspend");
        yield return Power(CommandBarSystem.PowerAction.Restart, CommandBarStrings.PowerRestart, CommandBarStrings.PowerRestartConfirm, CommandBarGlyphs.Restart, "reboot");
        yield return Power(CommandBarSystem.PowerAction.ShutDown, CommandBarStrings.PowerShutDown, CommandBarStrings.PowerShutDownConfirm, CommandBarGlyphs.Power, "power off turn off");
        yield return Power(CommandBarSystem.PowerAction.LogOut, CommandBarStrings.PowerLogOut, CommandBarStrings.PowerLogOutConfirm, CommandBarGlyphs.SignOut, "log out logout log off");
        if (CommandBarSystem.CachedWifiOn is { } wifiOn)
        {
            yield return new CommandBarEntry
            {
                Id = "action.wifi",
                Title = wifiOn ? CommandBarStrings.WifiOff : CommandBarStrings.WifiOn,
                Subtitle = CommandBarStrings.GeneralTitle,
                Keywords = "wifi wi-fi wireless",
                Glyph = CommandBarGlyphs.Wifi,
                IsActive = wifiOn,
                Run = _ => CommandBarSystem.SetWifi(!wifiOn),
            };
        }
    }

    private static CommandBarEntry Power(CommandBarSystem.PowerAction action, string title, string? confirm, string glyph, string keywords) => new()
    {
        Id = "action.power." + action.ToString().ToLowerInvariant(),
        Title = title,
        Subtitle = CommandBarStrings.GeneralTitle,
        Keywords = keywords,
        Glyph = glyph,
        ConfirmationPrompt = confirm,
        Run = _ => AfterBeat(() => CommandBarSystem.Run(action), 250),
    };

    private static IEnumerable<CommandBarEntry> SoundEntries()
    {
        if (CommandBarSystem.ReadVolume() is not { } volume) yield break;
        yield return new CommandBarEntry
        {
            Id = "action.volume",
            Title = "Volume",
            Subtitle = "Sound",
            Keywords = "sound audio level loudness",
            Glyph = Glyphs.Volume,
            AnswerValue = volume.Muted ? "0%" : volume.Volume + "%",
            NumericRange = (0, 100),
            Run = value =>
            {
                if (value is { } v) CommandBarSystem.SetVolume(v);
            },
        };
        yield return new CommandBarEntry
        {
            Id = "action.soundMute",
            Title = volume.Muted ? "Turn the sound back on" : "Mute the sound",
            Subtitle = "Sound",
            Keywords = "mute unmute silence sound audio",
            Glyph = volume.Muted ? Glyphs.Volume : Glyphs.Mute,
            IsActive = volume.Muted,
            Run = _ => CommandBarSystem.SetMute(!volume.Muted),
        };
    }

    // ------------------------------------------------------------------ answers about this PC

    private static IEnumerable<CommandBarEntry> SystemAnswers()
    {
        if (CommandBarSystem.ReadBattery() is { } battery)
        {
            var value = battery.Percent + "%";
            yield return new CommandBarEntry
            {
                Id = "answer.battery",
                Title = CommandBarStrings.AnswerBatteryLabel,
                Subtitle = battery.Charging ? CommandBarStrings.AnswerBatteryCharging : battery.OnPower ? CommandBarStrings.AnswerBatteryPlugged : CommandBarStrings.AnswerBatteryLabel,
                Glyph = battery.OnPower ? CommandBarGlyphs.BatteryCharging : CommandBarGlyphs.Battery,
                AnswerValue = value,
                CountsUsage = false,
                Run = _ => CommandBarSystem.Copy(value),
            };
        }
        if (CommandBarSystem.ReadMemory() is { } memory)
        {
            var used = Apps.Common.ByteFormat.Format((long)memory.Used);
            var total = Apps.Common.ByteFormat.Format((long)memory.Total);
            var percent = (int)Math.Round(memory.Used * 100.0 / memory.Total);
            yield return new CommandBarEntry
            {
                Id = "answer.memory",
                Title = CommandBarStrings.AnswerMemoryLabel,
                Subtitle = string.Format(CommandBarStrings.AnswerMemoryFormat, used, total),
                Glyph = CommandBarGlyphs.Memory,
                AnswerValue = percent + "%",
                CountsUsage = false,
                Run = _ => CommandBarSystem.Copy($"{used} / {total}"),
            };
        }
        if (CommandBarSystem.ReadSystemDrive() is { } drive)
        {
            var free = Apps.Common.ByteFormat.Format(drive.Free);
            var total = Apps.Common.ByteFormat.Format(drive.Total);
            yield return new CommandBarEntry
            {
                Id = "answer.storage",
                Title = CommandBarStrings.AnswerStorageLabel,
                Subtitle = string.Format(CommandBarStrings.AnswerStorageFormat, free, total),
                Glyph = CommandBarGlyphs.Drive,
                AnswerValue = (int)Math.Round(drive.Free * 100.0 / drive.Total) + "%",
                CountsUsage = false,
                Run = _ => CommandBarSystem.Copy(free),
            };
        }
    }

    public static List<CommandBarEntry> DateAnswers(CultureInfo culture, DateTime now)
    {
        var date = now.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
        var time = now.ToString(culture.DateTimeFormat.LongTimePattern, culture);
        return
        [
            new CommandBarEntry
            {
                Id = "answer.date",
                Title = CommandBarStrings.AnswerDateLabel,
                Subtitle = now.ToString(culture.DateTimeFormat.LongDatePattern, culture),
                Glyph = CommandBarGlyphs.Calendar,
                AnswerValue = date,
                CountsUsage = false,
                Run = _ => CommandBarSystem.Copy(date),
            },
            new CommandBarEntry
            {
                Id = "answer.time",
                Title = CommandBarStrings.AnswerTimeLabel,
                Subtitle = time,
                Glyph = CommandBarGlyphs.Clock,
                AnswerValue = time,
                CountsUsage = false,
                Run = _ => CommandBarSystem.Copy(time),
            },
        ];
    }

    // ------------------------------------------------------------------ saved shortcuts

    public static List<CommandBarEntry> LinkEntries(CommandBarController controller, ICommandBarHost host) =>
        controller.Prefs.Links.Select(link => new CommandBarEntry
        {
            Id = "link." + link.Id,
            Title = link.Name,
            Subtitle = link.Kind == CommandBarLinkKind.Script
                ? link.RunsWithoutArgument ? CommandBarStrings.ScriptBareSearchHint : CommandBarStrings.ScriptSearchHint
                : link.TakesArgument ? CommandBarStrings.LinkSearchHint : CommandBarStrings.KindLink,
            Keywords = CommandBarStrings.KindLink,
            Glyph = link.Kind switch
            {
                CommandBarLinkKind.Place => Glyphs.Folder,
                CommandBarLinkKind.Script => CommandBarGlyphs.Script,
                _ => Glyphs.Link,
            },
            KeepsBarOpen = link.TakesArgument,
            TakesArgument = link.TakesArgument,
            RevealPath = CommandBarLinks.RevealPath(link),
            Run = _ =>
            {
                if (link.Kind == CommandBarLinkKind.Script) RunScript(link, controller, host);
                else OpenLink(link, controller, host);
            },
        }).ToList();

    private static void OpenLink(CommandBarLink link, CommandBarController controller, ICommandBarHost host)
    {
        var typed = controller.QueryWhenRun.Trim();
        var argument = CommandBarLinks.TrailingArgument(typed, link.Name) ?? string.Empty;
        // A saved search with nothing to search for puts its name in the field instead of opening an empty page.
        if (link.TakesQuery && argument.Length == 0)
        {
            controller.Prefill(link.Name + " ");
            return;
        }
        host.Hide();
        var clipboard = string.Empty;
        if (link.Destination.Contains(CommandBarLinks.ClipboardToken, StringComparison.Ordinal))
        {
            try { clipboard = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
            catch (Exception) { clipboard = string.Empty; }
        }
        var date = DateTime.Now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentCulture);
        var expanded = CommandBarLinks.Expand(link.Destination, link.Kind, argument, clipboard, string.Empty, date);
        if (link.Kind == CommandBarLinkKind.Place)
        {
            var path = CommandBarLinks.ExpandHome(expanded.Trim());
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                CommandBarSystem.Beep();
                return;
            }
            CommandBarSystem.Open(path);
            return;
        }
        if (CommandBarLinks.Url(link, expanded) is { } url) CommandBarSystem.Open(url.ToString());
        else CommandBarSystem.Beep();
    }

    private static void RunScript(CommandBarLink link, CommandBarController controller, ICommandBarHost host)
    {
        var typed = controller.QueryWhenRun.Trim();
        var argument = CommandBarLinks.TrailingArgument(typed, link.Name) ?? string.Empty;
        if (argument.Length == 0 && !link.RunsWithoutArgument)
        {
            controller.Prefill(link.Name + " ");
            return;
        }
        if (controller.ScriptRunner.Cached(link.Id, argument) is { } cached)
        {
            CommandBarSystem.Copy(cached);
            host.Hide();
            return;
        }
        controller.ScriptRunner.RunNow(link, argument);
        controller.Prefill(typed);
    }

    public static CommandBarEntry ScriptAnswer(CommandBarLink link, string? output) => new()
    {
        Id = "link." + link.Id,
        Title = output ?? CommandBarStrings.ScriptRunning,
        Subtitle = output is null ? link.Name : CommandBarStrings.CopyHint,
        Glyph = CommandBarGlyphs.Script,
        IsAnswer = true,
        CountsUsage = false,
        KeepsBarOpen = output is null,
        Run = _ =>
        {
            if (output is not null) CommandBarSystem.Copy(output);
        },
    };

    // ------------------------------------------------------------------ answers to what was typed

    /// <summary>The leading answer when what was typed is a sum, conversion, colour or date question.</summary>
    public static CommandBarEntry? Answer(string query)
    {
        if (CommandBarMath.Evaluate(query) is { } math)
        {
            var expression = query.Trim().TrimEnd('=').Trim();
            var completed = math.ClosingBrackets.Length == 0 ? string.Empty : expression + math.ClosingBrackets + " · ";
            return AnswerRow("math.result", math.Formatted, completed + CommandBarStrings.CopyHint, Glyphs.Calculator);
        }
        if (CommandBarUnits.Convert(query) is { } converted)
            return AnswerRow("units.result", converted.Formatted, CommandBarStrings.CopyHint, CommandBarGlyphs.Conversion);
        if (CommandBarColors.Convert(query) is { } color)
            return AnswerRow("color.result", color.Formatted, CommandBarStrings.CopyHint, Glyphs.Eyedropper) with
            {
                IconKind = CommandBarIconKind.Color,
                Swatch = color.Color,
            };
        if (CommandBarColors.Parse(query) is { } preview)
            return AnswerRow("color.preview", query.Trim(), CommandBarStrings.CopyHint, Glyphs.Eyedropper) with
            {
                IconKind = CommandBarIconKind.Color,
                Swatch = preview,
            };
        if (CommandBarDates.Evaluate(query) is { } dated)
            return AnswerRow("date.result", dated.Formatted, dated.Detail, CommandBarGlyphs.Calendar);
        return null;
    }

    private static CommandBarEntry AnswerRow(string id, string value, string subtitle, string glyph) => new()
    {
        Id = id,
        Title = value,
        Subtitle = subtitle,
        Glyph = glyph,
        IsAnswer = true,
        CountsUsage = false,
        Run = _ => CommandBarSystem.Copy(value),
    };

    /// <summary>A web address typed on its own opens, not searches.</summary>
    public static CommandBarEntry? OpenUrl(string query)
    {
        var trimmed = query.Trim();
        if (CommandBarLinks.TypedUrl(trimmed) is not { } url) return null;
        return new CommandBarEntry
        {
            Id = "action.openURL",
            Title = trimmed,
            Subtitle = CommandBarStrings.OpenInBrowser,
            Glyph = CommandBarGlyphs.Globe,
            CountsUsage = false,
            Run = _ => CommandBarSystem.Open(url.ToString()),
        };
    }

    /// <summary>A found file: opens with its default app; Ctrl+Enter shows it in File Explorer.</summary>
    public static CommandBarEntry FileEntry(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new CommandBarEntry
        {
            Id = "file." + path,
            Title = Path.GetFileName(path),
            Subtitle = CommandBarFileSearchSupport.Abbreviating(Path.GetDirectoryName(path) ?? path, home),
            Keywords = CommandBarStrings.SourceFiles,
            IconKind = CommandBarIconKind.Shell,
            IconPath = path,
            Glyph = CommandBarGlyphs.Document,
            CountsUsage = false,
            RevealPath = path,
            Run = _ =>
            {
                if (File.Exists(path) || Directory.Exists(path)) CommandBarSystem.Open(path);
                else CommandBarSystem.Beep();
            },
        };
    }

    // ------------------------------------------------------------------ apps, windows, settings, emoji

    public static List<CommandBarEntry> AppEntries(IEnumerable<CommandBarApp> apps, IReadOnlySet<string> runningPaths) =>
        apps.Select(app =>
        {
            var exeName = app.ExePath is null ? string.Empty : Path.GetFileNameWithoutExtension(app.ExePath);
            var running = app.ExePath is not null && runningPaths.Contains(app.ExePath);
            return new CommandBarEntry
            {
                Id = CommandBarRowShortcuts.AppKey(app.Id),
                Title = app.Name,
                Subtitle = CommandBarStrings.KindApp,
                Keywords = CommandBarSearch.Normalize(exeName) == CommandBarSearch.Normalize(app.Name) ? string.Empty : exeName,
                IconKind = CommandBarIconKind.Shell,
                IconPath = CommandBarIcons.AppsFolder(app.Id),
                Glyph = CommandBarGlyphs.Apps,
                IsActive = running,
                RevealPath = app.ExePath ?? app.ShortcutPath,
                Run = _ => AfterBeat(() => ActivateOrLaunch(app), 60),
            };
        }).ToList();

    /// <summary>A running app comes forward (like upstream's openApplication); otherwise it launches.</summary>
    public static void ActivateOrLaunch(CommandBarApp app)
    {
        if (app.ExePath is { } exe)
        {
            var window = WindowHelper.GetAltTabWindows().FirstOrDefault(w => string.Equals(w.ProcessPath, exe, StringComparison.OrdinalIgnoreCase));
            if (window is not null)
            {
                WindowHelper.Activate(window.Handle);
                return;
            }
        }
        try
        {
            CommandBarApps.Launch(app);
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: launching {app.Name} failed: {ex.Message}");
            CommandBarSystem.Beep();
        }
    }

    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The friendly name of a process ("Google Chrome" for chrome.exe).</summary>
    public static string AppName(string path, string fallback)
    {
        if (string.IsNullOrEmpty(path)) return fallback;
        lock (AppNames)
        {
            if (AppNames.TryGetValue(path, out var cached)) return cached;
        }
        string name;
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            name = string.IsNullOrEmpty(description) ? Path.GetFileNameWithoutExtension(path) : description;
        }
        catch (Exception)
        {
            name = Path.GetFileNameWithoutExtension(path);
        }
        lock (AppNames) AppNames[path] = name;
        return name;
    }

    public static void ClearCaches()
    {
        lock (AppNames) AppNames.Clear();
    }

    public static List<CommandBarEntry> WindowEntries(IEnumerable<WindowInfo> windows) =>
        windows.Select(window =>
        {
            var title = window.Title.Trim();
            var appName = AppName(window.ProcessPath, window.ProcessName);
            if (title.Length == 0 || string.Equals(title, appName, StringComparison.CurrentCultureIgnoreCase)) return null;
            var handle = window.Handle;
            return new CommandBarEntry
            {
                Id = "window." + handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                Title = title,
                Subtitle = appName.Length == 0 ? CommandBarStrings.KindWindow : appName,
                Keywords = CommandBarStrings.KindWindow + " " + appName,
                IconKind = CommandBarIconKind.Window,
                WindowHandle = handle,
                Glyph = CommandBarGlyphs.Window,
                CountsUsage = false,
                Run = _ => AfterBeat(() => WindowHelper.Activate(handle), 80),
            };
        }).OfType<CommandBarEntry>().ToList();

    /// <summary>One "Quit X" row per app with windows. Quitting closes its windows the way their close button does.</summary>
    public static List<CommandBarEntry> QuitEntries(IEnumerable<WindowInfo> windows) =>
        windows.Where(w => w.ProcessPath.Length > 0).GroupBy(w => w.ProcessPath, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var path = group.Key;
            var name = AppName(path, group.First().ProcessName);
            var handles = group.Select(w => w.Handle).ToList();
            return new CommandBarEntry
            {
                Id = "quit." + Path.GetFileName(path).ToLowerInvariant(),
                Title = string.Format(CommandBarStrings.QuitFormat, name),
                Subtitle = CommandBarStrings.KindApp,
                Keywords = name,
                IconKind = CommandBarIconKind.Shell,
                IconPath = path,
                Glyph = CommandBarGlyphs.QuitApp,
                ConfirmationPrompt = string.Format(CommandBarStrings.QuitConfirmFormat, name),
                Run = _ => AfterBeat(() =>
                {
                    foreach (var h in handles) WindowHelper.Close(h);
                }),
            };
        }).ToList();

    public static List<CommandBarEntry> WindowsSettingsEntries() =>
        CommandBarWindowsSettings.All.Select(page => new CommandBarEntry
        {
            Id = CommandBarWindowsSettings.RowId(page),
            Title = page.Name,
            Subtitle = CommandBarStrings.SourceWindowsSettings,
            Keywords = page.Keywords,
            Glyph = CommandBarGlyphs.WindowsSettings,
            Run = _ => CommandBarSystem.Open(page.Uri),
        }).ToList();

    private static HashSet<int>? _emojiFontMap;

    /// <summary>Whether this PC's emoji font has an outline WPF can draw for the emoji (newer ones may only exist as colour layers).</summary>
    private static bool CanDraw(string character)
    {
        if (_emojiFontMap is null)
        {
            _emojiFontMap = [];
            try
            {
                var typeface = new System.Windows.Media.Typeface("Segoe UI Emoji");
                if (typeface.TryGetGlyphTypeface(out var glyphs)) _emojiFontMap = glyphs.CharacterToGlyphMap.Keys.ToHashSet();
            }
            catch (Exception)
            {
                _emojiFontMap = [];
            }
        }
        if (_emojiFontMap.Count == 0) return true;
        return _emojiFontMap.Contains(char.ConvertToUtf32(character, 0));
    }

    public static List<CommandBarEntry> EmojiEntries(CommandBarEmoji.SkinTone tone, ICommandBarHost host) =>
        CommandBarEmoji.All.Where(e => CanDraw(e.Character)).Select(emoji =>
        {
            var character = CommandBarEmoji.Applying(tone, emoji.Character);
            return new CommandBarEntry
            {
                Id = CommandBarPreferences.EmojiRowId(emoji.Identity),
                // WPF draws emoji without colour and lacks outlines for the newest ones in text runs,
                // so the glyph lives in the icon only.
                Title = emoji.Name,
                Subtitle = CommandBarStrings.KindEmoji,
                Keywords = emoji.Name + " " + emoji.Keywords + " " + CommandBarStrings.KindEmoji,
                IconKind = CommandBarIconKind.Emoji,
                Glyph = character,
                MatchTitle = emoji.Name,
                Run = _ => CommandBarSystem.TypeInto(host.PreviousWindow, character),
            };
        }).ToList();

    /// <summary>Row-specific actions: the other skin tones of an emoji, quitting a running app.</summary>
    public static List<CommandBarRowAction> ExtraActions(CommandBarEntry entry, CommandBarController controller, ICommandBarHost host,
        IReadOnlyDictionary<string, CommandBarApp> appsById)
    {
        var actions = new List<CommandBarRowAction>();
        if (entry.IsActive && appsById.TryGetValue(entry.Id, out var app) && app.ExePath is { } exe)
        {
            actions.Add(new CommandBarRowAction("quitApp", string.Format(CommandBarStrings.QuitFormat, app.Name), CommandBarGlyphs.QuitApp, () =>
            {
                host.Hide();
                AfterBeat(() =>
                {
                    foreach (var w in WindowHelper.GetAltTabWindows().Where(w => string.Equals(w.ProcessPath, exe, StringComparison.OrdinalIgnoreCase)))
                        WindowHelper.Close(w.Handle);
                });
            }));
        }
        if (CommandBarPreferences.EmojiIdentity(entry.Id) is { } identity && CommandBarEmoji.AcceptsSkinTone(identity))
        {
            foreach (var tone in Enum.GetValues<CommandBarEmoji.SkinTone>())
            {
                if (tone == controller.Prefs.SkinTone) continue;
                var character = CommandBarEmoji.Applying(tone, identity);
                actions.Add(new CommandBarRowAction("emojiSkinTone." + CommandBarEmoji.StorageValue(tone), CommandBarSettingsPage.ToneName(tone), "✋", () =>
                {
                    host.Hide();
                    CommandBarSystem.TypeInto(host.PreviousWindow, character);
                }));
            }
        }
        return actions;
    }
}
