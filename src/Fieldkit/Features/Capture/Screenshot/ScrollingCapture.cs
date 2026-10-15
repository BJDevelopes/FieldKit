// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Scrolling screenshot: watches the chosen area while it scrolls (one wheel notch at a time over
/// the area, or the person's own scrolling), joins only confidently matched overlaps, and stops at
/// the end of the page, on Enter/Done, or at the safe limits.
/// </summary>
public static class ScrollingCapture
{
    private static Session? _session;

    public static bool IsActive => _session is not null;

    public static void Start(PixelRect rect)
    {
        if (_session is not null || rect.Width < 16 || rect.Height < 48) return;
        _session = new Session(rect);
        _session.Begin();
    }

    public static void Cancel() => _session?.Finish(keep: false);

    private sealed class Session
    {
        private readonly PixelRect _rect;
        private readonly List<byte[]> _frames = [];
        private readonly List<ScrollMatch> _matches = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private byte[]? _lastAcceptedGray;
        private byte[]? _pendingGray;
        private byte[]? _pendingFrame;
        private int _stillTicks;
        private int _unmatchedTicks;
        private long _pixels;
        private Window? _controls;
        private TextBlock? _status;
        private IDisposable? _keys;
        private Native.POINT _restoreCursor;
        private bool _done;

        public Session(PixelRect rect) => _rect = rect;

        public void Begin()
        {
            Native.GetCursorPos(out _restoreCursor);
            ShowControls();
            // Enter finishes and Esc cancels even though the page under the area keeps focus.
            _keys = InputHooks.AddKeyboardHandler((ref KeyEvent e) =>
            {
                if (e.IsOwn || !e.IsDown) return false;
                if (e.VirtualKey == 0x0D) { Ui.OnUi(() => Finish(keep: true)); return true; }
                if (e.VirtualKey == 0x1B) { Ui.OnUi(() => Finish(keep: false)); return true; }
                return false;
            });
            Native.SetCursorPos(_rect.X + _rect.Width / 2, _rect.Y + _rect.Height / 2);
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private void Tick()
        {
            if (_done) return;
            RasterImage frame;
            try { frame = ScreenGrabber.Capture(_rect, includeCursor: false, hideOwnWindows: true); }
            catch (Exception ex)
            {
                Log.Error("Scrolling capture frame failed", ex);
                Finish(keep: true);
                return;
            }
            var gray = ScrollStitcher.ToGray(frame.Pixels, frame.Width, frame.Height);

            // Wait for the view to settle (smooth scrolling animates for a moment).
            if (_pendingGray is null || !gray.AsSpan().SequenceEqual(_pendingGray))
            {
                _pendingGray = gray;
                _pendingFrame = frame.Pixels;
                return;
            }

            if (_lastAcceptedGray is null)
            {
                Accept(frame.Pixels, gray, null);
                ScrollOnce();
                return;
            }
            var match = ScrollStitcher.Match(_lastAcceptedGray, gray, _rect.Width, _rect.Height);
            switch (match.Step)
            {
                case ScrollStep.Advanced:
                    _stillTicks = 0;
                    _unmatchedTicks = 0;
                    Accept(_pendingFrame!, gray, match);
                    break;
                case ScrollStep.End:
                    // Nothing moved after a scroll: the end of the page (or the person stopped).
                    if (++_stillTicks >= 4)
                    {
                        Finish(keep: true);
                        return;
                    }
                    break;
                default:
                    if (++_unmatchedTicks >= 6)
                    {
                        Hud.Show(Glyphs.Info, "The page changed. The completed part was kept.");
                        Finish(keep: true);
                        return;
                    }
                    break;
            }
            if (_frames.Count >= ScrollStitcher.MaximumFrames || _pixels >= ScrollStitcher.MaximumPixels || _clock.Elapsed >= ScrollStitcher.MaximumDuration)
            {
                Hud.Show(Glyphs.Info, "Capture stopped at the safe limit");
                Finish(keep: true);
                return;
            }
            ScrollOnce();
        }

        private void Accept(byte[] frame, byte[] gray, ScrollMatch? match)
        {
            _frames.Add(frame);
            if (match is { } m) _matches.Add(m);
            _lastAcceptedGray = gray;
            _pixels += match is { } mm ? (long)mm.Advance * _rect.Width : (long)_rect.Width * _rect.Height;
            var rows = _rect.Height + _matches.Sum(x => x.Advance);
            if (_status is not null) _status.Text = $"{_frames.Count} views \u00B7 {rows} px";
        }

        private void ScrollOnce()
        {
            // Only scroll for the person while the pointer is over the area.
            if (!Native.GetCursorPos(out var p) || !_rect.Contains(p.X, p.Y)) return;
            InputSender.Wheel(-Native.WHEEL_DELTA);
        }

        public void Finish(bool keep)
        {
            if (_done) return;
            _done = true;
            _timer.Stop();
            _keys?.Dispose();
            _controls?.Close();
            _session = null;
            Native.SetCursorPos(_restoreCursor.X, _restoreCursor.Y);
            if (!keep || _frames.Count == 0) return;
            try
            {
                var (pixels, height) = ScrollStitcher.Stitch(_frames, _matches, _rect.Width, _rect.Height);
                var image = new RasterImage(_rect.Width, height, pixels.Length == _rect.Width * height * 4 ? pixels : pixels[..(_rect.Width * height * 4)])
                {
                    Scale = WindowHelper.GetScaleAt(_rect.X, _rect.Y),
                };
                ScreenshotFlow.Receive(image);
            }
            catch (Exception ex)
            {
                Log.Error("Joining the scrolling capture failed", ex);
                Hud.Show(Glyphs.Warning, "The screen could not be captured");
            }
        }

        private void ShowControls()
        {
            _status = Ui.Text("Starting\u2026", 11.5, brush: "TextSecondary", wrap: false);
            var done = Ui.Button("Done", () => Finish(keep: true), accent: true);
            var cancel = Ui.Button("Cancel", () => Finish(keep: false));
            var text = Ui.Stack(2,
                Ui.Text("Scrolling screenshot", 13, FontWeights.SemiBold, wrap: false),
                Ui.Text("Scroll with your mouse or trackpad. Press Enter or choose Done.", 11.5, brush: "TextSecondary", wrap: false),
                _status);
            var row = Ui.Row(14, Ui.Icon(SelectorChrome.StackGlyph, 18), text, done, cancel);
            var card = new Border { Child = row, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            card.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = card,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Left = -32000,
            };
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
                ScreenGrabber.ExcludeFromCapture(hwnd);
            };
            window.Loaded += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                Native.GetWindowRect(hwnd, out var r);
                var work = WindowHelper.GetWorkAreaAt(_rect.X + _rect.Width / 2, _rect.Y + _rect.Height / 2);
                var x = Math.Clamp(_rect.X + (_rect.Width - r.Width) / 2, work.Left, Math.Max(work.Left, work.Right - r.Width));
                // Below the area when it fits, otherwise above it, otherwise at the top of the screen.
                var y = _rect.Bottom + 12 + r.Height <= work.Bottom ? _rect.Bottom + 12
                    : _rect.Y - 12 - r.Height >= work.Top ? _rect.Y - 12 - r.Height
                    : work.Top + 12;
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            };
            _controls = window;
            window.Show();
        }
    }
}
