// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fieldkit.Core;
using Fieldkit.Features.Input.Buttons;
using Fieldkit.Features.Input.Common;
using Fieldkit.Features.Input.Focus;
using Fieldkit.Features.Input.Scrolling;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Pages;

/// <summary>
/// Settings → Mouse &amp; touchpad (upstream MouseSettings): a legend of every mouse feature with
/// its on/off light, each a click away from its card, then one card per feature.
/// </summary>
internal static class MousePage
{
    public const string BackGlyph = "";
    public const string TouchpadGlyph = "";
    public const string ClickGlyph = "";

    private static readonly string[] LegendOrder =
    [
        "scrollInverter", "scrollHorizontal", "focusFollowsMouse", "smoothScroll", "linearScroll", "mouseAcceleration",
        "mouseNavigation", "mouseButtonShortcuts", "mouseClickDebounce", "middleClick",
    ];

    public static FrameworkElement Build()
    {
        var cards = new Dictionary<string, FrameworkElement>();
        FrameworkElement? Add(string id, FrameworkElement? card)
        {
            if (card is not null) cards[id] = card;
            return card;
        }

        var scroll = FeatureCatalog.IsAvailable("scrollInverter") || FeatureCatalog.IsAvailable("scrollHorizontal") ? ScrollCard() : null;
        if (scroll is not null)
        {
            cards["scrollInverter"] = scroll;
            cards["scrollHorizontal"] = scroll;
        }
        var content = new List<UIElement?>
        {
            null, // legend placeholder
            scroll,
            Add("focusFollowsMouse", Available("focusFollowsMouse") ? FocusCard() : null),
            Add("smoothScroll", Available("smoothScroll") ? SmoothCard() : null),
            Add("linearScroll", Available("linearScroll") ? LinearCard() : null),
            Add("mouseAcceleration", Available("mouseAcceleration") ? AccelerationCard() : null),
            Add("mouseNavigation", Available("mouseNavigation") ? NavigationCard() : null),
            Add("mouseButtonShortcuts", Available("mouseButtonShortcuts") ? MouseButtonsSection.Build() : null),
            Add("mouseClickDebounce", Available("mouseClickDebounce") ? ClickDebounceCard() : null),
            Add("middleClick", Available("middleClick") ? MiddleClickCard() : null),
        };
        if (cards.Count > 0) content[0] = Legend(cards);
        if (cards.Count == 0) content[0] = Ui.Caption("Every mouse feature is uninstalled. Install them again from Features.");
        return Ui.Page("Mouse & touchpad", "Give the wheel, the side buttons and the touchpad new jobs.", content.ToArray());
    }

    private static bool Available(string id) => FeatureCatalog.IsAvailable(id);

    // ------------------------------------------------------------------ legend

    private static bool IsOn(string id) => id switch
    {
        "scrollInverter" => Settings.GetBool("scrollInverter.vertical") || Settings.GetBool("scrollInverter.horizontal"),
        "scrollHorizontal" => Settings.GetBool("scrollHorizontal.enabled"),
        "focusFollowsMouse" => Settings.GetBool("focusFollowsMouse.enabled"),
        "smoothScroll" => Settings.GetBool("smoothScroll.enabled"),
        "linearScroll" => Settings.GetBool("linearScroll.enabled"),
        "mouseAcceleration" => Settings.GetBool("mouseAcceleration.enabled"),
        "mouseNavigation" or "middleClick" => true, // built into Windows
        "mouseButtonShortcuts" => Settings.GetBool("mouseButtonShortcuts.enabled") || Settings.GetBool("mouseButtonShortcuts.spacesEnabled"),
        "mouseClickDebounce" => Settings.GetBool("mouseClickDebounce.enabled"),
        _ => false,
    };

