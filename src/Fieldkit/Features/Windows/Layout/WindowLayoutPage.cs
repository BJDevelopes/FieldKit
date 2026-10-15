// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>Settings → Window layout (with the Maximize windows section, like upstream).</summary>
public static class WindowLayoutPage
{
    public static FrameworkElement Build()
    {
        var layout = FeatureCatalog.IsAvailable("windowLayout");
        var maximizer = FeatureCatalog.IsAvailable("windowMaximizer");
        return Ui.Page("Window layout",
            "Arrange windows into screen sections or move and resize them with a mouse or touchpad.",
            maximizer ? MaximizerGroup() : null,
            layout ? DraggingGroup() : null,
            layout ? IgnoredGroup() : null,
            layout ? GapsGroup() : null,
            layout ? ShortcutsGroup() : null,
            layout && FeatureCatalog.IsAvailable("dockClick")
                ? Ui.Group(null, Ui.ToggleRow("Click the taskbar icon to cycle windows",
                    "Click an active app’s taskbar icon to rotate through its windows, like Alt+`.",
                    TaskbarClickService.CycleKey, false))
                : null,
            layout ? ActionGroups() : null);
    }

    // ------------------------------------------------------------------ maximize

    private static FrameworkElement MaximizerGroup()
    {
        var status = WindowsUi.Status(Glyphs.Check, "Maximize override active", "Green");
        status.Visibility = Visibility.Collapsed;
        Ui.Every((FrameworkElement)status, TimeSpan.FromSeconds(1), () =>
            status.Visibility = FeatureRuntime.Service<WindowMaximizerService>()?.IsRunning == true ? Visibility.Visible : Visibility.Collapsed);
        return Ui.Group(null,
            Ui.Stack(8, Ui.ToggleRow("Maximize windows",
                "The maximize button fills the screen as a normal window, so it stays movable and resizable. Maximize again to put it back.",
                WindowMaximizerService.EnabledKey, false),
            status),
            WindowsUi.ChipRow("When to fill instead of maximize", null, WindowMaximizerService.ModeKey, "always",
                [("always", "Always"), ("shift", "Only while Shift is held")]),
            WindowsUi.AppListEditor("Keep the normal maximize in these apps",
                "The maximize button keeps its Windows behavior in these apps, so games, emulators and video players still maximize normally.",
                "Add an app…", "Remove", WindowMaximizerService.ExcludedAppsKey,
                () => WindowMaximizerService.ExcludedApps.Items, WindowMaximizerService.ExcludedApps.Add, WindowMaximizerService.ExcludedApps.Remove));
    }

    // ------------------------------------------------------------------ dragging

    private static FrameworkElement DraggingGroup()
    {
        var snapConflict = Ui.Stack(6,
            WindowsUi.Note(Glyphs.Warning, "Windows Snap is using the same edges. Turn off “Snap windows” in Settings › System › Multitasking so Fieldkit can take over.", "Orange"),
            WindowsUi.VisibleWhen(Ui.Caption("Enabled in Fieldkit. Windows Snap also reacts until it is off."),
                WindowLayoutService.EdgeSnapEnabledKey, false),
            LeftButton(Ui.Button("Open Multitasking settings", () => WindowsUi.OpenUri("ms-settings:multitasking"))));
        Ui.Every(snapConflict, TimeSpan.FromSeconds(3), () =>
            snapConflict.Visibility = WindowLayoutService.IsWindowsSnapOn() ? Visibility.Visible : Visibility.Collapsed);

        var gestureDetails = WindowsUi.VisibleWhen(Ui.Stack(10,
            ModifierPicker(),
            GestureHints(),
            Ui.Caption("The starting point chooses the nearest edge or corner. On a mouse, right-button drag also resizes."),
            Ui.ToggleRow("Bring the dragged window to front", null, WindowLayoutService.GestureRaiseKey, false)),
            WindowLayoutService.GestureEnabledKey, false);

        return Ui.Group("Window dragging",
            Ui.Stack(10,
                Ui.ToggleRow("Snap windows at screen edges",
                    "Turn this on and drag a window title bar to a highlighted area below. Choose what each area does.",
                    WindowLayoutService.EdgeSnapEnabledKey, false),
                WindowsUi.EnabledWhen(ZonePicker(), WindowLayoutService.EdgeSnapEnabledKey, false),
                snapConflict),
            Ui.Stack(10,
                Ui.ToggleRow("Move and resize by dragging",
                    "With a mouse or touchpad, hold the shown modifier keys and drag anywhere inside a window.",
                    WindowLayoutService.GestureEnabledKey, false),
                gestureDetails));
    }

