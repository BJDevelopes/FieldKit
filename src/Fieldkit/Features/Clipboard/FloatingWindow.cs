// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// A borderless, themed floating tool window (history, Clean URL, snippet library, shelf, installer):
/// rounded card with a shadow, out of Alt+Tab and the taskbar, optionally non-activating, closes on
/// Esc and (optionally) when it loses focus. Positioning uses physical pixels so it lands right on
/// any monitor.
/// </summary>
public class FloatingWindow : Window
{
    private readonly Border _card;
    private bool _suppressDeactivate;

    /// <param name="noActivate">Never takes focus (WS_EX_NOACTIVATE), e.g. the shelf.</param>
    public FloatingWindow(bool noActivate = false, double cornerRadius = 14)
    {
        NoActivate = noActivate;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !noActivate;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        _card = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.35 },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        _card.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        base.Content = _card;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_TOOLWINDOW;
            if (NoActivate) ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CloseOnEscape && !e.Handled)
            {
                e.Handled = true;
                Dismiss();
            }
        };
        Deactivated += (_, _) =>
        {
            if (HideOnDeactivate && !_suppressDeactivate && IsVisible) Dismiss();
        };
    }

    public bool NoActivate { get; }

    public bool CloseOnEscape { get; set; } = true;

    public bool HideOnDeactivate { get; set; } = true;

    /// <summary>The card's content (use instead of <see cref="ContentControl.Content"/>).</summary>
    public UIElement? Body
    {
        get => _card.Child;
        set => _card.Child = value;
    }

    public Border Card => _card;

    /// <summary>The margin around the card that holds the shadow, in DIPs.</summary>
    public double ShadowMargin => _card.Margin.Left;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Hides (default) or closes the window. Override to keep state.</summary>
    public virtual void Dismiss() => Hide();

    /// <summary>Runs <paramref name="action"/> (e.g. a modal dialog) without the focus loss dismissing the window.</summary>
    public T WithoutAutoDismiss<T>(Func<T> action)
    {
        _suppressDeactivate = true;
        try { return action(); }
        finally
        {
            _suppressDeactivate = false;
            if (IsVisible && !NoActivate) Activate();
        }
    }

    /// <summary>Scale (1.0 = 96 dpi) of the monitor that holds a physical point.</summary>
    public static double ScaleAt(int x, int y) => WindowHelper.GetScaleAt(x, y);

    /// <summary>
    /// Moves the (shown) window so its card's top-left lands at physical point (<paramref name="x"/>, <paramref name="y"/>),
    /// clamped to that monitor's work area.
    /// </summary>
    public void MoveCardTo(int x, int y)
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        var work = WindowHelper.GetWorkAreaAt(x, y);
        var scale = ScaleAt(x, y);
        var margin = (int)Math.Round(ShadowMargin * scale);
        var w = (int)Math.Round(ActualWidth * scale);
        var h = (int)Math.Round(ActualHeight * scale);
        var left = Math.Clamp(x - margin, work.Left - margin, Math.Max(work.Left - margin, work.Right - w + margin));
        var top = Math.Clamp(y - margin, work.Top - margin, Math.Max(work.Top - margin, work.Bottom - h + margin));
        Native.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Centers the card on the monitor holding the pointer, <paramref name="heightFraction"/> of the way down.</summary>
    public void CenterOnPointerScreen(double heightFraction = 0.5)
    {
        Native.GetCursorPos(out var cursor);
        var work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
        var scale = ScaleAt(cursor.X, cursor.Y);
        var cardW = (ActualWidth - 2 * ShadowMargin) * scale;
        var cardH = (ActualHeight - 2 * ShadowMargin) * scale;
        var x = work.Left + (work.Width - cardW) / 2;
        var y = work.Top + (work.Height - cardH) * heightFraction;
        MoveCardTo((int)x, (int)y);
    }

    /// <summary>The caret of the focused control in another app (physical pixels), if it reports one.</summary>
    public static Native.POINT? CaretPosition()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var thread = Native.GetWindowThreadProcessId(fg, out _);
        var info = new ClipboardNative.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<ClipboardNative.GUITHREADINFO>() };
        if (!ClipboardNative.GetGUIThreadInfo(thread, ref info) || info.hwndCaret == IntPtr.Zero) return null;
        var pt = new Native.POINT(info.rcCaret.Left, info.rcCaret.Bottom);
        if (!ClipboardNative.ClientToScreen(info.hwndCaret, ref pt)) return null;
        return pt.X == 0 && pt.Y == 0 ? null : pt;
    }

    /// <summary>A small round icon button for a window header (close, pin, preview).</summary>
    public static Button HeaderButton(string glyph, string tooltip, Action onClick, double size = 12) =>
        Ui.IconButton(glyph, tooltip, onClick, size);
}
