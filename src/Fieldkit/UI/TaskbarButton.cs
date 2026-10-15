// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.UI;

/// <summary>
/// A small Fieldkit button docked on the taskbar, just left of the notification area (Windows 11 no
/// longer supports taskbar toolbars, so this is a tiny always-on-top window owned by the taskbar, which
/// keeps it above the taskbar). Click opens the panel, right-click shows the menu. It steps aside when
/// it would cover app buttons, when the taskbar auto-hides or is not at the bottom, and for full-screen
/// apps. The notification-area icon stays as well.
/// </summary>
public sealed class TaskbarButton : Window
{
    public const string EnabledKey = "general.taskbarButton";

    /// <summary>Set while the Dynamic Island sits in the taskbar, in this button's spot: the button steps aside.</summary>
    public static volatile bool IslandInTaskbar;

    private readonly Border _surface;
    private readonly Canvas _mark = new();
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    private IntPtr _hwnd;
    private IntPtr _ownerTaskbar;
    private bool? _lightTaskbar;

    public event Action? Clicked;
    public event Action? OpenSettings;
    public event Action? Quit;

    public TaskbarButton()
    {
        Title = AppInfo.Name + " taskbar button";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 40;
        Height = 40;
        Left = -32000;
        Top = -32000;

        var grid = new Grid();
        grid.Children.Add(_mark);
        _dot.HorizontalAlignment = HorizontalAlignment.Right;
        _dot.VerticalAlignment = VerticalAlignment.Bottom;
        _dot.Margin = new Thickness(0, 0, 7, 9);
        grid.Children.Add(_dot);
        _mark.HorizontalAlignment = HorizontalAlignment.Center;
        _mark.VerticalAlignment = VerticalAlignment.Center;
        _surface = new Border
        {
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(2, 4, 2, 4),
            Background = Brushes.Transparent,
            Child = grid,
            Cursor = Cursors.Hand,
        };
        Content = _surface;

        _surface.MouseEnter += (_, _) => _surface.Background = HoverBrush();
        _surface.MouseLeave += (_, _) => _surface.Background = Brushes.Transparent;
        _surface.MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
        _surface.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
        _timer.Tick += (_, _) => Ui.Guard(Update);
        TrayStatus.Changed += RefreshBadge;
        Settings.Observe(Update, EnabledKey);
    }

    /// <summary>Starts tracking the taskbar (call once after construction).</summary>
    public void Start()
    {
        Show();
        _timer.Start();
        Update();
        RefreshBadge();
    }

    public void Stop()
    {
        _timer.Stop();
        TrayStatus.Changed -= RefreshBadge;
        Close();
    }