    private static FrameworkElement Legend(Dictionary<string, FrameworkElement> cards)
    {
        var grid = new UniformGrid { Columns = 3 };
        void Render()
        {
            grid.Children.Clear();
            foreach (var id in LegendOrder.Where(cards.ContainsKey))
            {
                var on = IsOn(id);
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "ToggleOff");
                var label = Ui.Text(FeatureCatalog.Get(id).Title, 13, brush: on ? "TextPrimary" : "TextSecondary", wrap: false);
                var chevron = Ui.Icon(Glyphs.ChevronRight, 9, "TextTertiary");
                var row = new Grid { Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                label.Margin = new Thickness(10, 0, 6, 0);
                row.Children.Add(dot);
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                Grid.SetColumn(chevron, 2);
                row.Children.Add(chevron);
                var target = cards[id];
                var hover = new Border { Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), Child = row, Background = System.Windows.Media.Brushes.Transparent };
                hover.MouseEnter += (_, _) => hover.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
                hover.MouseLeave += (_, _) => hover.Background = System.Windows.Media.Brushes.Transparent;
                hover.MouseLeftButtonUp += (_, _) => target.BringIntoView(new Rect(0, 0, target.ActualWidth, Math.Min(target.ActualHeight, 400)));
                grid.Children.Add(hover);
            }
        }
        Render();
        Ui.Track(grid, () => Settings.Observe(Render,
            "scrollInverter.vertical", "scrollInverter.horizontal", "scrollHorizontal.enabled", "focusFollowsMouse.enabled",
            "smoothScroll.enabled", "linearScroll.enabled", "mouseAcceleration.enabled", "mouseButtonShortcuts.enabled",
            "mouseButtonShortcuts.spacesEnabled", "mouseClickDebounce.enabled"));
        return Ui.Card(grid, new Thickness(8, 8, 8, 8));
    }

    // ------------------------------------------------------------------ scrolling

    private static FrameworkElement ScrollCard()
    {
        var inverter = Available("scrollInverter");
        var horizontal = Available("scrollHorizontal");
        return InputUi.Live(() =>
        {
            var sideways = horizontal && Settings.GetBool("scrollHorizontal.enabled");
            var invertOn = inverter && (Settings.GetBool("scrollInverter.vertical") || Settings.GetBool("scrollInverter.horizontal"));
            var rows = new List<UIElement?>();
            if (inverter)
            {
                rows.Add(InputUi.ToggleRow(Glyphs.ArrowsUpDown, "Invert vertical scrolling", null, "scrollInverter.vertical"));
                rows.Add(InputUi.ToggleRow(Glyphs.ArrowsLeftRight, "Invert horizontal scrolling", null, "scrollInverter.horizontal"));
            }
            if (horizontal)
            {
                var block = Ui.Stack(10,
                    InputUi.ToggleRow(Glyphs.ArrowsLeftRight, "Scroll sideways while holding a key",
                        "Hold only the selected modifier to scroll the vertical mouse wheel horizontally. Other key combinations are unchanged.",
                        "scrollHorizontal.enabled"),
                    sideways ? InputUi.Indent(Ui.Stack(8, Ui.Text("Modifier key", 13, FontWeights.Medium),
                        InputUi.ChipChoice("scrollHorizontal.modifier", "shift",
                            ("shift", "Shift", null), ("alt", "Alt", null), ("ctrl", "Ctrl", null), ("win", "Win", null)))) : null);
                rows.Add(block);
            }
            var footer = Ui.Stack(8,
                invertOn ? InputUi.LiveBadge("Inverting mouse scrolling right now",
                    () => FeatureRuntime.Service<ScrollInverterService>()?.IsRunning == true) : null,
                InputUi.Note("The touchpad is untouched: it keeps its own Windows scrolling settings."),
                invertOn || sideways
                    ? InputUi.ExceptionList("scrollInverter", "The wheel keeps the direction Windows gives it in these apps.")
                    : null);
            rows.Add(footer);
            return InputUi.Card("Scrolling", rows.ToArray());
        }, "scrollInverter.vertical", "scrollInverter.horizontal", "scrollHorizontal.enabled");
    }

    private static FrameworkElement FocusCard() => InputUi.Live(() =>
    {
        var on = Settings.GetBool("focusFollowsMouse.enabled");
        var raise = Settings.GetBool("focusFollowsMouse.raise", true);
        return InputUi.Card(null,
            InputUi.ToggleRow(Glyphs.Pointer, "Focus follows mouse", "Focuses the window under the pointer.", "focusFollowsMouse.enabled"),
            on ? InputUi.Indent(Ui.Stack(10,
                InputUi.SliderLine("Hover delay", "focusFollowsMouse.delay", FocusFollowsMouseRules.MinDelayMs, FocusFollowsMouseRules.MaxDelayMs,
                    FocusFollowsMouseRules.DefaultDelayMs, 50, v => $"{v:0} ms"),
                Ui.Check("Only when moving to another display", "focusFollowsMouse.betweenDisplaysOnly", false),
                Ui.Check("Bring the window to the front", "focusFollowsMouse.raise", true),
                raise ? null : Ui.Check("Wait for the pointer to stop", "focusFollowsMouse.waitForStop", true),
                InputUi.ExceptionList("focusFollowsMouse", "Hovering does not change focus or raise a window in these apps."))) : null);
    }, "focusFollowsMouse.enabled", "focusFollowsMouse.raise");

    private static FrameworkElement SmoothCard() => InputUi.Live(() =>
    {
        var on = Settings.GetBool("smoothScroll.enabled");
        FrameworkElement? options = null;
        if (on)
        {
            var more = InputUi.Disclosure("More options", Ui.Stack(8,
                InputUi.SliderLine("Response", "smoothScroll.response", SmoothScrollMath.ResponseMin, SmoothScrollMath.ResponseMax,
                    SmoothScrollMath.DefaultResponse, 5, v => $"{v:0}%"),
                InputUi.SliderLine("Coast", "smoothScroll.coast", SmoothScrollMath.CoastMin, SmoothScrollMath.CoastMax,
                    SmoothScrollMath.DefaultCoast, 5, v => $"{v:0}%")));
            options = InputUi.Indent(Ui.Stack(10,
                InputUi.SliderLine("Scrolling speed", "smoothScroll.step", SmoothScrollMath.StepMin, SmoothScrollMath.StepMax,
                    SmoothScrollMath.DefaultStep, 10, v => $"{v:0}"),
                more,
                InputUi.ExceptionList("smoothScroll", "The wheel keeps its plain steps in these apps, for apps that read it their own way, like 3D and design tools.")));
        }
        return InputUi.Card(null,
            InputUi.ToggleRow(Glyphs.Mouse, "Smooth scrolling",
                "Turns each mouse wheel step into a short, gentle glide. The touchpad is not affected.", "smoothScroll.enabled"),
            options);
    }, "smoothScroll.enabled");

    private static FrameworkElement LinearCard() => InputUi.Live(() =>
    {
        var on = Settings.GetBool("linearScroll.enabled");
        return InputUi.Card(null,
            InputUi.ToggleRow(Glyphs.ArrowsUpDown, "Linear scrolling",
                "Every notch of the mouse wheel scrolls the same distance, no matter how fast it spins. The touchpad is not affected.",
                "linearScroll.enabled"),
            on ? InputUi.Indent(Ui.Stack(10,
                InputUi.SliderLine("Lines per step", "linearScroll.lines", LinearScrollMath.LinesMin, LinearScrollMath.LinesMax,
                    LinearScrollMath.DefaultLines, 1, v => $"{v:0}"),
                InputUi.ExceptionList("linearScroll", "The wheel keeps the pace Windows gives it in these apps, for games and 3D tools that count the notches themselves."))) : null);
    }, "linearScroll.enabled");

    // ------------------------------------------------------------------ others

    private static FrameworkElement AccelerationCard() => InputUi.Card(null,
        InputUi.ToggleRow(Glyphs.Pointer, "Disable mouse acceleration",
            "Turns off Enhance pointer precision so the pointer moves exactly as far as the mouse. Your previous setting returns when this is turned off or Fieldkit quits.",
            "mouseAcceleration.enabled"));

    private static FrameworkElement NavigationCard() => InputUi.Card("Navigation",
        InputUi.Row(BackGlyph, "Use side buttons for Back and Forward",
            "Windows already turns the mouse Back and Forward buttons into navigation in File Explorer, browsers and compatible apps. Give them another job under Mouse button shortcuts below.",
            Ui.Badge("Built into Windows")));

    private static FrameworkElement ClickDebounceCard() => InputUi.Live(() =>
    {
        var on = Settings.GetBool("mouseClickDebounce.enabled");
        return InputUi.Card(null,
            InputUi.ToggleRow(ClickGlyph, "Extra click filter",
                "Ignores rapid extra clicks from worn mouse buttons without slowing normal clicks.", "mouseClickDebounce.enabled"),
            on ? InputUi.Indent(Ui.Stack(6,
                InputUi.Stepper("Filter window", "mouseClickDebounce.windowMs", Debounce.ClickDebounceState.MinWindowMs,
                    Debounce.ClickDebounceState.MaxWindowMs, Debounce.ClickDebounceState.DefaultWindowMs, 1,
                    Debounce.ClickDebounceState.SanitizedWindow),
                InputUi.Note("A repeated click inside this interval is treated as an accidental duplicate."))) : null);
    }, "mouseClickDebounce.enabled");

    private static FrameworkElement MiddleClickCard()
    {
        var open = Ui.Button("Open touchpad settings", () => InputUi.OpenSystemSettings("ms-settings:devices-touchpad"));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        return InputUi.Card("Touchpad middle click",
            InputUi.Row(TouchpadGlyph, "Three-finger click acts as middle click",
                "Precision touchpads do this natively: in Touchpad settings, under Three-finger gestures, set Taps to Middle mouse button. Opening links in a new tab and closing tabs then work like a wheel click.",
                Ui.Badge("Built into Windows")),
            InputUi.Indent(open));
    }
}
