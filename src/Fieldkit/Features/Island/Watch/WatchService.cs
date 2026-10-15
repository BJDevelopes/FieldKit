// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Interop;
using Fieldkit.UI;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Fieldkit.Features.Island.Watch;

/// <summary>
/// Watch (port of upstream <c>NotchWatchService</c>): reads a chosen screen area every 2 s with GDI capture and
/// offline Windows OCR, and tells you when it changes, stops changing, shows a text or reaches a number.
/// One watch at a time; nothing is stored or sent.
/// </summary>
public sealed class WatchService : IslandExtensionService
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _loop = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private OcrEngine? _ocr;
    private WatchTracker? _tracker;
    private byte[]? _lastFingerprint;
    private double _lastReadAt;
    private string? _signature;
    private bool _reading;
    private IDisposable? _hotkey;

    public override string FeatureId => "notchWatch";
    protected override string EnableKey => IslandKeys.WatchEnabled;

    /// <summary>The watched area in physical screen pixels.</summary>
    public Native.RECT? Target { get; private set; }
    public bool Active { get; private set; }
    public DateTime Since { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public string Headline { get; private set; } = string.Empty;
    public BitmapSource? Preview { get; private set; }
    public WatchOutcome? Outcome { get; private set; }
    public string? MatchText { get; set; }
    public double? MatchNumber { get; set; }

    public static WatchCondition Condition => Settings.GetString(IslandKeys.WatchCondition, "changes") switch
    {
        "settles" => WatchCondition.Settles,
        "contains" => WatchCondition.Contains,
        "reaches" => WatchCondition.Reaches,
        _ => WatchCondition.Changes,
    };

    public static string ConditionLabel(WatchCondition c) => c switch
    {
        WatchCondition.Settles => "When it stops changing",
        WatchCondition.Contains => "When it shows",
        WatchCondition.Reaches => "When the number reaches",
        _ => "When it changes",
    };

    public WatchService()
    {
        _loop.Tick += (_, _) => _ = ReadAsync();
    }

    protected override void OnStart()
    {
        IslandHub.Watch = this;
        try { _ocr = OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch (Exception ex) { Log.Warn($"OCR unavailable: {ex.Message}"); }
        _hotkey = Hotkeys.Bind(IslandKeys.WatchShortcut, string.Empty, ChooseArea);
    }

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        StopWatching();
        Target = null;
        Preview = null;
        Outcome = null;
        _ocr = null;
        if (IslandHub.Watch == this) IslandHub.Watch = null;
        IslandHub.RaiseChanged();
    }

    /// <summary>Lets the user drag a rectangle, then starts watching it.</summary>
    public void ChooseArea()
    {
        AreaPicker.Pick(rect =>
        {
            if (rect is not { } r || !IsRunning) return;
            Target = r;
            Begin();
        });
    }

    public void WatchAgain()
    {
        if (Target is not null) Begin();
    }

    public void ResetRule()
    {
        if (!Active) return;
        _tracker = new WatchTracker(Condition, MatchText, MatchNumber);
        if (_signature is not null) _tracker.Observe(_signature, Text, Clock.Elapsed.TotalSeconds);
    }

    private void Begin()
    {
        _tracker = new WatchTracker(Condition, MatchText, MatchNumber);
        _lastFingerprint = null;
        _signature = null;
        Outcome = null;
        Text = Headline = string.Empty;
        Active = true;
        Since = DateTime.Now;
        _loop.Start();
        IslandHub.RaiseChanged();
        _ = ReadAsync();
    }

    public void StopWatching()
    {
        Active = false;
        _loop.Stop();
        IslandHub.RaiseChanged();
    }

    private async Task ReadAsync()
    {
        if (_reading || !Active || Target is not { } r) return;
        _reading = true;
        try
        {
            var frame = await Task.Run(() => ScreenGrabber.Capture(r.Left, r.Top, r.Width, r.Height, 900));
            if (!Active || frame is null) return;
            var now = Clock.Elapsed.TotalSeconds;
            var fingerprint = WatchLogic.Fingerprint(frame);
            Preview = frame.ToBitmap();
            if (_signature is not null && _lastFingerprint is not null && WatchLogic.SameFingerprint(fingerprint, _lastFingerprint) && now - _lastReadAt < 10)
            {
                Finish(_tracker?.Observe(_signature, Text, now));
                return;
            }
            _lastFingerprint = fingerprint;
            _lastReadAt = now;
            var text = await RecognizeAsync(frame);
            if (!Active) return;
            Text = text;
            Headline = WatchLogic.Headline(text);
            _signature = WatchLogic.Signature(text, fingerprint);
            Finish(_tracker?.Observe(_signature, text, now));
            IslandHub.RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Warn($"Watch read failed: {ex.Message}");
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task<string> RecognizeAsync(WatchFrame frame)
    {
        if (_ocr is null) return string.Empty;
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(frame.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);
            var result = await _ocr.RecognizeAsync(bitmap);
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        catch (Exception ex)
        {
            Log.Warn($"OCR failed: {ex.Message}");
            return string.Empty;
        }
    }

    private void Finish(WatchOutcome? outcome)
    {
        if (outcome is null) return;
        Outcome = outcome;
        StopWatching();
        if (Settings.GetBool(IslandKeys.WatchSound, true))
        {
            try
            {
                var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Calendar.wav");
                if (File.Exists(media)) new SoundPlayer(media).Play();
                else SystemSounds.Asterisk.Play();
            }
            catch { }
        }
        IslandHub.Post(new IslandNotice(IslandNoticeEvent.Watch, outcome.Text, "Watch", Glyphs.Eye, "Purple", OpenSection: "watch"));
        IslandHub.React(outcome switch
        {
            WatchOutcome.Changed => MascotEvent.WatchChanged,
            WatchOutcome.Closed => MascotEvent.WatchClosed,
            _ => MascotEvent.WatchSettled,
        });
    }
}
