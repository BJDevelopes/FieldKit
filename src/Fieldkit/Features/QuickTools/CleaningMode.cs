// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Energy;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.QuickTools;

/// <summary>
/// Upstream CleaningUnlockCounter: Escape pressed 5 times, each within 6 s of the previous one,
/// unlocks. Auto-repeat is ignored; any other key resets the count. Pure and thread-safe enough
/// for the hook thread (one caller).
/// </summary>
public sealed class CleaningUnlockCounter
{
    public const int EscapeKey = 0x1B;
    public const int Threshold = 5;
    public static readonly TimeSpan PressWindow = TimeSpan.FromSeconds(6);

    private long? _lastPressMs;
    private bool _escapeDown;

    public int Progress { get; private set; }

    /// <summary>Feeds one key event. Returns true when the unlock gesture completes.</summary>
    public bool Register(int virtualKey, bool isDown, long nowMs)
    {
        if (virtualKey != EscapeKey)
        {
            Reset();
            return false;
        }
        if (!isDown)
        {
            _escapeDown = false;
            return false;
        }
        if (_escapeDown) return false; // auto-repeat
        _escapeDown = true;
        if (_lastPressMs is { } last && nowMs - last <= PressWindow.TotalMilliseconds) Progress++;
        else Progress = 1;
        _lastPressMs = nowMs;
        if (Progress < Threshold) return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        Progress = 0;
        _lastPressMs = null;
    }
}

/// <summary>
/// Cleaning Mode: swallows every key (and the scroll wheel) behind a black screen on every
/// display, or a small corner indicator, until Escape is pressed 5 times or Unlock is
/// clicked. Pointer movement and clicks keep working so Unlock is always reachable, it fails
/// open when the session is locked or switched, and it unlocks by itself after the chosen
/// maximum time. Ctrl+Alt+Del always works (Windows reserves it).
/// </summary>
public static class CleaningMode
{
    public const string KeepScreenVisibleKey = "cleaningMode.keepScreenVisible";
    public const string MaxMinutesKey = "cleaningMode.maxMinutes";
    public static readonly int[] MaxMinuteChoices = [2, 5, 10, 15, 30];

    private static readonly CleaningUnlockCounter Counter = new();
    private static readonly List<Window> Overlays = [];
    private static readonly List<Action<int>> ProgressViews = [];
    private static IDisposable? _keyboard;
    private static IDisposable? _mouse;
    private static DispatcherTimer? _timeout;
    private static int _progress;

    public static bool IsActive { get; private set; }

    public static void Start()
    {
        if (IsActive) return;
        IsActive = true;
        Counter.Reset();
        _progress = 0;
        PanelHost.Hide();
        BuildOverlays();
        _keyboard = InputHooks.AddKeyboardHandler(OnKey, priority: -1000);
        _mouse = InputHooks.AddMouseHandler(OnMouse, priority: -1000);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        var minutes = MaxMinuteChoices.Contains(Settings.GetInt(MaxMinutesKey, 10)) ? Settings.GetInt(MaxMinutesKey, 10) : 10;
        _timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _timeout.Tick += (_, _) =>
        {
            Stop();
            Notifier.Show("Cleaning Mode", "The keyboard was unlocked after the maximum time.");
        };
        _timeout.Start();
        Log.Info("Cleaning Mode on");
    }

