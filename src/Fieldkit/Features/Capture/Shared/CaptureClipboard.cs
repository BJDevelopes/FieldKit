// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Fieldkit.Core;

namespace Fieldkit.Features.Capture;

/// <summary>Clipboard writes with retries (another app may hold the clipboard for a moment).</summary>
public static class CaptureClipboard
{
    public static bool CopyText(string text) => Try(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));

    /// <summary>Copies an image as a bitmap and as PNG (keeps transparency for apps that read PNG).</summary>
    public static bool CopyImage(RasterImage image)
    {
        var data = new DataObject();
        data.SetImage(image.ToBitmapSource());
        data.SetData("PNG", new MemoryStream(image.EncodePng()), false);
        return Try(() => Clipboard.SetDataObject(data, true));
    }

    /// <summary>Copies a file (recordings, GIFs) so it can be pasted into Explorer, chats and mail.</summary>
    public static bool CopyFile(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        return Try(() => Clipboard.SetDataObject(data, true));
    }

    public static RasterImage? ReadImage()
    {
        try
        {
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png)
            {
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(png, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                return RasterImage.FromBitmapSource(decoder.Frames[0]);
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bmp) return RasterImage.FromBitmapSource(bmp);
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var file in Clipboard.GetFileDropList())
                {
                    if (file is null) continue;
                    try { return RasterImage.DecodeFile(file); }
                    catch { /* not an image */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the clipboard image failed: {ex.Message}");
        }
        return null;
    }

    private static bool Try(Action write)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                write();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        Log.Warn("The clipboard stayed busy; nothing was copied");
        return false;
    }
}
