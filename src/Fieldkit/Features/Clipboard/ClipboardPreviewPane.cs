// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>The history's preview sidebar (upstream ClipboardEntryPreviewSidebar): the whole entry, editable for text.</summary>
public static class ClipboardPreviewPane
{
    public static FrameworkElement Build(ClipboardEntry? entry, bool editing, Action onClose, Action onEdit, Action onCancel,
        Action<string> onSave, Action onCopy)
    {
        var header = new Grid { Margin = new Thickness(12, 9, 8, 9) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Row(6, Ui.Icon(Glyphs.Eye, 12, "TextSecondary"), Ui.Text("Preview", 12, FontWeights.SemiBold, wrap: false)));
        var close = Ui.IconButton(Glyphs.Close, "Close", onClose, 10);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        FrameworkElement body;
        TextBox? editor = null;
        if (entry is null)
        {
            var empty = Ui.Stack(8, Ui.Icon(Glyphs.Clipboard, 24, "TextTertiary"), Ui.Caption("Nothing selected"));
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            foreach (var c in empty.Children.OfType<FrameworkElement>()) c.HorizontalAlignment = HorizontalAlignment.Center;
            body = empty;
        }
        else
        {
            switch (entry.Kind)
            {
                case ClipboardEntryKind.Image:
                    body = ImageBody(entry);
                    break;
                case ClipboardEntryKind.Files:
                    body = FilesBody(entry);
                    break;
                default:
                    editor = new TextBox
                    {
                        Text = editing ? entry.Text : ClipboardJsonFormat.Pretty(entry.Text) ?? entry.Text,
                        IsReadOnly = !editing,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        AcceptsTab = true,
                        BorderThickness = new Thickness(editing ? 1 : 0),
                        Background = editing ? null : Brushes.Transparent,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        FontSize = 12.5,
                        Padding = new Thickness(4),
                        VerticalContentAlignment = VerticalAlignment.Top,
                    };
                    if (ClipboardJsonFormat.Pretty(entry.Text) is not null && !editing) editor.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    body = editor;
                    if (editing) editor.Loaded += (_, _) => editor.Focus();
                    break;
            }
        }
        body.Margin = new Thickness(12, 0, 12, 0);

        var footer = new Grid { Margin = new Thickness(12, 8, 12, 10) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (entry is not null)
        {
            var meta = Ui.Row(6,
                entry.IsPinned ? Ui.Icon(Glyphs.Pin, 10, "Accent") : null,
                Ui.Text(entry.CopiedAt.ToString("t", CultureInfo.CurrentCulture), 10.5, brush: "TextTertiary", wrap: false));
            footer.Children.Add(meta);
            StackPanel buttons;
            if (editing && editor is not null)
            {
                var cancel = Ui.Button("Cancel", onCancel);
                var save = Ui.Button("Save", () => onSave(editor.Text), accent: true);
                buttons = Ui.Row(6, cancel, save);
            }
            else
            {
                var copy = Ui.Button("Copy", onCopy);
                buttons = entry.Kind == ClipboardEntryKind.Text ? Ui.Row(6, Ui.Button("Edit", onEdit), copy) : Ui.Row(6, copy);
            }
            foreach (var b in buttons.Children.OfType<Button>())
            {
                b.Padding = new Thickness(9, 3, 9, 4);
                b.FontSize = 12;
            }
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);
        }

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        return root;
    }

    private static FrameworkElement ImageBody(ClipboardEntry entry)
    {
        var bytes = ClipboardHistoryStore.ReadImage(entry.ImageFile);
        var bitmap = bytes is null ? null : ClipboardImageCodec.Decode(bytes, 600);
        var caption = Ui.Caption($"Image · {entry.DimensionsLabel}", 11.5);
        if (bitmap is null) return caption;
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var panel = new DockPanel();
        DockPanel.SetDock(caption, Dock.Bottom);
        caption.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(caption);
        panel.Children.Add(image);
        return panel;
    }

    private static FrameworkElement FilesBody(ClipboardEntry entry)
    {
        var stack = Ui.Stack(8);
        stack.Children.Add(Ui.Text(entry.FilePaths.Count == 1 ? "1 file" : $"{entry.FilePaths.Count} files", 12, FontWeights.SemiBold));
        foreach (var path in entry.FilePaths.Take(200))
        {
            var exists = File.Exists(path) || Directory.Exists(path);
            var parts = new List<string>();
            if (ClipboardHistoryLogic.IsImageFileName(path) && ClipboardImageCodec.Dimensions(path) is { } d) parts.Add($"{d.Width}×{d.Height}");
            if (File.Exists(path)) parts.Add(FormatSize(new FileInfo(path).Length));
            var icon = AppIcons.Element(AppIcons.ForPath(exists ? path : null), 20, Glyphs.Document);
            var name = Ui.Text(Path.GetFileName(path), 12.5, FontWeights.Medium, exists ? "TextPrimary" : "TextTertiary", wrap: false);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var detail = Ui.Text(parts.Count > 0 ? string.Join(" · ", parts) : path, 10.5, brush: "TextTertiary", wrap: false);
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            var text = Ui.Stack(1, name, detail);
            text.ToolTip = path;
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            icon.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(icon);
            row.Children.Add(text);
            Ui.Append(stack, 8, row);
        }
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : $"{value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}

/// <summary>
/// Lays copied JSON out for reading in the preview. Only the whitespace between tokens changes, so the
/// preview shows what the paste will give (upstream ClipboardJsonFormat).
/// </summary>
public static class ClipboardJsonFormat
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxOutputBytes = 4 * MaxBytes;

    public static string? Pretty(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return null;
        var first = text.FirstOrDefault(c => !char.IsWhiteSpace(c));
        if (first is not ('{' or '[')) return null;
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
        var sb = new StringBuilder(text.Length * 2);
        int depth = 0, i = 0;
        bool inString = false, escaped = false;
        void NewLine()
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }
        while (i < text.Length)
        {
            if (sb.Length > MaxOutputBytes) return null;
            var c = text[i++];
            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case ' ' or '\t' or '\n' or '\r':
                    continue;
                case '"':
                    inString = true;
                    sb.Append(c);
                    break;
                case '{' or '[':
                    sb.Append(c);
                    var next = i;
                    while (next < text.Length && text[next] is ' ' or '\t' or '\n' or '\r') next++;
                    if (next < text.Length && text[next] == (c == '{' ? '}' : ']'))
                    {
                        sb.Append(text[next]);
                        i = next + 1;
                    }
                    else
                    {
                        depth++;
                        NewLine();
                    }
                    break;
                case '}' or ']':
                    // A trailing comma's line break would leave an empty line before the bracket.
                    while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                    if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
                    depth--;
                    NewLine();
                    sb.Append(c);
                    break;
                case ',':
                    sb.Append(c);
                    NewLine();
                    break;
                case ':':
                    sb.Append(": ");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
