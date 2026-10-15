// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>The "Keep awake" panel section (upstream KeepAwakeCard).</summary>
public static class KeepAwakeView
{
    private static KeepAwakeService? Service => KeepAwakeService.Current;

    public static FrameworkElement Build()
    {
        var root = Ui.Stack(10);
        var status = Ui.Text(string.Empty, 11.5, brush: "TextSecondary");
        var extendRow = new WrapPanel();
        var mainSwitch = new CheckBox();
        mainSwitch.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        mainSwitch.Click += (_, _) =>
        {
            var s = Service;
            if (s is null) { mainSwitch.IsChecked = false; return; }
            if (mainSwitch.IsChecked == true) s.StartLastPick();
            else s.Stop();
        };
        var statusStack = Ui.Stack(4, status, extendRow);
        var row1 = Ui.Split(statusStack, mainSwitch);

        var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var hint = Ui.Text(string.Empty, 11, brush: "TextTertiary");

        var lidCaption = Ui.Text(string.Empty, 11, brush: "TextSecondary");
        var lidSwitch = new CheckBox { IsChecked = Settings.GetBool(KeepAwakeService.LidClosedKey, false) };
        lidSwitch.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitchSmall");
        lidSwitch.Click += (_, _) =>
        {
            var on = lidSwitch.IsChecked == true;
            if (Service is { } s) s.SetLidPreference(on);
            else Settings.Set(KeepAwakeService.LidClosedKey, on);
        };
        var lidRow = Ui.Split(Ui.Stack(2, Ui.Text("Keep working with the lid closed", 12.5), lidCaption), lidSwitch);

        var card = Ui.Card(Ui.Stack(10,
            row1,
            chips,
            hint,
            EnergyUi.Disclosure("Options", BuildPanelOptions),
            KeepAwakeService.LidPresent ? Ui.Divider(2) : null,
            KeepAwakeService.LidPresent ? lidRow : null));
        root.Children.Add(card);

        void Refresh()
        {
            var s = Service;
            var active = s?.IsActive == true;
            mainSwitch.IsChecked = active;
            status.Text = s?.StatusText() ?? "The PC follows its normal energy rules";

            extendRow.Children.Clear();
            if (s is { IsActive: true, End: not null })
            {
                foreach (var m in new[] { 15, 30, 60 })
                    extendRow.Children.Add(EnergyUi.Chip(KeepAwakeLogic.ExtendLabel(m), false, () => s.Extend(m)));
            }
            extendRow.Visibility = extendRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            BuildChips(chips, s);

            if (KeepAwakeService.BatteryBlockingPercent() is { } pct)
            {
                hint.Text = $"Battery at {pct}%. Plug in or lower the battery limit to start";
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Orange");
            }
            else
            {
                hint.Text = "Click a chip to start. Click it again to stop";
                hint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
            }

            lidSwitch.IsChecked = Settings.GetBool(KeepAwakeService.LidClosedKey, false);
            lidCaption.Text = LidCaption(s);
            lidCaption.SetResourceReference(TextBlock.ForegroundProperty, s?.LidSetupFailed == true ? "Red" : "TextSecondary");
        }

        Refresh();
        Ui.Every(root, TimeSpan.FromSeconds(1), Refresh);
        Ui.Track(root, () =>
        {
            var s = FeatureRuntime.Service<KeepAwakeService>();
            if (s is null) return null;
            s.Changed += Refresh;
            return new ActionDisposable(() => s.Changed -= Refresh);
        });
        Ui.Track(root, () => Settings.Observe(Refresh, KeepAwakeService.LidClosedKey, KeepAwakeService.BatteryLimitKey));
        return root;
    }

    public static string LidCaption(KeepAwakeService? s)
    {
        if (s?.LidSetupFailed == true) return "Couldn’t turn on closed-lid mode. Try again.";
        if (s?.LidOverrideActive == true) return "Sleep fully disabled. Mind the power";
        if (Settings.GetBool(KeepAwakeService.LidClosedKey, false)) return "Applied whenever “Keep awake” is active";
        return "Changes the lid action only while “Keep awake” is active";
    }

