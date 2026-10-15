// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Fans;
using Fieldkit.Features.Monitor.Logic;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;
using static Fieldkit.Features.Monitor.Views.MonitorWidgets;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>The Fan Control section (upstream FanControlSection): live RPM, mode, manual level or curve, apply.</summary>
internal static class FanControlView
{
    public const string SafetyCaption =
        "Control stays active until you return to System. It returns automatically if Fieldkit quits, the PC sleeps, sensor readings fail or the temperature rises too high.";

    public static FrameworkElement Build() =>
        MonitorPanel.Live("fanControl", () => MonitorNeeds.Fans | MonitorNeeds.Temperatures, Render, null,
            MonitorKeys.FanMode, MonitorKeys.HardwareSensors, MonitorKeys.FanApplied, MonitorKeys.TemperatureUnit);

    private static (FrameworkElement, Action<MonitorSnapshot>) Render()
    {
        var stack = Ui.Stack(10);
        var updates = new List<Action<MonitorSnapshot>>();
        var pre = HardwareSensors.Prerequisite();

        // Status header.
        var fanIcon = Ui.Icon(Glyphs.Fan, 20, FanController.IsControlling ? "Cyan" : "TextSecondary");
        fanIcon.Width = 26;
        var status = Ui.Text(string.Empty, 12, brush: FanController.IsControlling ? "Cyan" : "TextSecondary", wrap: false);
        var beta = Ui.Badge("Beta", "Accent", "TextOnAccent");
        var title = Ui.Row(6, Ui.Text("Fan Control", 13.5, FontWeights.SemiBold, wrap: false), beta);
        var head = Columns([Auto, Star], fanIcon, Ui.Stack(1, title, status));
        stack.Children.Add(head);

        var fans = Ui.Stack(5);
        Ui.Append(stack, 10, fans);
        var message = Ui.Text(string.Empty, 12, brush: "TextSecondary");
        Ui.Append(stack, 10, message);

        if (pre != SensorStatus.Ready)
        {
            message.Text = pre switch
            {
                SensorStatus.DriverOff =>
                    "Fan speeds and control come from the motherboard through LibreHardwareMonitor and the PawnIO driver. Allow hardware sensors in Settings to use them.",
                SensorStatus.NeedsAdmin => "Fan control needs administrator rights. Restart Fieldkit as administrator to read and control the fans.",
                _ => "Fan control needs the PawnIO driver, which LibreHardwareMonitor uses to reach the fan controller. Install it from pawnio.eu, then reopen this panel.",
            };
            var action = pre switch
            {
                SensorStatus.DriverOff => Ui.Button("Allow fan control", () => { PanelHost.Hide(); PanelHost.OpenSettings("fanControl"); }, accent: true),
                SensorStatus.NeedsAdmin => Ui.Button("Restart as administrator", Shell.FeatureHubPage.RestartElevated, accent: true),
                _ => Ui.Button("Get PawnIO", () => Shell.FeatureHubPage.OpenUri("https://pawnio.eu"), accent: true),
            };
            action.HorizontalAlignment = HorizontalAlignment.Stretch;
            Ui.Append(stack, 10, action);
            status.Text = "System";
            var fallback = Ui.Stack(5);
            fans.Children.Add(fallback);
            return (Card(stack), _ => { });
        }

        // Mode, controls and actions (only when the board exposes a controllable fan).
        var controls = Ui.Stack(10);
        Ui.Append(stack, 10, controls);
        var mode = FanController.Mode;
        controls.Children.Add(Segmented(MonitorKeys.FanMode, "system",
            [("system", "System"), ("manual", "Manual"), ("curve", "Curve")]));
        CurveEditor? editor = null;
        if (mode == FanMode.Manual)
        {
            Ui.Append(controls, 10, Ui.SliderRow("Fan speed", MonitorKeys.FanManualLevel, FanPolicy.MinimumLevel, FanPolicy.MaximumLevel,
                FanPolicy.DefaultLevel, v => $"{v:0}%", FanPolicy.LevelStep));
        }
        else if (mode == FanMode.Curve)
        {
            Ui.Append(controls, 10, editor = new CurveEditor());
        }

        var applyText = mode switch
        {
            FanMode.Manual => "Apply manual control",
            FanMode.Curve => "Apply fan curve",
            _ => "Use system control",
        };
        var apply = Ui.Button(applyText, () =>
        {
            if (FanController.Mode == FanMode.System) FanController.ReturnToSystem();
            else FanController.Apply();
        }, accent: true);
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        apply.Visibility = mode == FanMode.System && !FanController.IsControlling ? Visibility.Collapsed : Visibility.Visible;
        Ui.Append(controls, 10, apply);
        if (mode != FanMode.System)
            Ui.Append(controls, 8, Ui.ToggleRow("Resume after restart or sleep", null, MonitorKeys.FanResume, false));
        Ui.Append(controls, 8, Note(SafetyCaption));

        var fanKey = string.Empty;
        updates.Add(s =>
        {
            var controlling = FanController.IsControlling;
            fanIcon.SetResourceReference(TextBlock.ForegroundProperty, controlling ? "Cyan" : "TextSecondary");
            status.SetResourceReference(TextBlock.ForegroundProperty, controlling ? "Cyan" : "TextSecondary");
            var level = FanController.CurrentLevel;
            status.Text = !controlling ? "System"
                : FanController.AppliedMode == FanMode.Manual ? $"Manual · {level ?? FanController.AppliedLevel}%"
                : FanController.AppliedCurves is [{ } only] && FanController.Temperatures(s).TryGetValue(only.Sensor, out var t)
                    ? $"Curve · {MetricFormat.Temperature(t, MonitorKeys.Fahrenheit)} · {level}%"
                    : $"Curve · {level}%";

            var key = string.Join("|", s.Fans.Select(f => $"{f.Id}:{Math.Round(f.Rpm ?? -1)}:{Math.Round(f.DutyPercent ?? -1)}"));
            if (key != fanKey)
            {
                fanKey = key;
                fans.Children.Clear();
                var index = 0;
                foreach (var f in s.Fans)
                {
                    index++;
                    var name = Ui.Text(string.IsNullOrWhiteSpace(f.Name) ? $"Fan {index}" : f.Name, 12.5, brush: "TextSecondary", wrap: false);
                    var current = Ui.Text(f.Rpm is { } rpm ? $"Current {Math.Round(rpm).ToString("0", CultureInfo.CurrentCulture)} RPM" : "Current - RPM",
                        12.5, FontWeights.SemiBold, wrap: false);
                    current.HorizontalAlignment = HorizontalAlignment.Right;
                    var right = Ui.Stack(1, current);
                    if (controlling && f.Controllable && f.DutyPercent is { } duty)
                    {
                        var target = Ui.Text($"Target {Math.Round(duty)}%", 11, brush: "TextSecondary", wrap: false);
                        target.HorizontalAlignment = HorizontalAlignment.Right;
                        right.Children.Add(target);
                    }
                    fans.Children.Add(Ui.Split(name, right));
                }
            }

            var controllable = s.Fans.Any(f => f.Controllable);
            controls.Visibility = controllable ? Visibility.Visible : Visibility.Collapsed;
            message.Text = FanController.Message
                           ?? (s.Sensors == SensorStatus.Starting ? "Starting sensors…"
                               : s.Sensors == SensorStatus.Failed ? "Fan control is not available on this PC."
                               : s.Sensors == SensorStatus.Ready && s.Fans.Count == 0 ? "This PC has no fan the sensors can see."
                               : s.Sensors == SensorStatus.Ready && !controllable ? "This PC's fans can be read but not controlled by software."
                               : string.Empty);
            message.Visibility = message.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            editor?.SetCurrent(FanController.Temperatures(s));
        });

        void OnChanged() => updates[0](MonitorSampler.Latest);
        var view = Card(stack);
        Ui.Track(view, () =>
        {
            FanController.Changed += OnChanged;
            return new ActionDisposable(() => FanController.Changed -= OnChanged);
        });
        return (view, s =>
        {
            foreach (var u in updates) u(s);
        });
    }

