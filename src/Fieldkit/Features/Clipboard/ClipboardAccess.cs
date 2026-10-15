// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.Core;
using static Fieldkit.Features.ClipboardTools.ClipboardNative;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>A copied picture, as PNG bytes plus its size.</summary>
public sealed record ClipboardImage(byte[] Png, int Width, int Height);

/// <summary>Every format on the clipboard, copied out so it can be put back later.</summary>
public sealed class ClipboardSnapshot
{
    internal List<(uint Format, byte[] Data)> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public long TotalBytes => Items.Sum(i => (long)i.Data.Length);
}

/// <summary>
/// Clipboard reads and writes. Only valid on the <see cref="ClipboardLane"/> thread, which hands an
/// instance to its callers. Every method opens and closes the clipboard itself (with retries, since
/// another app may hold it for a moment).
/// </summary>
public sealed class ClipboardAccess
{
    public const int MaxImageBytes = 64 * 1024 * 1024;
    public const int MaxFiles = 100;
    private const long MaxSnapshotBytes = 128L * 1024 * 1024;

    private static readonly Dictionary<string, uint> Registered = new(StringComparer.Ordinal);

    internal ClipboardAccess() { }

    public static uint Format(string name)
    {
        lock (Registered)
        {
            if (!Registered.TryGetValue(name, out var id))
            {
                id = RegisterClipboardFormat(name);
                Registered[name] = id;
            }
            return id;
        }
    }

    /// <summary>The clipboard's current sequence number (changes with every write).</summary>
    public uint Sequence => Interop.Native.GetClipboardSequenceNumber();

    // ----------------------------------------------------------------- read

    public static bool HasFormat(uint format) => IsClipboardFormatAvailable(format);

    public static bool HasFormat(string name) => IsClipboardFormatAvailable(Format(name));

    /// <summary>Names of every format on the clipboard (standard ones as CF_* names).</summary>
    public List<string> FormatNames()
    {
        var names = new List<string>();
        WithOpen(() =>
        {
            uint f = 0;
            while ((f = EnumClipboardFormats(f)) != 0) names.Add(NameOf(f));
        });
        return names;
    }

    /// <summary>
    /// Whether the content carries a "do not record" mark (password managers, see <see cref="ClipboardPrivacy"/>).
    /// </summary>
    public bool IsPrivate()
    {
        if (HasFormat(ClipboardPrivacy.ExcludeFromMonitoring) || HasFormat(ClipboardPrivacy.ViewerIgnore)) return true;
        if (!HasFormat(ClipboardPrivacy.CanIncludeInHistory)) return false;
        uint? value = null;
        WithOpen(() =>
        {
            var bytes = ReadBytes(Format(ClipboardPrivacy.CanIncludeInHistory), 16);
            if (bytes is { Length: >= 4 }) value = BitConverter.ToUInt32(bytes, 0);
        });
        return ClipboardPrivacy.IsPrivate([], value);
    }

    public string? ReadText()
    {
        if (!HasFormat(CF_UNICODETEXT)) return null;
        string? text = null;
        WithOpen(() =>
        {
            var bytes = ReadBytes(CF_UNICODETEXT, 16 * 1024 * 1024);
            if (bytes is null) return;
            var s = Encoding.Unicode.GetString(bytes);
            var nul = s.IndexOf('\0');
            text = nul >= 0 ? s[..nul] : s;
        });
        return text;
    }

    /// <summary>Raw text of a registered format (e.g. "HTML Format", UTF-8).</summary>
    public string? ReadUtf8(string formatName, int maxBytes)
    {
        var format = Format(formatName);
        if (!HasFormat(format)) return null;
        string? text = null;
        WithOpen(() =>
        {
            var bytes = ReadBytes(format, maxBytes);
            if (bytes is null) return;
            var len = Array.IndexOf(bytes, (byte)0);
            text = Encoding.UTF8.GetString(bytes, 0, len >= 0 ? len : bytes.Length);
        });
        return text;
    }

    /// <summary>Raw text of a registered UTF-16 format (e.g. "UniformResourceLocatorW").</summary>
    public string? ReadUtf16(string formatName, int maxBytes)
    {
        var format = Format(formatName);
        if (!HasFormat(format)) return null;
        string? text = null;
        WithOpen(() =>
        {
            var bytes = ReadBytes(format, maxBytes);
            if (bytes is null) return;
            var s = Encoding.Unicode.GetString(bytes);
            var nul = s.IndexOf('\0');
            text = nul >= 0 ? s[..nul] : s;
        });
        return text;
    }