    private static void BuildChips(WrapPanel chips, KeepAwakeService? s)
    {
        chips.Children.Clear();
        foreach (var minutes in KeepAwakeLogic.Durations)
        {
            var selected = s is { IsActive: true, Trigger: AwakeTrigger.Manual, IsUntilSession: false } && s.Preset == minutes;
            var text = KeepAwakeLogic.ChipLabel(minutes);
            if (selected && s!.End is { } end) text = KeepAwakeLogic.Countdown(end - DateTime.Now);
            var chip = EnergyUi.Chip(text, selected, () =>
            {
                if (Service is not { } svc) return;
                if (selected) svc.Stop();
                else svc.Activate(minutes);
            }, minWidth: minutes == 0 ? 0 : 40);
            chip.ToolTip = KeepAwakeLogic.LongLabel(minutes);
            chips.Children.Add(chip);
        }
        var untilActive = s is { IsActive: true, IsUntilSession: true, End: not null };
        Border? untilChip = null;
        untilChip = EnergyUi.Chip(untilActive ? s!.End!.Value.ToString("t") : "Until…", untilActive, () =>
        {
            if (Service is not { } svc) return;
            if (untilActive) svc.Stop();
            else ShowUntilPicker(untilChip!, svc);
        }, Glyphs.Clock);
        chips.Children.Add(untilChip);
    }

