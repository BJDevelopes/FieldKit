// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>The "Displays" panel section and the per-display rows shared with Settings.</summary>
public static class BrightnessView
{
    /// <summary>Laptop glyph (Segoe Fluent Icons E7F8), checked with --render-glyph-range.</summary>
    public const string LaptopGlyph = "\uE7F8";
    /// <summary>Half-filled circle (E7A1).</summary>
    public const string HalfCircleGlyph = "\uE7A1";

    public static FrameworkElement Build()
    {
        if (!Settings.GetBool(BrightnessService.EnabledKey, false))
        {
            return Ui.Card(Ui.Split(
                Ui.Stack(2, Ui.Text("Control displays", 13, FontWeights.Medium),
                    Ui.Text("Brightness controls for the built-in and external displays, here and in Settings.", 11, brush: "TextSecondary")),
                Ui.Toggle(BrightnessService.EnabledKey, false, _ => PanelHost.Show("brightness"), small: true)));
        }

        var list = new DisplayList(compact: true);
        var stack = Ui.Stack(10, list.Root);
        Ui.Append(stack, 10, Ui.Divider(0));
        Ui.Append(stack, 10, EnergyUi.MiniToggle(null, "Show brightness when adjusting", null, BrightnessService.OsdKey, false));
        if (FeatureCatalog.IsAvailable(ExtraBrightnessService.Id))
        {
            Ui.Append(stack, 10, Ui.Divider(0));
            var extra = EnergyUi.MiniToggle(null, "Extra brightness", null, ExtraBrightnessService.EnabledKey, false);
            extra.ToolTip = ExtraBrightnessCaption;
            Ui.Append(stack, 10, extra);
        }
        Ui.Append(stack, 10, Ui.Divider(0));
        Ui.Append(stack, 10, EnergyUi.Disclosure("Options", () => Ui.Stack(10,
            EnergyUi.MiniToggle(null, "Shortcuts follow the pointer", "The display brightness shortcuts change the display under the pointer.",
                BrightnessService.FollowPointerKey, false),
            EnergyUi.MiniToggle(null, "Shortcuts change every display", "Each press steps all displays together.",
                BrightnessService.AllDisplaysKey, false),
            ShortcutControls(compact: true))));
        return Ui.Card(stack);
    }

    public const string ExtraBrightnessCaption =
        "Uses the display’s HDR headroom to go past the usual brightness of desktop content. Uses more power and the PC can run warm.";

