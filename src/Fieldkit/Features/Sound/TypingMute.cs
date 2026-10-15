// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>Pure rules for muting the microphone while typing (unit tested).</summary>
public static class TypingMuteLogic
{
    public const int DefaultHoldMs = 600;

    /// <summary>Keys that are typing: letters, digits, punctuation, space, Enter, Backspace. Modifiers alone,
    /// navigation and function keys are not (pressing Ctrl to talk, or F-keys, must not mute).</summary>
    public static bool IsTypingKey(int vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F
            or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0x20 or 0x0D or 0x08 or 0x09;

    /// <summary>Whether the typing mute should end: no typing key for the hold time.</summary>
    public static bool ShouldRelease(long nowMs, long lastKeyMs, int holdMs) => nowMs - lastKeyMs >= holdMs;
}

/// <summary>
/// Mutes every microphone while you type and unmutes it shortly after you stop, so keyboard noise does
/// not reach calls (upstream PR #2714). Microphones that were already muted are left alone, and a mute you
/// turned on yourself is never undone. What it muted is remembered in settings so a crash cannot leave the
/// microphone muted for good: the next start releases it.
/// </summary>
internal sealed class TypingMuteService : FeatureService
{
    public const string EnabledKey = "micMute.whileTyping";
    public const string HoldKey = "micMute.whileTypingHoldMs";
    private const string ClaimsKey = "micMute.typingMutedDevices";

    private readonly DispatcherTimer _release = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
    private IDisposable? _hook;
    private long _lastKey;
    private int _muting; // 0 idle, 1 muted by typing (hook thread reads it)

    public override string FeatureId => "micMute";
    public override IReadOnlyList<string> EnableKeys => [EnabledKey];
    public override bool EnabledByDefault => false;

    public TypingMuteService() => _release.Tick += (_, _) => Ui.Guard(CheckRelease);

    protected override void OnStart()
    {
        ReleaseClaims(); // a previous run that ended while typing
        _hook = InputHooks.AddKeyboardHandler(OnKey, priority: 900);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        _release.Stop();
        ReleaseClaims();
        _muting = 0;
    }

    // Hook thread: record the time; only the first key of a burst crosses to the UI thread.
    private bool OnKey(ref KeyEvent e)
    {
        if (!e.IsDown || e.IsInjected || !TypingMuteLogic.IsTypingKey(e.VirtualKey)) return false;
        Interlocked.Exchange(ref _lastKey, Environment.TickCount64);
        if (Interlocked.CompareExchange(ref _muting, 1, 0) == 0) Ui.OnUi(StartMute);
        return false;
    }

    private void StartMute()
    {
        if (!IsRunning) return;
        // The user's own mute already silences everything; nothing to do (and nothing to undo later).
        if (MicMute.IsMuted)
        {
            _muting = 0;
            return;
        }
        var claims = new List<string>();
        foreach (var device in AudioEndpoints.List(EDataFlow.Capture))
        {
            if (AudioEndpoints.GetMute(device.Id) == true) continue;
            if (AudioEndpoints.SetMute(device.Id, true)) claims.Add(device.Id);
        }
        SoundKeys.SetList(ClaimsKey, claims);
        _release.Start();
    }

    private void CheckRelease()
    {
        var hold = Math.Clamp(Settings.GetInt(HoldKey, TypingMuteLogic.DefaultHoldMs), 200, 3000);
        if (!TypingMuteLogic.ShouldRelease(Environment.TickCount64, Interlocked.Read(ref _lastKey), hold)) return;
        _release.Stop();
        ReleaseClaims();
        _muting = 0;
    }

    private static void ReleaseClaims()
    {
        var claims = SoundKeys.GetList(ClaimsKey);
        if (claims.Count == 0) return;
        // If the user muted everything meanwhile, keep it muted: their mute wins.
        if (!MicMute.IsMuted)
        {
            foreach (var id in claims) AudioEndpoints.SetMute(id, false);
        }
        SoundKeys.SetList(ClaimsKey, []);
    }
}
