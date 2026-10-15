// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Saving a place (upstream CommandBarLinkEditor): a name to call it by, where it goes, and the words
/// filled in when it opens. Three fields and a picker; scripts get their two switches instead.
/// </summary>
public sealed class CommandBarLinkEditor : ToolWindow
{
    private readonly CommandBarLink _draft;
    private readonly Action<CommandBarLink> _save;
    private readonly TextBox _name = new();
    private readonly TextBox _destination = new();
    private readonly ContentControl _kindPart = new() { Focusable = false };
    private readonly Button _choose;
    private readonly Button _saveButton;

    private CommandBarLinkEditor(CommandBarLink draft, Action<CommandBarLink> save) : base(CommandBarStrings.LinksTitle, 520, 470)
    {
        _draft = draft;
        _save = save;
        MinHeight = 380;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, label) in new[]
                 {
                     (CommandBarLinkKind.Link, CommandBarStrings.LinkKindLink),
                     (CommandBarLinkKind.Place, CommandBarStrings.LinkKindPlace),
                     (CommandBarLinkKind.Script, CommandBarStrings.LinkKindScript),
                 })
        {
            var button = new RadioButton { GroupName = "commandBarLinkKind", Content = label, IsChecked = draft.Kind == kind, Margin = new Thickness(0, 0, 2, 0) };
            button.SetResourceReference(StyleProperty, "SegmentButton");
            var value = kind;
            button.Checked += (_, _) =>
            {
                _draft.Kind = value;
                RenderKindPart();
            };
            kinds.Children.Add(button);
        }

        _name.Text = draft.Name;
        _destination.Text = draft.Destination;
        _name.TextChanged += (_, _) => Validate();
        _destination.TextChanged += (_, _) => Validate();
        _choose = Ui.Button(CommandBarStrings.ChooseButton, ChooseDestination);
        var destinationRow = new DockPanel();
        DockPanel.SetDock(_choose, Dock.Right);
        _choose.Margin = new Thickness(6, 0, 0, 0);
        destinationRow.Children.Add(_choose);
        destinationRow.Children.Add(_destination);

        var cancel = Ui.Button(CommandBarStrings.CancelButton, Close);
        _saveButton = Ui.Button(CommandBarStrings.SaveButton, Save, accent: true);
        var buttons = Ui.Row(8, cancel, _saveButton);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        var stack = Ui.Stack(14,
            Ui.Title(CommandBarStrings.LinksTitle),
            kinds,
            Ui.Stack(5, Ui.Caption(CommandBarStrings.NameLabel), _name),
            Ui.Stack(5, Ui.Caption(CommandBarStrings.LinkDestinationLabel), destinationRow),
            _kindPart,
            buttons);
        stack.Margin = new Thickness(20);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        RenderKindPart();
        Validate();
        Loaded += (_, _) => _name.Focus();
    }

    /// <summary>Opens the editor over the active window; <paramref name="save"/> gets the cleaned link.</summary>
    public static void Edit(CommandBarLink draft, Action<CommandBarLink> save)
    {
        var editor = new CommandBarLinkEditor(draft, save)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };
        editor.ShowDialog();
    }

    private void RenderKindPart()
    {
        _choose.Visibility = _draft.Kind is CommandBarLinkKind.Place or CommandBarLinkKind.Script ? Visibility.Visible : Visibility.Collapsed;
        if (_draft.Kind != CommandBarLinkKind.Script)
        {
            var chips = new WrapPanel();
            foreach (var (token, meaning) in new[]
                     {
                         (CommandBarLinks.QueryToken, CommandBarStrings.PlaceholderQuery),
                         (CommandBarLinks.ClipboardToken, CommandBarStrings.PlaceholderClipboard),
                         (CommandBarLinks.SelectionToken, CommandBarStrings.PlaceholderSelection),
                         (CommandBarLinks.DateToken, CommandBarStrings.PlaceholderDate),
                     })
            {
                var content = Ui.Stack(1, Ui.Text(token, 11.5, FontWeights.SemiBold, wrap: false), Ui.Text(meaning, 10, brush: "TextSecondary", wrap: false));
                var t = token;
                var chip = Ui.Clickable(new Border { Child = content, Padding = new Thickness(8, 4, 8, 4) }, () =>
                {
                    _destination.Text += t;
                    _destination.CaretIndex = _destination.Text.Length;
                    _destination.Focus();
                }, meaning);
                chip.Margin = new Thickness(0, 0, 6, 6);
                chips.Children.Add(chip);
            }
            _kindPart.Content = Ui.Stack(6, Ui.Caption(CommandBarStrings.LinkPlaceholdersHint, 12), chips);
            return;
        }
        var bare = new CheckBox { Content = Ui.Text(CommandBarStrings.ScriptRunsWithoutArgument, 12.5), IsChecked = _draft.RunsWithoutArgument };
        bare.Click += (_, _) => _draft.RunsWithoutArgument = bare.IsChecked == true;
        var direct = new CheckBox { Content = Ui.Text(CommandBarStrings.ScriptRunsDirectly, 12.5), IsChecked = _draft.RunsDirectly };
        direct.Click += (_, _) => _draft.RunsDirectly = direct.IsChecked == true;
        _kindPart.Content = Ui.Stack(8, Ui.Caption(CommandBarStrings.ScriptHint, 12), bare, direct);
    }

    private void Validate() =>
        _saveButton.IsEnabled = _name.Text.Trim().Length > 0 && _destination.Text.Trim().Length > 0;

    private void ChooseDestination()
    {
        string? chosen = null;
        if (_draft.Kind == CommandBarLinkKind.Place)
        {
            // A place is usually a folder; a file path can be typed or pasted into the field.
            var folder = new Microsoft.Win32.OpenFolderDialog();
            if (folder.ShowDialog(this) == true) chosen = folder.FolderName;
        }
        else
        {
            var file = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Scripts and programs (*.ps1;*.cmd;*.bat;*.exe)|*.ps1;*.cmd;*.bat;*.exe|All files (*.*)|*.*",
            };
            if (file.ShowDialog(this) == true) chosen = file.FileName;
        }
        if (chosen is null) return;
        _destination.Text = chosen;
        if (_name.Text.Trim().Length == 0) _name.Text = Path.GetFileNameWithoutExtension(chosen.TrimEnd('\\'));
    }

    private void Save()
    {
        _draft.Name = _name.Text.Trim();
        _draft.Destination = _destination.Text.Trim();
        if (_draft.Name.Length == 0 || _draft.Destination.Length == 0) return;
        _save(_draft);
        Close();
    }
}
