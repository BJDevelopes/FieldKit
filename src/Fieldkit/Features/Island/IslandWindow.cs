// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Island.Interop;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Features.Island.Media;
using Fieldkit.Features.Island.Views;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Island;

/// <summary>
/// The island window: a borderless, topmost, non-activating, per-monitor-DPI-aware window across the top
/// centre of the chosen display. Only the black pill inside it is drawn; the rest is transparent and lets clicks
/// through. Closed, the pill shows the live activity; open (click, or hover when chosen), it grows into the
/// sections with upstream's spring-like motion and takes focus so Esc, shortcuts and text fields work.
/// </summary>
public sealed class IslandWindow : Window
{
    private const double ShadowMargin = 24;
    private const double TopGap = 4;

    private readonly IslandContext _ctx;
    private readonly Border _pill;
    private readonly ContentControl _content = new();
    private readonly Border _stub;
    private readonly ContentControl _stubContent = new();
    private readonly DispatcherTimer _hoverTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _closeTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _noticeTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _environment = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly IslandGestureTracker _gestures = new();
    private readonly MascotReactionGate _mascotGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly IslandNav _nav;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _expanded;
    private bool _openedByHover;
    private bool _hovered;
    private bool _hiddenForFullscreen;
    private IslandNotice? _notice;
    private IslandActivity? _preferredActivity;
    private string? _collapsedSignature;
    private string? _stubSignature;
    private (MascotReaction Reaction, double Until)? _cameo;
    private IntPtr _monitor;
    private TaskbarPlacement? _dock;
    private IntPtr _owner;
    private Thickness _restMargin = new(0, TopGap, 0, 0);
    private Thickness _openMargin = new(0, TopGap, 0, 0);