    /// <summary>"Use display brightness shortcuts" + the two recorders (upstream DisplayBrightnessShortcutControls).</summary>
    public static FrameworkElement ShortcutControls(bool compact)
    {
        var recorders = Ui.Stack(8,
            Ui.ShortcutRow("Decrease display brightness", BrightnessService.DecreaseShortcutKey, string.Empty),
            Ui.ShortcutRow("Increase display brightness", BrightnessService.IncreaseShortcutKey, string.Empty));
        bool Enabled() => Settings.GetString(BrightnessService.DecreaseShortcutKey, string.Empty).Length > 0 ||
                          Settings.GetString(BrightnessService.IncreaseShortcutKey, string.Empty).Length > 0;
        var toggle = new CheckBox { IsChecked = Enabled() };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, compact ? "ToggleSwitchSmall" : "ToggleSwitch");
        toggle.Click += (_, _) =>
        {
            var on = toggle.IsChecked == true;
            Settings.Set(BrightnessService.DecreaseShortcutKey, on ? BrightnessService.DefaultDecreaseShortcut : string.Empty);
            Settings.Set(BrightnessService.IncreaseShortcutKey, on ? BrightnessService.DefaultIncreaseShortcut : string.Empty);
        };
        const string caption = "Shortcuts adjust the primary display, or the display under the pointer when pointer following is on.";
        var header = Ui.Split(Ui.Stack(1,
            Ui.Text("Use display brightness shortcuts", compact ? 12.5 : 13.5, compact ? FontWeights.Normal : FontWeights.Medium),
            compact ? Ui.Text(caption, 11, brush: "TextSecondary") : Ui.Caption(caption)), toggle);
        var stack = Ui.Stack(8, header, recorders);
        void Sync()
        {
            toggle.IsChecked = Enabled();
            recorders.Visibility = Enabled() ? Visibility.Visible : Visibility.Collapsed;
        }
        Sync();
        Ui.Track(stack, () => Settings.Observe(Sync, BrightnessService.DecreaseShortcutKey, BrightnessService.IncreaseShortcutKey));
        return stack;
    }

    /// <summary>One row per display, kept in sync with the service.</summary>
    public sealed class DisplayList
    {
        private readonly bool _compact;
        private readonly Dictionary<string, (Slider Slider, TextBlock Percent)> _rows = new(StringComparer.OrdinalIgnoreCase);
        private string _signature = string.Empty;
        private bool _updating;

        public StackPanel Root { get; } = new();

        public DisplayList(bool compact)
        {
            _compact = compact;
            Rebuild();
            Ui.Track(Root, () =>
            {
                var s = BrightnessService.Current;
                if (s is null) return null;
                s.Refresh();
                s.Changed += OnChanged;
                return new ActionDisposable(() => s.Changed -= OnChanged);
            });
        }

        private static string Signature(IEnumerable<DisplayState> displays) =>
            string.Join("|", displays.Select(d => $"{d.Key}:{d.Route}:{d.ExtendedDimming}:{d.ForcedSoftware}"));

        private void OnChanged()
        {
            var displays = BrightnessService.Current?.Displays ?? [];
            if (Signature(displays) != _signature)
            {
                Rebuild();
                return;
            }
            _updating = true;
            foreach (var d in displays)
            {
                if (!_rows.TryGetValue(d.Key, out var row)) continue;
                if (!row.Slider.IsMouseCaptureWithin) row.Slider.Value = d.Level;
                row.Percent.Text = $"{DisplayMath.WholePercent(d.Level)}%";
            }
            _updating = false;
        }

        private void Rebuild()
        {
            Root.Children.Clear();
            _rows.Clear();
            var service = BrightnessService.Current;
            var displays = service?.Displays ?? [];
            _signature = Signature(displays);
            if (displays.Count == 0)
            {
                Root.Children.Add(Ui.Text(service is null ? "Display control is starting…" : "No display found.", 11, brush: "TextSecondary"));
                return;
            }
            foreach (var d in displays)
            {
                Ui.Append(Root, _compact ? 12 : 14, BuildRow(d));
            }
        }

        private FrameworkElement BuildRow(DisplayState d)
        {
            var icon = Ui.Icon(d.Display.IsInternal ? LaptopGlyph : Glyphs.Display, _compact ? 11 : 16, _compact ? "TextSecondary" : "Accent");
            icon.Width = _compact ? 16 : 22;
            var name = Ui.Text(d.Name, _compact ? 12 : 13.5, FontWeights.Medium, wrap: false);
            var percent = Ui.Text($"{DisplayMath.WholePercent(d.Level)}%", _compact ? 11.5 : 13, brush: "TextSecondary", wrap: false);
            percent.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
            percent.MinWidth = 40;
            percent.TextAlignment = TextAlignment.Right;
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = d.Level, SmallChange = 1.0 / 64, LargeChange = 1.0 / 16 };
            System.Windows.Automation.AutomationProperties.SetName(slider, d.Name);
            slider.ValueChanged += (_, e) =>
            {
                percent.Text = $"{DisplayMath.WholePercent(e.NewValue)}%";
                if (_updating) return;
                BrightnessService.Current?.SetLevel(d.Key, e.NewValue);
            };
            _rows[d.Key] = (slider, percent);

            var header = Ui.Split(Ui.Row(4, icon, name), percent);
            var row = Ui.Stack(_compact ? 4 : 6, header, slider);
            if (!_compact && d.SpeakerVolume is { } volume)
            {
                // Monitors with built-in speakers (DDC/CI VCP 0x62).
                var speaker = new Slider { Minimum = 0, Maximum = 1, Value = volume, SmallChange = 1.0 / 50, LargeChange = 0.1 };
                System.Windows.Automation.AutomationProperties.SetName(speaker, d.Name + " speakers");
                var speakerPercent = Ui.Text($"{DisplayMath.WholePercent(volume)}%", 13, brush: "TextSecondary", wrap: false);
                speakerPercent.MinWidth = 40;
                speakerPercent.TextAlignment = TextAlignment.Right;
                speaker.ValueChanged += (_, e) =>
                {
                    speakerPercent.Text = $"{DisplayMath.WholePercent(e.NewValue)}%";
                    if (!_updating) BrightnessService.Current?.SetSpeakerVolume(d.Key, e.NewValue);
                };
                var speakerHeader = Ui.Split(Ui.Row(4, Ui.Icon(Glyphs.Volume, 14, "TextSecondary"), Ui.Text("Speakers", 12.5, brush: "TextSecondary", wrap: false)), speakerPercent);
                Ui.Append(row, 6, Ui.Stack(2, speakerHeader, speaker));
            }
            if (DimmingButtons(d) is { } dimming) Ui.Append(row, 4, dimming);
            if (!_compact && d.Route == BrightnessRoute.Software && !d.ForcedSoftware)
                Ui.Append(row, 2, Ui.Text("This display does not accept brightness commands, so the picture is dimmed instead.", 11, brush: "TextTertiary"));
            return row;
        }

        /// <summary>"Extra dimming" / "Dim the picture" choices for external displays.</summary>
        private FrameworkElement? DimmingButtons(DisplayState d)
        {
            if (d.Display.IsInternal) return null;
            var buttons = new WrapPanel();
            void Add(string text, bool chosen, Action toggle, string tooltip)
            {
                var chip = EnergyUi.Chip(text, chosen, toggle, chosen ? Glyphs.Check : HalfCircleGlyph);
                chip.ToolTip = tooltip;
                buttons.Children.Add(chip);
            }
            if (d.Route == BrightnessRoute.Ddc && (!_compact || d.ExtendedDimming))
            {
                Add("Extra dimming", d.ExtendedDimming, () => BrightnessService.SetExtendedDimming(d.Key, !d.ExtendedDimming),
                    "Dims the picture below the monitor's own minimum in the lowest quarter of the slider.");
            }
            if ((d.Route == BrightnessRoute.Ddc && !_compact) || d.ForcedSoftware)
            {
                Add("Dim the picture", d.ForcedSoftware, () => BrightnessService.SetForcedSoftware(d.Key, !d.ForcedSoftware),
                    "Use when the monitor ignores brightness commands: the slider dims the picture instead.");
            }
            return buttons.Children.Count == 0 ? null : buttons;
        }
    }
}
