// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>The translucent accent rectangle showing where a dragged window will land. Click-through.</summary>
internal sealed class SnapPreviewWindow : Window
{
    private Native.RECT _shown;

    public SnapPreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            Margin = new Thickness(4),
        };
        border.Background = WindowsUi.Alpha("Accent", 0x40);
        border.BorderBrush = WindowsUi.Alpha("Accent", 0xC0);
        Content = border;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = WinApi.ExStyle(hwnd) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    public void ShowAt(Rect frame)
    {
        var r = WindowLayoutEngine.ToRect(frame);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (IsVisible && r.Left == _shown.Left && r.Top == _shown.Top && r.Right == _shown.Right && r.Bottom == _shown.Bottom) return;
        _shown = r;
        const uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, 0, 0, flags | Native.SWP_NOSIZE);
        var scale = WindowHelper.GetScaleAt(r.Left + 1, r.Top + 1);
        Width = r.Width / scale;
        Height = r.Height / scale;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, flags);
        if (!IsVisible) Show();
    }
}
