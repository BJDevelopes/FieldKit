// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;
using Path = System.IO.Path;
using System.Windows.Threading;
using Fieldkit.Features.Island;
using Fieldkit.UI;
using Fieldkit.UI.Panel;
using Fieldkit.UI.Prefs;

namespace Fieldkit.Core;

/// <summary>
/// <c>--export-site &lt;dir&gt;</c>: writes the real Settings pages, panel sections and island states as positioned
/// nodes (text, cards, switches, dropdowns, sliders, buttons, and small pictures for custom drawings) so the
/// project website can redraw them exactly, with working controls. Run it with a scratch data folder: the
/// panel shows live readings, so every exported string goes through <see cref="Scrub"/> first and the whole
/// text is written to <c>texts.txt</c> for review before publishing.
/// </summary>
public static class SiteExport
{
    private const double PhoneWidth = 340;
    private const double PhoneIslandWidth = 360;
    private static string _images = string.Empty;
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.OrdinalIgnoreCase);

    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        _images = Path.Combine(directory, "img");
        if (Directory.Exists(_images)) Directory.Delete(_images, recursive: true);
        Directory.CreateDirectory(_images);
        CollectPrivateStrings();
        var texts = new SortedSet<string>(StringComparer.Ordinal);

        var settings = new SettingsWindow { Left = -20000, Top = -20000, ShowActivated = false, Width = 1080, Height = 760 };
        settings.Show();
        var pages = new List<object>();
        foreach (var page in FeatureRuntime.Registry.SettingsPages.OrderBy(p => p.Category).ThenBy(p => p.Order))
        {
            try
            {
                settings.Navigate(page.Id);
                Pump(TimeSpan.FromMilliseconds(400));
                if (settings.PageContent is not { } root) continue;
                var (w, h, nodes) = (Round(root.ActualWidth), Round(root.ActualHeight), Walk(root, texts));
                // The same page laid out at phone width: WPF reflows it as the app would in a narrow window.
                var width = root.Width;
                root.Width = PhoneWidth;
                root.UpdateLayout();
                Pump(TimeSpan.FromMilliseconds(150));
                var phone = new { w = Round(root.ActualWidth), h = Round(root.ActualHeight), nodes = Walk(root, texts) };
                root.Width = width;
                pages.Add(new
                {
                    id = page.Id, title = page.Title, category = page.Category.ToString(), glyph = page.Glyph, gm = GlyphMask(page.Glyph, IconFont),
                    gate = page.FeatureGate, w, h, nodes, phone,
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Exporting settings page {page.Id} failed", ex);
            }
        }
        settings.Close();

        var panel = new PanelWindow { Left = -20000, Top = -20000, ShowActivated = false };
        var sections = new List<object>();
        foreach (var section in FeatureRuntime.Registry.PanelSections.OrderBy(s => s.Order))
        {
            try
            {
                panel.ShowPanel(section.Id);
                panel.Left = -20000;
                Pump(TimeSpan.FromMilliseconds(1600));
                if (panel.Surface is not { } root) continue;
                sections.Add(new
                {
                    id = section.Id, title = section.Title, glyph = section.Glyph, gate = section.FeatureGate,
                    w = Round(root.ActualWidth), h = Round(root.ActualHeight), nodes = Walk(root, texts),
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Exporting panel section {section.Id} failed", ex);
            }
        }
        panel.HidePanel();
        panel.Close();

        var island = new List<object>();
        foreach (var state in Enum.GetValues<IslandPreviewState>())
        {
            try
            {
                Dictionary<string, object?> Surface(double width)
                {
                    var pill = IslandPreview.BuildPill(state, width);
                    pill.Margin = new Thickness(0);
                    var host = new Grid { Width = pill.Width, Height = pill.Height };
                    host.Children.Add(pill);
                    host.Measure(new Size(host.Width, host.Height));
                    host.Arrange(new Rect(0, 0, host.Width, host.Height));
                    host.UpdateLayout();
                    return new() { ["id"] = state.ToString(), ["w"] = Round(pill.Width), ["h"] = Round(pill.Height), ["r"] = Round(pill.CornerRadius.TopLeft), ["nodes"] = Walk(pill, texts) };
                }
                var expanded = !state.ToString().StartsWith("Collapsed", StringComparison.Ordinal);
                var surface = Surface(560);
                if (expanded) surface["phone"] = Surface(PhoneIslandWidth);
                island.Add(surface);
            }
            catch (Exception ex)
            {
                Log.Error($"Exporting island state {state} failed", ex);
            }
        }

        var features = FeatureCatalog.All.Select(f => new
        {
            id = f.Id, group = f.Group.ToString(), title = f.Title, description = f.Description, glyph = f.Glyph,
            beta = f.Beta, extra = f.Extra, parent = f.Parent,
        });
        var data = new { generated = AppInfo.Version, features, pages, panel = sections, island };
        File.WriteAllText(Path.Combine(directory, "ui.json"), JsonSerializer.Serialize(data));
        File.WriteAllLines(Path.Combine(directory, "texts.txt"), texts);
        Log.Info($"Site export: {pages.Count} pages, {sections.Count} panel sections, {island.Count} island states");
    }

    // ------------------------------------------------------------------ privacy

    /// <summary>Names and addresses of this PC that must never reach the website, with neutral stand-ins.</summary>
    private static void CollectPrivateStrings()
    {
        void Add(string? value, string replacement)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 3) Replacements[value.Trim()] = replacement;
        }
        Add(Environment.UserName, "you");
        Add(Environment.MachineName, "DESKTOP");
        Add(Environment.UserDomainName, "DESKTOP");
        Add(Environment.GetEnvironmentVariable("FIELDKIT_DATA_DIR"), @"C:\Users\you\AppData\Roaming\Fieldkit");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"C:\Users\you");
        Add(@"\OneDrive\", @"\");
        // Device and app names seen on this PC, as generic stand-ins.
        Add("Realtek(R) Audio", "High Definition Audio");
        Add("Realtek Audio Console", "Music");
        Add("tailscaled", "node");
        try
        {
            foreach (var ip in Features.Monitor.Sampling.NetworkReader.LocalAddresses()) Add(ip, "192.168.1.20");
        }
        catch { }
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady)) Add(d.VolumeLabel, "Local Disk");
        }
        catch { }
    }

    private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    // Three or more colon-separated hex groups: an IPv6 address (times like 12:34 have only two).
    private static readonly Regex Ipv6 = new(@"\[?\b[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{0,4}){2,7}\]?", RegexOptions.Compiled);

    private static string Scrub(string text)
    {
        // Longest first, so a full path is replaced before the profile folder inside it.
        foreach (var (secret, replacement) in Replacements.OrderByDescending(r => r.Key.Length))
            text = text.Replace(secret, replacement, StringComparison.OrdinalIgnoreCase);
        text = Ipv4.Replace(text, "192.168.1.20");
        return Ipv6.Replace(text, "[fe80::1]");
    }

    // ------------------------------------------------------------------ the walk

    private static List<Dictionary<string, object?>> Walk(FrameworkElement root, ISet<string> texts)
    {
        var nodes = new List<Dictionary<string, object?>>();
        Visit(root, root, new Rect(0, 0, root.ActualWidth, root.ActualHeight), 1.0, nodes, texts);
        return nodes;
    }

    private static void Visit(DependencyObject obj, FrameworkElement root, Rect clip, double opacity, List<Dictionary<string, object?>> nodes, ISet<string> texts)
    {
        if (obj is not FrameworkElement fe)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) Visit(VisualTreeHelper.GetChild(obj, i), root, clip, opacity, nodes, texts);
            return;
        }
        if (fe.Visibility != Visibility.Visible || fe.ActualWidth <= 0.5 || fe.ActualHeight <= 0.5 || fe.Opacity <= 0.01) return;
        Rect bounds;
        try { bounds = fe.TransformToAncestor(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight)); }
        catch (InvalidOperationException) { return; }
        if (!bounds.IntersectsWith(clip)) return;
        opacity *= fe.Opacity;

        Dictionary<string, object?> Node(string kind)
        {
            var n = new Dictionary<string, object?> { ["k"] = kind, ["x"] = Round(bounds.X), ["y"] = Round(bounds.Y), ["w"] = Round(bounds.Width), ["h"] = Round(bounds.Height) };
            if (opacity < 0.99) n["o"] = Math.Round(opacity, 2);
            nodes.Add(n);
            return n;
        }
        string? Id() => AutomationProperties.GetAutomationId(fe) is { Length: > 0 } id ? id : null;

        switch (fe)
        {
            case TextBlock tb:
            {
                var text = Scrub(tb.Text ?? string.Empty);
                if (text.Length == 0) return;
                var n = Node("t");
                n["s"] = text;
                n["fs"] = Math.Round(tb.FontSize, 1);
                var weight = tb.FontWeight.ToOpenTypeWeight();
                if (weight != 400) n["fw"] = weight;
                n["c"] = Css(tb.Foreground);
                var family = tb.FontFamily?.Source ?? string.Empty;
                if (family.Contains("Icons", StringComparison.OrdinalIgnoreCase) || family.Contains("MDL2", StringComparison.OrdinalIgnoreCase))
                {
                    n["f"] = "icon";
                    n["m"] = GlyphMask(text, tb.FontFamily!);
                }
                else if (family.Contains("Mono", StringComparison.OrdinalIgnoreCase) || family.Contains("Consolas", StringComparison.OrdinalIgnoreCase)) n["f"] = "mono";
                else if (family.Contains("Display", StringComparison.OrdinalIgnoreCase)) n["f"] = "display";
                else if (family.Contains("Emoji", StringComparison.OrdinalIgnoreCase)) n["f"] = "emoji";
                if (tb.TextAlignment != TextAlignment.Left) n["al"] = tb.TextAlignment.ToString().ToLowerInvariant();
                if (tb.TextWrapping != TextWrapping.NoWrap) n["wr"] = 1;
                if (tb.TextTrimming != TextTrimming.None) n["el"] = 1;
                if (n.GetValueOrDefault("f") is not "icon") texts.Add(text);
                if (Id() is { } textId && textId.StartsWith("installed:", StringComparison.Ordinal)) n["id"] = textId;
                return;
            }
            case CheckBox cb when IsToggle(cb):
            {
                var n = Node("sw");
                n["on"] = cb.IsChecked == true;
                n["id"] = Id();
                return;
            }
            case CheckBox cb:
            {
                var n = Node("cb");
                n["on"] = cb.IsChecked == true;
                n["id"] = Id();
                if (cb.Content is UIElement content) Visit(content, root, clip, opacity, nodes, texts);
                else if (cb.Content is string s) n["s"] = Scrub(s);
                return;
            }
            case ComboBox combo:
            {
                var n = Node("sel");
                n["id"] = Id();
                var items = combo.Items.Cast<object>().Select(ItemText).Select(Scrub).ToList();
                n["items"] = items;
                n["i"] = combo.SelectedIndex;
                items.ForEach(i => texts.Add(i));
                return;
            }
            case Slider slider:
            {
                var n = Node("sl");
                n["id"] = Id();
                n["min"] = slider.Minimum;
                n["max"] = slider.Maximum;
                n["v"] = Math.Round(slider.Value, 2);
                n["step"] = slider.TickFrequency;
                return;
            }
            case TextBox box:
            {
                var n = Node("in");
                n["id"] = Id();
                n["s"] = Scrub(box.Text ?? string.Empty);
                if (box.AcceptsReturn) n["ml"] = 1;
                if (Css(box.Background) is { } boxFill) n["bg"] = boxFill;
                n["fs"] = Round(box.FontSize);
                return;
            }
            case ScrollBar:
                return;
            case ButtonBase button:
            {
                var n = Node("btn");
                n["id"] = Id();
                n["style"] = StyleOf(button);
                if (button is ToggleButton { IsChecked: true }) n["on"] = 1;
                // Tabs and segments draw their state on the template's border, not on Background.
                var drawn = button is RadioButton && button.Template?.FindName("Bd", button) is Border bd ? bd.Background : button.Background;
                if (drawn is { } bg && Css(bg) is { } fill) n["bg"] = fill;
                if (button.Content is UIElement content) Visit(content, root, clip, opacity, nodes, texts);
                else if (button.Content is string s && s.Length > 0)
                {
                    // The template draws the string with its own TextBlock: that one carries the real font, size and colour.
                    if (FirstTextBlock(button) is { } label) Visit(label, root, clip, opacity, nodes, texts);
                    else
                    {
                        n["s"] = Scrub(s);
                        texts.Add(s);
                    }
                }
                return;
            }
            case Image:
            case Canvas:
            case Shape when fe is not Rectangle and not Ellipse:
                Picture(fe, Node("img"));
                return;
            case Rectangle rect when Css(rect.Fill) is { } rf:
            {
                var n = Node("r");
                n["bg"] = rf;
                if (rect.RadiusX > 0) n["rad"] = Round(rect.RadiusX);
                return;
            }
            case Ellipse ellipse when Css(ellipse.Fill) is { } ef:
            {
                var n = Node("r");
                n["bg"] = ef;
                n["rad"] = Round(Math.Min(bounds.Width, bounds.Height) / 2);
                return;
            }
            case ScrollViewer sv:
                clip = Rect.Intersect(clip, bounds);
                if (clip.IsEmpty) return;
                break;
        }

        // Elements that draw themselves (graphs, meters, the companion, equalizer bars) become pictures.
        if (VisualTreeHelper.GetChildrenCount(fe) == 0 && fe is not (Panel or Border or Decorator or ContentPresenter or Control)
            && fe.GetType().Namespace?.StartsWith("Fieldkit", StringComparison.Ordinal) == true)
        {
            Picture(fe, Node("img"));
            return;
        }

        // Boxes: cards, pills, tiles, separators.
        var (background, border, thickness, radius) = fe switch
        {
            Border b => (b.Background, b.BorderBrush, b.BorderThickness.Top, b.CornerRadius.TopLeft),
            Panel p => (p.Background, null, 0d, 0d),
            _ => (null, null, 0d, 0d),
        };
        var bgCss = Css(background);
        var bdCss = thickness > 0 ? Css(border) : null;
        if (bgCss is not null || bdCss is not null)
        {
            var n = Node("r");
            if (Id() is { } boxId && boxId.StartsWith("installed:", StringComparison.Ordinal)) n["id"] = boxId;
            if (bgCss is not null) n["bg"] = bgCss;
            if (bdCss is not null)
            {
                n["bd"] = bdCss;
                n["bw"] = Math.Round(thickness, 1);
            }
            if (radius > 0) n["rad"] = Round(Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(fe); i++)
            Visit(VisualTreeHelper.GetChild(fe, i), root, clip, opacity, nodes, texts);
    }

    private static bool IsToggle(CheckBox cb) =>
        cb.Style is { } style && (ReferenceEquals(style, cb.TryFindResource("ToggleSwitch")) || ReferenceEquals(style, cb.TryFindResource("ToggleSwitchSmall")));

    private static TextBlock? FirstTextBlock(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock { Visibility: Visibility.Visible } tb && !string.IsNullOrEmpty(tb.Text)) return tb;
            if (FirstTextBlock(child) is { } found) return found;
        }
        return null;
    }

    private static string StyleOf(ButtonBase b)
    {
        if (b.Style is null) return "default";
        if (ReferenceEquals(b.Style, b.TryFindResource("AccentButton"))) return "accent";
        if (ReferenceEquals(b.Style, b.TryFindResource("GhostButton"))) return "ghost";
        return b is RadioButton ? "nav" : "plain";
    }

    private static string ItemText(object item) => item switch
    {
        ComboBoxItem { Content: string s } => s,
        ComboBoxItem { Content: TextBlock t } => t.Text,
        ComboBoxItem c => c.Content?.ToString() ?? string.Empty,
        string s => s,
        _ => item.ToString() ?? string.Empty,
    };

    /// <summary>A CSS colour or gradient for a WPF brush, or null when it draws nothing.</summary>
    private static string? Css(Brush? brush)
    {
        switch (brush)
        {
            case SolidColorBrush s when s.Color.A > 0 && s.Opacity > 0:
                return Rgba(s.Color, s.Opacity);
            case LinearGradientBrush g when g.GradientStops.Count > 0:
            {
                var angle = Math.Atan2(g.EndPoint.Y - g.StartPoint.Y, g.EndPoint.X - g.StartPoint.X) * 180 / Math.PI + 90;
                var stops = string.Join(", ", g.GradientStops.Select(st => $"{Rgba(st.Color, g.Opacity)} {Math.Round(st.Offset * 100)}%"));
                return $"linear-gradient({Math.Round(angle)}deg, {stops})";
            }
            default:
                return null;
        }
    }

    private static string Rgba(Color c, double opacity) =>
        string.Create(CultureInfo.InvariantCulture, $"rgba({c.R},{c.G},{c.B},{Math.Round(c.A / 255.0 * opacity, 3)})");

    private static double Round(double v) => Math.Round(v, 1);

    /// <summary>Renders an element at 2× into a PNG named by its content (identical pictures are stored once).</summary>
    private static void Picture(FrameworkElement fe, Dictionary<string, object?> node)
    {
        var w = Math.Max(1, (int)Math.Ceiling(fe.ActualWidth * 2));
        var h = Math.Max(1, (int)Math.Ceiling(fe.ActualHeight * 2));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(2, 2));
            dc.DrawRectangle(new VisualBrush(fe) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();
        var name = Convert.ToHexString(SHA1.HashData(bytes))[..16].ToLowerInvariant() + ".png";
        var path = Path.Combine(_images, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        node["src"] = "img/" + name;
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private const double GlyphSize = 64;

    /// <summary>
    /// Windows' icon font is not on phones or Macs, so each glyph is also stored as a white mask the site tints.
    /// The picture keeps the text box the glyph lays out in; w and h are that box per pixel of font size.
    /// </summary>
    private static object? GlyphMask(string glyph, FontFamily family)
    {
        if (string.IsNullOrEmpty(glyph)) return null;
        var name = "g-" + string.Join("-", glyph.Select(c => ((int)c).ToString("x4", CultureInfo.InvariantCulture))) + ".png";
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), GlyphSize, Brushes.White, 1.0);
        var w = Math.Max(1, text.WidthIncludingTrailingWhitespace);
        var h = Math.Max(1, text.Height);
        var path = Path.Combine(_images, name);
        if (!File.Exists(path))
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawText(text, new Point(0, 0));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        return new { src = "img/" + name, w = Math.Round(w / GlyphSize, 3), h = Math.Round(h / GlyphSize, 3) };
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
