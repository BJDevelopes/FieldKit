// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// Settings → Radial menu (upstream <c>RadialMenuSettings</c>): the master switch, opening behaviour
/// and placement, profiles (name, colour, shortcut, mouse button) and the visual wheel editor.
/// Every change lands in settings right away.
/// </summary>
internal sealed class RadialMenuPage
{
    private readonly RadialCatalog _catalog = RadialCatalog.Current;
    private readonly ContentControl _profilesHost = new();
    private readonly ContentControl _actionsHost = new();
    private readonly TextBlock _registrationWarning = Ui.Text(RadialStrings.ShortcutRegistrationFailed, 12.5, brush: "Orange");
    private List<RadialMenuProfile> _profiles = RadialMenuStore.Load();
    private Guid _selectedId;
    private Guid? _openSubmenuId;
    private bool _showList;
    private bool _saving;
    private ComboBox? _profilePicker;

    public static FrameworkElement Build() => new RadialMenuPage().BuildPage();

    private bool Enabled => Settings.GetBool(RadialMenuStore.EnabledKey, false);

    private RadialMenuProfile Selected => _profiles.FirstOrDefault(p => p.Id == _selectedId) ?? _profiles[0];

    private RadialMenuItem? OpenSubmenu =>
        _openSubmenuId is { } id ? Selected.Items.FirstOrDefault(i => i.Id == id && i.Kind == RadialItemKind.Submenu) : null;

    /// <summary>The wheel being edited: the open submenu's children, or the root (also when the submenu vanished).</summary>
    private List<RadialMenuItem> Level => OpenSubmenu?.Children ?? Selected.Items;

    private FrameworkElement BuildPage()
    {
        _selectedId = _profiles[0].Id;
        var page = Ui.Page(RadialStrings.PageTitle, RadialStrings.HubDescription,
            BuildGeneral(),
            _profilesHost,
            _actionsHost);
        Render();
        Ui.Track(page, () =>
        {
            var settings = Settings.Observe(() =>
            {
                if (_saving) return;
                _profiles = RadialMenuStore.Load();
                Render();
            }, RadialMenuStore.ProfilesKey, RadialMenuStore.ShortcutKey, RadialMenuStore.EnabledKey);
            void State() => Ui.OnUi(SyncWarning);
            RadialMenuService.StateChanged += State;
            SyncWarning();
            return new ActionDisposable(() =>
            {
                settings.Dispose();
                RadialMenuService.StateChanged -= State;
            });
        });
        return page;
    }

    private void SyncWarning() =>
        _registrationWarning.Visibility = RadialMenuService.RegistrationFailed && Enabled ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- general

    private FrameworkElement BuildGeneral()
    {
        var position = Segmented(
            [(true, RadialStrings.PositionPointer), (false, RadialStrings.PositionCenter)],
            Settings.GetBool(RadialMenuStore.AtPointerKey, true),
            value => Settings.Set(RadialMenuStore.AtPointerKey, value));
        var tryIt = Ui.Button(RadialStrings.TryButton, () => RadialMenuService.Preview(Selected.Clone()), glyph: Glyphs.Radial);
        tryIt.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Group(null,
            Ui.Stack(6, Ui.ToggleRow(RadialStrings.EnableLabel, RadialStrings.EnableCaption, RadialMenuStore.EnabledKey, false),
                _registrationWarning),
            Ui.ChoiceRow(RadialStrings.ActivationModeLabel, RadialMenuStore.ActivationModeKey,
                [
                    (RadialRaw.Activation(RadialActivationMode.PressOrHold), RadialStrings.ActivationModePressOrHold),
                    (RadialRaw.Activation(RadialActivationMode.Press), RadialStrings.ActivationModePress),
                    (RadialRaw.Activation(RadialActivationMode.Hold), RadialStrings.ActivationModeHold),
                ],
                RadialRaw.Activation(RadialActivationMode.PressOrHold), RadialStrings.ActivationModeCaption),
            Ui.Split(Ui.Text(RadialStrings.PositionLabel, 13.5, FontWeights.Medium), position),
            tryIt);
    }

