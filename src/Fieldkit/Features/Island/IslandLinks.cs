// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Fieldkit.Core;

namespace Fieldkit.Features.Island;

/// <summary>
/// The one place the island opens anything. Links that come from outside data (calendar feeds) must be
/// absolute http, https or mailto URIs; anything else (file:, UNC paths, ms-* or other protocol handlers,
/// relative or local paths) is refused. Files are opened only when they are existing local paths the user put
/// there (shelf, watched downloads folder), never network paths.
/// </summary>
public static class IslandLinks
{
    private static readonly HashSet<string> WebSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>The URI when <paramref name="text"/> is an absolute http(s)/mailto link, otherwise null.</summary>
    public static Uri? SafeWebUri(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (!WebSchemes.Contains(uri.Scheme)) return null;
        if (uri.IsUnc || uri.IsFile) return null;
        if (uri.Scheme != Uri.UriSchemeMailto && string.IsNullOrEmpty(uri.Host)) return null;
        return uri;
    }

    /// <summary>Opens a web link from outside data in the default browser; refuses anything else.</summary>
    public static bool OpenWeb(string? text)
    {
        if (SafeWebUri(text) is not { } uri)
        {
            Log.Warn("Refused to open a link that is not http, https or mailto");
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening a link failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether a path is an existing file or folder on a local (non-UNC) drive.</summary>
    public static bool IsLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (path.Contains("://", StringComparison.Ordinal)) return false;
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>Opens a local file or folder (shelf, downloads) with its default app.</summary>
    public static bool OpenLocal(string? path)
    {
        if (!IsLocalPath(path))
        {
            Log.Warn("Refused to open a path that is not an existing local file");
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening {path} failed: {ex.Message}");
            System.Media.SystemSounds.Beep.Play();
            return false;
        }
    }

    /// <summary>Shows a local file in Explorer.</summary>
    public static bool RevealLocal(string? path)
    {
        if (!IsLocalPath(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Revealing {path} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads an HTTP body as text, aborting once it passes <paramref name="limit"/> bytes whatever Content-Length says.
    /// Returns null when it is too large.
    /// </summary>
    public static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }
}
