// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Sound;

/// <summary>
/// "Stop the music app from opening on its own" (upstream MusicLaunchBlocker). Windows opens
/// Media Player (or Groove) when Play is pressed and nothing is playing. A media key seen by
/// the shared keyboard hook arms a short window; if no media session existed at that moment
/// and one of the blocked apps starts within it, with no newer click or key press that could
/// mean the user opened it on purpose, it is closed (and the replacement app opened, if set).
/// Launches without an observed key (voice, automation, headphone buttons that never reach
/// the keyboard hook) are left alone.
/// </summary>
internal sealed class MusicBlockService : FeatureService
{
    private IDisposable? _keyboard;
    private IDisposable? _mouse;
    private DispatcherTimer? _poll;
    private long _triggerMs = -1;
    private int _triggerKey;
    private long _lastGestureMs = -1;
    private HashSet<int> _runningAtTrigger = [];
    private HashSet<string> _blocked = [];

    public override string FeatureId => "musicBlock";
    public override IReadOnlyList<string> EnableKeys => [SoundKeys.MusicEnabled];
    public override bool EnabledByDefault => false;

    protected override void OnStart() => _keyboard = InputHooks.AddKeyboardHandler(OnKey, priority: 200);

    protected override void OnStop()
    {
        _keyboard?.Dispose();
        _keyboard = null;
        Disarm();
    }

    // Hook thread: record and hand off, never block, never swallow.
    private bool OnKey(ref KeyEvent e)
    {
        if (!e.IsDown || e.IsOwn) return false;
        var now = Environment.TickCount64;
        if (SoundLogic.IsMusicLaunchKey(e.VirtualKey))
        {
            var vk = e.VirtualKey;
            UI.Ui.OnUi(() => Arm(now, vk));
        }
        else if (!Shortcut.IsModifierKey(e.VirtualKey))
        {
            Interlocked.Exchange(ref _lastGestureMs, now);
        }
        return false;
    }

    private void Arm(long now, int vk)
    {
        if (!IsRunning) return;
        _triggerMs = now;
        _triggerKey = vk;
        _blocked = SoundLogic.ParseProcessNames(Settings.GetString(SoundKeys.MusicApps, SoundLogic.DefaultBlockedMusicApps));
        _runningAtTrigger = RunningBlockedPids();
        _ = CheckMediaSessionAsync(now);
        // Clicks only matter during the short window, so the mouse hook lives just as long.
        _mouse ??= InputHooks.AddMouseHandler(OnMouse, priority: 200);
        if (_poll is null)
        {
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _poll.Tick += (_, _) => Poll();
        }
        _poll.Start();
    }

    private bool OnMouse(ref MouseEvent e)
    {
        if (e.Action is MouseAction.LeftDown or MouseAction.RightDown or MouseAction.MiddleDown && !e.IsOwn)
            Interlocked.Exchange(ref _lastGestureMs, Environment.TickCount64);
        return false;
    }

    /// <summary>A player already holding a media session gets the key; Windows launches nothing, so stand down.</summary>
    private async Task CheckMediaSessionAsync(long trigger)
    {
        try
        {
            var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager.GetCurrentSession() is not null && _triggerMs == trigger) Disarm();
        }
        catch (Exception ex)
        {
            Log.Warn($"Media session query failed: {ex.Message}");
        }
    }

    private void Poll()
    {
        var now = Environment.TickCount64;
        if (_triggerMs < 0 || now - _triggerMs > SoundLogic.MusicLaunchWindowMs)
        {
            Disarm();
            return;
        }
        foreach (var process in BlockedProcesses())
        {
            using (process)
            {
                var wasRunning = _runningAtTrigger.Contains(process.Id);
                if (!SoundLogic.ShouldBlockLaunch(now, _triggerMs, Interlocked.Read(ref _lastGestureMs), wasRunning)) continue;
                Block(process);
            }
        }
    }

    private void Block(Process process)
    {
        Log.Info($"Blocked {process.ProcessName} opened by a media key");
        var key = _triggerKey;
        Disarm();
        try
        {
            if (!process.CloseMainWindow() || !process.WaitForExit(800)) process.Kill();
        }
        catch (Exception ex)
        {
            Log.Warn($"Closing {process.ProcessName} failed: {ex.Message}");
        }
        OpenReplacement(startPlayback: key is 0xB3 or 0xFA && Settings.GetBool(SoundKeys.MusicPlayReplacement, true));
    }

    private static void OpenReplacement(bool startPlayback)
    {
        var path = Settings.GetString(SoundKeys.MusicReplacement, string.Empty);
        if (path.Length == 0 || !File.Exists(path)) return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening the replacement player failed: {ex.Message}");
            return;
        }
        if (!startPlayback) return;
        // Give the player a moment to register its media session, then press Play for it.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            InputSender.Tap(0xB3);
        };
        timer.Start();
    }

    private void Disarm()
    {
        _triggerMs = -1;
        _poll?.Stop();
        _mouse?.Dispose();
        _mouse = null;
        _runningAtTrigger = [];
    }

    private HashSet<int> RunningBlockedPids()
    {
        var pids = new HashSet<int>();
        foreach (var p in BlockedProcesses())
        {
            using (p) pids.Add(p.Id);
        }
        return pids;
    }

    private IEnumerable<Process> BlockedProcesses() =>
        _blocked.SelectMany(name =>
        {
            try { return Process.GetProcessesByName(name); }
            catch { return []; }
        });
}
