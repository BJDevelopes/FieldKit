// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.UI.Panel;

/// <summary>
/// The tray panel (the counterpart of the upstream menu bar popover): brand mark, a row of
/// section tabs, the selected section and the Settings / Quit buttons. Rebuilt on every open.
/// </summary>
public sealed class PanelWindow : Window
{
    public const string LastSectionKey = "panel.lastSection";
    public const double PanelWidth = 352;
    /// <summary>Space kept between the panel and the taskbar / screen edges (DIPs).</summary>
    private const double EdgeGap = 8;

    // One row of equal cells: with every feature installed there are up to 12 tabs, which must
    // shrink to fit the panel instead of overflowing (the style's 36 px is the most a tab takes).
    private readonly System.Windows.Controls.Primitives.UniformGrid _tabs = new() { Rows = 1 };
    private readonly ContentControl _content = new();
    private readonly ContentControl _headerAccessory = new();
    private readonly TextBlock _header = Ui.SectionHeader(string.Empty);
    private readonly ScrollViewer _scroll;
    private DateTime _hiddenAt = DateTime.MinValue;
    // Where the panel was summoned (cursor at open time); kept so resizes re-anchor without jumping.
    private Native.POINT _anchor;

    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public PanelWindow()
    {
        Title = AppInfo.Name;
        // A normal (opaque) window: Windows draws the rounded corners and the shadow, and WPF renders it
        // on the GPU. A transparent layered window with a blur shadow is re-copied by the CPU every
        // frame, which made hovering, scrolling and animations stutter.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.CanResize; // keeps the frame Windows needs for its shadow; no resize borders
        MinWidth = MaxWidth = PanelWidth;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        SetResourceReference(BackgroundProperty, "PanelBackground");
        ShowInTaskbar = false;
        Topmost = true;
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(ForegroundProperty, "TextPrimary");

        var brand = BrandMark(34);
        brand.Margin = new Thickness(0, 14, 0, 12);
        brand.HorizontalAlignment = HorizontalAlignment.Center;

        var tabBar = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
            Child = _tabs,
        };
        tabBar.SetResourceReference(Border.BackgroundProperty, "TabBarBackground");
        tabBar.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        var headerRow = Ui.Split(_header, _headerAccessory);
        headerRow.Margin = new Thickness(0, 16, 0, 8);