    /// <summary>Copied file paths (Explorer copy), or null.</summary>
    public List<string>? ReadFiles()
    {
        if (!HasFormat(CF_HDROP)) return null;
        List<string>? files = null;
        WithOpen(() =>
        {
            var hDrop = GetClipboardData(CF_HDROP);
            if (hDrop == IntPtr.Zero) return;
            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0 || count > MaxFiles) return;
            files = [];
            for (uint i = 0; i < count; i++)
            {
                var len = (int)DragQueryFile(hDrop, i, null, 0);
                var sb = new StringBuilder(len + 1);
                DragQueryFile(hDrop, i, sb, sb.Capacity);
                files.Add(sb.ToString());
            }
        });
        return files;
    }

    /// <summary>Whether a picture is on the clipboard (without reading it).</summary>
    public static bool HasImage() =>
        HasFormat("PNG") || HasFormat(CF_DIBV5) || HasFormat(CF_DIB) || HasFormat(CF_BITMAP);

    /// <summary>The copied picture as PNG (prefers the app's own PNG, else converts the DIB).</summary>
    public ClipboardImage? ReadImage()
    {
        byte[]? png = null;
        byte[]? dib = null;
        WithOpen(() =>
        {
            var pngFormat = Format("PNG");
            if (IsClipboardFormatAvailable(pngFormat)) png = ReadBytes(pngFormat, MaxImageBytes);
            if (png is null)
            {
                if (IsClipboardFormatAvailable(CF_DIBV5)) dib = ReadBytes(CF_DIBV5, MaxImageBytes);
                if (dib is null && IsClipboardFormatAvailable(CF_DIB)) dib = ReadBytes(CF_DIB, MaxImageBytes);
            }
        });
        try
        {
            if (png is not null)
            {
                var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                return new ClipboardImage(png, frame.PixelWidth, frame.PixelHeight);
            }
            if (dib is not null) return ClipboardImageCodec.DibToPng(dib);
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the copied image failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Copies every format out, so the content can be restored later. Null if something can't be kept.</summary>
    public ClipboardSnapshot? Snapshot()
    {
        var snapshot = new ClipboardSnapshot();
        var ok = true;
        WithOpen(() =>
        {
            var formats = new List<uint>();
            uint f = 0;
            while ((f = EnumClipboardFormats(f)) != 0) formats.Add(f);
            var hasUnicode = formats.Contains(CF_UNICODETEXT);
            long total = 0;
            foreach (var format in formats)
            {
                // GDI handles cannot be copied as bytes; Windows synthesizes them from the DIB / text again.
                if (format is CF_BITMAP or CF_PALETTE or CF_METAFILEPICT or CF_ENHMETAFILE or CF_OWNERDISPLAY
                    or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE) continue;
                if (hasUnicode && format is CF_TEXT or CF_OEMTEXT) continue;
                var data = ReadBytes(format, MaxImageBytes);
                if (data is null) continue;
                total += data.Length;
                if (total > MaxSnapshotBytes)
                {
                    ok = false;
                    return;
                }
                snapshot.Items.Add((format, data));
            }
        });
        return ok ? snapshot : null;
    }

    // ---------------------------------------------------------------- write

    /// <summary>Replaces the clipboard with text.</summary>
    /// <param name="excludeFromHistory">Marks the write so Windows' clipboard history and other managers skip it.</param>
    public bool WriteText(string text, OwnWriteKind kind, bool excludeFromHistory = false, string? source = null) =>
        Write(kind, source, () =>
        {
            SetBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
            if (excludeFromHistory) MarkPrivate();
        });

    /// <summary>Replaces the clipboard with a file list (pastes as the files themselves in Explorer).</summary>
    public bool WriteFiles(IReadOnlyList<string> paths, OwnWriteKind kind) =>
        Write(kind, null, () =>
        {
            SetBytes(CF_HDROP, DropFiles(paths));
            SetBytes(Format("Preferred DropEffect"), BitConverter.GetBytes(1)); // DROPEFFECT_COPY
            SetBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(string.Join("\r\n", paths) + "\0"));
        });

    /// <summary>Replaces the clipboard with a picture (PNG plus a DIB for apps that only read that).</summary>
    public bool WriteImage(byte[] png, OwnWriteKind kind) =>
        Write(kind, null, () =>
        {
            SetBytes(Format("PNG"), png);
            var dib = ClipboardImageCodec.PngToDib(png);
            if (dib is not null) SetBytes(CF_DIB, dib);
        });

    /// <summary>Puts a snapshot back.</summary>
    public bool Restore(ClipboardSnapshot snapshot, OwnWriteKind kind = OwnWriteKind.Restore) =>
        Write(kind, null, () =>
        {
            foreach (var (format, data) in snapshot.Items) SetBytes(format, data);
        });

    /// <summary>Empties the clipboard.</summary>
    public bool Clear() => Write(OwnWriteKind.Clear, null, () => { });

    // -------------------------------------------------------------- helpers

    private static void MarkPrivate()
    {
        SetBytes(Format(ClipboardPrivacy.ExcludeFromMonitoring), BitConverter.GetBytes(0));
        SetBytes(Format(ClipboardPrivacy.CanIncludeInHistory), BitConverter.GetBytes(0));
        SetBytes(Format(ClipboardPrivacy.CanUploadToCloud), BitConverter.GetBytes(0));
    }

    private static bool Write(OwnWriteKind kind, string? source, Action fill)
    {
        var owner = ClipboardLane.WindowHandle;
        if (!TryOpen(owner)) return false;
        var ok = false;
        try
        {
            if (!EmptyClipboard()) return false;
            fill();
            ok = true;
        }
        catch (Exception ex)
        {
            Log.Error("Writing the clipboard failed", ex);
        }
        finally
        {
            CloseClipboard();
        }
        if (ok) ClipboardLane.NoteOwnWrite(kind, source);
        return ok;
    }

    private static void WithOpen(Action action)
    {
        if (!TryOpen(ClipboardLane.WindowHandle)) return;
        try { action(); }
        finally { CloseClipboard(); }
    }

    private static bool TryOpen(IntPtr owner)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(15 + attempt * 5);
        }
        Log.Warn("The clipboard is held by another app");
        return false;
    }

    private static byte[]? ReadBytes(uint format, int maxBytes)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (long)GlobalSize(handle).ToUInt64();
        if (size <= 0 || size > maxBytes) return null;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static void SetBytes(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
        if (handle == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed");
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("GlobalLock failed");
        }
        try { Marshal.Copy(data, 0, ptr, data.Length); }
        finally { GlobalUnlock(handle); }
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException($"SetClipboardData({format}) failed: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>A DROPFILES block: header, then NUL-separated wide paths, double-NUL terminated.</summary>
    public static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        var list = string.Join("\0", paths) + "\0\0";
        var text = Encoding.Unicode.GetBytes(list);
        var bytes = new byte[20 + text.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0); // pFiles
        BitConverter.GetBytes(1).CopyTo(bytes, 16); // fWide
        text.CopyTo(bytes, 20);
        return bytes;
    }

    private static string NameOf(uint format)
    {
        switch (format)
        {
            case CF_TEXT: return "CF_TEXT";
            case CF_BITMAP: return "CF_BITMAP";
            case CF_METAFILEPICT: return "CF_METAFILEPICT";
            case CF_OEMTEXT: return "CF_OEMTEXT";
            case CF_DIB: return "CF_DIB";
            case CF_PALETTE: return "CF_PALETTE";
            case CF_UNICODETEXT: return "CF_UNICODETEXT";
            case CF_ENHMETAFILE: return "CF_ENHMETAFILE";
            case CF_HDROP: return "CF_HDROP";
            case CF_LOCALE: return "CF_LOCALE";
            case CF_DIBV5: return "CF_DIBV5";
        }
        var sb = new StringBuilder(256);
        return GetClipboardFormatName(format, sb, sb.Capacity) > 0 ? sb.ToString() : $"#{format}";
    }
}

