// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// On-demand website icon download (upstream <c>RadialMenuFaviconFetcher</c>). Runs only when the
/// user clicks "Fetch Website Icon": one request to the site's /favicon.ico, bounded in size and
/// time, redirects only within the same origin, scaled to a 64 px PNG kept in the slice.
/// </summary>
public static class RadialFavicon
{
    public const int MaxDownloadBytes = 2 * 1024 * 1024;
    public const int MaxSourceDimension = 4096;
    public const int MaxSourcePixels = 16_777_216;
    private const int MaxRedirects = 3;

    /// <summary>https://host[:port]/favicon.ico for an http(s) link (credentials, query and fragment dropped).</summary>
    public static Uri? FaviconUrl(string rawUrl)
    {
        if (RadialMenuSupport.NormalizedUrl(rawUrl) is not { } normalized) return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/favicon.ico");
        return builder.Uri;
    }

    /// <summary>Whether a redirect stays on the origin of the first request.</summary>
    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public static bool DimensionsAreSafe(int width, int height) =>
        width > 0 && height > 0 && width <= MaxSourceDimension && height <= MaxSourceDimension && width <= MaxSourcePixels / height;

    /// <summary>Downloads and converts the icon. Call on the UI thread; returns null when none is found.</summary>
    public static async Task<byte[]?> FetchAsync(string rawUrl)
    {
        if (FaviconUrl(rawUrl) is not { } url) return null;
        var bytes = await Task.Run(() => DownloadAsync(url));
        return bytes is null ? null : ToPng(bytes);
    }

    private static async Task<byte[]?> DownloadAsync(Uri url)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var current = url;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd("image/*");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!SameOrigin(url, next)) return null;
                    current = next;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK) return null;
                if (response.Content.Headers.ContentLength is > MaxDownloadBytes) return null;
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxDownloadBytes) return null;
                    buffer.Write(chunk, 0, read);
                }
                return buffer.Length > 0 ? buffer.ToArray() : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Decodes (largest frame of an .ico), checks its size and scales it to a 64 px PNG of at most 64 KB.</summary>
    public static byte[]? ToPng(byte[] data, int target = 64)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).FirstOrDefault();
            if (frame is null || !DimensionsAreSafe(frame.PixelWidth, frame.PixelHeight)) return null;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                dc.DrawImage(frame, new Rect(0, 0, target, target));
            }
            var bitmap = new RenderTargetBitmap(target, target, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.Length <= RadialMenuSupport.MaxStoredIconBytes ? output.ToArray() : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
