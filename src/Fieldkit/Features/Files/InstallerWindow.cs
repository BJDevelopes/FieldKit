// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.UI;

namespace Fieldkit.Features.Files;

/// <summary>
/// The installer helper: the newest disk images and ZIP archives in Downloads, each with an Install
/// button, plus "Choose file…". Shows progress while an installation runs and its outcome afterwards.
/// </summary>
public sealed class InstallerWindow : FloatingWindow
{
    private const double CardWidth = 460;
    private const string ArchiveGlyph = Glyphs.Package; // U+E7B8, a box

    private static InstallerWindow? _instance;

    private readonly StackPanel _list = Ui.Stack(2);
    private readonly TextBlock _subtitle = Ui.Caption(string.Empty, 12.5);
    private readonly Border _status;
    private readonly TextBlock _statusIcon = Ui.Icon(Glyphs.Refresh, 16);
    private readonly TextBlock _statusTitle = Ui.Text(string.Empty, 13.5, FontWeights.SemiBold);
    private readonly TextBlock _statusBody = Ui.Caption(string.Empty, 12.5);
    private readonly Button _choose;
    private readonly RotateTransform _spin = new();
    private readonly bool _isPreview;

    private InstallerWindow(bool isPreview = false)
    {
        _isPreview = isPreview;
        Title = InstallerText.Title;
        Width = CardWidth + 2 * ShadowMargin;
        SizeToContent = SizeToContent.Height;

        var closeButton = HeaderButton(Glyphs.Close, "Close", Dismiss);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Ui.IconTile(Glyphs.Disc, 36, "AccentSubtle", "Accent");
        var titles = Ui.Stack(1, Ui.Title(InstallerText.Title, 15), _subtitle);
        titles.Margin = new Thickness(12, 0, 8, 0);
        titles.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(titles, 1);
        closeButton.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(closeButton, 2);
        header.Children.Add(tile);
        header.Children.Add(titles);
        header.Children.Add(closeButton);

        var scroller = new ScrollViewer
        {
            Content = _list,
            MaxHeight = 372,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _statusIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        _statusIcon.VerticalAlignment = VerticalAlignment.Top;
        _statusIcon.Margin = new Thickness(0, 1, 0, 0);
        var statusText = Ui.Stack(2, _statusTitle, _statusBody);
        statusText.Margin = new Thickness(10, 0, 0, 0);
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.Children.Add(_statusIcon);
        Grid.SetColumn(statusText, 1);
        statusGrid.Children.Add(statusText);
        _status = Ui.Card(statusGrid, new Thickness(12, 10, 12, 10), 10);
        _status.Visibility = Visibility.Collapsed;

        _choose = Ui.Button("Choose file…", ChooseFile, glyph: Glyphs.Folder);
        var refresh = Ui.IconButton(Glyphs.Refresh, "Refresh", () => _ = LoadAsync());
        var footer = Ui.Split(Ui.Caption("Installs, ejects and tidies up.", 12), Ui.Row(4, refresh, _choose));

        var body = Ui.Stack(12, header, Ui.Divider(0), scroller, _status, footer);
        body.Margin = new Thickness(16, 14, 16, 14);
        Body = body;
    }

    /// <summary>Opens (or brings back) the installer helper on the pointer's screen.</summary>
    public static void Open()
    {
        var window = _instance ??= new InstallerWindow();
        _ = window.LoadAsync();
        var wasVisible = window.IsVisible;
        window.Show();
        window.Activate();
        if (!wasVisible)
        {
            window.UpdateLayout();
            window.CenterOnPointerScreen(0.4);
        }
    }

    public override void Dismiss()
    {
        if (_isPreview) return;
        Hide();
        if (!InstallerRunner.IsBusy) _status.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- list

    private async Task LoadAsync()
    {
        var folder = InstallerNative.DownloadsFolder();
        _subtitle.Text = $"Newest disk images and ZIP archives in {Path.GetFileName(folder.TrimEnd('\\'))}";
        var files = await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(folder)) return (IReadOnlyList<InstallerDownload>)[];
                var all = new DirectoryInfo(folder)
                    .EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false })
                    .Where(f => InstallerFiles.IsSupported(f.Name))
                    .Select(f => new InstallerDownload(f.FullName, f.LastWriteTime, f.Length));
                return InstallerFiles.Newest(all);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Listing {folder} failed: {ex.Message}");
                return (IReadOnlyList<InstallerDownload>)[];
            }
        });
        ShowDownloads(files);
    }

    private void ShowDownloads(IReadOnlyList<InstallerDownload> files)
    {
        _list.Children.Clear();
        if (files.Count == 0)
        {
            var empty = Ui.Stack(6, Ui.Icon(Glyphs.Download, 24, "TextTertiary"),
                Ui.Caption("No disk images or ZIP archives in Downloads. Use Choose file… to pick one elsewhere.", 12.5));
            ((TextBlock)empty.Children[1]).TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(24, 18, 24, 18);
            _list.Children.Add(empty);
            return;
        }
        var now = DateTime.Now;
        foreach (var file in files) Ui.Append(_list, 2, Row(file, now));
        UpdateBusy();
    }

    private FrameworkElement Row(InstallerDownload file, DateTime now)
    {
        var fallback = InstallerFiles.KindOf(file.Path) == InstallerSourceKind.Zip ? ArchiveGlyph : Glyphs.Disc;
        var icon = _isPreview
            ? Ui.IconTile(fallback, 32)
            : AppIcons.Element(AppIcons.ForPath(file.Path), 32, fallback);
        var name = Ui.Text(Path.GetFileName(file.Path), 13.5, FontWeights.Medium, wrap: false);
        name.ToolTip = file.Path;
        var meta = Ui.Caption($"{InstallerFiles.KindLabel(file.Path)} · {InstallerFiles.FormatSize(file.Size)} · {InstallerFiles.FormatAge(file.Modified, now)}", 12);
        meta.TextWrapping = TextWrapping.NoWrap;
        meta.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = Ui.Stack(1, name, meta);
        text.Margin = new Thickness(10, 0, 10, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        var install = Ui.Button(InstallerText.InstallButton, () => _ = InstallAsync(file.Path));
        install.MinWidth = 76;
        install.Tag = "install";
        install.VerticalAlignment = VerticalAlignment.Center;

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(icon);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        Grid.SetColumn(install, 2);
        g.Children.Add(install);

        var row = new Border { Child = g, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 7, 8, 7), Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private void ChooseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a disk image or archive",
            Filter = InstallerFiles.DialogFilter,
            InitialDirectory = InstallerNative.DownloadsFolder(),
            CheckFileExists = true,
        };
        if (WithoutAutoDismiss(() => dialog.ShowDialog(this) == true)) _ = InstallAsync(dialog.FileName);
    }

    // ---------------------------------------------------------------- install

    private async Task InstallAsync(string path)
    {
        if (InstallerRunner.IsBusy) return;
        var name = InstallerText.DisplayName(null, Path.GetFileName(path));
        HideOnDeactivate = false;
        ShowProgress(InstallerText.Installing(name), "Preparing…");
        InstallerResult result;
        try
        {
            result = await InstallerRunner.InstallAsync(path, Confirm, line => ShowProgress(InstallerText.Installing(name), line));
        }
        finally
        {
            Topmost = true;
            HideOnDeactivate = true;
        }
        var (title, body, warning, notify) = InstallerText.Describe(result);
        ShowOutcome(title, body, warning);
        if (notify) Notifier.Show(title, body, warning);
        await LoadAsync();
    }

    private bool Confirm(InstallerPrompt prompt)
    {
        var ok = WithoutAutoDismiss(() => ConfirmDialog.Show(this, prompt.Title, prompt.Body, prompt.ConfirmText, destructive: false));
        // Let the installer's own windows come to the front while it runs.
        if (ok) Topmost = false;
        return ok;
    }

    private void ShowProgress(string title, string line)
    {
        _status.Visibility = Visibility.Visible;
        _statusTitle.Text = title;
        _statusBody.Text = line;
        _statusIcon.Text = Glyphs.Refresh;
        _statusIcon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _statusIcon.RenderTransform = _spin;
        if (!_isPreview)
            _spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        UpdateBusy();
    }

    private void ShowOutcome(string title, string body, bool warning)
    {
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _statusIcon.RenderTransform = null;
        _status.Visibility = Visibility.Visible;
        _statusTitle.Text = title;
        _statusBody.Text = body;
        _statusIcon.Text = warning ? Glyphs.Warning : Glyphs.Check;
        _statusIcon.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Orange" : "Green");
        UpdateBusy();
    }

    private void UpdateBusy()
    {
        var busy = InstallerRunner.IsBusy;
        _choose.IsEnabled = !busy;
        foreach (var row in _list.Children.OfType<Border>())
        {
            if (row.Child is Grid g)
                foreach (var b in g.Children.OfType<Button>().Where(b => (string?)b.Tag == "install")) b.IsEnabled = !busy;
        }
    }

    // ---------------------------------------------------------------- preview

    /// <summary>Renders the window with sample rows and a progress line (for UI review, no disk access).</summary>
    public static void RenderPreview(string path, bool? light = null)
    {
        var window = new InstallerWindow(isPreview: true)
        {
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            HideOnDeactivate = false,
        };
        PreviewRendering.ApplyTheme(window, light);
        var now = DateTime.Now;
        window._subtitle.Text = "Newest disk images and ZIP archives in Downloads";
        window.ShowDownloads(
        [
            new InstallerDownload(@"C:\Users\Sample\Downloads\VeraCrypt_Setup_x64_1.26.iso", now.AddMinutes(-4), 38_400_000),
            new InstallerDownload(@"C:\Users\Sample\Downloads\WinMerge-2.16.44-x64-PerUser.zip", now.AddHours(-3), 9_800_000),
            new InstallerDownload(@"C:\Users\Sample\Downloads\ubuntu-24.04.1-desktop-amd64.iso", now.AddDays(-1), 6_114_656_256),
            new InstallerDownload(@"C:\Users\Sample\Downloads\DevBox.vhdx", now.AddDays(-5), 21_474_836_480),
            new InstallerDownload(@"C:\Users\Sample\Downloads\paint.net.5.1.portable.x64.zip", now.AddDays(-12), 81_300_000),
        ]);
        window.ShowProgress(InstallerText.Installing("VeraCrypt_Setup_x64_1.26"), "Waiting for the VeraCrypt_Setup_x64_1.26 installer to finish…");
        window.Show();
        PreviewRendering.Save(window, path);
        window.Close();
    }

    /// <summary>Renders the outcome state (for UI review).</summary>
    public static void RenderOutcomePreview(string path, InstallerResult result, bool? light = null)
    {
        var window = new InstallerWindow(isPreview: true)
        {
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            HideOnDeactivate = false,
        };
        PreviewRendering.ApplyTheme(window, light);
        window._subtitle.Text = "Newest disk images and ZIP archives in Downloads";
        window.ShowDownloads([]);
        var (title, body, warning, _) = InstallerText.Describe(result);
        window.ShowOutcome(title, body, warning);
        window.Show();
        PreviewRendering.Save(window, path);
        window.Close();
    }
}
