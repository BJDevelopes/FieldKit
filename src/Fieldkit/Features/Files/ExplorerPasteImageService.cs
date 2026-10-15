// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.Interop;

namespace Fieldkit.Features.Files;

/// <summary>Names and checks behind "Paste copied images as files" (upstream FinderPasteImageSupport).</summary>
public static class PastedImageNaming
{
    /// <summary>"Pasted image 2026-10-14 153012.png" (invariant digits, sortable).</summary>
    public static string FileName(DateTime when) =>
        "Pasted image " + when.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ".png";

    /// <summary>
    /// Whether Ctrl+V should become "save the image": a picture is on the clipboard and no files are.
    /// A copied image file also advertises picture data; the files win so a normal paste is never
    /// turned into a newly rendered PNG.
    /// </summary>
    public static bool ShouldSaveImage(bool hasImage, bool hasFiles) => hasImage && !hasFiles;

    /// <summary>Focus classes of Explorer's file views (folder view, desktop); text fields keep their paste.</summary>
    public static bool IsFileViewFocus(string focusClass) =>
        focusClass is "DirectUIHWND" or "SysListView32" or "SHELLDLL_DefView";
}

/// <summary>
/// File Explorer already cuts and pastes files, so the part of upstream "Cut &amp; paste" Windows lacks is
/// pasting a copied picture as a file: with File Explorer in front, its file view focused, and an
/// image (but no files) on the clipboard, Ctrl+V saves <c>Pasted image &lt;timestamp&gt;.png</c> into
/// the folder shown. Everything else reaches Explorer untouched.
/// </summary>
public sealed class ExplorerPasteImageService : FeatureService
{
    public const string Id = "finderCutPaste";
    public const string EnabledKey = "finderCutPaste.pasteImageAsFile";
    private const int VK_V = 0x56;

    private IDisposable? _hook;
    private int _busy;

    public override string FeatureId => Id;

    public override IReadOnlyList<string> EnableKeys => [EnabledKey];

    public override bool EnabledByDefault => false;

    protected override void OnStart() => _hook = InputHooks.AddKeyboardHandler(OnKey, priority: 60);

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
    }

    // Hook thread: only cheap checks (window class, clipboard format availability — no clipboard open).
    private bool OnKey(ref KeyEvent e)
    {
        if (!e.IsDown || e.IsOwn || e.VirtualKey != VK_V) return false;
        if (Shortcut.CurrentModifiers() != Modifiers.Ctrl) return false;
        var foreground = Native.GetForegroundWindow();
        if (!ExplorerShell.IsExplorerWindow(foreground)) return false;
        if (!PastedImageNaming.IsFileViewFocus(FocusClass(foreground))) return false;
        if (!PastedImageNaming.ShouldSaveImage(ClipboardAccess.HasImage(), ClipboardAccess.HasFormat(ClipboardNative.CF_HDROP))) return false;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return true;
        _ = SaveAsync(foreground);
        return true;
    }

    private static string FocusClass(IntPtr foreground)
    {
        var thread = Native.GetWindowThreadProcessId(foreground, out _);
        var info = new ClipboardNative.GUITHREADINFO { cbSize = Marshal.SizeOf<ClipboardNative.GUITHREADINFO>() };
        return ClipboardNative.GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero
            ? WindowHelper.GetClassName(info.hwndFocus)
            : string.Empty;
    }

    private async Task SaveAsync(IntPtr explorer)
    {
        try
        {
            var location = await ExplorerShell.QueryAsync(explorer).ConfigureAwait(false);
            if (location is null)
            {
                SystemSounds.Beep.Play();
                return;
            }
            var image = await ClipboardLane.Run(access => access.ReadImage()).ConfigureAwait(false);
            if (image is null)
            {
                SystemSounds.Beep.Play();
                return;
            }
            var path = ExplorerShell.UniquePath(location.Folder, PastedImageNaming.FileName(DateTime.Now));
            await File.WriteAllBytesAsync(path, image.Png).ConfigureAwait(false);
            Log.Info($"Pasted an image as {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Pasting an image as a file failed", ex);
            SystemSounds.Beep.Play();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