    /// <summary>The "Until…" popover: hour and minute wheels, a summary and Start.</summary>
    private static void ShowUntilPicker(FrameworkElement anchor, KeepAwakeService service)
    {
        var initial = Settings.Get<DateTime?>(KeepAwakeService.UntilTimeKey, null) ?? DateTime.Now.AddHours(1);
        var hour = initial.Hour;
        var minute = initial.Minute;
        var summary = Ui.Text(string.Empty, 10.5, brush: "TextSecondary");
        var field = Ui.Text(string.Empty, 11.5, FontWeights.SemiBold, wrap: false);

        void Update()
        {
            field.Text = $"{hour:00}:{minute:00}";
            summary.Text = KeepAwakeLogic.UntilSummary(DateTime.Now, KeepAwakeLogic.ResolvedUntil(DateTime.Now, hour, minute));
        }

        FrameworkElement Wheel(Func<int> get, Action<int> set, int modulo, string tooltip)
        {
            var prev = Ui.Text(string.Empty, 12, brush: "TextTertiary");
            var cur = Ui.Text(string.Empty, 22, FontWeights.SemiBold, "Accent");
            var next = Ui.Text(string.Empty, 12, brush: "TextTertiary");
            foreach (var t in new[] { prev, cur, next }) t.HorizontalAlignment = HorizontalAlignment.Center;
            void Sync()
            {
                var v = get();
                prev.Text = ((v - 1 + modulo) % modulo).ToString("00");
                cur.Text = v.ToString("00");
                next.Text = ((v + 1) % modulo).ToString("00");
                Update();
            }
            void Step(int d)
            {
                set(((get() + d) % modulo + modulo) % modulo);
                Sync();
            }
            prev.Cursor = next.Cursor = Cursors.Hand;
            prev.MouseLeftButtonUp += (_, _) => Step(-1);
            next.MouseLeftButtonUp += (_, _) => Step(1);
            var col = Ui.Stack(2, prev, cur, next);
            col.Width = 56;
            col.Background = Brushes.Transparent;
            col.ToolTip = tooltip;
            col.Focusable = true;
            col.MouseWheel += (_, e) => Step(e.Delta > 0 ? -1 : 1);
            col.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Up) { Step(-1); e.Handled = true; }
                else if (e.Key == Key.Down) { Step(1); e.Handled = true; }
            };
            col.MouseLeftButtonDown += (_, _) => col.Focus();
            Sync();
            return col;
        }

        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var start = Ui.Button("Start", () =>
        {
            popup.IsOpen = false;
            service.ActivateUntil(KeepAwakeLogic.ResolvedUntil(DateTime.Now, hour, minute));
        }, accent: true);
        start.IsDefault = true;
        start.HorizontalAlignment = HorizontalAlignment.Stretch;
        var colon = Ui.Text(":", 22, FontWeights.SemiBold, "TextSecondary");
        var wheels = Ui.Row(4, Wheel(() => hour, v => hour = v, 24, "Hour (0–23)"), colon, Wheel(() => minute, v => minute = v, 60, "Minute"));
        wheels.HorizontalAlignment = HorizontalAlignment.Center;
        var body = Ui.Stack(10, Ui.Split(Ui.Text("Until", 11, FontWeights.Bold), field), wheels, Ui.Divider(0), summary, start);
        var border = new Border
        {
            Width = 224,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            Child = body,
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        popup.Child = border;
        popup.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) popup.IsOpen = false;
        };
        Update();
        popup.IsOpen = true;
    }

    private static FrameworkElement BuildPanelOptions() => Ui.Stack(10,
        EnergyUi.MiniToggle(Glyphs.Display, "Allow the display to sleep", null, KeepAwakeService.AllowDisplaySleepKey, false),
        EnergyUi.MiniToggle(Glyphs.Play, $"Keep Awake when {AppInfo.Name} opens", null, KeepAwakeService.AutoStartKey, false),
        EnergyUi.Disclosure("Automation", () => Ui.Stack(10,
                AutomationEditor(compact: true),
                EnergyUi.MiniToggle(Glyphs.Lock, "Pause while the PC is locked", null, KeepAwakeService.PauseWhenLockedKey, false)),
            Glyphs.Bolt, AutomationBadges(), indent: 4),
        JiggleOptions(compact: true));

    public static FrameworkElement JiggleOptions(bool compact)
    {
        var host = Ui.Stack(8);
        var toggle = compact
            ? EnergyUi.MiniToggle(Glyphs.Pointer, "Move pointer slightly", null, KeepAwakeService.JiggleKey, false)
            : Ui.ToggleRow("Move pointer slightly", "During a session, moves the pointer a little at the chosen interval.", KeepAwakeService.JiggleKey, false);
        host.Children.Add(toggle);
        var picker = IntervalPicker();
        picker.Margin = new Thickness(compact ? 26 : 16, 8, 0, 0);
        host.Children.Add(picker);
        void Sync() => picker.Visibility = Settings.GetBool(KeepAwakeService.JiggleKey, false) ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Ui.Track(host, () => Settings.Observe(Sync, KeepAwakeService.JiggleKey));
        return host;
    }

    private static FrameworkElement IntervalPicker() =>
        EnergyUi.IntChoiceRow("Interval", null, KeepAwakeService.JiggleIntervalKey, KeepAwakeLogic.JiggleIntervals, m => $"{m} min",
            KeepAwakeLogic.SanitizedJiggleInterval(Settings.GetInt(KeepAwakeService.JiggleIntervalKey, 5)), compact: true);

    /// <summary>Summary badges for the Automation disclosure: "Off" or one icon per enabled condition.</summary>
    private static FrameworkElement AutomationBadges()
    {
        var host = new ContentControl();
        void Sync()
        {
            var enabled = KeepAwakeService.EnabledConditions();
            var pause = Settings.GetBool(KeepAwakeService.PauseWhenLockedKey, false);
            if (enabled == AwakeCondition.None && !pause)
            {
                host.Content = Ui.Text("Off", 11, brush: "TextTertiary");
                return;
            }
            var row = Ui.Row(4);
            if (enabled.HasFlag(AwakeCondition.ExternalDisplay)) row.Children.Add(Ui.Icon(Glyphs.Display, 11));
            if (enabled.HasFlag(AwakeCondition.Power)) row.Children.Add(Ui.Icon(Glyphs.Plug, 11));
            if (enabled.HasFlag(AwakeCondition.RunningApps)) row.Children.Add(Ui.Icon(Glyphs.Grid, 11));
            if (enabled.HasFlag(AwakeCondition.AgentsWorking)) row.Children.Add(Ui.Icon(Glyphs.Robot, 11));
            if (pause) row.Children.Add(Ui.Icon(Glyphs.Lock, 11));
            host.Content = row;
        }
        Sync();
        Ui.Track(host, () => Settings.ObservePrefix("keepAwake.", Sync));
        return host;
    }

    /// <summary>Condition tiles, Any/All, and the selected apps list (upstream KeepAwakeAutomationEditor).</summary>
    public static FrameworkElement AutomationEditor(bool compact)
    {
        var host = new ContentControl();
        void Build()
        {
            var enabledFlags = new[]
            {
                (Key: KeepAwakeService.AutoExternalDisplayKey, Glyph: Glyphs.Display, Title: "External display"),
                (Key: KeepAwakeService.AutoPowerKey, Glyph: Glyphs.Plug, Title: "Power"),
                (Key: KeepAwakeService.AutoAppsKey, Glyph: Glyphs.Grid, Title: "Applications"),
                (Key: KeepAwakeService.AutoAgentsKey, Glyph: Glyphs.Robot, Title: "AI agents"),
            };
            var tiles = enabledFlags.Select(f =>
            {
                var on = Settings.GetBool(f.Key, false);
                return (UIElement)EnergyUi.SelectTile(f.Glyph, f.Title, on, () => Settings.Set(f.Key, !on), compact ? 62 : 78);
            }).ToArray();
            var stack = Ui.Stack(10);
            var selectedCount = enabledFlags.Count(f => Settings.GetBool(f.Key, false));
            if (!compact)
            {
                stack.Children.Add(Ui.Caption(Settings.GetBool(KeepAwakeService.RequireAllKey, false)
                    ? "Starts only when every selected condition is active."
                    : "Starts when any selected condition is active."));
            }
            Ui.Append(stack, 10, Ui.Columns(compact ? 2 : tiles.Length, 8, tiles));
            if (selectedCount > 1)
            {
                Ui.Append(stack, 10, EnergyUi.Segmented([("any", "Any"), ("all", "All")],
                    Settings.GetBool(KeepAwakeService.RequireAllKey, false) ? "all" : "any",
                    v => Settings.Set(KeepAwakeService.RequireAllKey, v == "all")));
            }
            if (Settings.GetBool(KeepAwakeService.AutoAppsKey, false)) Ui.Append(stack, 10, AppList());
            if (Settings.GetBool(KeepAwakeService.AutoAgentsKey, false))
            {
                Ui.Append(stack, 10, Ui.Caption(KeepAwakeService.AgentTrackingAvailable
                    ? "Stays awake while Claude Code, Codex, OpenCode or Copilot is working, and for two minutes after."
                    : "Follows the AI agents section of the Dynamic Island, which is off. Turn it on in Settings → Dynamic Island.", 12));
            }
            host.Content = stack;
        }
        Build();
        Ui.Track(host, () => Settings.Observe(Build, KeepAwakeService.AutoExternalDisplayKey, KeepAwakeService.AutoPowerKey,
            KeepAwakeService.AutoAppsKey, KeepAwakeService.AutoAgentsKey, KeepAwakeService.AppsKey, KeepAwakeService.RequireAllKey));
        return host;
    }

    private static FrameworkElement AppList()
    {
        var apps = KeepAwakeService.SelectedApps();
        var list = Ui.Stack(6);
        list.Children.Add(Ui.Text("Selected apps", 12, FontWeights.SemiBold));
        list.Children.Add(Ui.Text("Keep Awake starts while any of these apps is open, even in the background.", 11, brush: "TextSecondary"));
        foreach (var app in apps)
        {
            var name = Path.GetFileName(app);
            var row = Ui.Split(Ui.Row(6, Ui.Icon(Glyphs.Grid, 12, "TextSecondary"), Ui.Text(name, 12, wrap: false)),
                Ui.IconButton(Glyphs.Remove, "Remove", () => Settings.Set(KeepAwakeService.AppsKey, apps.Where(a => a != app).ToList()), 11));
            Ui.Append(list, 6, row);
        }
        Button? add = null;
        add = Ui.Button("Add an app…", () => ShowAddAppMenu(add!), glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        Ui.Append(list, 6, add);
        return list;
    }

    private static void ShowAddAppMenu(FrameworkElement anchor)
    {
        var current = KeepAwakeService.SelectedApps();
        var running = WindowHelper.GetAltTabWindows()
            .Select(w => w.ProcessPath)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !current.Any(c => KeepAwakeLogic.ProcessKey(c) == KeepAwakeLogic.ProcessKey(p)))
            .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .ToList();
        var items = running.Select(p => (Path.GetFileNameWithoutExtension(p), (Action)(() => AddApp(p)))).ToList();
        if (items.Count > 0) items.Add(("-", () => { }));
        items.Add(("Browse…", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Add an app" };
            if (dialog.ShowDialog() == true) AddApp(dialog.FileName);
        }));
        EnergyUi.ShowMenu(anchor, items);
    }

    private static void AddApp(string path)
    {
        var list = KeepAwakeService.SelectedApps();
        if (list.Any(a => KeepAwakeLogic.ProcessKey(a) == KeepAwakeLogic.ProcessKey(path))) return;
        list.Add(path);
        Settings.Set(KeepAwakeService.AppsKey, list);
    }
}