    public IslandWindow(IslandContext ctx)
    {
        _ctx = ctx;
        Title = "Dynamic Island";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = IslandPalette.Font;
        Foreground = IslandPalette.Text;
        UseLayoutRounding = true;

        _nav = new IslandNav
        {
            Section = Settings.GetString(IslandKeys.LastModule, IslandDefaults.HomeModule),
            Width = IslandLogic.ExpandedWidth(SizeChoice, Settings.GetDouble(IslandKeys.CustomWidth, IslandDefaults.CustomWidth)),
            Budget = IslandLogic.ContentBudget(SizeChoice, Settings.GetDouble(IslandKeys.CustomHeight, IslandDefaults.CustomHeight)),
            Navigate = Navigate,
            Collapse = () => Collapse(),
            Relayout = () => { if (_expanded) Rebuild(animate: true); },
        };

        _pill = new Border
        {
            Background = IslandPalette.Background,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, TopGap, 0, 0),
            Child = _content,
            ClipToBounds = true,
            AllowDrop = true,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0, Direction = 270 },
            Width = IslandCollapsed.RestWidth,
            Height = IslandCollapsed.Height,
            CornerRadius = new CornerRadius(IslandCollapsed.Height / 2),
            Cursor = Cursors.Hand,
        };
        // In the taskbar the closed pill keeps its spot while the island is open, like a running app's button.
        _stub = new Border
        {
            Background = IslandPalette.Background,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = _stubContent,
            ClipToBounds = true,
            Height = IslandCollapsed.Height,
            CornerRadius = new CornerRadius(IslandCollapsed.Height / 2),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
        };
        var root = new Grid { Background = null };
        root.Children.Add(_pill);
        root.Children.Add(_stub);
        Content = root;

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_hovered && !_expanded) Expand(byHover: true);
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (!_hovered && _expanded && _openedByHover) Collapse();
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            if (_hovered && _notice is not null) { _noticeTimer.Interval = TimeSpan.FromSeconds(1); _noticeTimer.Start(); return; }
            _notice = null;
            RefreshCollapsed(force: true);
        };
        _environment.Tick += (_, _) => CheckEnvironment();

        _pill.MouseEnter += (_, _) => OnHover(true);
        _pill.MouseLeave += (_, _) => OnHover(false);
        _pill.MouseLeftButtonUp += OnPillClick;
        _pill.MouseRightButtonUp += OnPillRightClick;
        _stub.MouseEnter += (_, _) => OnHover(true);
        _stub.MouseLeave += (_, _) => OnHover(false);
        _stub.MouseLeftButtonUp += (_, e) => { Collapse(); e.Handled = true; };
        _stub.MouseRightButtonUp += OnPillRightClick;
        _pill.MouseWheel += (_, e) => OnScroll(0, e.Delta, e.Timestamp, precise: e.Delta % Native.WHEEL_DELTA != 0);
        _pill.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            if (!_expanded || _nav.Section != "files") { _nav.Section = "files"; Expand(byHover: true); }
        };
        _pill.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        _pill.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                _ctx.AddToShelf(files);
                _nav.Section = "files";
                Rebuild(animate: true);
            }
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            if (_expanded && !_openedByHover && !IsMenuOpen()) Collapse();
        };
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
    }

    private static string SizeChoice => Settings.GetString(IslandKeys.Size, IslandDefaults.Size);

    public void ShowIsland()
    {
        Show();
        IslandHub.Changed += OnHubChanged;
        IslandHub.NoticePosted += OnNotice;
        IslandHub.MascotEvent += OnMascotEvent;
        IslandHub.OpenRequested += OnOpenRequested;
        IslandHub.ToggleRequested += OnToggleRequested;
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        _environment.Start();
        RefreshCollapsed(force: true);
    }

    public void CloseIsland()
    {
        IslandHub.Changed -= OnHubChanged;
        IslandHub.NoticePosted -= OnNotice;
        IslandHub.MascotEvent -= OnMascotEvent;
        IslandHub.OpenRequested -= OnOpenRequested;
        IslandHub.ToggleRequested -= OnToggleRequested;
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _environment.Stop();
        _hoverTimer.Stop();
        _closeTimer.Stop();
        _noticeTimer.Stop();
        _source?.RemoveHook(WndProc);
        TaskbarButton.IslandInTaskbar = false;
        Close();
    }

    /// <summary>Settings under notch* changed: re-read sizes and rebuild.</summary>
    public void SettingsChanged()
    {
        ApplyCaptureAffinity();
        Reposition();
        if (_expanded) Rebuild(animate: false);
        else RefreshCollapsed(force: true);
    }

    // ------------------------------------------------------------------ window setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        SetNoActivate(true);
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        ApplyCaptureAffinity();
        Reposition();
    }

    private void SetNoActivate(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_TOOLWINDOW;
        ex = on ? ex | Native.WS_EX_NOACTIVATE : ex & ~Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void ApplyCaptureAffinity()
    {
        if (_hwnd == IntPtr.Zero) return;
        var show = Settings.GetBool(IslandKeys.ShowInCaptures, true);
        IslandNative.SetWindowDisplayAffinity(_hwnd, show ? IslandNative.WDA_NONE : IslandNative.WDA_EXCLUDEFROMCAPTURE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == IslandNative.WM_MOUSEACTIVATE && !_expanded)
        {
            handled = true;
            return new IntPtr(IslandNative.MA_NOACTIVATE);
        }
        if (msg == Native.WM_MOUSEHWHEEL)
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            OnScroll(-delta, 0, Environment.TickCount, precise: delta % Native.WHEEL_DELTA != 0);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>The display the island lives on: automatic/main = primary, pointer = under the pointer, else by device name.</summary>
    private (IntPtr Handle, Native.RECT Bounds) TargetMonitor()
    {
        var monitors = WindowHelper.GetMonitors();
        var choice = Settings.GetString(IslandKeys.Display, IslandDefaults.Display);
        if (choice == "pointer" && Native.GetCursorPos(out var p))
        {
            var h = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
            return (h, WindowHelper.GetMonitorInfo(h).rcMonitor);
        }
        if (choice is not ("automatic" or "main"))
        {
            foreach (var m in monitors)
                if (string.Equals(WindowHelper.GetMonitorInfo(m.Handle).szDevice, choice, StringComparison.OrdinalIgnoreCase))
                    return (m.Handle, m.Bounds);
        }
        var primary = monitors.FirstOrDefault(m => m.Primary);
        return primary.Handle == IntPtr.Zero && monitors.Count > 0 ? (monitors[0].Handle, monitors[0].Bounds) : (primary.Handle, primary.Bounds);
    }

    /// <summary>
    /// In the taskbar (the Fieldkit button's spot) unless asked for the top of the screen: Windows has no notch area,
    /// so at the top the island sits on window title bars and browser tabs.
    /// </summary>
    private static bool WantsTaskbar => Settings.GetString(IslandKeys.Placement, "taskbar") == "taskbar";

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        // The taskbar spot needs a bottom taskbar that does not auto-hide; otherwise the island stays at the top.
        _dock = WantsTaskbar ? TaskbarDock.Find() : null;
        TaskbarButton.IslandInTaskbar = _dock is not null;
        var (handle, bounds) = _dock is { } d ? (d.Monitor, WindowHelper.GetMonitorInfo(d.Monitor).rcMonitor) : TargetMonitor();
        _monitor = handle;
        var scale = Native.GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var widthDip = Math.Max(_nav.Width, 600) + ShadowMargin * 2;
        var heightDip = 46 + _nav.Budget + 240 + ShadowMargin;
        var w = (int)Math.Ceiling(widthDip * scale);
        var h = (int)Math.Ceiling(heightDip * scale);
        if (_dock is { } dock)
        {
            // Bottom-right of the window on the taskbar spot: the pill opens upward and to the left from there.
            var taskbarDip = dock.Bounds.Height / scale;
            _pill.HorizontalAlignment = HorizontalAlignment.Right;
            _pill.VerticalAlignment = VerticalAlignment.Bottom;
            // Closed: centred on the taskbar. Open: standing just above it, like a flyout.
            _restMargin = new Thickness(0, 0, ShadowMargin, Math.Max(2, (taskbarDip - IslandCollapsed.Height) / 2));
            _openMargin = new Thickness(0, 0, ShadowMargin, taskbarDip + 8);
            ApplyPillMargin();
            RefreshStub(force: true);
            SetOwner(dock.Taskbar); // owned windows stay above the taskbar even when it is clicked
            var x = Math.Max(bounds.Left, dock.Bounds.Right + (int)Math.Round(ShadowMargin * scale) - w);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, dock.Bounds.Bottom - h, w, h, Native.SWP_NOACTIVATE);
            return;
        }
        _pill.HorizontalAlignment = HorizontalAlignment.Center;
        _pill.VerticalAlignment = VerticalAlignment.Top;
        _restMargin = _openMargin = new Thickness(0, TopGap, 0, 0);
        ApplyPillMargin();
        RefreshStub(force: true);
        SetOwner(IntPtr.Zero);
        var left = bounds.Left + (bounds.Width - w) / 2;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, bounds.Top, w, h, Native.SWP_NOACTIVATE);
    }

    private void ApplyPillMargin() => _pill.Margin = _expanded ? _openMargin : _restMargin;

    private void SetOwner(IntPtr owner)
    {
        if (_owner == owner) return;
        _owner = owner;
        Native.SetWindowLongPtr(_hwnd, GwlpHwndParent, owner);
    }

    private const int GwlpHwndParent = -8;

    private void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);

    /// <summary>Every second: hide in full screen when asked, follow the pointer's display, stay on top.</summary>
    private void CheckEnvironment()
    {
        if (_hwnd == IntPtr.Zero) return;
        var hide = Settings.GetBool(IslandKeys.HideInFullscreen, true) && FullscreenOnIslandDisplay();
        if (hide != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = hide;
            if (hide) { Collapse(); _notice = null; }
            _pill.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!_expanded && !_hovered && (WantsTaskbar || _dock is not null))
        {
            // Follow the taskbar: it can move, resize, start auto-hiding, or the notification area can grow.
            var dock = WantsTaskbar ? TaskbarDock.Find() : null;
            if (dock != _dock) Reposition(); // record equality: taskbar, display and spot
        }
        else if (Settings.GetString(IslandKeys.Display, IslandDefaults.Display) == "pointer" && !_expanded && !_hovered)
        {
            var (handle, _) = TargetMonitor();
            if (handle != _monitor) Reposition();
        }
        else if (!_expanded)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private bool FullscreenOnIslandDisplay()
    {
        if (IslandNative.SHQueryUserNotificationState(out var state) == 0 &&
            state is IslandNative.QueryUserNotificationState.RunningD3DFullScreen or IslandNative.QueryUserNotificationState.PresentationMode)
            return true;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || fg == IslandNative.GetShellWindow() || fg == IslandNative.GetDesktopWindow()) return false;
        var cls = WindowHelper.GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != _monitor) return false;
        Native.GetWindowRect(fg, out var r);
        var m = WindowHelper.GetMonitorInfo(_monitor).rcMonitor;
        var style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom && (style & Native.WS_CAPTION) != Native.WS_CAPTION;
    }

    // ------------------------------------------------------------------ input

    private void OnHover(bool inside)
    {
        _hovered = inside;
        if (inside)
        {
            _closeTimer.Stop();
            if (!_expanded && Settings.GetString(IslandKeys.Opening, IslandDefaults.Opening) == "hover")
            {
                _hoverTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.GetDouble(IslandKeys.HoverDelay, IslandDefaults.HoverDelay), 0.1, 1.0));
                _hoverTimer.Start();
            }
            if (!_expanded) RefreshCollapsed(force: false);
        }
        else
        {
            _hoverTimer.Stop();
            _gestures.Reset();
            if (_expanded && _openedByHover)
            {
                _closeTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.GetDouble(IslandKeys.CloseDelay, IslandDefaults.CloseDelay), 0.1, 2.0));
                _closeTimer.Start();
            }
            if (!_expanded) RefreshCollapsed(force: false);
        }
    }

    private void OnPillClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (_expanded)
        {
            // A click on the open island's empty area pins it open like a click-open.
            if (_openedByHover) { _openedByHover = false; TakeFocus(); }
            return;
        }
        if (_notice is { } n)
        {
            _nav.Section = n.OpenSection ?? IslandLogic.NoticeSection(n.Event);
            _notice = null;
        }
        else if (Settings.GetBool(IslandKeys.OpensActivity, true) && CurrentState().Activity is { } a)
        {
            _nav.Section = IslandLogic.ActivitySection(a);
        }
        else if (Settings.GetBool(IslandKeys.ReturnHome, IslandDefaults.ReturnHome))
        {
            _nav.Section = Settings.GetString(IslandKeys.HomeModule, IslandDefaults.HomeModule);
        }
        Expand(byHover: false);
        e.Handled = true;
    }

    /// <summary>Right-click on the closed island (or its taskbar spot while open): choose which live activity it shows (upstream's picker).</summary>
    private void OnPillRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_expanded && !ReferenceEquals(sender, _stub)) return;
        var activities = new List<IslandActivity>();
        var state = CurrentState();
        foreach (var a in Enum.GetValues<IslandActivity>())
        {
            var s = IslandCollapsed.State(_ctx, null, a, false);
            if (s.Activity == a) activities.Add(a);
        }
        var menu = new ContextMenu();
        if (activities.Count >= 2)
        {
            foreach (var a in activities)
            {
                var item = new MenuItem { Header = IslandLogic.Section(IslandLogic.ActivitySection(a))?.Title ?? a.ToString(), IsChecked = state.Activity == a };
                item.Click += (_, _) => { _preferredActivity = a; RefreshCollapsed(force: true); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }
        // The switch between the two spots, one right-click away (at the top the island can cover browser tabs).
        var inTaskbar = WantsTaskbar;
        menu.Items.Add(MenuItem(inTaskbar ? "Move to the top of the screen" : "Move to the taskbar",
            () => Settings.Set(IslandKeys.Placement, inTaskbar ? "top" : "taskbar")));
        menu.Items.Add(MenuItem("Open Fieldkit panel", () => PanelHost.Show()));
        menu.Items.Add(MenuItem("Dynamic Island settings…", () => PanelHost.OpenSettings("island")));
        menu.PlacementTarget = sender as UIElement ?? _pill;
        menu.Placement = _dock is not null ? System.Windows.Controls.Primitives.PlacementMode.Top : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Ui.Guard(action);
        return item;
    }

    private void OnScroll(double dx, double dy, int timestamp, bool precise)
    {
        if (_ctx.GesturesOn() is false || Shortcut.CurrentModifiers() != Modifiers.None) return;
        // Wheel deltas: a notch (120) counts as 24 points; positive y = towards the user (scroll down).
        var x = dx / Native.WHEEL_DELTA * IslandGestureTracker.WheelNotch;
        var y = -dy / Native.WHEEL_DELTA * IslandGestureTracker.WheelNotch;
        var musicSurface = _expanded ? _nav.Section == "music" && !_nav.LyricsOpen && !_nav.QueueOpen : CurrentState().Activity == IslandActivity.Music;
        var overHeader = _expanded && Mouse.GetPosition(_pill).Y < IslandPages.HeaderHeight + 12;
        var action = _gestures.Feed(x, y, _clock.Elapsed.TotalSeconds, precise, _expanded,
            verticalAllowed: !_expanded || overHeader, horizontalAllowed: musicSurface && !overHeader);
        switch (action)
        {
            case IslandGestureAction.Open: Expand(byHover: true); break;
            case IslandGestureAction.Close: Collapse(); break;
            case IslandGestureAction.NextTrack: _ctx.Next(); Nudge(-1); break;
            case IslandGestureAction.PreviousTrack: _ctx.Previous(); Nudge(1); break;
        }
    }

    private void Nudge(int direction)
    {
        var t = new TranslateTransform();
        _content.RenderTransform = t;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(8 * direction, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.09)), new CubicEase()));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.34)), new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));
        t.BeginAnimation(TranslateTransform.XProperty, a);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_expanded) return;
        if (e.Key == Key.Escape)
        {
            if (_nav.LyricsOpen || _nav.QueueOpen) { _nav.LyricsOpen = _nav.QueueOpen = false; Rebuild(animate: true); }
            else Collapse();
            e.Handled = true;
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            var letter = key.ToString();
            if (VisibleSections().FirstOrDefault(s => s.Hotkey == letter) is { } s) { Navigate(s.Id); e.Handled = true; }
        }
        else if (mods.HasFlag(ModifierKeys.Control) && key == Key.Tab)
        {
            var sections = VisibleSections();
            var i = sections.FindIndex(s => s.Id == _nav.Section);
            var next = (i + (mods.HasFlag(ModifierKeys.Shift) ? -1 : 1) + sections.Count) % sections.Count;
            Navigate(sections[next].Id);
            e.Handled = true;
        }
    }

    private static bool IsMenuOpen() => Keyboard.FocusedElement is MenuItem || Mouse.Captured is ContextMenu || Mouse.Captured is MenuItem;

    // ------------------------------------------------------------------ hub events

    private void OnHubChanged() => RefreshCollapsed(force: false);

    private void OnNotice(IslandNotice notice)
    {
        if (_hiddenForFullscreen) return;
        if (!IslandLogic.ShouldReplace(_notice?.Event, notice.Event, held: _hovered && _notice is not null)) return;
        _notice = notice;
        _noticeTimer.Stop();
        _noticeTimer.Interval = IslandLogic.Duration(notice.Event);
        _noticeTimer.Start();
        RefreshCollapsed(force: true);
    }

    private void OnMascotEvent(MascotEvent e)
    {
        if (!_ctx.MascotOn() || !Settings.GetBool(IslandKeys.MascotReactions, true)) return;
        if (MascotLogic.ReactionFor(e) is not { } reaction || !_mascotGate.TryPass(reaction, _clock.Elapsed.TotalSeconds)) return;
        if (_expanded) return;
        if (_content.Content is FrameworkElement { Tag: MascotView resting })
        {
            resting.React(reaction);
            return;
        }
        // Over an activity: a short cameo in place of the strip's mark.
        _cameo = (reaction, _clock.Elapsed.TotalSeconds + MascotLogic.Length(reaction) + 0.3);
        RefreshCollapsed(force: true);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MascotLogic.Length(reaction) + 0.35) };
        t.Tick += (_, _) => { t.Stop(); _cameo = null; if (!_expanded) RefreshCollapsed(force: true); };
        t.Start();
    }

    private void OnOpenRequested(string? section)
    {
        if (section is not null) _nav.Section = section;
        if (_expanded) Rebuild(animate: true);
        else Expand(byHover: false);
    }

    private void OnToggleRequested()
    {
        if (_expanded) Collapse();
        else Expand(byHover: false);
    }

    // ------------------------------------------------------------------ states

    private List<IslandSection> VisibleSections() =>
        IslandLogic.VisibleSections(Settings.GetString(IslandKeys.ModuleOrder, string.Empty), Settings.GetString(IslandKeys.HiddenModules, string.Empty),
            id => id switch
            {
                "notchCalendar" => IslandHub.Calendar is not null,
                "notchTimer" => IslandHub.Timer is not null,
                "notchDownloads" => IslandHub.Downloads is not null,
                "notchAgents" => IslandHub.Agents is not null,
                "notchWatch" => IslandHub.Watch is not null,
                _ => FeatureCatalog.IsAvailable(id),
            });

    private CollapsedState CurrentState() => IslandCollapsed.State(_ctx, _notice, _preferredActivity, _hovered);

    private void RefreshCollapsed(bool force)
    {
        if (_expanded)
        {
            RefreshStub(force);
            return;
        }
        var state = CurrentState();
        if (state.Activity is null) _preferredActivity = null;
        var signature = Signature(state);
        if (!force && signature == _collapsedSignature) return;
        _collapsedSignature = signature;
        var content = IslandCollapsed.Build(_ctx, state);
        if (_cameo is { } cameo && state.Activity is not null && content is Grid strip && strip.Children.Count > 0 && strip.Children[0] is FrameworkElement mark)
        {
            var mascot = new MascotView(IslandCollapsed.Height - 8, animate: true) { Margin = mark.Margin };
            mascot.React(cameo.Reaction);
            strip.Children.RemoveAt(0);
            strip.Children.Insert(0, mascot);
        }
        SetContent(content);
        content.Measure(new Size(double.PositiveInfinity, IslandCollapsed.Height));
        var width = Math.Max(content.MinWidth, Math.Min(content.MaxWidth, content.DesiredSize.Width));
        AnimateTo(width, IslandCollapsed.Height, IslandCollapsed.Height / 2, grow: width > _pill.ActualWidth);
        _pill.ToolTip = null;
    }

    /// <summary>The closed pill's copy that stays in the taskbar while the island is open there.</summary>
    private void RefreshStub(bool force)
    {
        if (!_expanded || _dock is null || _hiddenForFullscreen)
        {
            _stub.Visibility = Visibility.Collapsed;
            _stubContent.Content = null;
            _stubSignature = null;
            return;
        }
        var state = CurrentState();
        var signature = Signature(state);
        if (!force && signature == _stubSignature) return;
        _stubSignature = signature;
        var content = IslandCollapsed.Build(_ctx, state);
        _stubContent.Content = content;
        content.Measure(new Size(double.PositiveInfinity, IslandCollapsed.Height));
        _stub.Width = Math.Max(content.MinWidth, Math.Min(content.MaxWidth, content.DesiredSize.Width));
        _stub.Margin = _restMargin;
        _stub.Visibility = Visibility.Visible;
    }

    private string Signature(CollapsedState state)
    {
        var music = _ctx.Music();
        return $"{state.Notice?.GetHashCode()}|{state.Activity}|{state.Idle}|{state.CompanionRests}|{(state.Activity == IslandActivity.Music && _hovered)}|{music?.TrackKey}|{music?.Artwork?.GetHashCode()}|{_cameo?.Reaction}|{(state.Activity == IslandActivity.Watch ? _ctx.Watch()?.Headline : null)}|{(state.Activity == IslandActivity.Downloads ? _ctx.Downloads()?.FirstOrDefault()?.ReceivedBytes : null)}|{state.Idle == IslandIdleContent.Battery && DateTime.Now.Second / 30 == 0}";
    }

    private void SetContent(FrameworkElement content)
    {
        content.Opacity = 0;
        _content.Content = content;
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)) { BeginTime = TimeSpan.FromSeconds(0.04) });
    }

    private void Expand(bool byHover)
    {
        if (_hiddenForFullscreen && byHover) return;
        _openedByHover = byHover;
        _hoverTimer.Stop();
        var first = !_expanded;
        _expanded = true;
        if (!byHover) TakeFocus();
        Rebuild(animate: true, reveal: first);
        if (first) RefreshStub(force: true);
    }

    private void TakeFocus()
    {
        SetNoActivate(false);
        Activate();
        Native.SetForegroundWindow(_hwnd);
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _openedByHover = false;
        _nav.LyricsOpen = _nav.QueueOpen = false;
        ApplyPillMargin();
        RefreshStub(force: false);
        SetNoActivate(true);
        _pill.Cursor = Cursors.Hand;
        ((DropShadowEffect)_pill.Effect).BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.2)));
        _collapsedSignature = null;
        RefreshCollapsed(force: true);
    }

    private void Navigate(string section)
    {
        _nav.Section = section;
        _nav.LyricsOpen = _nav.QueueOpen = false;
        Settings.Set(IslandKeys.LastModule, section);
        Rebuild(animate: true);
    }

    private void Rebuild(bool animate, bool reveal = false)
    {
        ApplyPillMargin();
        var width = IslandLogic.ExpandedWidth(SizeChoice, Settings.GetDouble(IslandKeys.CustomWidth, IslandDefaults.CustomWidth));
        var budget = IslandLogic.ContentBudget(SizeChoice, Settings.GetDouble(IslandKeys.CustomHeight, IslandDefaults.CustomHeight));
        var nav = new IslandNav
        {
            Section = _nav.Section, Width = width, Budget = budget, Navigate = _nav.Navigate, Collapse = _nav.Collapse, Relayout = _nav.Relayout,
            LyricsOpen = _nav.LyricsOpen, QueueOpen = _nav.QueueOpen,
        };
        var content = IslandPages.Build(_ctx, nav, VisibleSections());
        _nav.Section = nav.Section;
        _nav.LyricsOpen = nav.LyricsOpen;
        _nav.QueueOpen = nav.QueueOpen;
        content.Width = width;
        SetContent(content);
        content.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Max(120, content.DesiredSize.Height);
        _pill.Cursor = Cursors.Arrow;
        ((DropShadowEffect)_pill.Effect).BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.45, TimeSpan.FromSeconds(0.3)));
        if (animate) AnimateTo(width, height, Math.Min(28, 0.34 * height), grow: true);
        else { _pill.BeginAnimation(WidthProperty, null); _pill.BeginAnimation(HeightProperty, null); _pill.Width = width; _pill.Height = height; }
        if (reveal) content.RenderTransform = null;
    }

    /// <summary>Upstream springs: grow 0.44 s / 0.38 s with a little bounce, shrink 0.30 s / 0.26 s without.</summary>
    private void AnimateTo(double width, double height, double radius, bool grow)
    {
        IEasingFunction ease = grow ? new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
        var wd = TimeSpan.FromSeconds(grow ? 0.44 : 0.30);
        var hd = TimeSpan.FromSeconds(grow ? 0.38 : 0.26);
        var fromW = double.IsNaN(_pill.Width) ? _pill.ActualWidth : _pill.ActualWidth > 0 ? _pill.ActualWidth : _pill.Width;
        var fromH = _pill.ActualHeight > 0 ? _pill.ActualHeight : _pill.Height;
        if (Math.Abs(fromW - width) < 0.5 && Math.Abs(fromH - height) < 0.5)
        {
            _pill.CornerRadius = new CornerRadius(radius);
            return;
        }
        _pill.BeginAnimation(WidthProperty, new DoubleAnimation(fromW, width, wd) { EasingFunction = ease });
        _pill.BeginAnimation(HeightProperty, new DoubleAnimation(fromH, height, hd) { EasingFunction = ease });
        _pill.CornerRadius = new CornerRadius(radius);
    }
}
