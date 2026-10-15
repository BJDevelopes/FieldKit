// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>
/// The Audio priority block (upstream AudioPriorityDisclosure): independent Output and
/// Microphone lists, each a complete ordered list of every device seen. Position is priority;
/// disconnected devices keep their place under their last-known name.
/// </summary>
internal static class AudioPriorityView
{
    public static FrameworkElement Build(bool showHeader, bool initiallyExpanded)
    {
        var body = Ui.Stack(10);
        void Render()
        {
            body.Children.Clear();
            var outputs = AudioEndpoints.List(EDataFlow.Render);
            var inputs = AudioEndpoints.List(EDataFlow.Capture);
            var all = outputs.Concat(inputs).ToList();
            Ui.Append(body, 10, Ui.Caption("Devices are selected in priority order. When a higher-priority device connects, " +
                "it becomes active. When the active one disconnects, the next available takes over.", 11));
            Ui.Append(body, 10, Subsection(EDataFlow.Render, "Output priority", SoundUi.SpeakerGlyph,
                "Automatically switch to the highest-priority output", outputs, all, "No outputs found"));
            Ui.Append(body, 10, Ui.Divider(2));
            Ui.Append(body, 10, Subsection(EDataFlow.Capture, "Microphone priority", Glyphs.Microphone,
                "Automatically switch to the highest-priority microphone", inputs, all, "No microphones found"));
        }
        Render();
        Ui.Track(body, () => AudioDeviceWatcher.Subscribe(Render));
        Ui.Track(body, () => Settings.Observe(Render, SoundKeys.PriorityOutputIds, SoundKeys.PriorityInputIds));
        if (!showHeader) return body;
        return SoundUi.Disclosure("Audio priority", body, initiallyExpanded, glyph: Glyphs.List, chevronLeading: false);
    }

    private static FrameworkElement Subsection(EDataFlow flow, string title, string glyph, string enableHelp,
        List<AudioDevice> devices, List<AudioDevice> allDevices, string emptyText)
    {
        var toggle = Ui.Toggle(PriorityLists.EnabledKey(flow), true, small: true);
        toggle.ToolTip = enableHelp;
        System.Windows.Automation.AutomationProperties.SetName(toggle, enableHelp);
        var header = Ui.Split(SoundUi.Label(glyph, title), toggle, 4);

        var list = PriorityLists.ForDisplay(flow, devices);
        if (list.Count == 0) return Ui.Stack(5, header, Ui.Caption(emptyText, 11.5));
        var current = devices.FirstOrDefault(d => d.IsDefault)?.Id;
        var rows = Ui.Stack(3);
        for (var i = 0; i < list.Count; i++)
            Ui.Append(rows, 3, Row(flow, list, i, devices.Any(d => d.Id == list[i]), list[i] == current, allDevices));
        return Ui.Stack(5, header, rows);
    }

    private static FrameworkElement Row(EDataFlow flow, List<string> list, int index, bool available, bool isCurrent, List<AudioDevice> devices)
    {
        var id = list[index];
        var number = Ui.Text($"{index + 1}", 11.5, FontWeights.Medium, "TextSecondary", wrap: false);
        number.Width = 16;
        number.TextAlignment = TextAlignment.Center;
        var name = Ui.Text(PriorityLists.DisplayName(id, devices), 12, brush: available ? "TextPrimary" : "TextSecondary", wrap: false);
        name.ToolTip = name.Text;
        var nameLine = Ui.Split(name, isCurrent ? Ui.Text("Current", 10.5, brush: "Accent", wrap: false) : null, 4);
        var text = Ui.Stack(1, nameLine, available ? null : Ui.Text("Unavailable", 10.5, brush: "Orange", wrap: false));

        void Move(int offset) => PriorityLists.Save(flow, SoundLogic.MoveInList(list, id, offset), devices);
        var up = Ui.IconButton(Glyphs.ChevronUp, "Move up", () => Move(-1), 9);
        var down = Ui.IconButton(Glyphs.ChevronDown, "Move down", () => Move(1), 9);
        up.IsEnabled = index > 0;
        down.IsEnabled = index < list.Count - 1;
        up.Padding = down.Padding = new Thickness(4, 3, 4, 3);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.Margin = new Thickness(8, 0, 4, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        var buttons = Ui.Row(0, up, down);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        number.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(number);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        var border = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 4, 3),
            Child = grid,
        };
        border.SetResourceReference(Border.BackgroundProperty, isCurrent ? "AccentSubtle" : "ControlBackground");
        if (isCurrent) border.SetResourceReference(Border.BorderBrushProperty, "Accent");
        else border.BorderBrush = System.Windows.Media.Brushes.Transparent;
        return border;
    }
}

/// <summary>The output switcher preferences, shared by the mixer options and Settings → Audio devices.</summary>
internal static class OutputSwitcherControls
{
    public static FrameworkElement Build()
    {
        var check = Ui.Check("Switch outputs with shortcut", SoundKeys.SwitcherEnabled, false, enabled =>
        {
            // First time on: start the cycle with the output in use.
            if (enabled && SoundKeys.GetList(SoundKeys.SwitcherDevices).Count == 0 &&
                AudioEndpoints.DefaultId(EDataFlow.Render) is { } current)
                SoundKeys.SetList(SoundKeys.SwitcherDevices, [current]);
        });
        var caption = Ui.Caption("Choose outputs and use the shortcut to move to the next available one.", 11);
        caption.Margin = new Thickness(24, 0, 0, 0);
        var details = Ui.Stack(6);
        details.Margin = new Thickness(24, 2, 0, 0);
        void Render()
        {
            details.Children.Clear();
            var enabled = Settings.GetBool(SoundKeys.SwitcherEnabled, false);
            details.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled) return;
            Ui.Append(details, 6, Ui.ShortcutRow("Shortcut", SoundKeys.SwitcherShortcut, SoundKeys.SwitcherDefaultShortcut));
            if (OutputSwitcherService.LastSwitchFailed)
                Ui.Append(details, 6, SoundUi.Message(Glyphs.Warning, "Select at least one available output.", "Orange"));
            Ui.Append(details, 6, Ui.Text("Outputs in cycle", 12, FontWeights.Medium, "TextSecondary"));
            var outputs = AudioEndpoints.List(EDataFlow.Render);
            if (outputs.Count == 0) Ui.Append(details, 6, SoundUi.Message(Glyphs.Mute, "No outputs found"));
            var selected = SoundKeys.GetList(SoundKeys.SwitcherDevices);
            foreach (var device in outputs)
            {
                var box = new CheckBox { Content = Ui.Text(SoundUi.DeviceTitle(device), 12, wrap: false), IsChecked = selected.Contains(device.Id) };
                box.Click += (_, _) =>
                {
                    var next = SoundKeys.GetList(SoundKeys.SwitcherDevices);
                    next.Remove(device.Id);
                    if (box.IsChecked == true) next.Add(device.Id);
                    // Visible order first, then remembered devices that are not connected now.
                    var order = outputs.Select(d => d.Id).ToList();
                    SoundKeys.SetList(SoundKeys.SwitcherDevices, order.Where(next.Contains).Concat(next.Where(id => !order.Contains(id))));
                };
                Ui.Append(details, 4, box);
            }
        }
        Render();
        var stack = Ui.Stack(4, check, caption, details);
        Ui.Track(stack, () => Settings.Observe(Render, SoundKeys.SwitcherEnabled));
        Ui.Track(stack, () => AudioDeviceWatcher.Subscribe(Render));
        return stack;
    }
}
