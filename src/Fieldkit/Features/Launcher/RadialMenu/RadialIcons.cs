// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// Icons of slices: the curated symbol set of the editor (upstream SF Symbol names mapped to
/// Segoe Fluent glyphs, so settings stay portable), each kind's default glyph, real file and app
/// icons (cached, so the wheel never touches the disk while the pointer is tracked) and
/// downloaded website icons.
/// </summary>
public static class RadialIcons
{
    // Glyphs not in UI/Glyphs.cs (verified with --render-glyph-range and the preview test).
    public const string AppGeneric = "\uECAA";
    public const string More = "\uE712";
    public const string PlayPause = "\uE768";
    public const string Heart = "\uEB51";
    public const string Mail = "\uE715";
    public const string Message = "\uE8BD";
    public const string Video = "\uE714";
    public const string Game = "\uE7FC";
    public const string Home = "\uE80F";
    public const string Cart = "\uE7BF";
    public const string Repair = "\uE90F";
    public const string Brush = "\uE790";
    public const string Book = "\uE82D";
    public const string Airplane = "\uE709";
    public const string Error = "\uE783";
    public const string Help = "\uE897";
    public const string Unlock = "\uE785";
    public const string Key = "\uE8D7";
    public const string Person = "\uE77B";
    public const string People = "\uE716";
    public const string Flag = "\uE7C1";
    public const string Bookmark = "\uE728";
    public const string Tag = "\uE8EC";
    public const string Attach = "\uE723";
    public const string Upload = "\uE898";
    public const string Archive = "\uE7B8";
    public const string Phone = "\uE8EA";
    public const string Tablet = "\uE70A";
    public const string Laptop = "\uE7F8";
    public const string Pc = "\uE977";
    public const string Shuffle = "\uE8B1";
    public const string Repeat = "\uE8EE";
    public const string Lightbulb = "\uEA80";
    public const string Hide = "\uED1A";
    public const string MapPin = "\uE707";
    public const string Map = "\uE800";
    public const string Send = "\uE724";
    public const string Chat = "\uE8F2";
    public const string Call = "\uE717";
    public const string Equalizer = "\uE9E9";
    public const string Print = "\uE749";
    public const string Font = "\uE8D2";
    public const string Hashtag = "\uE8EF";
    public const string Fire = "\uECAD";
    public const string Watch = "\uE916";