    private static FrameworkElement LeftButton(Button b)
    {
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    private static FrameworkElement ZonePicker()
    {
        var root = Ui.Stack(8);
        void Rebuild()
        {
            root.Children.Clear();
            var disabled = EdgeSnap.DisabledZones(Settings.GetString(WindowLayoutService.EdgeSnapDisabledZonesKey, string.Empty));
            var layout = EdgeSnapLayout.Parse(Settings.GetString(WindowLayoutService.EdgeSnapZoneActionsKey, string.Empty));
            foreach (var zone in Enum.GetValues<EdgeZone>())
            {
                var check = new CheckBox { Content = Ui.Text(zone.Title(), 13), IsChecked = !disabled.Contains(zone), MinWidth = 170 };
                var z = zone;
                check.Click += (_, _) =>
                {
                    var set = EdgeSnap.DisabledZones(Settings.GetString(WindowLayoutService.EdgeSnapDisabledZonesKey, string.Empty));
                    if (check.IsChecked == true) set.Remove(z);
                    else set.Add(z);
                    Settings.Set(WindowLayoutService.EdgeSnapDisabledZonesKey, EdgeSnap.DisabledZonesStorage(set));
                };
                var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                var actions = layout.Actions(zone);
                if (zone.IsHorizontalEdge() || zone.IsVerticalEdge())
                {
                    var parts = new ComboBox { MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
                    for (var n = 1; n <= EdgeSnap.MaximumParts; n++) parts.Items.Add(new ComboBoxItem { Content = n == 1 ? "1 area" : $"{n} areas", Tag = n });
                    parts.SelectedIndex = actions.Count - 1;
                    parts.SelectionChanged += (_, _) =>
                    {
                        var l = EdgeSnapLayout.Parse(Settings.GetString(WindowLayoutService.EdgeSnapZoneActionsKey, string.Empty));
                        l.SetPartCount(z, parts.SelectedIndex + 1);
                        Settings.Set(WindowLayoutService.EdgeSnapZoneActionsKey, l.Storage);
                    };
                    controls.Children.Add(parts);
                }
                for (var i = 0; i < actions.Count; i++)
                {
                    var combo = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
                    foreach (var a in LayoutActions.All.Where(a => a.IsPlacement()))
                        combo.Items.Add(new ComboBoxItem { Content = a.Title(), Tag = a });
                    combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(c => (LayoutAction)c.Tag == actions[i]);
                    var part = i;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is not ComboBoxItem item) return;
                        var l = EdgeSnapLayout.Parse(Settings.GetString(WindowLayoutService.EdgeSnapZoneActionsKey, string.Empty));
                        l.SetAction(z, part, (LayoutAction)item.Tag);
                        Settings.Set(WindowLayoutService.EdgeSnapZoneActionsKey, l.Storage);
                    };
                    controls.Children.Add(combo);
                }
                root.Children.Add(Ui.Split(check, controls));
            }
            var reset = Ui.Button("Reset", () =>
            {
                Settings.Set(WindowLayoutService.EdgeSnapZoneActionsKey, string.Empty);
                Settings.Set(WindowLayoutService.EdgeSnapDisabledZonesKey, string.Empty);
            });
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(reset);
        }
        Rebuild();
        Ui.Track(root, () => Settings.Observe(Rebuild, WindowLayoutService.EdgeSnapZoneActionsKey, WindowLayoutService.EdgeSnapDisabledZonesKey));
        return root;
    }

