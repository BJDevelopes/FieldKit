// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Core;
using WinDataTransfer = global::Windows.ApplicationModel.DataTransfer;
using WinStorage = global::Windows.Storage;

namespace Fieldkit.Features.Files;

/// <summary>
/// The Windows share sheet for shelf items (upstream ShelfSharePresenter), through
/// IDataTransferManagerInterop: files and pictures go as storage items, a single link as a web link,
/// notes as text.
/// </summary>
public static class ShelfShare
{
    public static bool IsSupported
    {
        get
        {
            try { return WinDataTransfer.DataTransferManager.IsSupported(); }
            catch { return false; }
        }
    }

    /// <summary>Opens the share sheet for <paramref name="items"/>, anchored to <paramref name="hwnd"/>.</summary>
    public static bool Show(IntPtr hwnd, IReadOnlyList<ShelfItem> items)
    {
        if (hwnd == IntPtr.Zero || items.Count == 0 || !IsSupported) return false;
        try
        {
            var manager = WinDataTransfer.DataTransferManagerInterop.GetForWindow(hwnd);
            global::Windows.Foundation.TypedEventHandler<WinDataTransfer.DataTransferManager, WinDataTransfer.DataRequestedEventArgs>? handler = null;
            handler = async (sender, args) =>
            {
                sender.DataRequested -= handler;
                var request = args.Request;
                var deferral = request.GetDeferral();
                try
                {
                    request.Data.Properties.Title = items.Count == 1 ? items[0].Title : $"{items.Count} items";
                    var storage = new List<WinStorage.IStorageItem>();
                    foreach (var item in items.Where(i => i.HoldsFile))
                    {
                        try
                        {
                            if (Directory.Exists(item.Path))
                                storage.Add(await WinStorage.StorageFolder.GetFolderFromPathAsync(item.Path));
                            else if (File.Exists(item.Path))
                                storage.Add(await WinStorage.StorageFile.GetFileFromPathAsync(item.Path));
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Sharing a shelf file failed: {ex.Message}");
                        }
                    }
                    if (storage.Count > 0) request.Data.SetStorageItems(storage);
                    if (items.Count == 1 && items[0].Kind == ShelfItemKind.Link
                        && Uri.TryCreate(items[0].Text, UriKind.Absolute, out var uri))
                        request.Data.SetWebLink(uri);
                    else if (ShelfRules.JoinedText(items) is { } text)
                        request.Data.SetText(text);
                }
                catch (Exception ex)
                {
                    Log.Error("Preparing the shelf share failed", ex);
                    request.FailWithDisplayText("These items could not be shared.");
                }
                finally
                {
                    deferral.Complete();
                }
            };
            manager.DataRequested += handler;
            WinDataTransfer.DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Opening the share sheet failed", ex);
            return false;
        }
    }
}