    /// <summary>A segmented picker bound to a string setting.</summary>
    public static FrameworkElement Segmented(string key, string fallback, IReadOnlyList<(string Value, string Label)> options,
        bool intValue = false)
    {
        var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = options.Count };
        var group = $"seg-{key}-{Guid.NewGuid():N}";
        var current = intValue
            ? Settings.GetInt(key, int.Parse(fallback, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)
            : Settings.GetString(key, fallback);
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = value == current, Padding = new Thickness(8, 4, 8, 5) };
            rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            rb.Checked += (_, _) =>
            {
                if (intValue) Settings.Set(key, int.Parse(value, CultureInfo.InvariantCulture));
                else Settings.Set(key, value);
            };
            row.Children.Add(rb);
        }
        var border = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Child = row };
        border.SetResourceReference(Border.BackgroundProperty, "TabBarBackground");
        return border;
    }
}

/// <summary>
/// Temperature → fan speed curve editor (upstream FanControlCurveEditor, simplified): pick the
/// sensor, drag points (temperature right, speed up), add or remove points. Saved as you edit.
/// </summary>
internal sealed class CurveEditor : StackPanel
{
    private const double GraphWidth = 290, GraphHeight = 120;
    private readonly Canvas _canvas = new() { Width = GraphWidth, Height = GraphHeight, ClipToBounds = false };
    private readonly Line _now = new() { StrokeThickness = 1, StrokeDashArray = [3, 3], Visibility = Visibility.Collapsed };
    private readonly TextBlock _readout = Ui.Text(string.Empty, 11.5, brush: "TextSecondary", wrap: false);
    private List<FanCurvePoint> _points;
    private FanTemperatureSource _sensor;
    private int _dragging = -1;