    /// <summary>The editor's icon grid: upstream symbol name → Fluent glyph, in upstream order.</summary>
    public static readonly IReadOnlyList<(string Symbol, string Glyph)> Symbols =
    [
        ("star.fill", Glyphs.Star), ("heart.fill", Heart), ("bolt.fill", Glyphs.Bolt), ("flame.fill", Fire), ("sparkles", Glyphs.Sparkle),
        ("folder.fill", Glyphs.Folder), ("doc.fill", Glyphs.Document), ("tray.full.fill", Archive), ("terminal.fill", Glyphs.Command), ("globe", Glyphs.Globe),
        ("envelope.fill", Mail), ("message.fill", Message), ("music.note", Glyphs.MusicNote), ("headphones", Glyphs.Headphones), ("camera.fill", Glyphs.Camera),
        ("photo.fill", Glyphs.Photo), ("video.fill", Video), ("gamecontroller.fill", Game), ("calendar", Glyphs.Calendar), ("clock.fill", Glyphs.Clock),
        ("house.fill", Home), ("cart.fill", Cart), ("hammer.fill", Repair), ("paintbrush.fill", Brush), ("book.fill", Book),
        ("keyboard", Glyphs.Keyboard), ("magnifyingglass", Glyphs.Search), ("airplane", Airplane),
        ("checkmark.circle.fill", Glyphs.Check), ("xmark.circle.fill", Glyphs.Close), ("plus.circle.fill", Glyphs.Add), ("minus.circle.fill", Glyphs.Remove),
        ("exclamationmark.triangle.fill", Glyphs.Warning), ("questionmark.circle.fill", Help), ("info.circle.fill", Glyphs.Info),
        ("lock.fill", Glyphs.Lock), ("lock.open.fill", Unlock), ("key.fill", Key), ("person.fill", Person), ("person.2.fill", People),
        ("bell.fill", Glyphs.Bell), ("flag.fill", Flag), ("bookmark.fill", Bookmark), ("tag.fill", Tag),
        ("paperclip", Attach), ("link", Glyphs.Link), ("scissors", Glyphs.Cut), ("doc.on.clipboard", Glyphs.Clipboard),
        ("square.and.arrow.up", Upload), ("square.and.arrow.down", Glyphs.Download), ("trash.fill", Glyphs.Delete),
        ("externaldrive.fill", Glyphs.HardDrive), ("display", Glyphs.Display), ("desktopcomputer", Pc),
        ("laptopcomputer", Laptop), ("iphone", Phone), ("ipad", Tablet), ("applewatch", Watch),
        ("wifi", Glyphs.Wifi), ("network", Glyphs.Network), ("antenna.radiowaves.left.and.right", Glyphs.Bluetooth),
        ("speaker.wave.2.fill", Glyphs.Volume), ("mic.fill", Glyphs.Microphone), ("waveform", Glyphs.Waveform),
        ("play.fill", Glyphs.Play), ("pause.fill", Glyphs.Pause), ("stop.fill", Glyphs.Stop), ("backward.fill", Glyphs.Previous), ("forward.fill", Glyphs.Next),
        ("shuffle", Shuffle), ("repeat", Repeat),
        ("sun.max.fill", Glyphs.Brightness), ("moon.fill", Glyphs.Moon), ("lightbulb.fill", Lightbulb), ("battery.100", Glyphs.Battery), ("power", Glyphs.Power),
        ("eye.fill", Glyphs.Eye), ("eye.slash.fill", Hide), ("location.fill", MapPin), ("map.fill", Map),
        ("paperplane.fill", Send), ("bubble.left.fill", Chat), ("phone.fill", Call),
        ("gearshape.fill", Glyphs.Settings), ("slider.horizontal.3", Equalizer), ("switch.2", Glyphs.Toggles), ("command", Glyphs.Keyboard),
        ("printer.fill", Print), ("textformat", Font), ("number", Hashtag),
    ];

    private static readonly Dictionary<string, string> SymbolLookup =
        Symbols.GroupBy(s => s.Symbol).ToDictionary(g => g.Key, g => g.First().Glyph, StringComparer.Ordinal);

    public static string? GlyphForSymbol(string symbol) => SymbolLookup.TryGetValue(symbol, out var g) ? g : null;

    /// <summary>The glyph drawn when the user picked none (app and file slices prefer their real icons).</summary>
    public static string DefaultGlyph(RadialMenuItem item, RadialCatalog catalog) => item.Kind switch
    {
        RadialItemKind.App => AppGeneric,
        RadialItemKind.File => Glyphs.Folder,
        RadialItemKind.Url => Glyphs.Link,
        RadialItemKind.Shortcut => Glyphs.Keyboard,
        RadialItemKind.Tool => catalog.Tool(item.Payload)?.Glyph ?? Glyphs.Tools,
        RadialItemKind.QuickToggle => catalog.Toggle(item.Payload)?.Glyph ?? Glyphs.Toggles,
        RadialItemKind.WindowLayout => catalog.Layout(item.Payload)?.Glyph ?? Glyphs.Layout,
        RadialItemKind.Media => item.MediaKey switch
        {
            RadialMediaKey.PreviousTrack => Glyphs.Previous,
            RadialMediaKey.NextTrack => Glyphs.Next,
            RadialMediaKey.NowPlaying => Glyphs.MusicNote,
            _ => PlayPause,
        },
        _ => More,
    };

    public static string EffectiveGlyph(RadialMenuItem item, RadialCatalog catalog) =>
        (item.SymbolName.Length > 0 ? GlyphForSymbol(item.SymbolName) : null) ?? DefaultGlyph(item, catalog);

