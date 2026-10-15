// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fieldkit.UI.Panel;
using Fieldkit.UI.Prefs;

namespace Fieldkit.Core;

/// <summary>
/// Developer tool: <c>Fieldkit.exe --render-ui &lt;dir&gt; [--theme dark|light] [--only id]</c> renders every
/// panel section and settings page to PNG files without user interaction, for visual review.
/// </summary>
public static class UiSnapshots
{
    public static void Render(string directory, string? only)
    {
        Directory.CreateDirectory(directory);
        var panel = new PanelWindow { Left = -20000, Top = -20000, ShowActivated = false };
        foreach (var section in FeatureRuntime.Registry.PanelSections.OrderBy(s => s.Order))
        {
            if (only is not null && only != section.Id) continue;
            try
            {
                panel.ShowPanel(section.Id);
                panel.Left = -20000;
                Pump(TimeSpan.FromMilliseconds(1600)); // let timers tick and first counter reads (PDH ~0.7 s) land
                Save((Visual)panel.Content, Path.Combine(directory, $"panel-{section.Id}.png"));
            }
            catch (Exception ex)
            {
                Log.Error($"Rendering panel section {section.Id} failed", ex);
            }
        }
        panel.HidePanel();

        var settings = new SettingsWindow { Left = -20000, Top = -20000, ShowActivated = false, Height = 900 };
        settings.Show();
        foreach (var page in FeatureRuntime.Registry.SettingsPages.OrderBy(p => p.Category).ThenBy(p => p.Order))
        {
            if (only is not null && only != page.Id) continue;
            try
            {
                settings.Navigate(page.Id);
                Pump(TimeSpan.FromMilliseconds(500));
                Save((Visual)settings.Content, Path.Combine(directory, $"settings-{page.Id}.png"));
            }
            catch (Exception ex)
            {
                Log.Error($"Rendering settings page {page.Id} failed", ex);
            }
        }
        settings.Close();
    }

    /// <summary>Renders every <see cref="UI.Glyphs"/> constant with its name, to check code points.</summary>
    public static void RenderGlyphSheet(string path)
    {
        var fields = typeof(UI.Glyphs).GetFields().Where(f => f.IsLiteral && f.Name != nameof(UI.Glyphs.FontFamily)).ToList();
        var panel = new System.Windows.Controls.WrapPanel { Width = 900, Background = Brushes.Black };
        foreach (var f in fields)
        {
            var cell = new System.Windows.Controls.StackPanel { Width = 110, Margin = new Thickness(4) };
            cell.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = (string)f.GetRawConstantValue()!,
                FontFamily = new FontFamily(UI.Glyphs.FontFamily),
                FontSize = 22,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            cell.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = f.Name,
                FontSize = 10,
                Foreground = Brushes.LightGray,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(cell);
        }
        panel.Measure(new Size(900, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        Save(panel, path);
    }

    /// <summary>Writes the brand mark as a multi-size .ico (PNG-compressed entries) for the executable.</summary>
    public static void WriteAppIcon(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(size =>
        {
            // Light tiles on a dark rounded badge read on any background (Explorer, taskbar, Start).
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var r = size * 0.22;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(28, 28, 30)), null, new Rect(0, 0, size, size), r, r);
                var inset = size * 0.18;
                var inner = size - inset * 2;
                var gap = inner * 0.12;
                var tile = (inner - gap) / 2;
                for (var row = 0; row < 2; row++)
                for (var col = 0; col < 2; col++)
                {
                    var brush = row == 0 && col == 1
                        ? new SolidColorBrush(Color.FromRgb(10, 132, 255))
                        : Brushes.White;
                    dc.DrawRoundedRectangle(brush, null,
                        new Rect(inset + col * (tile + gap), inset + row * (tile + gap), tile, tile), tile * 0.3, tile * 0.3);
                }
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (size, data: ms.ToArray());
        }).ToList();

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
    }

    /// <summary>Renders a code-point range of the icon font with hex labels (for choosing glyphs).</summary>
    public static void RenderGlyphRange(int from, int to, string path)
    {
        var panel = new System.Windows.Controls.WrapPanel { Width = 1600, Background = Brushes.Black };
        for (var cp = from; cp <= to; cp++)
        {
            var cell = new System.Windows.Controls.StackPanel { Width = 48, Margin = new Thickness(1) };
            cell.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = char.ConvertFromUtf32(cp),
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            cell.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = cp.ToString("X4"),
                FontSize = 9,
                Foreground = Brushes.Gold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(cell);
        }
        panel.Measure(new Size(1600, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        Save(panel, path);
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Save(Visual visual, string path)
    {
        var element = (FrameworkElement)visual;
        // Shadows widen the visual's bounds beyond its layout box and would offset the picture.
        var effect = element.Effect;
        element.Effect = null;
        element.UpdateLayout();
        var w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            var background = Application.Current?.TryFindResource("WindowBackground") as Brush
                             ?? new SolidColorBrush(Color.FromRgb(40, 40, 40));
            dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
        }
        bitmap.Render(bg);
        // Draw through a brush so the element lands at the origin: rendering the visual directly keeps
        // its layout offset (margins), which shifted panels right and cut off their edge.
        var content = new DrawingVisual();
        using (var dc = content.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, w, h));
        }
        bitmap.Render(content);
        element.Effect = effect;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