    public CurveEditor()
    {
        var curve = FanController.Curves[0];
        _points = curve.Points.ToList();
        _sensor = curve.Sensor;

        var sensor = new ComboBox { MinWidth = 150 };
        foreach (var s in Enum.GetValues<FanTemperatureSource>())
            sensor.Items.Add(new ComboBoxItem { Content = FanController.SourceTitle(s), Tag = s });
        sensor.SelectedItem = sensor.Items.OfType<ComboBoxItem>().First(i => (FanTemperatureSource)i.Tag == _sensor);
        sensor.SelectionChanged += (_, _) =>
        {
            if (sensor.SelectedItem is ComboBoxItem item) _sensor = (FanTemperatureSource)item.Tag;
            Save();
        };
        Children.Add(Ui.Split(Ui.Text("Temperature sensor", 12.5, brush: "TextSecondary", wrap: false), sensor));

        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
        frame.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        frame.Child = _canvas;
        frame.ToolTip = "Temperature and fan speed curve";
        Children.Add(frame);
        _now.SetResourceReference(Shape.StrokeProperty, "Orange");

        var add = Ui.Button("Add point", () =>
        {
            if (FanPolicy.NextPoint(_points) is not { } p) return;
            _points.Add(p);
            _points = [.. _points.OrderBy(x => x.Temperature)];
            Save();
        }, glyph: Glyphs.Add);
        var remove = Ui.Button("Remove point", () =>
        {
            if (_points.Count <= FanPolicy.MinimumPointCount) return;
            _points.RemoveAt(_points.Count - 1);
            Save();
        }, glyph: Glyphs.Remove);
        add.Padding = remove.Padding = new Thickness(8, 3, 10, 4);
        var buttons = Ui.Row(6, add, remove);
        buttons.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(Ui.Split(buttons, _readout));

        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += (_, _) => EndDrag();
        _canvas.LostMouseCapture += (_, _) => _dragging = -1;
        Draw();
    }

