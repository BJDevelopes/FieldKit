// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Sound;

/// <summary>
/// The Volume mixer (upstream MixerSection): the output and microphone pickers with their
/// levels, one row per app holding an audio session, and the Options disclosure. Used by the
/// panel section and, with the options always open, by Settings → Volume mixer.
/// </summary>
internal static class MixerView
{
    private const string PerAppUnsupported = "Choosing an output per app needs Windows 10 version 1803 or later.";

    public static FrameworkElement BuildPanel()
    {
        var card = Ui.Card(BuildControls(settingsMode: false), new Thickness(12, 10, 12, 10));
        return card;
    }

    public static FrameworkElement BuildControls(bool settingsMode)
    {
        var devices = new ContentControl();
        var rows = new StackPanel();
        var empty = Ui.Caption("Apps that use audio show up here", 11.5);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Margin = new Thickness(0, 4, 0, 4);
        var rowsDivider = Ui.Divider(2);
        var options = BuildOptions(settingsMode);

        var root = Ui.Stack(8, devices, rowsDivider, rows, empty, Ui.Divider(2), options);

        var model = new MixerRowsModel(rows, empty);
        void RenderDevices() => devices.Content = DevicesSection();
        RenderDevices();
        Ui.Track(root, () => AudioDeviceWatcher.Subscribe(RenderDevices));
        Ui.Track(root, () => Settings.Observe(RenderDevices, SoundKeys.MicActive, SoundKeys.MicPreferredInput, SoundKeys.PriorityInputEnabled));
        Ui.Track(root, () => Settings.Observe(model.Rebuild, SoundKeys.HideInactive, SoundKeys.Hidden, SoundKeys.Order, SoundKeys.Pinned));
        Ui.Track(root, () => model);
        var tick = 0;
        Ui.Every(root, TimeSpan.FromMilliseconds(250), () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var full = tick++ % 4 == 0;
            if (full) model.Refresh();
            else model.UpdateMeters();
            if (watch.ElapsedMilliseconds > 8) Log.Info($"Perf: mixer {(full ? "refresh" : "meters")} took {watch.ElapsedMilliseconds} ms");
        });
        return root;
    }

    // ------------------------------------------------------------ devices

    private static FrameworkElement DevicesSection()
    {
        var outputs = AudioEndpoints.List(EDataFlow.Render);
        var inputs = AudioEndpoints.List(EDataFlow.Capture);
        return Ui.Stack(6, OutputPicker(outputs), MicrophonePicker(inputs));
    }

    private static FrameworkElement OutputPicker(List<AudioDevice> outputs)
    {
        var current = outputs.FirstOrDefault(d => d.IsDefault);
        var items = outputs.Select(d => (d.Id, SoundUi.DeviceTitle(d))).ToList();
        if (current is null) items.Insert(0, ("", "Output unavailable"));
        var combo = SoundUi.Combo(items, current?.Id ?? "", id =>
        {
            if (id.Length > 0) AudioEndpoints.SetDefault(id);
        }, 164);
        combo.ToolTip = "Choose system output";
        combo.IsEnabled = outputs.Count > 0;
        var picker = Ui.Split(SoundUi.Label(SoundUi.SpeakerGlyph, "Output"), combo, 6);

        if (current is null)
            return Ui.Stack(4, picker, SoundUi.Message(Glyphs.Mute, "No outputs found"));
        var level = LevelRow(SoundUi.SpeakerGlyph, Glyphs.Mute, "Output",
            () => AudioEndpoints.GetVolume(current.Id) ?? 0, v => AudioEndpoints.SetVolume(current.Id, (float)v),
            () => AudioEndpoints.GetMute(current.Id) == true);
        return Ui.Stack(4, picker, level);
    }

    /// <summary>The microphone picker and input level, refreshed on device changes (Settings → Audio devices).</summary>
    internal static FrameworkElement MicrophoneSection()
    {
        var host = new ContentControl();
        void Render() => host.Content = MicrophonePicker(AudioEndpoints.List(EDataFlow.Capture));
        Render();
        Ui.Track(host, () => AudioDeviceWatcher.Subscribe(Render));
        Ui.Track(host, () => Settings.Observe(Render, SoundKeys.MicActive, SoundKeys.MicPreferredInput));
        return host;
    }

    private static FrameworkElement MicrophonePicker(List<AudioDevice> inputs)
    {
        // With Microphone tools, the picker sets a preferred microphone (used whenever it is
        // connected); when Audio priority manages inputs, or without Microphone tools, it
        // switches the default directly.
        var preferredMode = FeatureCatalog.IsAvailable("micMute") && !MicMuteService.InputPriorityIsActive;
        var preferred = Settings.GetString(SoundKeys.MicPreferredInput, string.Empty);
        var current = inputs.FirstOrDefault(d => d.IsDefault);
        var items = new List<(string, string)>();
        if (preferredMode) items.Add(("", "Default"));
        items.AddRange(inputs.Select(d => (d.Id, SoundUi.DeviceTitle(d))));
        var preferredUnavailable = preferredMode && preferred.Length > 0 && inputs.All(d => d.Id != preferred);
        if (preferredUnavailable) items.Add((preferred, "Microphone unavailable"));
        var selected = preferredMode ? preferred : current?.Id ?? "";
        var combo = SoundUi.Combo(items, selected, id =>
        {
            if (preferredMode) Settings.Set(SoundKeys.MicPreferredInput, id);
            else if (id.Length > 0) AudioEndpoints.SetDefault(id);
        }, 164);
        combo.ToolTip = "Choose microphone";
        combo.IsEnabled = inputs.Count > 0;
        var picker = Ui.Split(SoundUi.Label(Glyphs.Microphone, "Microphone"), combo, 6);

        var parts = new List<UIElement> { picker };
        if (current is not null)
        {
            var level = LevelRow(Glyphs.Microphone, MicMute.MicOffGlyph, "Microphone",
                () => AudioEndpoints.GetVolume(current.Id) ?? 0, v => AudioEndpoints.SetVolume(current.Id, (float)v),
                () => MicMute.IsMuted || AudioEndpoints.GetMute(current.Id) == true);
            level.IsEnabled = !MicMute.IsMuted;
            parts.Add(level);
        }
        if (inputs.Count == 0) parts.Add(SoundUi.Message(MicMute.MicOffGlyph, "No microphones found"));
        else if (preferredUnavailable) parts.Add(SoundUi.Message(MicMute.MicOffGlyph, "Using default until this microphone returns."));
        return Ui.Stack(4, parts.ToArray());
    }

    /// <summary>Icon + 0–100 % slider + editable percentage for an endpoint level, polled while shown.</summary>
    private static FrameworkElement LevelRow(string glyph, string mutedGlyph, string name,
        Func<double> read, Action<double> write, Func<bool> muted)
    {
        var icon = Ui.Icon(glyph, 11.5, "TextSecondary");
        icon.Width = 16;
        var slider = SoundUi.VolumeSlider(1);
        var updating = false;
        var percent = new SoundUi.EditablePercent(100, 40, v =>
        {
            write(v);
            updating = true;
            slider.Value = v;
            updating = false;
        }, name);
        void Sync()
        {
            if (slider.IsMouseCaptureWithin || percent.IsEditing) return;
            var level = read();
            updating = true;
            slider.Value = level;
            updating = false;
            percent.Show(SoundLogic.Percent(level), false);
            icon.Text = muted() || level <= 0.001 ? mutedGlyph : glyph;
        }
        slider.ValueChanged += (_, e) =>
        {
            percent.Show(SoundLogic.Percent(e.NewValue), false);
            if (!updating) write(e.NewValue);
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        slider.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(percent, 2);
        grid.Children.Add(icon);
        grid.Children.Add(slider);
        grid.Children.Add(percent);
        Ui.Every(grid, TimeSpan.FromSeconds(1), Sync);
        return grid;
    }

    // ------------------------------------------------------------ options

    private static FrameworkElement BuildOptions(bool settingsMode)
    {
        var body = Ui.Stack(10);
        var host = new ContentControl();
        void Render()
        {
            body.Children.Clear();
            Ui.Append(body, 10, Ui.Split(Ui.Text("Hide inactive apps", 12, brush: "TextSecondary"),
                Ui.Toggle(SoundKeys.HideInactive, false, small: true), 6));
            if (FeatureCatalog.IsAvailable("soundOutputSwitcher"))
            {
                Ui.Append(body, 10, HeadphoneGuardControls());
                if (!settingsMode) Ui.Append(body, 10, OutputSwitcherControls.Build());
            }
            if (!settingsMode && FeatureCatalog.IsAvailable("audioPriority"))
                Ui.Append(body, 10, AudioPriorityView.Build(showHeader: true, initiallyExpanded: false));
            Ui.Append(body, 10, VisibleAppsFooter());
        }
        Render();
        Ui.Track(body, () => FeatureAvailabilityToken(Render));
        if (settingsMode)
        {
            host.Content = body;
            return host;
        }
        return SoundUi.Disclosure("Options", body, Settings.GetBool(SoundKeys.OptionsExpanded, false),
            open => Settings.Set(SoundKeys.OptionsExpanded, open));
    }

    private static IDisposable FeatureAvailabilityToken(Action render)
    {
        void Handler() => render();
        FeatureRuntime.AvailabilityChanged += Handler;
        return new ActionDisposable(() => FeatureRuntime.AvailabilityChanged -= Handler);
    }

    /// <summary>"Lower volume when headphones disconnect" with the level used afterwards.</summary>
    internal static FrameworkElement HeadphoneGuardControls()
    {
        var check = Ui.Check("Lower volume when headphones disconnect", SoundKeys.LowerOnHeadphonesDisconnect, false);
        var caption = Ui.Caption("Adjusts output when wired or Bluetooth headphones disconnect.", 11);
        caption.Margin = new Thickness(24, 0, 0, 0);
        var level = Ui.SliderRow("Volume after disconnect", SoundKeys.HeadphonesVolume, SoundLogic.MinHeadphoneVolumePercent, 100,
            SoundLogic.DefaultHeadphoneVolumePercent, v => $"{v:0}%", step: 5);
        level.Margin = new Thickness(24, 0, 0, 0);
        void Sync() => level.Visibility = Settings.GetBool(SoundKeys.LowerOnHeadphonesDisconnect, false) ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        var stack = Ui.Stack(4, check, caption, level);
        Ui.Track(stack, () => Settings.Observe(Sync, SoundKeys.LowerOnHeadphonesDisconnect));
        return stack;
    }

    /// <summary>
    /// "Apps in the list": how many apps are hidden, and a checkbox per app (shown or hidden)
    /// to bring them back. Hidden apps stay here even while they are not running.
    /// </summary>
    private static FrameworkElement VisibleAppsFooter()
    {
        var list = Ui.Stack(4);
        var summary = Ui.Text(string.Empty, 11.5, brush: "TextSecondary", wrap: false);
        void Render()
        {
            var hidden = SoundKeys.GetMap(SoundKeys.Hidden);
            summary.Text = hidden.Count == 0 ? "All" : $"Hidden: {hidden.Count}";
            list.Children.Clear();
            var choices = new Dictionary<string, (string Name, bool Listed)>(StringComparer.Ordinal);
            foreach (var (id, name) in hidden) choices[id] = (name, false);
            using (var snapshot = MixerSnapshot.Capture())
            {
                foreach (var app in snapshot.Apps) choices.TryAdd(app.Id, (app.Name, true));
            }
            foreach (var (id, (name, listed)) in choices.OrderBy(c => c.Value.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var check = new CheckBox { Content = Ui.Text(name, 12, wrap: false), IsChecked = listed };
                check.Click += (_, _) => SetHidden(id, name, check.IsChecked != true);
                Ui.Append(list, 4, check);
            }
        }
        var body = list;
        var chevron = Ui.Icon(Glyphs.ChevronRight, 8, "TextSecondary");
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        var header = Ui.Split(Ui.Text("Apps in the list", 11.5, brush: "TextSecondary"), Ui.Row(6, summary, chevron), 6);
        var expanded = false;
        body.Visibility = Visibility.Collapsed;
        var button = Ui.Clickable(header, () =>
        {
            expanded = !expanded;
            if (expanded) Render();
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.RenderTransform = new RotateTransform(expanded ? 90 : 0);
        });
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var hiddenCount = SoundKeys.GetMap(SoundKeys.Hidden).Count;
        summary.Text = hiddenCount == 0 ? "All" : $"Hidden: {hiddenCount}";
        var stack = Ui.Stack(6, button, body);
        Ui.Track(stack, () => Settings.Observe(() =>
        {
            var count = SoundKeys.GetMap(SoundKeys.Hidden).Count;
            summary.Text = count == 0 ? "All" : $"Hidden: {count}";
        }, SoundKeys.Hidden));
        return stack;
    }

    internal static void SetHidden(string id, string name, bool hidden)
    {
        var map = SoundKeys.GetMap(SoundKeys.Hidden);
        if (hidden) map[id] = name;
        else map.Remove(id);
        Settings.Set(SoundKeys.Hidden, map);
    }

    // ---------------------------------------------------------------- rows

    /// <summary>
    /// Owns the current session snapshot and the row views. The structure is re-captured every
    /// second (sessions appear and go away); meters are read every 250 ms in between.
    /// Disposing it (panel closed) releases every session COM object.
    /// </summary>
    private sealed class MixerRowsModel(StackPanel rows, FrameworkElement empty) : IDisposable
    {
        private MixerSnapshot? _snapshot;
        private string _signature = string.Empty;
        private readonly Dictionary<string, MixerRow> _views = new(StringComparer.Ordinal);

        public void Refresh()
        {
            if (_views.Values.Any(v => v.IsInteracting)) return; // don't rebuild under the user's pointer
            var next = MixerSnapshot.Capture();
            var old = _snapshot;
            _snapshot = next;
            var signature = next.Signature;
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild();
            }
            else
            {
                foreach (var app in next.Apps)
                {
                    if (_views.TryGetValue(app.Id, out var view)) view.Update(app);
                }
            }
            old?.Dispose();
        }

        public void Rebuild()
        {
            if (_snapshot is null) return;
            var hidden = SoundKeys.GetMap(SoundKeys.Hidden);
            var hideInactive = Settings.GetBool(SoundKeys.HideInactive, false);
            var boosts = SoundKeys.GetIntMap(SoundKeys.Boost);
            var arrangement = new MixerArrangement(SoundKeys.GetList(SoundKeys.Order), SoundKeys.GetList(SoundKeys.Pinned));
            var outputs = AudioEndpoints.List(EDataFlow.Render);
            var visible = arrangement.Ordered(_snapshot.Apps.Where(a => !hidden.ContainsKey(a.Id)), a => a.Id)
                .Where(a =>
                {
                    boosts.TryGetValue(a.Id, out var boost);
                    var level = SoundLogic.DisplayLevel(a.Volume, boost);
                    var routed = !a.IsSystem && a.ProcessIds.Take(1).Any(pid => AppAudioRouting.GetOutput(pid) is not null);
                    return SoundLogic.ShouldShowApp(a.IsActive, level, routed, hideInactive);
                })
                .ToList();
            var ids = visible.Select(a => a.Id).ToList();
            rows.Children.Clear();
            _views.Clear();
            foreach (var app in visible)
            {
                var view = new MixerRow(app, arrangement, ids, outputs);
                _views[app.Id] = view;
                Ui.Append(rows, 10, view.Element);
            }
            empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            rows.Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public void UpdateMeters()
        {
            if (_snapshot is null) return;
            foreach (var app in _snapshot.Apps)
            {
                if (_views.TryGetValue(app.Id, out var view)) view.UpdateMeter(app);
            }
        }

        public void Dispose()
        {
            _snapshot?.Dispose();
            _snapshot = null;
            _signature = string.Empty;
            _views.Clear();
        }
    }

    /// <summary>One app: icon (green dot while sound flows), name, output, slider, %, reset and mute.</summary>
    private sealed class MixerRow
    {
        private readonly Ellipse _dot;
        private readonly Slider _slider;
        private readonly SoundUi.EditablePercent _percent;
        private readonly Button _reset;
        private readonly Button _mute;
        private readonly TextBlock _muteIcon;
        private readonly TextBlock _resetIcon;
        private MixerApp _app;
        private bool _updating;

        public FrameworkElement Element { get; }

        public bool IsInteracting => _slider.IsMouseCaptureWithin || _percent.IsEditing;

        public MixerRow(MixerApp app, MixerArrangement arrangement, IReadOnlyList<string> visibleIds, List<AudioDevice> outputs)
        {
            _app = app;
            var icon = app.IsSystem
                ? Ui.IconTile(SoundUi.SpeakerGlyph, 32)
                : AppIcons.Element(AppIcons.ForPath(app.ExePath), 32);
            _dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -2, -2),
                Visibility = Visibility.Hidden,
            };
            _dot.SetResourceReference(Shape.FillProperty, "Green");
            _dot.SetResourceReference(Shape.StrokeProperty, "CardBackground");
            var iconHost = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            iconHost.Children.Add(icon);
            iconHost.Children.Add(_dot);

            var name = Ui.Text(app.Name, 12.5, FontWeights.Medium, wrap: false);
            name.ToolTip = app.Name;
            var pinned = arrangement.IsPinned(app.Id);
            var pin = pinned ? Ui.Icon(Glyphs.Pin, 9.5, "TextSecondary") : null;
            if (pin is not null) pin.Margin = new Thickness(5, 1, 0, 0);
            var nameRow = new DockPanel { LastChildFill = true };
            if (pin is not null)
            {
                DockPanel.SetDock(pin, Dock.Right);
                nameRow.Children.Add(pin);
            }
            nameRow.Children.Add(name);
            var line1 = Ui.Split(nameRow, OutputCombo(app, outputs), 8);

            _slider = SoundUi.VolumeSlider(SoundLogic.MaxAppPercent / 100.0);
            _percent = new SoundUi.EditablePercent(SoundLogic.MaxAppPercent, 46, SetLevel, app.Name);
            _resetIcon = Ui.Icon(SoundUi.ResetGlyph, 10.5, "TextSecondary");
            _reset = Ui.Clickable(_resetIcon, () => SetLevel(1), "Reset to 100%");
            _reset.Padding = new Thickness(3);
            _muteIcon = Ui.Icon(SoundUi.SpeakerGlyph, 11.5, "TextSecondary");
            _mute = Ui.Clickable(_muteIcon, ToggleMute, "Mute");
            _mute.Padding = new Thickness(3);
            _slider.ValueChanged += (_, e) =>
            {
                ShowLevel(e.NewValue);
                if (!_updating) SetLevel(e.NewValue, fromSlider: true);
            };
            var line2 = new Grid();
            foreach (var w in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                line2.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            Grid.SetColumn(_percent, 1);
            Grid.SetColumn(_reset, 2);
            Grid.SetColumn(_mute, 3);
            _percent.Margin = new Thickness(6, 0, 2, 0);
            line2.Children.Add(_slider);
            line2.Children.Add(_percent);
            line2.Children.Add(_reset);
            line2.Children.Add(_mute);

            var content = Ui.Stack(3, line1, line2);
            var grid = new Grid { Background = Brushes.Transparent };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(content, 1);
            grid.Children.Add(iconHost);
            grid.Children.Add(content);
            grid.ContextMenu = BuildMenu(app, arrangement, visibleIds);
            Element = grid;
            Update(app);
        }

        private static ContextMenu BuildMenu(MixerApp app, MixerArrangement arrangement, IReadOnlyList<string> visibleIds)
        {
            var menu = new ContextMenu();
            void Add(string header, bool enabled, Action action)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (_, _) => Ui.Guard(action);
                menu.Items.Add(item);
            }
            void Save()
            {
                SoundKeys.SetList(SoundKeys.Order, arrangement.Order);
                SoundKeys.SetList(SoundKeys.Pinned, arrangement.Pinned);
            }
            Add(arrangement.IsPinned(app.Id) ? "Unpin" : "Pin to Top", true, () => { arrangement.TogglePin(app.Id); Save(); });
            Add("Move Up", arrangement.Neighbor(app.Id, -1, visibleIds) is not null, () => { arrangement.Move(app.Id, -1, visibleIds); Save(); });
            Add("Move Down", arrangement.Neighbor(app.Id, 1, visibleIds) is not null, () => { arrangement.Move(app.Id, 1, visibleIds); Save(); });
            menu.Items.Add(new Separator());
            Add("Hide from the list", true, () => SetHidden(app.Id, app.Name, true));
            return menu;
        }

        private static FrameworkElement OutputCombo(MixerApp app, List<AudioDevice> outputs)
        {
            var supported = !app.IsSystem && AppAudioRouting.IsSupported && app.ProcessIds.Any();
            var pid = app.ProcessIds.FirstOrDefault();
            var routed = supported ? AppAudioRouting.GetOutput(pid) : null;
            var items = new List<(string, string)> { ("", "Default") };
            items.AddRange(outputs.Select(d => (d.Id, SoundUi.DeviceTitle(d))));
            if (routed is not null && outputs.All(d => d.Id != routed)) items.Add((routed, "Output unavailable"));
            var combo = SoundUi.Combo(items, routed ?? "", id =>
            {
                foreach (var p in app.ProcessIds) AppAudioRouting.SetOutput(p, id.Length == 0 ? null : id);
            }, 112);
            combo.IsEnabled = supported;
            combo.ToolTip = supported ? "Choose output" : app.IsSystem ? "System sounds follow the default output." : PerAppUnsupported;
            ToolTipService.SetShowOnDisabled(combo, true);
            if (routed is not null && outputs.All(d => d.Id != routed))
                return Ui.Stack(2, combo, Ui.Caption("Using default until this device returns.", 10));
            return combo;
        }

        public void Update(MixerApp app)
        {
            _app = app;
            if (IsInteracting) return;
            var boosts = SoundKeys.GetIntMap(SoundKeys.Boost);
            var volume = app.Volume;
            boosts.TryGetValue(app.Id, out var boost);
            if (volume < 0.995f && boost > 0) ClearBoost(app.Id); // changed elsewhere below 100 %: the boost is gone
            var level = SoundLogic.DisplayLevel(volume, boost);
            _updating = true;
            _slider.Value = level;
            _updating = false;
            ShowLevel(level);
            var muted = app.Muted || level <= 0.001;
            _muteIcon.Text = muted ? Glyphs.Mute : SoundUi.SpeakerGlyph;
            _muteIcon.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Red" : "TextSecondary");
            _mute.ToolTip = app.Muted ? "Unmute" : "Mute";
            UpdateMeter(app);
        }

        public void UpdateMeter(MixerApp app) =>
            _dot.Visibility = app.Peak() > 0.0005f && !app.Muted ? Visibility.Visible : Visibility.Hidden;

        private void ShowLevel(double level)
        {
            var percent = SoundLogic.Percent(level);
            var boosting = percent > 100;
            _percent.Show(percent, boosting);
            _slider.SetResourceReference(Control.ForegroundProperty, boosting ? "Orange" : "Accent");
            _resetIcon.SetResourceReference(TextBlock.ForegroundProperty, boosting ? "Orange" : "TextSecondary");
            var atUnity = percent == 100;
            _reset.Opacity = atUnity ? 0 : 1;
            _reset.IsHitTestVisible = !atUnity;
        }

        /// <summary>
        /// Applies a level. Up to 100 % it is the session volume; above, the session stays at
        /// 100 % (Windows' ceiling) and the requested boost is remembered for the row.
        /// </summary>
        private void SetLevel(double level) => SetLevel(level, fromSlider: false);

        private void SetLevel(double level, bool fromSlider)
        {
            level = Math.Clamp(level, 0, SoundLogic.MaxAppPercent / 100.0);
            var percent = SoundLogic.Percent(level);
            if (percent > 100) SetBoost(_app.Id, percent);
            else ClearBoost(_app.Id);
            if (_app.Muted && level > 0.001) _app.SetMute(false);
            _app.SetVolume((float)Math.Min(level, 1));
            if (!fromSlider)
            {
                _updating = true;
                _slider.Value = level;
                _updating = false;
                ShowLevel(level);
            }
        }

        private void ToggleMute()
        {
            _app.SetMute(!_app.Muted);
            Update(_app);
        }

        private static void SetBoost(string id, int percent)
        {
            var map = SoundKeys.GetIntMap(SoundKeys.Boost);
            if (map.TryGetValue(id, out var existing) && existing == percent) return;
            map[id] = percent;
            Settings.Set(SoundKeys.Boost, map);
        }

        private static void ClearBoost(string id)
        {
            var map = SoundKeys.GetIntMap(SoundKeys.Boost);
            if (map.Remove(id)) Settings.Set(SoundKeys.Boost, map);
        }
    }
}
