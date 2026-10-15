// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// The floating bar: borderless, topmost, out of Alt+Tab (WS_EX_TOOLWINDOW), centred on the active
/// monitor with its top at 28% of the work area (or where the person dragged it), keyboard-first and
/// closed as soon as it loses focus.
/// </summary>
public sealed class CommandBarWindow : Window
{
    /// <summary>Room around the bar for its shadow.</summary>
    private const double ShadowMargin = 24;

    private readonly CommandBarController _controller;
    private readonly Action _hide;
    private bool _hiding;

    public CommandBarView View { get; }

    public CommandBarWindow(CommandBarController controller, Action hide)
    {
        _controller = controller;
        _hide = hide;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = true;
        Title = CommandBarStrings.PageTitle;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        View = new CommandBarView(controller)
        {
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        Content = new Grid { Margin = new Thickness(ShadowMargin), Children = { View } };

        View.MarkPressed += () =>
        {
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            SaveOffset();
        };
        View.MarkDoubleClicked += () =>
        {
            Settings.Set(CommandBarKeys.Position, string.Empty);
            PlaceOnActiveMonitor(_lastForeground);
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        Deactivated += (_, _) =>
        {
            if (!_hiding && IsVisible) _hide();
        };
        Closing += (_, e) =>
        {
            // Alt+F4 puts the bar away rather than destroying it.
            if (_closingForReal) return;
            e.Cancel = true;
            _hide();
        };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl) _controller.SetControlHeld(false);
        };
    }

    private bool _closingForReal;
    private IntPtr _lastForeground;

    /// <summary>Really closes (the feature stopped).</summary>
    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    public void ShowBar(IntPtr foreground)
    {
        _lastForeground = foreground;
        if (!IsVisible)
        {
            Left = -32000;
            Top = -32000;
            Show();
        }
        UpdateLayout();
        PlaceOnActiveMonitor(foreground);
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Native.GetForegroundWindow() != hwnd) WindowHelper.Activate(hwnd);
        View.Field.Focus();
        Keyboard.Focus(View.Field);
    }

    public void HideBar()
    {
        if (!IsVisible) return;
        _hiding = true;
        try { Hide(); }
        finally { _hiding = false; }
    }

    /// <summary>Where the bar goes: the work area of the monitor holding the foreground window, plus the saved offset.</summary>
    private void PlaceOnActiveMonitor(IntPtr foreground)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.RECT work;
        if (foreground != IntPtr.Zero && Native.IsWindow(foreground)) work = WindowHelper.GetWorkArea(foreground);
        else
        {
            Native.GetCursorPos(out var cursor);
            work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
        }
        var scale = WindowHelper.GetScaleAt(work.Left + work.Width / 2, work.Top + work.Height / 2);
        var offset = CommandBarPreferences.DecodePositionOffset(Settings.GetString(CommandBarKeys.Position, string.Empty));
        // Clamp against the tallest the bar can grow, so it never opens where its list would fall off screen.
        var tallest = (ListCeilingHeight() + 2 * ShadowMargin) * scale;
        var size = new Size((CommandBarView.BarWidth + 2 * ShadowMargin) * scale, tallest);
        var area = new Rect(work.Left, work.Top, work.Width, work.Height);
        var origin = CommandBarPreferences.ClampedOrigin(size, area, offset * scale);
        _defaultOrigin = CommandBarPreferences.ClampedOrigin(size, area, new Vector(0, 0));
        _scale = scale;
        Native.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(origin.X), (int)Math.Round(origin.Y - ShadowMargin * scale), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private Point _defaultOrigin;
    private double _scale = 1;

    private static double ListCeilingHeight() => 60 + 50 + CommandBarView.ListCeiling + 40;

    /// <summary>Keeps how far the bar was dragged from its default spot (an offset means the same thing on every display).</summary>
    private void SaveOffset()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var rect);
        var offset = new Vector((rect.Left - _defaultOrigin.X) / _scale, (rect.Top + ShadowMargin * _scale - _defaultOrigin.Y) / _scale);
        Settings.Set(CommandBarKeys.Position, CommandBarPreferences.EncodePositionOffset(offset));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);

        if (_controller.Mode == CommandBarMode.CapturingShortcut)
        {
            e.Handled = true;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            var m = Modifiers.None;
            if (ctrl) m |= Modifiers.Ctrl;
            if (mods.HasFlag(ModifierKeys.Alt)) m |= Modifiers.Alt;
            if (mods.HasFlag(ModifierKeys.Shift)) m |= Modifiers.Shift;
            if (mods.HasFlag(ModifierKeys.Windows)) m |= Modifiers.Win;
            _controller.HandleCaptureKey(vk, m);
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl)
        {
            _controller.SetControlHeld(true);
            return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.K:
                    _controller.OpenActions();
                    e.Handled = true;
                    return;
                case Key.P when mods.HasFlag(ModifierKeys.Shift):
                    if (_controller.SelectedEntry is { IsAnswer: false } pinEntry) _controller.TogglePin(pinEntry);
                    e.Handled = true;
                    return;
                case Key.P:
                    _controller.MoveSelection(-1);
                    e.Handled = true;
                    return;
                case Key.N:
                    if (!_controller.PeekHome()) _controller.MoveSelection(1);
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    _hide();
                    PanelHost.OpenSettings(CommandBarKeys.FeatureId);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    if (_controller.Mode == CommandBarMode.Search && _controller.SelectedEntry is { RevealPath: { } path })
                    {
                        _hide();
                        CommandBarSystem.Reveal(path);
                        e.Handled = true;
                    }
                    return;
                case >= Key.D1 and <= Key.D9:
                    _controller.RunAt(key - Key.D1);
                    e.Handled = true;
                    return;
                case >= Key.NumPad1 and <= Key.NumPad9:
                    _controller.RunAt(key - Key.NumPad1);
                    e.Handled = true;
                    return;
            }
        }

        switch (key)
        {
            case Key.Escape:
                _controller.StepBack();
                e.Handled = true;
                break;
            case Key.Enter:
                _controller.RunSelected();
                e.Handled = true;
                break;
            case Key.Up:
                _controller.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                if (!_controller.PeekHome()) _controller.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Left:
                if (_controller.MoveCategory(-1)) e.Handled = true;
                break;
            case Key.Right:
                if (_controller.MoveCategory(1)) e.Handled = true;
                break;
            case Key.Tab:
                _controller.CompleteSelection();
                e.Handled = true;
                break;
        }
    }
}