    private static FrameworkElement ModifierPicker()
    {
        var row = Ui.Row(8);
        void Rebuild()
        {
            row.Children.Clear();
            var mods = DragGestureMath.ParseModifiers(Settings.GetString(WindowLayoutService.GestureModifiersKey, "ctrl+win"));
            foreach (var (flag, label) in new[] { (Modifiers.Ctrl, "Ctrl"), (Modifiers.Alt, "Alt"), (Modifiers.Win, "Win") })
            {
                var on = mods.HasFlag(flag);
                var chip = Ui.Button(label, () =>
                {
                    var next = on ? mods & ~flag : mods | flag;
                    if ((next & DragGestureMath.MoveModifierMask) == Modifiers.None) return; // one key at least
                    Settings.Set(WindowLayoutService.GestureModifiersKey, DragGestureMath.ModifiersStorage(next));
                }, accent: on);
                chip.MinWidth = 64;
                Ui.Append(row, 8, chip);
            }
        }
        Rebuild();
        Ui.Track(row, () => Settings.Observe(Rebuild, WindowLayoutService.GestureModifiersKey));
        return Ui.Split(Ui.Text("Keys to move", 13.5, FontWeights.Medium), row);
    }

    private static FrameworkElement GestureHints()
    {
        var move = Ui.Badge(string.Empty);
        var resize = Ui.Badge(string.Empty);
        void Sync()
        {
            var mods = DragGestureMath.ParseModifiers(Settings.GetString(WindowLayoutService.GestureModifiersKey, "ctrl+win"));
            ((TextBlock)move.Child).Text = DragGestureMath.ModifiersText(mods);
            ((TextBlock)resize.Child).Text = DragGestureMath.ModifiersText(mods | Modifiers.Shift);
        }
        Sync();
        var stack = Ui.Stack(6,
            Ui.Row(8, move, Ui.Text("Drag to move", 13)),
            Ui.Row(8, resize, Ui.Text("Add Shift and drag to resize", 13)));
        Ui.Track(stack, () => Settings.Observe(Sync, WindowLayoutService.GestureModifiersKey));
        return stack;
    }

    // ------------------------------------------------------------------ others

    private static FrameworkElement IgnoredGroup() =>
        Ui.Group("Ignore apps",
            WindowsUi.AppListEditor("Pause in these apps",
                "Window layout does not use mouse or keyboard input while one of these apps is focused.",
                "Add an app…", "Remove", WindowLayoutEngine.IgnoredAppsKey,
                () => WindowLayoutEngine.IgnoredApps.Items, WindowLayoutEngine.IgnoredApps.Add, WindowLayoutEngine.IgnoredApps.Remove));

    private static FrameworkElement GapsGroup()
    {
        IReadOnlyList<(string, string)> presets = LayoutGeometry.GapPresets.Select(v => (v.ToString(), GapTitle(v))).ToList();
        return Ui.Group("Gaps",
            GapRow("Window gap", WindowLayoutEngine.WindowGapKey, presets),
            GapRow("Screen gap", WindowLayoutEngine.ScreenGapKey, presets),
            Ui.Caption("Space between snapped windows, and between windows and the screen edge."));
    }

    private static string GapTitle(int value) => value switch
    {
        0 => "None",
        8 => "Tiny (8 px)",
        16 => "Small (16 px)",
        32 => "Medium (32 px)",
        64 => "Large (64 px)",
        128 => "Extra large (128 px)",
        _ => $"{value} px",
    };