    private void Update()
    {
        if (_hwnd == IntPtr.Zero) return;
        var placement = Settings.GetBool(EnabledKey, true) && !IslandInTaskbar ? TaskbarDock.Find() : null;
        if (placement is not { } p || TaskbarDock.ForegroundIsFullScreen(p.Monitor))
        {
            if (IsVisible) Hide();
            return;
        }

        // Owned windows always sit above their owner, so clicks on the taskbar never cover the button.
        if (_ownerTaskbar != p.Taskbar)
        {
            Native.SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, p.Taskbar);
            _ownerTaskbar = p.Taskbar;
        }
        var light = TrayIcon.TaskbarIsLight();
        if (light != _lightTaskbar)
        {
            _lightTaskbar = light;
            DrawMark(light);
        }
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, p.Bounds.Left, p.Bounds.Top, p.Bounds.Width, p.Bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void DrawMark(bool lightTaskbar)
    {
        _mark.Children.Clear();
        const double size = 18;
        _mark.Width = _mark.Height = size;
        var gap = size * 0.12;
        var tile = (size - gap) / 2;
        var fg = new SolidColorBrush(lightTaskbar ? Color.FromRgb(28, 28, 30) : Colors.White);
        var accent = new SolidColorBrush(Color.FromRgb(10, 132, 255));
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                var rect = new Rectangle
                {
                    Width = tile,
                    Height = tile,
                    RadiusX = tile * 0.3,
                    RadiusY = tile * 0.3,
                    Fill = row == 0 && col == 1 ? accent : fg,
                };
                Canvas.SetLeft(rect, col * (tile + gap));
                Canvas.SetTop(rect, row * (tile + gap));
                _mark.Children.Add(rect);
            }
        }
        _dot.Stroke = new SolidColorBrush(lightTaskbar ? Colors.White : Color.FromRgb(28, 28, 30));
        _dot.StrokeThickness = 1.5;
    }

    private Brush HoverBrush() => new SolidColorBrush(_lightTaskbar == true
        ? Color.FromArgb(0x18, 0, 0, 0)
        : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    private void RefreshBadge()
    {
        var badges = TrayStatus.Current;
        ToolTip = badges.Count == 0 ? AppInfo.Name : string.Join(Environment.NewLine, [AppInfo.Name, .. badges.Select(b => b.Text)]);
        if (badges.Count == 0)
        {
            _dot.Visibility = Visibility.Collapsed;
            return;
        }
        var rgb = badges[0].Rgb;
        _dot.Fill = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        _dot.Visibility = Visibility.Visible;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open panel", () => Clicked?.Invoke()));
        menu.Items.Add(MenuItem("Settings…", () => OpenSettings?.Invoke()));
        menu.Items.Add(MenuItem("Hide from the taskbar", () => Settings.Set(EnabledKey, false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem($"Quit {AppInfo.Name}", () => Quit?.Invoke()));
        menu.PlacementTarget = _surface;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Ui.Guard(action);
        return item;
    }

    private const int GWLP_HWNDPARENT = -8;
}

/// <summary>Where the taskbar button goes, in physical pixels.</summary>
public readonly record struct TaskbarPlacement(IntPtr Taskbar, IntPtr Monitor, Native.RECT Bounds);

/// <summary>Finds the free spot left of the notification area on the primary taskbar.</summary>
public static class TaskbarDock
{
    private const uint ABM_GETSTATE = 0x4;
    private const int ABS_AUTOHIDE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>The button's rectangle, or null when it should not show.</summary>
    public static TaskbarPlacement? Find()
    {
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !Native.IsWindowVisible(taskbar)) return null;
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar };
        if (((long)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0) return null;

        Native.GetWindowRect(taskbar, out var bar);
        var monitor = Native.MonitorFromWindow(taskbar, Native.MONITOR_DEFAULTTONEAREST);
        var screen = WindowHelper.GetMonitorInfo(monitor).rcMonitor;
        var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        Native.RECT notify = default;
        var hasNotify = tray != IntPtr.Zero && Native.GetWindowRect(tray, out notify);
        var apps = Native.FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        Native.RECT appsRect = default;
        var hasApps = apps != IntPtr.Zero && Native.GetWindowRect(apps, out appsRect);
        var scale = Native.GetDpiForWindow(taskbar) / 96.0;
        return Place(bar, screen, hasNotify ? notify : null, hasApps ? appsRect : null, scale) is { } rect
            ? new TaskbarPlacement(taskbar, monitor, rect)
            : null;
    }

    /// <summary>
    /// Pure placement rule (unit tested): a square-ish button against the left edge of the notification
    /// area on a bottom taskbar, or null when the taskbar is elsewhere or the space is taken by app buttons.
    /// </summary>
    public static Native.RECT? Place(Native.RECT taskbar, Native.RECT screen, Native.RECT? notify, Native.RECT? appButtons, double scale)
    {
        if (scale <= 0) scale = 1;
        var height = taskbar.Height;
        var atBottom = taskbar.Bottom == screen.Bottom && taskbar.Width >= screen.Width * 0.9 && height < screen.Height / 3;
        if (!atBottom || height <= 0) return null;

        var width = (int)Math.Round(40 * scale);
        var gap = (int)Math.Round(4 * scale);
        var right = notify is { } n && n.Width > 0 ? n.Left - gap : taskbar.Right - (int)Math.Round(320 * scale);
        var left = right - width;
        if (appButtons is { } a && a.Width > 0 && left < a.Right + gap) return null;
        if (left < taskbar.Left) return null;
        return new Native.RECT(left, taskbar.Top, right, taskbar.Bottom);
    }

    /// <summary>Whether the foreground app covers the whole monitor (games, videos, presentations).</summary>
    public static bool ForegroundIsFullScreen(IntPtr monitor)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var cls = WindowHelper.GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != monitor) return false;
        Native.GetWindowRect(fg, out var r);
        var screen = WindowHelper.GetMonitorInfo(monitor).rcMonitor;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }
}