    public void SetCurrent(IReadOnlyDictionary<FanTemperatureSource, double> temps)
    {
        if (temps.TryGetValue(_sensor, out var t))
        {
            var x = X(t);
            _now.X1 = _now.X2 = Math.Clamp(x, 0, GraphWidth);
            _now.Y1 = 0;
            _now.Y2 = GraphHeight;
            _now.Visibility = Visibility.Visible;
            _readout.Text = $"{MetricFormat.Temperature(t, MonitorKeys.Fahrenheit)} → {FanPolicy.InterpolatedLevel(_points, t)}%";
        }
        else
        {
            _now.Visibility = Visibility.Collapsed;
            _readout.Text = "Sensor not available";
        }
    }

    private static double X(double temperature) =>
        (temperature - FanPolicy.MinimumCurveTemperature) / (FanPolicy.MaximumCurveTemperature - FanPolicy.MinimumCurveTemperature) * GraphWidth;

    private static double Y(int level) => GraphHeight - level / 100.0 * GraphHeight;

    private void Draw()
    {
        _canvas.Children.Clear();
        for (var level = 0; level <= 100; level += 50)
        {
            var grid = new Line { X1 = 0, X2 = GraphWidth, Y1 = Y(level), Y2 = Y(level), StrokeThickness = 1 };
            grid.SetResourceReference(Shape.StrokeProperty, "Separator");
            _canvas.Children.Add(grid);
        }
        _canvas.Children.Add(_now);
        var line = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        line.SetResourceReference(Shape.StrokeProperty, "Cyan");
        line.Points.Add(new Point(0, Y(_points[0].Level)));
        foreach (var p in _points) line.Points.Add(new Point(X(p.Temperature), Y(p.Level)));
        line.Points.Add(new Point(GraphWidth, Y(_points[^1].Level)));
        _canvas.Children.Add(line);
        for (var i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            var dot = new Ellipse { Width = 12, Height = 12, StrokeThickness = 2, Cursor = Cursors.SizeAll };
            dot.SetResourceReference(Shape.FillProperty, "CardBackground");
            dot.SetResourceReference(Shape.StrokeProperty, "Cyan");
            dot.ToolTip = $"{p.Temperature} °C → {p.Level}%";
            Canvas.SetLeft(dot, X(p.Temperature) - 6);
            Canvas.SetTop(dot, Y(p.Level) - 6);
            var index = i;
            dot.MouseLeftButtonDown += (_, e) =>
            {
                _dragging = index;
                _canvas.CaptureMouse();
                e.Handled = true;
            };
            _canvas.Children.Add(dot);
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragging < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(_canvas);
        var i = _dragging;
        var minT = i == 0 ? FanPolicy.MinimumCurveTemperature : _points[i - 1].Temperature + 1;
        var maxT = i == _points.Count - 1 ? FanPolicy.MaximumCurveTemperature : _points[i + 1].Temperature - 1;
        var minL = i == 0 ? FanPolicy.MinimumLevel : _points[i - 1].Level;
        var maxL = i == _points.Count - 1 ? FanPolicy.MaximumLevel : _points[i + 1].Level;
        var t = (int)Math.Round(FanPolicy.MinimumCurveTemperature + pos.X / GraphWidth * (FanPolicy.MaximumCurveTemperature - FanPolicy.MinimumCurveTemperature));
        var level = (int)Math.Round((GraphHeight - pos.Y) / GraphHeight * 100 / FanPolicy.LevelStep) * FanPolicy.LevelStep;
        _points[i] = new FanCurvePoint(Math.Clamp(t, minT, Math.Max(minT, maxT)), Math.Clamp(level, minL, Math.Max(minL, maxL)));
        Draw();
    }

    private void EndDrag()
    {
        if (_dragging < 0) return;
        _dragging = -1;
        _canvas.ReleaseMouseCapture();
        Save();
    }

    private void Save()
    {
        var curve = new FanCurve(_sensor, _points.ToList());
        if (FanPolicy.ValidCurve(curve)) Settings.Set(MonitorKeys.FanCurves, FanPolicy.EncodeCurves([curve]));
        Draw();
    }
}
