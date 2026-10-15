// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// The clipboard formats apps use to say "do not record this". Password managers set them when
/// they hand over a secret; Windows' own clipboard history honours the same formats.
/// See https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats.
/// </summary>
public static class ClipboardPrivacy
{
    /// <summary>Present at all: no clipboard monitor may process the content.</summary>
    public const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>A DWORD; 0 means "keep out of clipboard history".</summary>
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";

    /// <summary>A DWORD; 0 means "do not sync to the cloud clipboard".</summary>
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";

    /// <summary>Legacy convention (clipboard viewers): present means "ignore this change".</summary>
    public const string ViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>
    /// Whether the content must stay out of every record (history, cleaning, shelf).
    /// </summary>
    /// <param name="formatNames">Names of the formats on the clipboard.</param>
    /// <param name="canIncludeInHistoryValue">The DWORD stored under <see cref="CanIncludeInHistory"/>, if present.</param>
    public static bool IsPrivate(IEnumerable<string> formatNames, uint? canIncludeInHistoryValue)
    {
        foreach (var name in formatNames)
        {
            if (string.Equals(name, ExcludeFromMonitoring, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, ViewerIgnore, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return canIncludeInHistoryValue == 0;
    }
}
