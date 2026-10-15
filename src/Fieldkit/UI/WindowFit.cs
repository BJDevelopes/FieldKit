// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Interop;

namespace Fieldkit.UI;

/// <summary>Pure sizing rule (unit tested): a window never opens larger than the display it opens on.</summary>
public static class WindowFitLogic
{
    /// <summary>Share of the usable area (screen minus taskbar) a window may take at most.</summary>
    public const double MaxShare = 0.92;

    /// <summary>The size and minimum to use for one dimension, in DIPs.</summary>
    public static (double Size, double Min) Fit(double wanted, double min, double available)
    {
        if (available <= 0 || double.IsNaN(available)) return (wanted, min);
        var limit = Math.Floor(available * MaxShare);
        return (Math.Min(wanted, limit), Math.Min(min, limit));
    }
}

/// <summary>Applies <see cref="WindowFitLogic"/> to a window before it is shown.</summary>
public static class WindowFit
{
    /// <summary>
    /// Shrinks <paramref name="window"/> (and its minimum size) to fit the usable area of the display under the
    /// pointer, where it is about to open: at 150 % scaling, on a small laptop panel or with a tall taskbar a
    /// fixed size could otherwise run off the screen.
    /// </summary>
    public static void ToDisplay(Window window)
    {
        if (!Native.GetCursorPos(out var p)) return;
        var work = WindowHelper.GetWorkAreaAt(p.X, p.Y);
        var scale = WindowHelper.GetScaleAt(p.X, p.Y);
        if (scale <= 0) scale = 1;
        var (w, minW) = WindowFitLogic.Fit(window.Width, window.MinWidth, work.Width / scale);
        var (h, minH) = WindowFitLogic.Fit(window.Height, window.MinHeight, work.Height / scale);
        window.MinWidth = minW;
        window.MinHeight = minH;
        if (!double.IsNaN(window.Width)) window.Width = w;
        if (!double.IsNaN(window.Height)) window.Height = h;
    }
}
