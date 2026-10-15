// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// A borderless, top-most tool window kept out of Alt+Tab and the taskbar. Opaque (not a
/// per-pixel-alpha layered window) so DWM thumbnails can be drawn onto it; DWM rounds its
/// corners on Windows 11. With <c>noActivate</c> it never takes focus from the app in front.
/// </summary>
public class OverlayWindow : Window
{
    private readonly bool _noActivate;

    public OverlayWindow(bool noActivate)
    {
        _noActivate = noActivate;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = !noActivate;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "PanelBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        SourceInitialized += (_, _) =>
        {
            WinApi.MakeToolWindow(Handle, _noActivate);
            WinApi.RoundCorners(Handle);
            ThemeManager.ApplyTitleBar(this);
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>
    /// Puts the window at a physical-pixel position with a DIP size measured for the target
    /// monitor's scale (moving first so a DPI change across monitors cannot resize it after).
    /// </summary>
    public void PlaceAt(int xPx, int yPx, double widthDip, double heightDip)
    {
        var hwnd = Handle;
        const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
        Native.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, 0, 0, flags | Native.SWP_NOSIZE);
        var scale = WindowHelper.GetScaleAt(xPx + 1, yPx + 1);
        Width = widthDip;
        Height = heightDip;
        Native.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, (int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(heightDip * scale), flags);
    }

    /// <summary>Shows without stealing focus when the window is non-activating.</summary>
    public void ShowQuietly()
    {
        if (!IsVisible) Show(); // ShowActivated=false makes WPF use SW_SHOWNA
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
