// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Copy text from screen: the shared selector on Text, then offline OCR straight to the clipboard;
/// a QR code in the area is shown to copy or open instead.
/// </summary>
public sealed class ScreenTextService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => "screenOCR";

    protected override void OnStart() =>
        _hotkey = Hotkeys.Bind(CaptureSettings.OcrShortcut, string.Empty, () => CaptureEntry.Start(CaptureTool.Text, fromShortcut: true));

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        QrResultWindow.CloseCurrent();
        if (CaptureSelector.IsActive) CaptureSelector.CancelActive();
    }

    public static void Capture() => CaptureEntry.Start(CaptureTool.Text, fromShortcut: false);

    /// <summary>Recognises an area (from the selector or the screenshot editor's Copy text).</summary>
    public static async void Recognize(RasterImage image, bool fromEditor = false)
    {
        if (!fromEditor && !FeatureCatalog.IsAvailable("screenOCR")) return;
        try
        {
            if (Settings.GetBool(CaptureSettings.OcrDetectQr, true) && OcrRecognizer.DecodeQr(image) is { Length: > 0 } qr)
            {
                QrResultWindow.Show(qr);
                return;
            }
            // Holding Shift as the selection ends flips "Remove line breaks" for this capture (upstream PR #2117).
            var removeLineBreaks = OcrText.RemoveLineBreaks(Settings.GetBool(CaptureSettings.OcrRemoveLineBreaks, false),
                !fromEditor && InputHooks.IsKeyDown(0x10));
            var text = await OcrRecognizer.RecognizeAsync(image, removeLineBreaks);
            if (string.IsNullOrWhiteSpace(text))
            {
                Hud.Show(Glyphs.TextScan, "No text found");
                return;
            }
            CaptureClipboard.CopyText(text);
            Hud.Show(Glyphs.TextScan, "Text copied");
        }
        catch (Exception ex)
        {
            Log.Error("Text recognition failed", ex);
            Hud.Show(Glyphs.Warning, ex is InvalidOperationException ? ex.Message : "No text found");
        }
    }
}

/// <summary>Shows the content of a QR code with Copy and (for links) Open (upstream QRResultController).</summary>
public sealed class QrResultWindow : Window
{
    private static QrResultWindow? _current;

    public static void Show(string content)
    {
        _current?.Close();
        _current = new QrResultWindow(content);
        _current.Show();
        _current.Activate();
    }

    public static void CloseCurrent() => _current?.Close();

    private QrResultWindow(string content)
    {
        Title = "QR code";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Left = -32000;

        var text = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var copy = Ui.Button("Copy", () =>
        {
            CaptureClipboard.CopyText(content);
            Hud.Show(Glyphs.Copy, "QR code copied");
            Close();
        }, accent: true, glyph: Glyphs.Copy);
        var buttons = Ui.Row(8, copy);
        if (OcrText.IsOpenableLink(content))
        {
            buttons.Children.Add(Ui.Button("Open", () =>
            {
                try { Process.Start(new ProcessStartInfo(content.Trim()) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("Opening the QR link failed", ex); }
                Close();
            }, glyph: Glyphs.Link));
            ((FrameworkElement)buttons.Children[^1]).Margin = new Thickness(8, 0, 0, 0);
        }
        var close = Ui.IconButton(Glyphs.Close, "Close", Close, 11);
        var header = Ui.Split(Ui.Row(8, Ui.Icon("\uED14", 15, "Accent"), Ui.Text("QR code", 13.5, FontWeights.SemiBold)), close);
        var card = new Border
        {
            Child = Ui.Stack(10, header, text, buttons),
            Padding = new Thickness(14),
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            MinWidth = 300,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        Content = card;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) => Hud.PlaceBottomCenter(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Deactivated += (_, _) =>
        {
            if (IsVisible) Close();
        };
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }
}