    private static FrameworkElement Segmented<T>(IReadOnlyList<(T Value, string Label)> options, T current, Action<T> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "radial-" + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var button = new RadioButton { Content = label, GroupName = group, IsChecked = Equals(value, current) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            button.Checked += (_, _) => changed(value);
            row.Children.Add(button);
        }
        return row;
    }

    // --------------------------------------------------------------- render

    private void Render()
    {
        if (_profiles.All(p => p.Id != _selectedId)) _selectedId = _profiles[0].Id;
        if (_openSubmenuId is not null && OpenSubmenu is null) _openSubmenuId = null;
        _profilesHost.Content = BuildProfiles();
        _actionsHost.Content = BuildActions();
        SyncWarning();
    }

    private void Persist()
    {
        _saving = true;
        try
        {
            RadialMenuStore.Save(_profiles);
            var selected = _selectedId;
            _profiles = RadialMenuStore.Load();
            _selectedId = _profiles.Any(p => p.Id == selected) ? selected : _profiles[0].Id;
        }
        finally
        {
            _saving = false;
        }
    }

    private void Change(Action mutate, bool render = true)
    {
        mutate();
        Persist();
        if (render) Render();
    }

    // -------------------------------------------------------------- profiles

    private FrameworkElement BuildProfiles()
    {
        var enabled = Enabled;
        var profile = Selected;

        var picker = new ComboBox { MinWidth = 200, IsEnabled = enabled };
        foreach (var p in _profiles) picker.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Id });
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Guid)i.Tag == profile.Id);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not ComboBoxItem { Tag: Guid id } || id == _selectedId) return;
            _selectedId = id;
            _openSubmenuId = null;
            Render();
        };
        _profilePicker = picker;

        var add = Ui.IconButton(Glyphs.Add, RadialStrings.AddProfileButton, () => { });
        var menu = new ContextMenu();
        foreach (var preset in Enum.GetValues<RadialMenuPreset>())
        {
            var entry = new MenuItem { Header = RadialStrings.PresetTitle(preset) };
            entry.Click += (_, _) => AddProfile(preset);
            menu.Items.Add(entry);
        }
        add.ContextMenu = menu;
        add.Click += (_, _) =>
        {
            menu.PlacementTarget = add;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        add.IsEnabled = enabled;
        var duplicate = Ui.IconButton(Glyphs.Copy, RadialStrings.DuplicateProfileButton, DuplicateProfile);
        duplicate.IsEnabled = enabled;
        var delete = Ui.IconButton(Glyphs.Delete, RadialStrings.DeleteProfileButton, DeleteProfile);
        delete.IsEnabled = enabled && _profiles.Count > 1;

        var pickerRow = Ui.Split(Ui.Text(RadialStrings.ProfilePickerLabel, 13.5, FontWeights.Medium), Ui.Row(6, picker, add, duplicate, delete));

        var name = RadialItemEditor.PlaceholderBox(profile.Name, RadialStrings.PresetGeneral, text =>
        {
            var target = Selected;
            target.Name = text;
            Persist();
            if (_profilePicker?.SelectedItem is ComboBoxItem item) item.Content = Selected.DisplayName;
        }, 220);
        name.IsEnabled = enabled;

        var colors = ColorPicker(profile.Color, color => Change(() => Selected.Color = color));
        colors.IsEnabled = enabled;

        return Ui.Group(RadialStrings.ProfilesHeader,
            pickerRow,
            enabled && !RadialMenuSupport.HasTrigger(profile) ? Ui.Caption(RadialStrings.ProfileNoTrigger, 12.5) : null,
            Ui.Split(Ui.Text(RadialStrings.ProfileNameLabel, 13.5, FontWeights.Medium), name),
            Ui.Split(Ui.Text(RadialStrings.ProfileColorLabel, 13.5, FontWeights.Medium), colors),
            ShortcutRow(profile, enabled),
            MouseRows(profile, enabled));
    }

    private void AddProfile(RadialMenuPreset preset)
    {
        var created = RadialPresets.Create(preset, RadialMenuSupport.NewProfileName(preset, _profiles.Count));
        Change(() =>
        {
            _profiles.Add(created);
            _selectedId = created.Id;
            _openSubmenuId = null;
        });
    }

    private void DuplicateProfile()
    {
        var copy = Selected.Duplicate($"{Selected.DisplayName} 2");
        Change(() =>
        {
            _profiles.Add(copy);
            _selectedId = copy.Id;
            _openSubmenuId = null;
        });
    }

    private void DeleteProfile()
    {
        if (_profiles.Count <= 1) return;
        var target = Selected;
        var owner = Window.GetWindow(_profilesHost);
        var message = RadialStrings.DeleteProfileConfirmMessage;
        var title = string.Format(RadialStrings.DeleteProfileConfirmFormat, target.DisplayName);
        var answer = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        Change(() =>
        {
            var index = _profiles.FindIndex(p => p.Id == target.Id);
            if (index < 0) return;
            _profiles.RemoveAt(index);
            _selectedId = _profiles[Math.Min(index, _profiles.Count - 1)].Id;
            _openSubmenuId = null;
        });
    }

    private static FrameworkElement ColorPicker(RadialMenuColor selected, Action<RadialMenuColor> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var accent = (Application.Current?.TryFindResource("AccentColor") as Color?) ?? Color.FromRgb(0x0A, 0x84, 0xFF);
        foreach (var color in Enum.GetValues<RadialMenuColor>())
        {
            var value = RadialColors.Resolve(color, ThemeManager.IsDark, accent);
            var grid = new Grid { Width = 22, Height = 22 };
            grid.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(value) });
            if (color == selected)
            {
                grid.Children.Add(new Ellipse { Width = 12, Height = 12, Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), StrokeThickness = 2 });
                grid.Children.Add(new Ellipse { Width = 20, Height = 20, Stroke = new SolidColorBrush(value), StrokeThickness = 1.5 });
            }
            var button = Ui.Clickable(grid, () => pick(color), RadialStrings.ColorTitle(color));
            button.Margin = new Thickness(0, 0, 4, 0);
            row.Children.Add(button);
        }
        return row;
    }

    private FrameworkElement ShortcutRow(RadialMenuProfile profile, bool enabled)
    {
        var message = Ui.Text(string.Empty, 12, brush: "Orange");
        message.Visibility = Visibility.Collapsed;
        // Dimmed rather than disabled: the shared button style has no disabled look.
        var box = new RadialShortcutBox(Shortcut.Parse(profile.Shortcut)) { IsHitTestVisible = enabled, Focusable = enabled, Opacity = enabled ? 1 : 0.5 };
        box.Recording += () => message.Visibility = Visibility.Collapsed;
        box.Invalid += () =>
        {
            message.Text = RadialStrings.ShortcutInvalid;
            message.Visibility = Visibility.Visible;
        };
        box.Cleared += () => Change(() => Selected.Shortcut = string.Empty);
        box.Captured += shortcut =>
        {
            if (ConflictTitle(shortcut, profile.Id) is { } owner)
            {
                message.Text = string.Format(RadialStrings.ShortcutConflictFormat, owner);
                message.Visibility = Visibility.Visible;
                box.SetShortcut(Shortcut.Parse(profile.Shortcut));
                return;
            }
            Change(() => Selected.Shortcut = shortcut.ToString());
        };
        return Ui.Stack(4, Ui.Split(Ui.Text(RadialStrings.ProfileShortcutLabel, 13.5, FontWeights.Medium), box), message);
    }

    /// <summary>Who already answers to a combination: another wheel or another feature's shortcut.</summary>
    private string? ConflictTitle(Shortcut shortcut, Guid profileId)
    {
        if (RadialMenuSupport.ProfileUsing(shortcut, _profiles, profileId) is { } other) return $"“{other.DisplayName}”";
        foreach (var action in FeatureRuntime.Registry.Actions)
        {
            if (action.ShortcutKey is null || action.FeatureId == RadialCatalog.FeatureId || !FeatureCatalog.IsAvailable(action.FeatureId)) continue;
            if (Shortcut.FromSetting(action.ShortcutKey, action.DefaultShortcut) == shortcut) return action.Title;
        }
        return null;
    }

    private FrameworkElement MouseRows(RadialMenuProfile profile, bool enabled)
    {
        var buttons = new ComboBox { MinWidth = 200, IsEnabled = enabled };
        foreach (var b in Enum.GetValues<RadialMouseButton>())
            buttons.Items.Add(new ComboBoxItem { Content = RadialStrings.MouseButtonTitle(b), Tag = b });
        buttons.SelectedItem = buttons.Items.OfType<ComboBoxItem>().First(i => (RadialMouseButton)i.Tag == profile.Button);
        buttons.SelectionChanged += (_, _) =>
        {
            if (buttons.SelectedItem is not ComboBoxItem { Tag: RadialMouseButton b }) return;
            var mods = b == RadialMouseButton.Off ? Modifiers.None : Selected.ButtonModifiers;
            Change(() => _profiles = RadialMenuSupport.AssigningMouseButton(b, mods, _selectedId, _profiles));
        };
        var rows = new List<UIElement>
        {
            Ui.Split(Ui.Text(RadialStrings.ProfileMouseTriggerLabel, 13.5, FontWeights.Medium), buttons),
            Ui.Caption(RadialStrings.MouseTriggerRequirement, 12.5),
        };
        if (profile.Button != RadialMouseButton.Off)
        {
            Modifiers[] choices = [Modifiers.None, Modifiers.Ctrl, Modifiers.Alt, Modifiers.Shift, Modifiers.Ctrl | Modifiers.Shift, Modifiers.Ctrl | Modifiers.Alt];
            var keys = new ComboBox { MinWidth = 200, IsEnabled = enabled };
            foreach (var m in choices)
                keys.Items.Add(new ComboBoxItem { Content = m == Modifiers.None ? RadialStrings.MouseModifierNone : RadialMenuSupport.ModifiersText(m), Tag = m });
            keys.SelectedItem = keys.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Modifiers)i.Tag == profile.ButtonModifiers)
                                ?? keys.Items[0];
            keys.SelectionChanged += (_, _) =>
            {
                if (keys.SelectedItem is not ComboBoxItem { Tag: Modifiers m }) return;
                Change(() => _profiles = RadialMenuSupport.AssigningMouseButton(Selected.Button, m, _selectedId, _profiles));
            };
            rows.Add(Ui.Split(Ui.Text(RadialStrings.MouseModifierLabel, 13.5, FontWeights.Medium), keys));
            if (profile.ButtonModifiers == Modifiers.None)
            {
                rows.Add(Ui.Caption(profile.Button == RadialMouseButton.Middle
                    ? RadialStrings.MouseTriggerMiddleWarning
                    : RadialStrings.MouseTriggerWarning, 12.5));
            }
            rows.Add(ButtonTest(profile.Button));
        }
        return Ui.Stack(8, rows.ToArray());
    }

    /// <summary>Tells whether presses of the chosen button reach Fieldkit (watched only while shown).</summary>
    private static FrameworkElement ButtonTest(RadialMouseButton expected)
    {
        var icon = Ui.Icon(Glyphs.Info, 13, "TextSecondary");
        var state = Ui.Text(RadialStrings.ButtonTestWaiting, 12.5, brush: "TextSecondary", wrap: false);
        var head = Ui.Split(Ui.Row(6, icon, Ui.Text(RadialStrings.ButtonTestLabel, 13, FontWeights.Medium, wrap: false)), state);
        var root = Ui.Stack(4, head, Ui.Caption(RadialStrings.ButtonTestHint, 12));
        Ui.Track(root, () => InputHooks.AddMouseHandler((ref MouseEvent e) =>
        {
            if (e.IsOwn || e.Action is not (MouseAction.MiddleDown or MouseAction.XDown)) return false;
            var seen = RadialMenuSupport.ButtonOf(e.Action, e.XButton);
            Ui.OnUi(() =>
            {
                var ok = seen == expected;
                state.Text = ok ? RadialStrings.ButtonTestSeen : RadialStrings.ButtonTestOther;
                icon.Text = ok ? Glyphs.Check : Glyphs.Warning;
                icon.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Green" : "Orange");
            });
            return false;
        }, priority: 10));
        return root;
    }

    // --------------------------------------------------------------- actions

    private FrameworkElement BuildActions()
    {
        var level = Level;
        var submenu = OpenSubmenu;
        var preset = RadialMenuSupport.ResolvedPreset(Selected);
        var canvas = new RadialMenuCanvas(_catalog, new RadialCanvasActions(
            Select: item => Edit(item, isNew: false),
            Reorder: Swap,
            Remove: item => Remove(item.Id),
            OpenSubmenu: item => OpenChildren(item.Id),
            Back: () => OpenChildren(null),
            Add: () => Edit(new RadialMenuItem(RadialItemKind.App), isNew: true),
            Reset: preset is null || submenu is not null ? null : () => ResetToPreset(preset.Value)));
        canvas.Update(level, Selected.Color, submenu);

        var add = level.Count < RadialMenuSupport.MaxItemsPerWheel
            ? Ui.Button(RadialStrings.AddButton, () => Edit(new RadialMenuItem(RadialItemKind.App), isNew: true), glyph: Glyphs.Add)
            : null;
        if (add is not null) add.HorizontalAlignment = HorizontalAlignment.Left;

        var toggle = Ui.Clickable(Ui.Row(6,
                Ui.Icon(_showList ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10, "TextSecondary"),
                Ui.Text(_showList ? RadialStrings.HideListButton : RadialStrings.ShowListButton, 13, FontWeights.Medium, wrap: false)),
            () =>
            {
                _showList = !_showList;
                _actionsHost.Content = BuildActions();
            });
        toggle.HorizontalAlignment = HorizontalAlignment.Left;

        var group = Ui.Group(RadialStrings.ActionsHeader,
            canvas,
            Ui.Caption(RadialStrings.CanvasHint, 12.5),
            add,
            toggle,
            _showList ? ListRows(level, submenu) : null);
        var footer = Ui.Caption(level.Count >= RadialMenuSupport.MaxItemsPerWheel ? RadialStrings.LimitCaption : RadialStrings.HubDescription, 12.5);
        footer.Margin = new Thickness(2, 0, 0, 0);
        return Ui.Stack(6, group, footer);
    }

    private FrameworkElement ListRows(List<RadialMenuItem> level, RadialMenuItem? submenu)
    {
        var rows = new List<UIElement>();
        if (submenu is not null)
        {
            var back = Ui.Clickable(Ui.Row(6, Mirror(Ui.Icon(Glyphs.ChevronRight, 10, "Accent")),
                Ui.Text($"{RadialStrings.BackButton}  {_catalog.DisplayName(submenu)}", 13, FontWeights.Medium, "Accent", wrap: false)),
                () => OpenChildren(null));
            back.HorizontalAlignment = HorizontalAlignment.Left;
            rows.Add(back);
        }
        if (level.Count == 0) rows.Add(Ui.Caption(RadialStrings.EmptyCaption, 12.5));
        for (var i = 0; i < level.Count; i++)
        {
            var item = level[i];
            var index = i;
            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8) };
            if (item.UsesFileIcon) badge.Background = Brushes.Transparent;
            else badge.SetResourceReference(Border.BackgroundProperty, "Accent");
            badge.Child = RadialIcons.Visual(item, _catalog, 26, 13, highlighted: true);
            var text = Ui.Stack(1, Ui.Text(_catalog.DisplayName(item), 13.5, wrap: false), Ui.Caption(RadialStrings.KindTitle(item.Kind), 12));
            var main = Ui.Clickable(Ui.Row(10, badge, text), () => Edit(item, isNew: false));
            main.HorizontalContentAlignment = HorizontalAlignment.Left;
            var up = Ui.IconButton(Glyphs.ChevronUp, RadialStrings.MoveUp, () => Move(index, index - 1));
            up.IsEnabled = index > 0;
            var down = Ui.IconButton(Glyphs.ChevronDown, RadialStrings.MoveDown, () => Move(index, index + 1));
            down.IsEnabled = index < level.Count - 1;
            var tools = Ui.Row(2,
                item.Kind == RadialItemKind.Submenu ? Ui.Button(RadialStrings.EditActionsButton, () => OpenChildren(item.Id)) : null,
                up, down,
                Ui.IconButton(Glyphs.Delete, RadialStrings.DeleteButton, () => Remove(item.Id)));
            rows.Add(Ui.Split(main, tools));
        }
        return Ui.Stack(8, rows.ToArray());
    }

    private static FrameworkElement Mirror(TextBlock glyph)
    {
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        glyph.RenderTransform = new ScaleTransform(-1, 1);
        return glyph;
    }

    private void OpenChildren(Guid? submenuId)
    {
        _openSubmenuId = submenuId;
        _actionsHost.Content = BuildActions();
    }

    private void Edit(RadialMenuItem item, bool isNew)
    {
        var allowsSubmenu = _openSubmenuId is null;
        RadialItemEditor.Show(Window.GetWindow(_actionsHost), item, isNew, allowsSubmenu, _catalog,
            save: saved =>
            {
                Upsert(saved);
                if (isNew && saved.Kind == RadialItemKind.Submenu && allowsSubmenu) OpenChildren(saved.Id);
            },
            delete: isNew ? null : () => Remove(item.Id),
            openChildren: item.Kind == RadialItemKind.Submenu && !isNew ? () => OpenChildren(item.Id) : null);
    }

    private void SetLevel(List<RadialMenuItem> items)
    {
        Change(() =>
        {
            if (OpenSubmenu is { } submenu) submenu.Children = items;
            else Selected.Items = items;
        });
    }

    private void Upsert(RadialMenuItem item)
    {
        RadialIcons.Invalidate(item.Payload);
        var level = Level.ToList();
        var index = level.FindIndex(i => i.Id == item.Id);
        if (index >= 0) level[index] = item;
        else level.Add(item);
        SetLevel(level);
    }

    private void Remove(Guid id) => SetLevel(Level.Where(i => i.Id != id).ToList());

    private void Swap(RadialMenuItem first, RadialMenuItem second)
    {
        var level = Level.ToList();
        var a = level.FindIndex(i => i.Id == first.Id);
        var b = level.FindIndex(i => i.Id == second.Id);
        if (a < 0 || b < 0 || a == b) return;
        (level[a], level[b]) = (level[b], level[a]);
        SetLevel(level);
    }

    private void Move(int from, int to)
    {
        var level = Level.ToList();
        if (from < 0 || from >= level.Count || to < 0 || to >= level.Count) return;
        var item = level[from];
        level.RemoveAt(from);
        level.Insert(to, item);
        SetLevel(level);
    }

    private void ResetToPreset(RadialMenuPreset preset) => Change(() =>
    {
        Selected.Items = RadialPresets.MakeItems(preset);
        _openSubmenuId = null;
    });
}
