// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using Fieldkit.Interop;

namespace Fieldkit.Features.WindowTools;

/// <summary>
/// Live DWM thumbnails of other windows drawn onto one of our windows. DWM composes them
/// itself, so they cost nothing per frame and need no screen capture. The host window must
/// not be a per-pixel-alpha layered window (WPF AllowsTransparency), or DWM skips them.
/// </summary>
public sealed class ThumbnailHost : IDisposable
{
    private readonly IntPtr _destination;
    private readonly Dictionary<IntPtr, IntPtr> _thumbs = [];

    public ThumbnailHost(IntPtr destination) => _destination = destination;

    /// <summary>Shows <paramref name="source"/> aspect-fitted inside <paramref name="area"/> (device pixels,
    /// relative to the host's client area). Returns the drawn rectangle, or null when DWM refused.</summary>
    public Rect? Show(IntPtr source, Rect area, byte opacity = 255)
    {
        if (_destination == IntPtr.Zero || source == IntPtr.Zero || area.Width < 2 || area.Height < 2) return null;
        if (!_thumbs.TryGetValue(source, out var thumb))
        {
            if (Native.DwmRegisterThumbnail(_destination, source, out thumb) != 0 || thumb == IntPtr.Zero) return null;
            _thumbs[source] = thumb;
        }
        if (Native.DwmQueryThumbnailSourceSize(thumb, out var size) != 0 || size.X <= 0 || size.Y <= 0)
        {
            Hide(source);
            return null;
        }
        var fitted = ThumbnailMath.AspectFit(size.X, size.Y, area);
        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY |
                      Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new Native.RECT((int)Math.Round(fitted.Left), (int)Math.Round(fitted.Top),
                (int)Math.Round(fitted.Right), (int)Math.Round(fitted.Bottom)),
            opacity = opacity,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        return Native.DwmUpdateThumbnailProperties(thumb, ref props) == 0 ? fitted : null;
    }

    public void Hide(IntPtr source)
    {
        if (!_thumbs.Remove(source, out var thumb)) return;
        Native.DwmUnregisterThumbnail(thumb);
    }

    /// <summary>Drops every thumbnail whose source is not in <paramref name="keep"/>.</summary>
    public void Prune(IEnumerable<IntPtr> keep)
    {
        var set = keep.ToHashSet();
        foreach (var source in _thumbs.Keys.Where(s => !set.Contains(s)).ToList()) Hide(source);
    }

    public void Clear()
    {
        foreach (var thumb in _thumbs.Values) Native.DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }

    public void Dispose() => Clear();

    /// <summary>Bounds of <paramref name="element"/> in device pixels relative to <paramref name="root"/>'s client area.</summary>
    public static Rect DeviceBounds(FrameworkElement element, Visual root)
    {
        if (!element.IsVisible || element.ActualWidth <= 0) return Rect.Empty;
        var source = PresentationSource.FromVisual(root);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new Rect(bounds.X * toDevice.M11, bounds.Y * toDevice.M22, bounds.Width * toDevice.M11, bounds.Height * toDevice.M22);
    }
}

/// <summary>Pure geometry for thumbnails, kept separate for tests.</summary>
public static class ThumbnailMath
{
    /// <summary>The largest rectangle with the source's aspect ratio centred in <paramref name="area"/>, never upscaled.</summary>
    public static Rect AspectFit(double sourceWidth, double sourceHeight, Rect area)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || area.Width <= 0 || area.Height <= 0) return area;
        var scale = Math.Min(1.0, Math.Min(area.Width / sourceWidth, area.Height / sourceHeight));
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        return new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }
}