        _scroll = new ScrollViewer
        {
            Content = _content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var settings = Ui.Button("Settings", () => SettingsRequested?.Invoke(), glyph: Glyphs.Settings);
        var quit = Ui.Button("Quit", () => QuitRequested?.Invoke(), glyph: Glyphs.Power);
        settings.Padding = quit.Padding = new Thickness(10, 8, 10, 8);
        var footer = Ui.Columns(2, 12, settings, quit);
        footer.Margin = new Thickness(0, 14, 0, 0);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(grid, brand, 0);
        AddRow(grid, tabBar, 1);
        AddRow(grid, headerRow, 2);
        AddRow(grid, _scroll, 3);
        AddRow(grid, footer, 4);

        var root = new Border
        {
            Padding = new Thickness(18, 0, 18, 18),
            Child = grid,
        };
        root.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        Content = root;

        Deactivated += (_, _) => HidePanel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HidePanel();
        };
        SourceInitialized += (_, _) =>
        {
            // Keep the panel out of Alt+Tab.
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            // Windows 11: rounded corners like other flyouts (ignored on Windows 10).
            var round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            ThemeManager.ApplyTitleBar(this);
        };
        // Sections grow as their data arrives (SizeToContent): keep the panel inside the work area.
        SizeChanged += (_, _) =>
        {
            if (IsVisible) PositionNearTray();
        };
        FeatureRuntime.AvailabilityChanged += () =>
        {
            if (IsVisible) Rebuild();
        };
    }

    private static void AddRow(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    /// <summary>Opens the panel near the tray, or closes it if it is open.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HidePanel();
            return;
        }
        // A click on the tray icon deactivates (and hides) the panel just before the click
        // arrives; treat that click as "close", not "reopen".
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 250) return;
        ShowPanel();
    }

    public void ShowPanel(string? sectionId = null)
    {
        if (sectionId is not null) Settings.Set(LastSectionKey, sectionId);
        Native.GetCursorPos(out _anchor);
        // A tray or taskbar-button click anchors at the pointer; a relaunch or the command line
        // (pointer anywhere) opens by the notification area of the main display instead.
        if (!IsOverTaskbar(_anchor)) _anchor = NotificationAreaCorner();
        Rebuild();
        Show();
        PositionNearTray();
        Activate();
        // Opened from another process (a second launch, the command line) Windows may refuse the
        // foreground; a panel that never gets focus would never close on click-away either.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Native.GetForegroundWindow() != hwnd) WindowHelper.Activate(hwnd);
    }

    /// <summary>The panel's drawn content (brand, tabs, section, footer), for the site export.</summary>
    internal FrameworkElement? Surface => Content as FrameworkElement;

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
        _content.Content = null; // unload the section so its timers and subscriptions stop
        _headerAccessory.Content = null;
    }

    private IEnumerable<PanelSectionDef> VisibleSections() =>
        FeatureRuntime.Registry.PanelSections
            .Where(s => s.FeatureGate.Length == 0 || s.FeatureGate.Any(FeatureCatalog.IsAvailable))
            .Where(s => Settings.GetBool($"panel.show.{s.Id}", true))
            .OrderBy(s => Settings.GetInt($"panel.order.{s.Id}", s.Order));

    private void Rebuild()
    {
        var sections = VisibleSections().ToList();
        _tabs.Children.Clear();
        if (sections.Count == 0)
        {
            _header.Text = string.Empty;
            _content.Content = Ui.Caption("Nothing is installed. Open Settings → Features to install features.");
            return;
        }
        var selectedId = Settings.GetString(LastSectionKey, sections[0].Id);
        var selected = sections.FirstOrDefault(s => s.Id == selectedId) ?? sections[0];
        foreach (var section in sections)
        {
            var tab = new RadioButton
            {
                Content = section.Glyph,
                GroupName = "panelTabs",
                ToolTip = section.Title,
                IsChecked = section == selected,
                Margin = new Thickness(1, 0, 1, 0),
                Width = double.NaN,
                MaxWidth = 36,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            tab.SetResourceReference(StyleProperty, "TabButton");
            var s = section;
            tab.Checked += (_, _) => Select(s);
            _tabs.Children.Add(tab);
        }
        Select(selected);
    }

    private void Select(PanelSectionDef section)
    {
        Settings.Set(LastSectionKey, section.Id);
        _header.Text = section.Title.ToUpperInvariant();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _headerAccessory.Content = section.HeaderAccessory?.Invoke();
            _content.Content = section.Build();
            UpdateLayout();
            if (watch.ElapsedMilliseconds > 40) Log.Info($"Perf: panel section {section.Id} took {watch.ElapsedMilliseconds} ms to build");
        }
        catch (Exception ex)
        {
            Log.Error($"Building panel section {section.Id} failed", ex);
            _content.Content = Ui.Caption($"This section failed to load: {ex.Message}");
        }
        _scroll.ScrollToTop();
        Dispatcher.BeginInvoke(PositionNearTray, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Anchors the panel to the taskbar edge nearest the cursor, clamped to the work area.</summary>
    /// <summary>Whether the point is on a taskbar (on its display but outside the work area).</summary>
    private static bool IsOverTaskbar(Native.POINT p)
    {
        var info = WindowHelper.GetMonitorInfo(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST));
        var work = info.rcWork;
        return p.X < work.Left || p.X >= work.Right || p.Y < work.Top || p.Y >= work.Bottom;
    }

    private static Native.POINT NotificationAreaCorner()
    {
        var work = WindowHelper.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY)).rcWork;
        return new Native.POINT(work.Right - 1, work.Bottom - 1);
    }

    private void PositionNearTray()
    {
        if (!IsVisible) return;
        var cursor = _anchor;
        var mon = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        var info = WindowHelper.GetMonitorInfo(mon);
        var work = info.rcWork;
        var bounds = info.rcMonitor;
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? WindowHelper.GetScaleAt(cursor.X, cursor.Y);

        var maxHeightPx = work.Height - 2 * EdgeGap * scale;
        MaxHeight = maxHeightPx / scale;
        var widthPx = ActualWidth * scale;
        var heightPx = Math.Min(ActualHeight * scale, maxHeightPx);

        double x, y;
        if (work.Top > bounds.Top) // taskbar on top
        {
            x = cursor.X - widthPx / 2;
            y = work.Top;
        }
        else if (work.Left > bounds.Left) // taskbar on the left
        {
            x = work.Left;
            y = cursor.Y - heightPx / 2;
        }
        else if (work.Right < bounds.Right) // taskbar on the right
        {
            x = work.Right - widthPx;
            y = cursor.Y - heightPx / 2;
        }
        else // bottom (default)
        {
            x = cursor.X - widthPx / 2;
            y = work.Bottom - heightPx;
        }
        var gap = EdgeGap * scale;
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - widthPx - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - heightPx - gap));
        Left = x / scale;
        Top = y / scale;
    }

    /// <summary>The app's brand mark as a WPF element (four tiles, top-right accented).</summary>
    public static FrameworkElement BrandMark(double size)
    {
        var canvas = new Canvas { Width = size, Height = size };
        var gap = size * 0.1;
        var tile = (size - gap * 3) / 2;
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                var rect = new Rectangle { Width = tile, Height = tile, RadiusX = tile * 0.3, RadiusY = tile * 0.3 };
                rect.SetResourceReference(Shape.FillProperty, row == 0 && col == 1 ? "Accent" : "TextPrimary");
                Canvas.SetLeft(rect, gap + col * (tile + gap));
                Canvas.SetTop(rect, gap + row * (tile + gap));
                canvas.Children.Add(rect);
            }
        }
        return canvas;
    }
}