    public static void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        _keyboard?.Dispose();
        _mouse?.Dispose();
        _keyboard = _mouse = null;
        _timeout?.Stop();
        _timeout = null;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        CloseOverlays();
        Counter.Reset();
        Log.Info("Cleaning Mode off");
    }

    // Hook thread: be quick.
    private static bool OnKey(ref KeyEvent e)
    {
        if (!IsActive) return false;
        var unlock = Counter.Register(e.VirtualKey, e.IsDown, Environment.TickCount64);
        var progress = Counter.Progress;
        if (unlock) Ui.OnUi(Stop);
        else if (progress != _progress)
        {
            _progress = progress;
            Ui.OnUi(() => ProgressViews.ForEach(v => v(progress)));
        }
        return true;
    }

    private static bool OnMouse(ref MouseEvent e) =>
        IsActive && e.Action is MouseAction.Wheel or MouseAction.HWheel;

    private static void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect
            or SessionSwitchReason.SessionLogoff)
            Ui.OnUi(Stop); // fail open
    }

    private static void OnDisplaysChanged(object? sender, EventArgs e) => Ui.OnUi(() =>
    {
        if (!IsActive) return;
        CloseOverlays();
        BuildOverlays();
    });

    // ---- Overlays ------------------------------------------------------------------------

    private static void BuildOverlays()
    {
        var compact = Settings.GetBool(KeepScreenVisibleKey, false);
        foreach (var monitor in WindowHelper.GetMonitors())
        {
            var (content, setProgress) = compact ? BuildHud() : BuildBlackout();
            ProgressViews.Add(setProgress);
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = compact ? Brushes.Transparent : Brushes.Black,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Title = "Cleaning Mode",
                Content = content,
                Left = -32000,
                Top = -32000,
                Width = compact ? 380 : 10,
                Height = compact ? 90 : 10,
            };
            var bounds = monitor.Bounds;
            window.SourceInitialized += (_, _) =>
            {
                OverlayPlacement.MakeOverlay(window, clickThrough: false, excludeFromCapture: false);
                if (compact) PlaceTopRight(window, bounds);
                else OverlayPlacement.Cover(window, bounds);
            };
            window.Show();
            Overlays.Add(window);
        }
    }

    private static void PlaceTopRight(Window window, Native.RECT bounds)
    {
        var scale = WindowHelper.GetScaleAt(bounds.Left + 1, bounds.Top + 1);
        var w = (int)(window.Width * scale);
        var h = (int)(window.Height * scale);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, bounds.Right - w - (int)(24 * scale), bounds.Top + (int)(40 * scale), w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private static void CloseOverlays()
    {
        foreach (var w in Overlays) w.Close();
        Overlays.Clear();
        ProgressViews.Clear();
    }

    private static (StackPanel Dots, Action<int> Set) Dots(double size, double spacing, Brush filled, Brush empty)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var dots = new List<Border>();
        for (var i = 0; i < CleaningUnlockCounter.Threshold; i++)
        {
            var dot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Margin = new Thickness(i == 0 ? 0 : spacing, 0, 0, 0), Background = empty };
            dots.Add(dot);
            row.Children.Add(dot);
        }
        return (row, n =>
        {
            for (var i = 0; i < dots.Count; i++) dots[i].Background = i < n ? filled : empty;
        });
    }

    private static (FrameworkElement, Action<int>) BuildBlackout()
    {
        TextBlock T(string text, double size, byte alpha, FontWeight weight) => new()
        {
            Text = text, FontSize = size, FontWeight = weight, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var icon = new TextBlock { Text = Glyphs.Keyboard, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 48, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        var (dots, set) = Dots(12, 11, Brushes.White, new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)));
        var unlock = new Button
        {
            Content = new TextBlock { Text = "Unlock", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black },
            MinWidth = 130,
            Padding = new Thickness(18, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = CapsuleTemplate(Brushes.White),
        };
        unlock.Click += (_, _) => Stop();
        var minutes = Settings.GetInt(MaxMinutesKey, 10);
        var stack = new StackPanel { MaxWidth = 460, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(44) };
        foreach (var (child, gap) in new (UIElement, double)[]
                 {
                     (icon, 0), (T("Keyboard locked for cleaning", 23, 255, FontWeights.SemiBold), 18),
                     (T("Press Escape 5 times to unlock", 15, 191, FontWeights.Normal), 18), (dots, 18), (unlock, 18),
                     (T("Your mouse still works", 12, 128, FontWeights.Normal), 18),
                     (T($"Unlocks by itself after {minutes} minutes", 11, 90, FontWeights.Normal), 6),
                 })
        {
            if (child is FrameworkElement fe) fe.Margin = new Thickness(0, gap, 0, 0);
            stack.Children.Add(child);
        }
        return (stack, set);
    }

    private static (FrameworkElement, Action<int>) BuildHud()
    {
        var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Child = Ui.Icon(Glyphs.Keyboard, 14) };
        icon.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
        var accent = ThemeManager.Brush("Accent");
        var (dots, set) = Dots(6, 4, accent, new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)));
        var text = Ui.Stack(2, Ui.Text("Keyboard locked for cleaning", 13, FontWeights.SemiBold, wrap: false),
            Ui.Row(6, Ui.Text("Press Escape 5 times to unlock", 11, brush: "TextSecondary", wrap: false), dots));
        var unlock = Ui.Button("Unlock", Stop, accent: true);
        unlock.Padding = new Thickness(10, 4, 10, 4);
        var row = Ui.Row(10, icon, text, unlock);
        var hud = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(8),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 6, Opacity = 0.22 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        hud.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        return (hud, set);
    }

    private static ControlTemplate CapsuleTemplate(Brush background)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, background);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    /// <summary>The blackout / indicator content with sample progress, for previews and tests.</summary>
    public static FrameworkElement BuildPreview(bool compact, int progress)
    {
        var (content, set) = compact ? BuildHud() : BuildBlackout();
        set(progress);
        if (compact) return content;
        return new Border { Background = Brushes.Black, Width = 900, Height = 560, Child = content };
    }
}

/// <summary>Ends Cleaning Mode if the feature is uninstalled while it is on.</summary>
public sealed class CleaningModeService : FeatureService
{
    public override string FeatureId => "cleaningMode";
    protected override void OnStart() { }
    protected override void OnStop() => CleaningMode.Stop();
}
