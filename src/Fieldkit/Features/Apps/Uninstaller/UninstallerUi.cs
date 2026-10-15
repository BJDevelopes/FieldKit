// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.IO.Path;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>Strings (upstream enUS wording, adapted to Windows) and small view helpers shared by the uninstaller views.</summary>
public static class UninstallerText
{
    public const string Name = "Uninstaller";
    public const string EnableCaption = "Removes an app together with the caches, preferences, logs and leftovers it leaves behind.";
    public const string Step1 = "Drag an app onto the Uninstaller, or pick one from the list.";
    public const string Step2 = "Review the files found and how much space they take.";
    public const string Step3 = "Move what you want to the Recycle Bin. Nothing is deleted permanently.";
    public const string MenuItem = "Uninstall an app…";
    public const string DropTitle = "Drag an app here";
    public const string DropSubtitle = "or choose one to scan";
    public const string Choose = "Choose app…";
    public const string Browse = "Browse…";
    public const string PickerTitle = "Choose app";
    public const string PickerSearch = "Search apps";
    public const string PickerEmpty = "No apps found";
    public const string EmptyNote = "Nothing is removed without your confirmation.";
    public const string Scanning = "Scanning files…";
    public const string Removing = "Moving to the Recycle Bin…";
    public const string FoundTitle = "found";
    public const string SelectedFormat = "{0} of {1} selected";
    public const string Remove = "Move to Recycle Bin";
    public const string Uninstall = "Uninstall";
    public const string Cancel = "Cancel";
    public const string KeepLeftovers = "Keep leftovers";
    public const string DoneTitle = "Done!";
    public const string FreedFormat = "{0} removed";
    public const string SomeFailed = "Some items couldn’t be moved to the Recycle Bin.";
    public const string FailedMoreFormat = "and {0} more";
    public const string Another = "Uninstall another";
    public const string OptionalSection = "Optional, review before";
    public const string Reveal = "Reveal in File Explorer";
    public const string CommandBarToggle = "Show in Command Bar";
    public const string CommandBarCaption = "Choose and uninstall apps in the Command Bar.";
    public const string SelectionUnavailable = "That file doesn’t belong to an app that can be removed. Choose another from the list.";
    public const string AdminNote = "Some items are in machine-wide locations (ProgramData, Program Files, the all-users Start menu or HKLM). Run Fieldkit as administrator to remove them.";
    public const string AdminRequired = "Needs administrator rights";

    public static string Category(LeftoverCategory c) => c switch
    {
        LeftoverCategory.App => "Application",
        LeftoverCategory.Support => "Support",
        LeftoverCategory.Caches => "Caches",
        LeftoverCategory.Preferences => "Preferences",
        LeftoverCategory.Containers => "Containers",
        LeftoverCategory.Logs => "Logs",
        LeftoverCategory.Shortcuts => "Shortcuts",
        _ => "Other",
    };
}

/// <summary>Segoe Fluent Icons code points used only here (checked with --render-glyph-range).</summary>
internal static class UninstallerGlyphs
{
    public const string AppDefault = "";
    public const string CheckCircle = "";
    public const string Question = "";
    public const string Key = "";
}

internal static class UninstallerUi
{
    private static ControlTemplate? _dangerTemplate;