    /// <summary>
    /// The icon element of a slice: a downloaded website icon, the real app/file icon, or a glyph
    /// (white on a highlighted chip). <paramref name="nowPlayingIcon"/> replaces the Now Playing note.
    /// </summary>
    public static FrameworkElement Visual(RadialMenuItem item, RadialCatalog catalog, double imageSize, double glyphSize,
        bool highlighted, ImageSource? nowPlayingIcon = null, bool loadFiles = true)
    {
        ImageSource? image = null;
        if (item.MediaKey == RadialMediaKey.NowPlaying) image = nowPlayingIcon;
        else if (item.SymbolName.Length == 0 && item.CustomIconData is { } data) image = CustomIcon(item.Id, data);
        else if (item.UsesFileIcon && loadFiles) image = FileIcon(item.Payload);

        if (image is not null)
        {
            var img = new Image { Source = image, Width = imageSize, Height = imageSize, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        var glyph = Ui.Icon(EffectiveGlyph(item, catalog), glyphSize, highlighted ? "TextOnAccent" : "TextPrimary");
        glyph.IsHitTestVisible = false;
        return glyph;
    }

    // ------------------------------------------------------------------ caches

    private static readonly ConcurrentDictionary<string, ImageSource?> FileIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(Guid, int, int), ImageSource?> CustomIcons = new();

    public static void Invalidate(string payload) => FileIcons.TryRemove(payload, out _);

    public static ImageSource? CustomIcon(Guid id, byte[] data) =>
        CustomIcons.GetOrAdd((id, data.Length, data.Length > 0 ? data[^1] : 0), _ => DecodeImage(data));

    public static ImageSource? DecodeImage(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(data);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The shell's icon for a path, folder or "shell:AppsFolder\…" entry (48 px), cached.</summary>
    public static ImageSource? FileIcon(string payload) =>
        FileIcons.GetOrAdd(payload, static p => ShellIcon(RadialMenuSupport.ExpandPath(p), 48));

    // ------------------------------------------------------------- shell icon

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int BmType;
        public int BmWidth;
        public int BmHeight;
        public int BmWidthBytes;
        public ushort BmPlanes;
        public ushort BmBitsPixel;
        public IntPtr BmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public NativeBitmap DsBm;
        public BitmapInfoHeader DsBmih;
        public uint DsBitfields0;
        public uint DsBitfields1;
        public uint DsBitfields2;
        public IntPtr DshSection;
        public uint DsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, out DibSection pv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    private const int SiigbfBiggerSizeOk = 0x1;
    private const int SiigbfIconOnly = 0x4;

    private static ImageSource? ShellIcon(string path, int size)
    {
        if (!path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !File.Exists(path) && !Directory.Exists(path))
            return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
            try
            {
                if (factory.GetImage(new NativeSize(size, size), SiigbfIconOnly | SiigbfBiggerSizeOk, out hbm) != 0 || hbm == IntPtr.Zero)
                    return null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
            return FromHBitmap(hbm);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }

    /// <summary>Copies a 32-bit DIB section with its alpha (CreateBitmapSourceFromHBitmap drops it).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DibSection>(), out var dib) == 0) return null;
        var bm = dib.DsBm;
        if (bm.BmBits == IntPtr.Zero || bm.BmBitsPixel != 32 || bm.BmWidth <= 0 || bm.BmHeight <= 0)
        {
            var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }
        var stride = bm.BmWidthBytes;
        var pixels = new byte[stride * bm.BmHeight];
        Marshal.Copy(bm.BmBits, pixels, 0, pixels.Length);
        if (dib.DsBmih.BiHeight > 0)
        {
            // Bottom-up DIB: flip rows so the icon stands upright.
            var row = new byte[stride];
            for (int top = 0, bottom = bm.BmHeight - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
            }
        }
        var source = BitmapSource.Create(bm.BmWidth, bm.BmHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }
}