    /// <summary>A dropdown of gap presets bound to an integer setting.</summary>
    private static FrameworkElement GapRow(string title, string key, IReadOnlyList<(string Value, string Label)> presets)
    {
        var combo = new ComboBox { MinWidth = 180 };
        foreach (var (v, l) in presets) combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        var current = Settings.GetInt(key, 0).ToString();
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item) Settings.Set(key, int.Parse((string)item.Tag));
        };
        return Ui.Split(Ui.Text(title, 13.5, FontWeights.Medium), combo);
    }

    private static FrameworkElement ShortcutsGroup() =>
        Ui.Group("Shortcuts",
            Ui.ToggleRow("Shortcuts", "Use global shortcuts to arrange the active window without opening the panel.",
                WindowLayoutService.ShortcutsEnabledKey, true));

    private static FrameworkElement ActionGroups()
    {
        var result = WindowsUi.Status(Glyphs.Info, string.Empty, "TextSecondary");
        var resultText = (TextBlock)((StackPanel)result).Children[1];
        result.Visibility = Visibility.Collapsed;
        Ui.Track(result, () =>
        {
            void OnResult(LayoutResult r)
            {
                result.Visibility = Visibility.Visible;
                resultText.Text = WindowLayoutEngine.ResultText(r);
                var brush = r is LayoutResult.Done or LayoutResult.Restored ? "Green" : "Orange";
                resultText.SetResourceReference(TextBlock.ForegroundProperty, brush);
            }
            WindowLayoutEngine.ResultChanged += OnResult;
            return new ActionDisposable(() => WindowLayoutEngine.ResultChanged -= OnResult);
        });

        var stack = Ui.Stack(20);
        foreach (var (title, actions) in LayoutActions.Groups)
        {
            var rows = new List<UIElement>(actions.Select(a => (UIElement)ActionRow(a)));
            if (actions == LayoutActions.Halves)
            {
                rows.Add(Ui.ToggleRow("Repeat Left or Right to cycle sizes",
                    "Half, then two thirds, then one third on the same display. Off, with more than one display, the repeat moves the window to the next display on that side.",
                    WindowLayoutEngine.SideCycleKey, false));
            }
            if (actions == LayoutActions.Other)
            {
                rows.Insert(2, Ui.SliderRow("Margin per edge", WindowLayoutEngine.MarginKey, 0, LayoutGeometry.MaxMarginPercent,
                    LayoutGeometry.DefaultMarginPercent, v => $"{v:0}%"));
            }
            Ui.Append(stack, 20, Ui.Group(title, rows.ToArray()));
        }
        Ui.Append(stack, 10, result);
        return stack;
    }

    /// <summary>"Try it" on the left, the action's shortcut recorder and Reset on the right.</summary>
    private static FrameworkElement ActionRow(LayoutAction action)
    {
        var tryIt = Ui.Clickable(Ui.Row(10, LayoutIcon.For(action), Ui.Text(action.Title(), 13.5, FontWeights.Medium, wrap: false)),
            () => WindowLayoutEngine.Apply(action), "Apply to the front window");
        tryIt.Padding = new Thickness(4, 2, 8, 2);
        tryIt.HorizontalAlignment = HorizontalAlignment.Left;
        var recorder = new ShortcutRecorder(action.ShortcutKey(), action.DefaultShortcut());
        var reset = Ui.Button("Reset", () => Settings.Set(action.ShortcutKey(), action.DefaultShortcut()));
        void Sync() => reset.IsEnabled = Shortcut.FromSetting(action.ShortcutKey(), action.DefaultShortcut()) != Shortcut.Parse(action.DefaultShortcut());
        Sync();
        Ui.Track(reset, () => Settings.Observe(Sync, action.ShortcutKey()));
        var right = WindowsUi.EnabledWhen(Ui.Row(8, recorder, reset), WindowLayoutService.ShortcutsEnabledKey, true);
        return Ui.Split(tryIt, right);
    }
}