/// <summary>Conversions between the clipboard's DIB and PNG (WPF imaging, any STA thread).</summary>
public static class ClipboardImageCodec
{
    /// <summary>Wraps a packed DIB in a BMP file header and re-encodes it as PNG.</summary>
    public static ClipboardImage? DibToPng(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var clrUsed = BitConverter.ToInt32(dib, 32);
        var colorTable = clrUsed != 0 ? clrUsed * 4 : bitCount <= 8 ? (1 << bitCount) * 4 : 0;
        if (compression == 3 && headerSize == 40) colorTable += 12; // BI_BITFIELDS masks follow a v1 header
        var offset = 14 + headerSize + colorTable;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);

        var decoder = new BmpBitmapDecoder(new MemoryStream(bmp), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        // 32-bit DIBs from most apps carry no alpha (all zero): show them opaque.
        if (bitCount == 32 && frame.Format == PixelFormats.Bgra32 && AllAlphaZero(frame))
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        return new ClipboardImage(EncodePng(frame), frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>A bottom-up 32-bit BI_RGB DIB of a PNG, for apps that do not read PNG.</summary>
    public static byte[]? PngToDib(byte[] png)
    {
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);
            var dib = new byte[40 + pixels.Length];
            BitConverter.GetBytes(40).CopyTo(dib, 0);
            BitConverter.GetBytes(w).CopyTo(dib, 4);
            BitConverter.GetBytes(h).CopyTo(dib, 8); // positive height = bottom-up
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);
            BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20);
            for (var y = 0; y < h; y++)
                Buffer.BlockCopy(pixels, y * stride, dib, 40 + (h - 1 - y) * stride, stride);
            return dib;
        }
        catch
        {
            return null;
        }
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Decodes PNG/any image bytes to a frozen bitmap, optionally scaled down to <paramref name="maxSide"/>.</summary>
    public static BitmapSource? Decode(byte[] bytes, int maxSide = 0)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            if (maxSide > 0) image.DecodePixelWidth = maxSide;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static bool AllAlphaZero(BitmapSource frame)
    {
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return false;
        return true;
    }

    /// <summary>Size of an image file on disk ("1280×720"), or null.</summary>
    public static (int Width, int Height)? Dimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A frozen thumbnail of an image file, or null.</summary>
    public static BitmapSource? Thumbnail(string path, int maxSide)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = maxSide;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    internal static Size Fit(int width, int height, double maxW, double maxH)
    {
        if (width <= 0 || height <= 0) return new Size(maxW, maxH);
        var scale = Math.Min(1, Math.Min(maxW / width, maxH / height));
        return new Size(width * scale, height * scale);
    }
}
