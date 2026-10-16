// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Core.Updates;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Shell;

/// <summary>
/// "Fieldkit 0.5.0 is available. Update now?": the release notes and three choices (update now, later, skip this
/// version). An installed copy downloads, checks and runs the installer and restarts; the portable copy is pointed
/// at the download page, because it cannot replace the file it is running from.
/// </summary>
public sealed class UpdatePromptWindow : ToolWindow
{
    private readonly UpdateRelease _release;
    private readonly InstallKind _kind;
    private readonly Button _update;
    private readonly Button _later;
    private readonly Button _skip;
    private readonly StackPanel _progressPanel;
    private readonly TextBlock _progressText;
    private readonly ProgressBar _bar;
    private readonly TextBlock _error;
    private CancellationTokenSource? _cancel;
    private bool _working;

    public static void ShowFor(UpdateRelease release) =>
        ShowSingle("update", () => new UpdatePromptWindow(release, UpdateService.Kind));

    internal UpdatePromptWindow(UpdateRelease release, InstallKind kind) : base("Fieldkit update", 540, 460)
    {
        _release = release;
        _kind = kind;
        ResizeMode = ResizeMode.NoResize;
        MinHeight = 0;
        MinWidth = 0;

        var notes = UpdateLogic.NotesExcerpt(release.Notes);
        var intro = kind == InstallKind.Installed
            ? $"You have {UpdateService.Current}. Fieldkit downloads the new installer, checks it against the checksum GitHub published for it, installs it and starts again."
            : $"You have {UpdateService.Current}, the portable version. It cannot replace the file it runs from: download the new Fieldkit.exe and unzip it over this one.";

        _progressText = Ui.Caption(string.Empty);
        _bar = new ProgressBar { Minimum = 0, Maximum = 1 };
        _progressPanel = (StackPanel)Ui.Stack(6, _progressText, _bar);
        _progressPanel.Visibility = Visibility.Collapsed;
        _error = Ui.Text(string.Empty, 12.5, brush: "Red");
        _error.Visibility = Visibility.Collapsed;

        _update = kind == InstallKind.Installed
            ? Ui.Button("Update now", () => _ = UpdateNowAsync(), accent: true)
            : Ui.Button("Open download page", OpenPage, accent: true);
        _later = Ui.Button("Later", Close);
        _skip = Ui.Button("Skip this version", () =>
        {
            UpdateService.Skip(_release);
            Close();
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { _skip, _later, _update })
        {
            b.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(b);
        }

        var whatsNew = notes.Length == 0
            ? null
            : Ui.Stack(6, Ui.Title("What's new", 14),
                Ui.Card(new ScrollViewer
                {
                    Content = Ui.Text(notes, 13),
                    MaxHeight = 170,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                }, new Thickness(14, 10, 14, 10)));

        var link = Ui.Button("Full release notes", OpenPage);
        link.HorizontalAlignment = HorizontalAlignment.Left;

        Content = new Border
        {
            Padding = new Thickness(26, 24, 26, 20),
            Child = Ui.Stack(14,
                Ui.Text($"Fieldkit {release.Version} is available", 20, FontWeights.SemiBold),
                Ui.Caption(intro, 13),
                whatsNew,
                notes.Length == 0 ? link : null,
                _progressPanel,
                _error,
                buttons),
        };
        SizeToContent = SizeToContent.Height;
        Closing += OnClosing;
    }

    private void OpenPage() => FeatureHubPage.OpenUri(string.IsNullOrEmpty(_release.PageUrl) ? UpdateLogic.ReleasesPage : _release.PageUrl);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_working && _cancel is { } cancel && !cancel.IsCancellationRequested)
        {
            // Closing while the file is still arriving cancels the download; once the installer runs there is no window left to close.
            cancel.Cancel();
        }
    }

    private async Task UpdateNowAsync()
    {
        if (_working) return;
        _working = true;
        _cancel = new CancellationTokenSource();
        _error.Visibility = Visibility.Collapsed;
        foreach (var b in new[] { _update, _skip }) b.IsEnabled = false;
        _later.Content = "Cancel";
        _progressPanel.Visibility = Visibility.Visible;
        _progressText.Text = "Starting the download…";
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                _bar.Maximum = p.Total;
                _bar.Value = p.Done;
                _progressText.Text = $"Downloading… {p.Done / 1048576.0:0.0} of {p.Total / 1048576.0:0.0} MB";
            });
            var (path, asset) = await UpdateService.Service.DownloadInstallerAsync(_release, RuntimeInformation.ProcessArchitecture, AppUpdater.TempDirectory, progress, _cancel.Token);
            _progressText.Text = "Checked against GitHub's checksum. Installing… Fieldkit will start again by itself.";
            _bar.IsIndeterminate = true;
            _later.IsEnabled = false;
            var allUsers = UpdateLogic.IsAllUsersInstall(AppContext.BaseDirectory,
                [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]);
            AppUpdater.LaunchInstaller(path, asset, allUsers);
            Log.Info($"Update to {_release.Version} started");
            // Give the installer a moment to take over, then step aside so it can replace the files.
            await Task.Delay(1200);
            UpdateService.QuitAction();
        }
        catch (OperationCanceledException)
        {
            Reset("The download was cancelled.");
        }
        catch (UpdateException ex)
        {
            Reset(ex.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Reset("The update was cancelled at the permission prompt. Fieldkit is unchanged.");
        }
        catch (Exception ex)
        {
            Log.Error("Updating failed", ex);
            Reset("The update could not be installed. You can download it by hand from the release page.");
        }
    }

    private void Reset(string message)
    {
        _working = false;
        _progressPanel.Visibility = Visibility.Collapsed;
        _bar.IsIndeterminate = false;
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        _update.IsEnabled = true;
        _skip.IsEnabled = true;
        _later.IsEnabled = true;
        _later.Content = "Later";
        _update.Content = "Try again";
    }
}
