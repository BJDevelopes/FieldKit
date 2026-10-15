// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Features.Input.Debounce;
using Fieldkit.Features.Input.SuperKey;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Pages;

/// <summary>Settings → Keyboard: the super key (upstream SuperKeySettings) and key debounce (KeyboardDebounceSettings).</summary>
internal static class KeyboardPage
{
    private const string ModifiersKey = "superKey.modifiers";

    public static FrameworkElement Build()
    {
        var content = new List<UIElement?>();
        if (FeatureCatalog.IsAvailable("superKey")) content.AddRange(SuperKeySections());
        if (FeatureCatalog.IsAvailable("keyboardDebounce")) content.AddRange(DebounceSections());
        if (content.Count == 0) content.Add(Ui.Caption("Every keyboard feature is uninstalled. Install them again from Features."));
        return Ui.Page("Keyboard", null, content.ToArray());
    }

    // ------------------------------------------------------------------ super key

    private static IEnumerable<UIElement?> SuperKeySections()
    {
        yield return InputUi.Live(() =>
        {
            var enabled = Settings.GetBool("superKey.enabled");
            var source = SuperKeyMath.ParseSource(Settings.GetString("superKey.source", "capsLock"));
            var service = FeatureRuntime.Service<SuperKeyService>();
            UIElement? status = null;
            if (enabled && service?.IsPaused == true)
                status = InputUi.InfoLine(Glyphs.Pause, "Paused while a selected app is open");
            else if (enabled)
                status = InputUi.LiveBadge("Working now", () => FeatureRuntime.Service<SuperKeyService>()?.IsRunning == true);
            return InputUi.Card("Super key",
                Ui.Split(Ui.Text($"Use {SuperKeyMath.Label(source)} as the super key", 13.5, FontWeights.Medium), Ui.Toggle("superKey.enabled", false)),
                Ui.ChoiceRow("Key to hold", "superKey.source",
                    SuperKeyMath.Sources.Select(s => (SuperKeyMath.StorageValue(s), SuperKeyMath.Label(s))).ToList(), "capsLock"),
                Ui.Stack(6,
                    Ui.Caption("Hold it and press any key. Choose one or more modifiers below."),
                    InputUi.InfoLine(Glyphs.Info,
                        "Windows reserves Ctrl+Alt+Shift+Win with a few letters (W, T, X, P, D, L, O, N, Y) for Office; leave one modifier out if a shortcut collides.")),
                Ui.Stack(8, Diagram(source, enabled), status));
        }, "superKey.enabled", "superKey.source", ModifiersKey);

        yield return InputUi.Live(() =>
        {
            var enabled = Settings.GetBool("superKey.enabled");
            var current = SuperKeyMath.ParseSolo(Settings.GetString("superKey.soloAction", "none"));
            var rows = new List<UIElement?>();
            foreach (var (action, label) in new[]
            {
                (SuperKeySoloAction.None, "Nothing"),
                (SuperKeySoloAction.CapsLock, "Turn capitals on and off"),
                (SuperKeySoloAction.InputSource, "Switch input language; hold for Caps Lock"),
                (SuperKeySoloAction.Escape, "Press Escape"),
            })
            {
                var radio = new RadioButton
                {
                    GroupName = "superKeySolo",
                    IsChecked = action == current,
                    IsEnabled = enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var value = SuperKeyMath.StorageValue(action);
                radio.Checked += (_, _) => Settings.Set("superKey.soloAction", value);
                var row = Ui.Split(Ui.Text(label, 13.5), radio);
                row.Opacity = enabled ? 1 : 0.5;
                row.Background = System.Windows.Media.Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => { if (enabled) Settings.Set("superKey.soloAction", value); };
                rows.Add(row);
            }
            rows.Add(Ui.Caption("What a quick tap does when no other key is pressed.", 12));
            return InputUi.Card("A tap on its own", rows.ToArray());
        }, "superKey.enabled", "superKey.soloAction");

        yield return InputUi.Live(() => Settings.GetBool("superKey.enabled")
            ? Ui.Card(InputUi.ExceptionList("superKey",
                "While any of these apps is open, even in the background, Super key pauses and the chosen key works normally."),
                new Thickness(16, 14, 16, 14))
            : new Border(), "superKey.enabled");
    }

    /// <summary>The key you hold and the modifiers it stands for; the modifier caps toggle.</summary>
    private static FrameworkElement Diagram(SuperKeySource source, bool enabled)
    {
        var selected = SuperKeyMath.ParseModifiers(Settings.GetString(ModifiersKey, SuperKeyMath.StorageValue(SuperKeyMath.DefaultModifiers)));
        var hold = Ui.Stack(3,
            InputUi.Chip(SuperKeyMath.Label(source), false, null, source == SuperKeySource.CapsLock ? Glyphs.Lock : null, 96),
            Ui.Text("Hold", 10.5, brush: "TextTertiary"));
        foreach (var child in hold.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
        var caps = Ui.Row(5);
        foreach (var (mod, name) in new[] { (Modifiers.Shift, "Shift"), (Modifiers.Ctrl, "Ctrl"), (Modifiers.Alt, "Alt"), (Modifiers.Win, "Win") })
        {
            var on = selected.HasFlag(mod);
            var next = on ? selected & ~mod : selected | mod;
            var canToggle = enabled && SuperKeyMath.HasPrimaryModifier(next);
            var chip = InputUi.Chip(name, on, canToggle ? () => Settings.Set(ModifiersKey, SuperKeyMath.StorageValue(next)) : null, minWidth: 44);
            chip.ToolTip = name;
            Ui.Append(caps, 5, chip);
        }
        caps.Margin = new Thickness(0, 0, 0, 16);
        var arrow = Ui.Icon(Glyphs.ChevronRight, 11, "TextTertiary");
        arrow.Margin = new Thickness(0, 0, 0, 16);
        var row = Ui.Row(10, hold, arrow, caps);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.Opacity = enabled ? 1 : 0.5;
        row.Margin = new Thickness(0, 4, 0, 4);
        return row;
    }

    // ------------------------------------------------------------------ debounce

    private static IEnumerable<UIElement?> DebounceSections()
    {
        yield return InputUi.Live(() =>
        {
            var enabled = Settings.GetBool("keyboardDebounce.enabled");
            return InputUi.Card("Debounce",
                Ui.Split(Ui.Text("Filter duplicate keys", 13.5, FontWeights.Medium), Ui.Toggle("keyboardDebounce.enabled", false)),
                Ui.Stack(6,
                    Ui.Caption("Filters very fast duplicate key presses."),
                    enabled ? InputUi.LiveBadge("Filter active", () => FeatureRuntime.Service<KeyboardDebounceService>()?.IsRunning == true) : null),
                InputUi.Stepper("Global window", "keyboardDebounce.windowMs", KeyDebounceState.MinWindowMs, KeyDebounceState.MaxWindowMs,
                    KeyDebounceState.DefaultWindowMs, 1, KeyDebounceState.SanitizedWindow, enabled));
        }, "keyboardDebounce.enabled");

        yield return InputUi.Live(() =>
        {
            var enabled = Settings.GetBool("keyboardDebounce.enabled");
            var windows = KeyWindowCodec.Decode(Settings.GetString("keyboardDebounce.keyWindows"));

            var picker = new ComboBox { MinWidth = 140 };
            picker.Items.Add(new ComboBoxItem { Content = "Key", Tag = 0 });
            foreach (var vk in KeyWindowCodec.CommonKeys) picker.Items.Add(new ComboBoxItem { Content = KeyWindowCodec.Label(vk), Tag = vk });
            picker.SelectedIndex = 0;
            var pending = KeyDebounceState.DefaultWindowMs;
            var pendingText = Ui.Text($"{pending} ms", 13, FontWeights.Medium, "TextSecondary", wrap: false);
            pendingText.MinWidth = 52;
            pendingText.TextAlignment = TextAlignment.Right;
            void Bump(int d)
            {
                pending = KeyDebounceState.SanitizedWindow(pending + d);
                pendingText.Text = $"{pending} ms";
            }
            var add = Ui.Button("Add key", () =>
            {
                if (picker.SelectedItem is ComboBoxItem { Tag: int vk } && vk > 0) SetWindow(vk, pending);
            });
            var addRow = Ui.Row(8, picker, pendingText, Ui.IconButton(Glyphs.Remove, "Decrease", () => Bump(-1), 12),
                Ui.IconButton(Glyphs.Add, "Increase", () => Bump(1), 12), add);

            var rows = new List<UIElement?> { Ui.Caption("Per-key values override the global window. Use 0 ms to stop filtering a key.", 12), addRow };
            if (windows.Count == 0) rows.Add(Ui.Text("No specific keys configured.", 12, brush: "TextTertiary"));
            foreach (var (vk, ms) in windows.OrderBy(p => KeyWindowCodec.Label(p.Key), StringComparer.Ordinal))
            {
                var key = vk;
                var label = Ui.Text(KeyWindowCodec.Label(key), 12.5, FontWeights.SemiBold, wrap: false);
                label.HorizontalAlignment = HorizontalAlignment.Left;
                label.VerticalAlignment = VerticalAlignment.Center;
                var value = Ui.Text($"{ms} ms", 13, FontWeights.Medium, "TextSecondary", wrap: false);
                value.MinWidth = 52;
                value.TextAlignment = TextAlignment.Right;
                rows.Add(Ui.Split(label, Ui.Row(4, value,
                    Ui.IconButton(Glyphs.Remove, "Decrease", () => SetWindow(key, ms - 1), 12),
                    Ui.IconButton(Glyphs.Add, "Increase", () => SetWindow(key, ms + 1), 12),
                    Ui.IconButton(Glyphs.Delete, "Remove key", () => RemoveWindow(key), 12))));
            }
            var card = InputUi.Card("Specific keys", rows.ToArray());
            card.IsEnabled = enabled;
            card.Opacity = enabled ? 1 : 0.55;
            return card;
        }, "keyboardDebounce.enabled", "keyboardDebounce.keyWindows");
    }

    private static void SetWindow(int vk, int ms)
    {
        var windows = KeyWindowCodec.Decode(Settings.GetString("keyboardDebounce.keyWindows"));
        windows[vk] = KeyDebounceState.SanitizedWindow(ms);
        Settings.Set("keyboardDebounce.keyWindows", KeyWindowCodec.Encode(windows));
    }

    private static void RemoveWindow(int vk)
    {
        var windows = KeyWindowCodec.Decode(Settings.GetString("keyboardDebounce.keyWindows"));
        if (windows.Remove(vk)) Settings.Set("keyboardDebounce.keyWindows", KeyWindowCodec.Encode(windows));
    }
}
