// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Fieldkit.Features.Capture;

/// <summary>
/// The Windows share sheet for a local file (Nearby sharing, Mail, Teams, …). Nothing is uploaded
/// by Fieldkit; the person picks the target app. Upstream's expiring links need a hosting service
/// and are left out.
/// </summary>
public static class ShareSheet
{
    private static readonly Dictionary<IntPtr, DataTransferManager> Managers = [];
    private static readonly Dictionary<IntPtr, string> Pending = [];

    public static void ShareFile(IntPtr ownerHwnd, string path, string title)
    {
        if (ownerHwnd == IntPtr.Zero || !File.Exists(path)) return;
        try
        {
            if (!Managers.TryGetValue(ownerHwnd, out var manager))
            {
                manager = DataTransferManagerInterop.GetForWindow(ownerHwnd);
                manager.DataRequested += async (_, args) =>
                {
                    if (!Pending.TryGetValue(ownerHwnd, out var file)) return;
                    var deferral = args.Request.GetDeferral();
                    try
                    {
                        var storage = await StorageFile.GetFileFromPathAsync(file);
                        args.Request.Data.Properties.Title = title;
                        args.Request.Data.SetStorageItems([storage]);
                        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                            args.Request.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(storage));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Preparing the share sheet failed", ex);
                        args.Request.FailWithDisplayText("The file could not be shared.");
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                };
                Managers[ownerHwnd] = manager;
            }
            Pending[ownerHwnd] = path;
            DataTransferManagerInterop.ShowShareUIForWindow(ownerHwnd);
        }
        catch (Exception ex)
        {
            Log.Error("Opening the share sheet failed", ex);
            Hud.Show(UI.Glyphs.Warning, "Sharing is not available");
        }
    }

    /// <summary>Forgets a window's share registration (call when the window closes).</summary>
    public static void Forget(IntPtr hwnd)
    {
        Managers.Remove(hwnd);
        Pending.Remove(hwnd);
    }
}