    /// <summary>A red prominent button (upstream's .borderedProminent .tint(.red)).</summary>
    public static Button DangerButton(string text, Action onClick, string? glyph = Glyphs.Delete)
    {
        var label = Ui.Text(text, 13, FontWeights.SemiBold, "TextOnAccent", wrap: false);
        var content = glyph is null ? (UIElement)label : Ui.Row(7, Ui.Icon(glyph, 13, "TextOnAccent"), label);
        var b = new Button { Content = content, Padding = new Thickness(14, 7, 14, 7), MinWidth = 96, Cursor = System.Windows.Input.Cursors.Hand };
        b.Template = _dangerTemplate ??= (ControlTemplate)XamlReader.Parse(
            """
            <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Border x:Name="Bd" CornerRadius="8" Background="{DynamicResource Red}" SnapsToDevicePixels="True">
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Opacity" Value="0.85"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Bd" Property="Opacity" Value="0.7"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        b.Click += (_, _) => Ui.Guard(onClick);
        return b;
    }

    /// <summary>A plain (non-accent) button with comfortable padding.</summary>
    public static Button PlainButton(string text, Action onClick, string? glyph = null)
    {
        var b = Ui.Button(text, onClick, glyph: glyph);
        b.Padding = new Thickness(14, 7, 14, 7);
        b.MinWidth = 80;
        return b;
    }

    /// <summary>An indeterminate spinner (rotating arc).</summary>
    public static FrameworkElement Spinner(double size = 30)
    {
        var arc = new System.Windows.Shapes.Path
        {
            Width = size,
            Height = size,
            StrokeThickness = Math.Max(2.5, size / 10),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            RenderTransformOrigin = new Point(0.5, 0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var r = size / 2 - arc.StrokeThickness;
        var c = size / 2;
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(new Point(c - r, c), new Size(r, r), 0, true, SweepDirection.Clockwise, true));
        arc.Data = new PathGeometry([figure]);
        arc.SetResourceReference(Shape.StrokeProperty, "Accent");
        var rotate = new RotateTransform();
        arc.RenderTransform = rotate;
        arc.Loaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        arc.Unloaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        return arc;
    }

    /// <summary>The dashed "Drag an app here" zone.</summary>
    public static Border DropZone(double height, bool large)
    {
        var dash = new Rectangle
        {
            RadiusX = large ? 18 : 12,
            RadiusY = large ? 18 : 12,
            StrokeThickness = large ? 2 : 1.5,
            StrokeDashArray = large ? [4.5, 3.5] : [4, 3.3],
        };
        dash.SetResourceReference(Shape.StrokeProperty, "TextTertiary");
        var icon = Ui.Icon(Glyphs.Delete, large ? 40 : 26, "TextSecondary");
        var title = Ui.Text(UninstallerText.DropTitle, large ? 16 : 13, FontWeights.SemiBold, wrap: false);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var subtitle = Ui.Text(UninstallerText.DropSubtitle, large ? 12.5 : 11.5, brush: "TextSecondary", wrap: false);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = Ui.Stack(large ? 10 : 4, icon, title, subtitle);
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        var grid = new Grid { Height = height };
        grid.Children.Add(dash);
        grid.Children.Add(stack);
        var border = new Border { Child = grid, CornerRadius = new CornerRadius(large ? 18 : 12), Background = Brushes.Transparent };
        border.Tag = (dash, icon);
        return border;
    }

    /// <summary>Highlights a drop zone while something is dragged over it.</summary>
    public static void SetDropTargeted(Border zone, bool targeted)
    {
        if (zone.Tag is not (Rectangle dash, TextBlock icon)) return;
        dash.SetResourceReference(Shape.StrokeProperty, targeted ? "Accent" : "TextTertiary");
        icon.SetResourceReference(TextBlock.ForegroundProperty, targeted ? "Accent" : "TextSecondary");
        if (targeted) zone.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
        else zone.Background = Brushes.Transparent;
    }

    /// <summary>An app icon at <paramref name="size"/>, or the generic app glyph.</summary>
    public static FrameworkElement AppIcon(ImageSource? icon, double size)
    {
        if (icon is not null)
            return new Image { Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        return Ui.IconTile(UninstallerGlyphs.AppDefault, size, "ControlBackground", "TextSecondary");
    }

    /// <summary>A small coloured notice ("The uninstaller didn't start…").</summary>
    public static FrameworkElement Notice(string glyph, string text, string brush = "Orange")
    {
        var icon = Ui.Icon(glyph, 14, brush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 0, 0);
        var caption = Ui.Text(text, 12, brush: "TextSecondary");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        caption.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(caption, 1);
        g.Children.Add(icon);
        g.Children.Add(caption);
        return Ui.Card(g, new Thickness(12, 9, 12, 9), 10);
    }

    /// <summary>Opens File Explorer with the item selected (or its parent when it is gone).</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Reveal {path} failed: {ex.Message}");
        }
    }
}
