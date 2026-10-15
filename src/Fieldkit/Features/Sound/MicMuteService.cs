// SPDX-License-Identifier: GPL-3.0-or-later
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>
/// Global microphone mute (upstream MicMuteService). Muting only the default input is not
/// enough, since an app can record from a device of its own, so every active capture
/// endpoint is muted. Only devices this app muted are unmuted again, even if they were
/// unplugged meanwhile (they are released when they return). The state survives restarts.
/// </summary>
internal static class MicMute
{
    public const string MicOffGlyph = "";

    /// <summary>Raised on the UI thread after the mute state changes.</summary>
    public static event Action? Changed;

    public static bool IsMuted => Settings.GetBool(SoundKeys.MicActive, false);

    public static bool HasClaims => SoundKeys.GetList(SoundKeys.MicMutedDevices).Count > 0;

    public static void Toggle() => SetMuted(!IsMuted, announce: true);

    /// <summary>Mutes or unmutes every microphone. Returns false when some device refused.</summary>
    public static bool SetMuted(bool muted, bool announce)
    {
        var ok = Apply(muted);
        Settings.Set(SoundKeys.MicActive, muted);
        if (announce)
        {
            var text = muted
                ? ok ? "Microphone muted" : "Some microphones could not be muted"
                : ok ? "Mute off" : "Some microphones are still muted";
            SoundHud.Show(muted ? MicOffGlyph : Glyphs.Microphone, text, muted ? "Red" : "Accent");
        }
        Changed?.Invoke();
        return ok;
    }

    /// <summary>One sweep over the capture endpoints; keeps the list of devices this app muted.</summary>
    public static bool Apply(bool muted)
    {
        var claims = SoundKeys.GetList(SoundKeys.MicMutedDevices);
        var ok = true;
        foreach (var device in AudioEndpoints.List(EDataFlow.Capture))
        {
            if (muted)
            {
                if (AudioEndpoints.GetMute(device.Id) == true) continue; // muted by the user: not ours to release
                if (AudioEndpoints.SetMute(device.Id, true)) claims.Add(device.Id);
                else ok = false;
            }
            else if (claims.Contains(device.Id))
            {
                if (AudioEndpoints.SetMute(device.Id, false)) claims.Remove(device.Id);
                else ok = false;
            }
        }
        SoundKeys.SetList(SoundKeys.MicMutedDevices, claims);
        return ok;
    }
}

/// <summary>
/// Runs while Microphone tools is installed: the mute shortcut (when enabled), keeping an
/// active mute true while devices come and go, the preferred input, and the notification-area
/// indicator. Uninstalling the feature unmutes what it muted.
/// </summary>
internal sealed class MicMuteService : FeatureService
{
    private readonly List<IDisposable> _subscriptions = [];
    private IDisposable? _hotkey;
    private IDisposable? _watch;
    private NotifyIcon? _indicator;
    private Icon? _indicatorIcon;

    public override string FeatureId => "micMute";

    protected override void OnStart()
    {
        // Re-assert a mute that was active when the app last quit, or release stale claims.
        if (MicMute.IsMuted) MicMute.Apply(true);
        else if (MicMute.HasClaims) MicMute.Apply(false);

        _subscriptions.Add(Settings.Observe(SyncHotkey, SoundKeys.MicShortcutEnabled));
        _subscriptions.Add(Settings.Observe(Sync, SoundKeys.MicActive, SoundKeys.MicTrayIndicator, SoundKeys.MicPreferredInput,
            SoundKeys.PriorityInputEnabled, "feature.audioPriority.available"));
        SyncHotkey();
        Sync();
        ApplyPreferredInput();
    }

    protected override void OnStop()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _hotkey?.Dispose();
        _hotkey = null;
        _watch?.Dispose();
        _watch = null;
        RemoveIndicator();
        // Switching the feature off must not strand a muted microphone with no control left.
        if (MicMute.IsMuted || MicMute.HasClaims) MicMute.SetMuted(false, announce: false);
    }

    private void SyncHotkey()
    {
        var enabled = Settings.GetBool(SoundKeys.MicShortcutEnabled, false);
        if (enabled && _hotkey is null) _hotkey = Hotkeys.Bind(SoundKeys.MicShortcut, SoundKeys.MicDefaultShortcut, MicMute.Toggle);
        else if (!enabled && _hotkey is not null)
        {
            _hotkey.Dispose();
            _hotkey = null;
        }
    }

    /// <summary>Listens to devices only while a mute, a claim or a preferred input needs it.</summary>
    private void Sync()
    {
        var needsWatch = MicMute.IsMuted || MicMute.HasClaims || PreferredInputIsActive;
        if (needsWatch && _watch is null) _watch = AudioDeviceWatcher.Subscribe(OnDevicesChanged);
        else if (!needsWatch && _watch is not null)
        {
            _watch.Dispose();
            _watch = null;
        }
        if (MicMute.IsMuted && Settings.GetBool(SoundKeys.MicTrayIndicator, true)) ShowIndicator();
        else RemoveIndicator();
    }

    private static bool PreferredInputIsActive =>
        Settings.GetString(SoundKeys.MicPreferredInput, string.Empty).Length > 0 && !InputPriorityIsActive;

    /// <summary>Audio priority, when it manages inputs, owns the default microphone instead.</summary>
    public static bool InputPriorityIsActive =>
        FeatureCatalog.IsAvailable("audioPriority") && PriorityLists.IsEnabled(EDataFlow.Capture);

    private void OnDevicesChanged()
    {
        if (MicMute.IsMuted) MicMute.Apply(true);
        else if (MicMute.HasClaims) MicMute.Apply(false);
        ApplyPreferredInput();
        Sync();
    }

    /// <summary>Uses the preferred microphone whenever it is connected; the default otherwise.</summary>
    private static void ApplyPreferredInput()
    {
        if (!PreferredInputIsActive) return;
        var preferred = Settings.GetString(SoundKeys.MicPreferredInput, string.Empty);
        var inputs = AudioEndpoints.List(EDataFlow.Capture);
        if (inputs.FirstOrDefault(d => d.Id == preferred) is { IsDefault: false })
            AudioEndpoints.SetDefault(preferred);
    }

    private void ShowIndicator()
    {
        if (_indicator is not null) return;
        _indicatorIcon = RenderGlyphIcon(MicMute.MicOffGlyph, Color.FromArgb(255, 69, 58));
        _indicator = new NotifyIcon { Icon = _indicatorIcon, Text = "Microphone muted — click to unmute", Visible = true };
        _indicator.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Ui.OnUi(() => MicMute.SetMuted(false, announce: true));
        };
    }

    private void RemoveIndicator()
    {
        if (_indicator is not null)
        {
            _indicator.Visible = false;
            _indicator.Dispose();
            _indicator = null;
        }
        if (_indicatorIcon is not null)
        {
            Native.DestroyIcon(_indicatorIcon.Handle);
            _indicatorIcon.Dispose();
            _indicatorIcon = null;
        }
    }

    /// <summary>A Segoe Fluent Icons glyph as a tray-sized icon. The caller owns (DestroyIcon) the result.</summary>
    private static Icon RenderGlyphIcon(string glyph, Color color)
    {
        var size = Math.Max(16, (int)Math.Round(16 * WindowHelper.GetScaleAt(0, 0)));
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var font = new Font(new FontFamily(GlyphFontName()), size * 0.8f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static string GlyphFontName()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }
}
