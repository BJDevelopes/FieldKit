// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Screen recording feature service: the record shortcut opens the selector on Record (or stops a
/// running take); uninstalling stops and saves any take in progress.
/// </summary>
public sealed class ScreenRecorderService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => "screenRecorder";

    protected override void OnStart() =>
        _hotkey = Hotkeys.Bind(CaptureSettings.RecorderShortcut, string.Empty, () => CaptureEntry.Start(CaptureTool.Recording, fromShortcut: true));

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (CaptureSelector.IsActive) CaptureSelector.CancelActive();
        RecorderController.Shutdown();
    }
}

/// <summary>Owns the take in progress: countdown, controls, stop, then editor or straight save.</summary>
public static class RecorderController
{
    private static ScreenRecording? _recording;
    private static RecorderControlsWindow? _controls;
    private static DispatcherTimer? _countdown;
    private static DispatcherTimer? _diskWatch;
    private static bool _stopping;

    public static bool IsActive => _recording is not null || _countdown is not null;
    public static bool IsRecording => _recording is not null;
    public static event Action? StateChanged;

    public static void Toggle()
    {
        if (!StopIfActive()) CaptureEntry.Start(CaptureTool.Recording, fromShortcut: false);
    }

    /// <summary>Stops (or cancels the countdown of) the active take. True when there was one.</summary>
    public static bool StopIfActive()
    {
        if (_countdown is not null)
        {
            _countdown.Stop();
            _countdown = null;
            Hud.Hide();
            StateChanged?.Invoke();
            return true;
        }
        if (_recording is null) return false;
        _ = StopAsync(openEditor: Settings.GetBool(CaptureSettings.RecorderOpenEditor, true));
        return true;
    }

    public static void Record(CaptureOutcome.Region region)
    {
        if (IsActive || !FeatureCatalog.IsAvailable("screenRecorder")) return;
        var folder = CaptureSettings.PrivateFolder("Recordings");
        if (FreeBytes(folder) is { } free && free < 1_000_000_000)
        {
            Hud.Show(Glyphs.Warning, "Not enough space to record");
            return;
        }
        var seconds = Settings.GetInt(CaptureSettings.RecorderCountdown, 3);
        if (!RecordingEditMath.Countdowns.Contains(seconds)) seconds = 3;
        if (seconds == 0)
        {
            _ = BeginAsync(region);
            return;
        }
        var remaining = seconds;
        Hud.ShowCountdown(remaining);
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                Hud.ShowCountdown(remaining);
                return;
            }
            _countdown?.Stop();
            _countdown = null;
            Hud.Hide();
            _ = BeginAsync(region);
        };
        _countdown.Start();
        StateChanged?.Invoke();
    }

    private static async Task BeginAsync(CaptureOutcome.Region region)
    {
        var options = new RecordingOptions(
            Settings.GetInt(CaptureSettings.RecorderFrameRate, 60) == 30 ? 30 : 60,
            Settings.GetString(CaptureSettings.RecorderQuality, "balanced"),
            Settings.GetBool(CaptureSettings.RecorderSystemAudio, true),
            Settings.GetBool(CaptureSettings.RecorderMicrophone, false));
        var path = Path.Combine(CaptureSettings.PrivateFolder("Recordings"), $"Take {DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var recording = new ScreenRecording(region, options, path);
        recording.Warning += message => Ui.OnUi(() => Hud.Show(Glyphs.Warning, message));
        _recording = recording;
        StateChanged?.Invoke();
        try
        {
            await recording.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Starting the recording failed", ex);
            recording.Dispose();
            _recording = null;
            TryDelete(path);
            StateChanged?.Invoke();
            Hud.Show(Glyphs.Warning, ex is InvalidOperationException ? ex.Message : "The screen could not be recorded");
            return;
        }
        _controls = new RecorderControlsWindow(recording);
        _controls.Show();
        _diskWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _diskWatch.Tick += (_, _) =>
        {
            if (FreeBytes(path) is { } free && free < 300_000_000)
            {
                Hud.Show(Glyphs.Warning, "Recording stopped, the disk is almost full");
                _ = StopAsync(openEditor: false);
            }
        };
        _diskWatch.Start();
    }

    public static void TogglePause()
    {
        if (_recording is null) return;
        if (_recording.IsPaused) _recording.Resume();
        else _recording.Pause();
        _controls?.Refresh();
        StateChanged?.Invoke();
    }

    /// <summary>Stops without keeping anything (Delete in the controls).</summary>
    public static void Discard()
    {
        var recording = _recording;
        if (recording is null) return;
        CleanupUi();
        _recording = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await recording.StopAsync();
                TryDelete(result.Path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Discarding the recording: {ex.Message}");
            }
            finally
            {
                recording.Dispose();
            }
        });
        StateChanged?.Invoke();
    }

    public static async Task StopAsync(bool openEditor)
    {
        var recording = _recording;
        if (recording is null || _stopping) return;
        _stopping = true;
        CleanupUi();
        Hud.Show(Glyphs.Record, "Saving\u2026", seconds: 30);
        try
        {
            var result = await recording.StopAsync();
            Hud.Hide();
            if (openEditor && result.Duration > 0.3) RecorderEditorWindow.Open(result);
            else SaveDirect(result);
        }
        catch (Exception ex)
        {
            Log.Error("Finishing the recording failed", ex);
            Hud.Show(Glyphs.Warning, "The recording could not be saved");
        }
        finally
        {
            recording.Dispose();
            _recording = null;
            _stopping = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Moves the master to the recordings folder untouched (editor off).</summary>
    public static string? SaveDirect(RecordingResult result)
    {
        try
        {
            var folder = CaptureSettings.RecordingFolderPath;
            Directory.CreateDirectory(folder);
            var name = CaptureFileNaming.UniqueFileName(CaptureFileNaming.DefaultFileName("Recording", DateTime.Now, "mp4"), n => File.Exists(Path.Combine(folder, n)));
            var target = Path.Combine(folder, name);
            File.Move(result.Path, target);
            RecentCaptures.AddRecording(target);
            Hud.Show(Glyphs.Check, $"Saved to {Path.GetFileName(folder)}");
            return target;
        }
        catch (Exception ex)
        {
            Log.Error("Saving the recording failed", ex);
            Hud.Show(Glyphs.Warning, "The recording could not be saved");
            return null;
        }
    }

    /// <summary>Feature switched off: finish and keep the take, without opening anything.</summary>
    public static void Shutdown()
    {
        _countdown?.Stop();
        _countdown = null;
        if (_recording is not null) _ = StopAsync(openEditor: false);
        RecorderEditorWindow.CloseAll();
    }

    private static void CleanupUi()
    {
        _diskWatch?.Stop();
        _diskWatch = null;
        _controls?.Close();
        _controls = null;
    }

    private static long? FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch { return null; }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Removing {path} failed: {ex.Message}"); }
    }
}
