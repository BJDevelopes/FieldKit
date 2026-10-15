// SPDX-License-Identifier: GPL-3.0-or-later
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.UI;

/// <summary>The app's notification-area icon (the Windows counterpart of the menu bar item).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _current;

    public event Action? LeftClick;
    public event Action? OpenSettings;
    public event Action? Quit;

    public TrayIcon()
    {
        _icon = new NotifyIcon { Text = AppInfo.Name, Visible = false };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) LeftClick?.Invoke();
        };
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open panel", null, (_, _) => LeftClick?.Invoke());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"Quit {AppInfo.Name}", null, (_, _) => Quit?.Invoke());
        _icon.ContextMenuStrip = menu;
        RefreshIcon();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        TrayStatus.Changed += RefreshIcon;
        Notifier.Sink = ShowBalloon;
    }

    public void Show() => _icon.Visible = true;

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string message, bool warning)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) Ui.OnUi(RefreshIcon);
    }

    private void RefreshIcon()
    {
        var size = (int)Math.Round(16 * WindowHelper.GetScaleAt(0, 0));
        var old = _current;
        var badges = TrayStatus.Current;
        Color? dot = badges.Count > 0 ? Color.FromArgb(badges[0].Rgb | unchecked((int)0xFF000000)) : null;
        _current = RenderBrandIcon(TaskbarIsLight(), Math.Max(16, size), dot);
        _icon.Icon = _current;
        SetTooltip(badges.Count == 0 ? AppInfo.Name : string.Join(Environment.NewLine, [AppInfo.Name, .. badges.Select(b => b.Text)]));
        if (old is not null)
        {
            Native.DestroyIcon(old.Handle);
            old.Dispose();
        }
    }

    /// <summary>Whether the taskbar uses the light theme (so the glyph must be dark).</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The brand mark: four mosaic tiles, the top-right one accented. Drawn at runtime so it
    /// stays crisp at every DPI and follows the taskbar theme.
    /// </summary>
    public static Icon RenderBrandIcon(bool lightTaskbar, int size, Color? statusDot = null)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var fg = lightTaskbar ? Color.FromArgb(28, 28, 30) : Color.White;
            var accent = Color.FromArgb(10, 132, 255);
            var gap = size * 0.1f;
            var tile = (size - gap * 3) / 2f;
            for (var row = 0; row < 2; row++)
            {
                for (var col = 0; col < 2; col++)
                {
                    var x = gap + col * (tile + gap);
                    var y = gap + row * (tile + gap);
                    using var path = RoundedRect(new RectangleF(x, y, tile, tile), tile * 0.3f);
                    using var brush = new SolidBrush(row == 0 && col == 1 ? accent : fg);
                    g.FillPath(brush, path);
                }
            }
            if (statusDot is { } c)
            {
                // A status dot over the bottom-right tile, ringed so it reads on any taskbar.
                var d = size * 0.5f;
                var r = new RectangleF(size - d, size - d, d - 0.5f, d - 0.5f);
                using var ring = new SolidBrush(lightTaskbar ? Color.White : Color.FromArgb(28, 28, 30));
                g.FillEllipse(ring, r);
                r.Inflate(-size * 0.07f, -size * 0.07f);
                using var fill = new SolidBrush(c);
                g.FillEllipse(fill, r);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Renders short text (e.g. "42°" or "23%") into a tray-sized icon, for menu-bar-style readouts.
    /// Up to two lines. The caller owns (and must DestroyIcon) the result.
    /// </summary>
    public static Icon RenderTextIcon(string line1, string? line2 = null, Color? color = null)
    {
        var size = Math.Max(16, (int)Math.Round(16 * WindowHelper.GetScaleAt(0, 0)));
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var fg = color ?? (TaskbarIsLight() ? Color.FromArgb(28, 28, 30) : Color.White);
            using var brush = new SolidBrush(fg);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            if (line2 is null)
            {
                using var font = FitFont(g, line1, size, size);
                g.DrawString(line1, font, brush, new RectangleF(-2, 0, size + 4, size), format);
            }
            else
            {
                using var font = FitFont(g, line1.Length > line2.Length ? line1 : line2, size, size / 2f);
                g.DrawString(line1, font, brush, new RectangleF(-2, 0, size + 4, size / 2f), format);
                g.DrawString(line2, font, brush, new RectangleF(-2, size / 2f, size + 4, size / 2f), format);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Font FitFont(Graphics g, string text, float width, float height)
    {
        for (var pt = height * 0.9f; pt > 4; pt -= 0.5f)
        {
            var font = new Font("Segoe UI Semibold", pt, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            if (measured.Width <= width + 2 && measured.Height <= height + 2) return font;
            font.Dispose();
        }
        return new Font("Segoe UI", 5, GraphicsUnit.Pixel);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        TrayStatus.Changed -= RefreshIcon;
        _icon.Visible = false;
        _icon.Dispose();
        if (_current is not null) Native.DestroyIcon(_current.Handle);
    }
}
