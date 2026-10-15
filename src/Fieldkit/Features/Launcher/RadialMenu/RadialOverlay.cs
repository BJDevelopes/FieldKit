// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// A transparent, topmost, non-activating tool window placed in physical pixels (so it lands on
/// the right monitor at the right DPI). Base of the wheel overlay and the Now Playing card.
/// </summary>
public class RadialOverlayWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);

    public RadialOverlayWindow(string title)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SourceInitialized += (_, _) =>
        {
            var hwnd = Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Moves the window so its top-left lands on (x, y) physical pixels with a DIP size scaled by <paramref name="scale"/>.</summary>
    public void PlacePhysical(double x, double y, double widthDip, double heightDip, double scale)
    {
        var hwnd = Handle;
        var w = (int)Math.Round(widthDip * scale);
        var h = (int)Math.Round(heightDip * scale);
        // Twice: crossing to a monitor with another DPI makes WPF resize on the first move.
        for (var i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, HwndTopmost, (int)Math.Round(x), (int)Math.Round(y), w, h, Native.SWP_NOACTIVATE);
    }

    public void ShowQuietly()
    {
        if (!IsVisible) Show();
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public void FadeIn(double seconds)
    {
        if (!AnimationsEnabled)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            return;
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    public void FadeOut(double seconds, Action done)
    {
        if (!AnimationsEnabled)
        {
            done();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(seconds)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, fade);
    }
}

/// <summary>The wheel's overlay: a square of <see cref="RadialLayout.PanelSize"/> DIPs around the wheel.</summary>
internal sealed class RadialMenuWindow : RadialOverlayWindow
{
    public RadialMenuWindow() : base("Fieldkit radial menu")
    {
        Width = RadialLayout.PanelSize;
        Height = RadialLayout.PanelSize;
    }

    public RadialWheelView? Wheel { get; private set; }

    public RadialWheelView NewWheel(RadialCatalog catalog, bool dark)
    {
        Wheel = new RadialWheelView(catalog, dark, AnimationsEnabled);
        Content = Wheel;
        return Wheel;
    }
}

/// <summary>
/// The card Now Playing opens next to the wheel (upstream <c>RadialNowPlayingCard</c>): artwork,
/// title, album, artist and "Open <app>". Clicking it brings the player forward.
/// </summary>
public sealed class RadialNowPlayingCard : RadialOverlayWindow
{
    private const double CardWidth = 320;

    public RadialNowPlayingCard(RadialNowPlayingSnapshot snapshot, Action open) : base("Fieldkit now playing")
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        var content = Build(snapshot, RadialNowPlaying.AppName(snapshot), RadialNowPlaying.AppIcon(snapshot));
        content.Cursor = Cursors.Hand;
        content.MouseLeftButtonUp += (_, _) => Ui.Guard(open);
        Content = new Border { Padding = new Thickness(16), Child = content };
    }

    public static FrameworkElement Build(RadialNowPlayingSnapshot snapshot, string? appName, ImageSource? appIcon)
    {
        var name = appName ?? RadialStrings.MediaNowPlaying;
        var title = Ui.Text(snapshot.Title ?? name, 13, FontWeights.SemiBold);
        title.MaxHeight = 36;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var lines = Ui.Stack(3, title,
            snapshot.Album is null ? null : Ui.Text(snapshot.Album, 11, brush: "TextSecondary", wrap: false),
            snapshot.Artist is null ? null : Ui.Text(snapshot.Artist, 10.5, brush: "TextSecondary", wrap: false));
        var open = Ui.Row(5,
            appIcon is null ? null : new Image { Source = appIcon, Width = 14, Height = 14 },
            Ui.Text(string.Format(RadialStrings.MediaOpenAppFormat, name), 9.5, FontWeights.Medium, "TextSecondary", wrap: false),
            Ui.Icon(Glyphs.Share, 8.5, "TextSecondary"));
        open.Margin = new Thickness(0, 5, 0, 0);
        Ui.Append(lines, 0, open);
        lines.VerticalAlignment = VerticalAlignment.Center;
        lines.Margin = new Thickness(12, 0, 0, 0);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Artwork(snapshot, appIcon));
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);

        var card = new Border
        {
            Width = CardWidth,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(0.6),
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        return card;
    }

    private static FrameworkElement Artwork(RadialNowPlayingSnapshot snapshot, ImageSource? appIcon)
    {
        var art = snapshot.ArtworkData is { } data ? RadialIcons.DecodeImage(data) : null;
        var tile = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(10), ClipToBounds = true };
        if (art is not null)
        {
            tile.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            return tile;
        }
        tile.Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x2F, 0x8F), Color.FromRgb(0x14, 0x5C, 0xB8), 45);
        tile.Child = appIcon is not null
            ? new Image { Source = appIcon, Width = 42, Height = 42 }
            : Ui.Icon(Glyphs.MusicNote, 24, "TextOnAccent");
        return tile;
    }

    /// <summary>Preview of the card for review (STA tests).</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var snapshot = new RadialNowPlayingSnapshot("Midnight City", "M83", "Hurry Up, We're Dreaming", null, "Spotify.exe");
        var card = Build(snapshot, "Spotify", null);
        PreviewRenderer.Render(new Border { Padding = new Thickness(20), Child = card }, path, 360, 150, dark);
    }
}
