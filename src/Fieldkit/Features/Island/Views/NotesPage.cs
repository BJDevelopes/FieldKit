// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Island.Notes;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Views;

/// <summary>
/// The island's Notes page: a box to jot something down (Enter saves, Shift+Enter starts a new line) over a list of
/// notes that can be edited in place, pinned, copied and deleted. The page has a fixed height, so adding and
/// removing notes never resizes the island; the list scrolls.
/// </summary>
internal static class NotesPage
{
    private const double ComposerHeight = 44;
    private const double CardLineHeight = 17;

    public static FrameworkElement Build(IslandContext ctx, IslandNav nav)
    {
        var listHeight = Math.Max(110, nav.Budget - ComposerHeight - 14);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, Height = listHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Margin = new Thickness(0, 8, 0, 0) };
        Guid? editing = null;

        // ----- the box for a new note
        var box = InputBox(ComposerHeight, new Thickness(14, 0, 44, 0));
        box.ToolTip = "Enter saves the note, Shift+Enter starts a new line";
        var hint = IslandUi.Text("Jot something down…", 13.5, brush: IslandPalette.Tertiary);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(15, 0, 0, 0);
        hint.HorizontalAlignment = HorizontalAlignment.Left;
        void Commit()
        {
            if (!ctx.AddNote(box.Text)) return;
            box.Clear();
            Fill();
            scroll.ScrollToTop();
        }
        var add = IslandUi.Button(Glyphs.Add, "Add note", Commit, 30, 13, IslandPalette.Text, IslandPalette.CardHover);
        add.HorizontalAlignment = HorizontalAlignment.Right;
        add.Margin = new Thickness(0, 0, 7, 0);
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            e.Handled = true;
            Commit();
        };
        // Esc empties the box first; the island only closes when there is nothing to clear.
        box.Tag = (Func<bool>)(() =>
        {
            if (box.Text.Length == 0) return false;
            box.Clear();
            return true;
        });
        box.PreviewMouseDown += (_, _) => nav.Focus();
        box.Loaded += (_, _) => box.Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Background);
        var composer = new Grid { Height = ComposerHeight };
        composer.Children.Add(box);
        composer.Children.Add(hint);
        composer.Children.Add(add);

        // ----- the notes
        void Fill()
        {
            list.Children.Clear();
            var notes = ctx.Notes();
            if (notes.Count == 0)
            {
                list.Children.Add(Empty(listHeight));
                return;
            }
            var now = ctx.Now().LocalDateTime;
            foreach (var note in notes) list.Children.Add(Card(note, now));
        }

        FrameworkElement Card(QuickNote note, DateTime now)
        {
            FrameworkElement body;
            if (editing == note.Id)
            {
                var edit = InputBox(double.NaN, new Thickness(10, 6, 10, 6));
                edit.MaxHeight = 96;
                edit.Text = note.Text;
                var done = false;
                void Finish(bool save)
                {
                    if (done) return;
                    done = true;
                    editing = null;
                    if (save && !ctx.UpdateNote(note.Id, edit.Text) && NotesRules.Clean(edit.Text).Length == 0) ctx.DeleteNote(note.Id);
                    Fill();
                }
                edit.Tag = (Func<bool>)(() => { Finish(false); return true; });
                edit.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
                    e.Handled = true;
                    Finish(true);
                };
                edit.LostKeyboardFocus += (_, _) => Finish(true);
                edit.PreviewMouseDown += (_, _) => nav.Focus();
                edit.Loaded += (_, _) => edit.Dispatcher.BeginInvoke(() => { edit.Focus(); edit.CaretIndex = edit.Text.Length; }, DispatcherPriority.Background);
                body = Ui.Stack(4, edit, IslandUi.Text("Enter saves · Esc cancels · empty deletes", 10.5, brush: IslandPalette.Tertiary));
            }
            else
            {
                var text = IslandUi.Text(note.Text, 13, wrap: true);
                text.MaxHeight = CardLineHeight * 3 + 2;
                text.VerticalAlignment = VerticalAlignment.Top;
                text.Cursor = Cursors.IBeam;
                text.ToolTip = note.Text.Length > 160 ? note.Text[..160] + "…" : note.Text;
                body = text;
                text.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    nav.Focus();
                    editing = note.Id;
                    Fill();
                };
                text.MouseLeftButtonDown += (_, e) => e.Handled = true;
            }

            var age = IslandUi.Text(NotesRules.Age(now, note.Updated), 10.5, brush: IslandPalette.Tertiary);
            var left = IslandUi.Row(6, note.Pinned ? IslandUi.Icon(Glyphs.Pin, 10, IslandPalette.Yellow) : null, age);
            var pin = IslandUi.Button(Glyphs.Pin, note.Pinned ? "Unpin" : "Pin to the top", () => { ctx.TogglePinNote(note.Id); Fill(); }, 26, 11,
                note.Pinned ? IslandPalette.Yellow : IslandPalette.Secondary);
            Border copy = null!;
            copy = IslandUi.Button(Glyphs.Copy, "Copy", () =>
            {
                if (!Copy(note.Text)) return;
                // The glyph turns into a tick for a moment so the click is seen.
                var icon = (TextBlock)copy.Child;
                icon.Text = Glyphs.Check;
                icon.Foreground = IslandPalette.Green;
                var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
                revert.Tick += (_, _) =>
                {
                    revert.Stop();
                    icon.Text = Glyphs.Copy;
                    icon.Foreground = IslandPalette.Secondary;
                };
                revert.Start();
            }, 26, 11, IslandPalette.Secondary);
            var delete = IslandUi.Button(Glyphs.Delete, "Delete", () => { ctx.DeleteNote(note.Id); Fill(); }, 26, 11, IslandPalette.Secondary);
            var footer = Ui.Split(left, IslandUi.Row(0, pin, copy, delete), 8);
            footer.Margin = new Thickness(0, 4, 0, 0);

            return new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = IslandPalette.Card,
                Padding = new Thickness(12, 9, 6, 5),
                Margin = new Thickness(0, 0, 0, 6),
                Child = Ui.Stack(0, body, footer),
            };
        }

        Fill();
        return Ui.Stack(0, composer, scroll);
    }

    private static TextBox InputBox(double height, Thickness padding)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = NotesRules.MaxLength,
            Background = IslandPalette.Card,
            Foreground = IslandPalette.Text,
            CaretBrush = IslandPalette.Text,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            FontFamily = IslandPalette.Font,
            FontSize = 13.5,
            // Fieldkit's text box template applies its padding twice (as the host's margin and as its padding), so half is asked for.
            Padding = new Thickness(padding.Left / 2, padding.Top / 2, padding.Right / 2, padding.Bottom / 2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (!double.IsNaN(height)) box.Height = height;
        return box;
    }

    private static FrameworkElement Empty(double height)
    {
        var empty = Ui.Stack(6,
            IslandUi.Icon(Glyphs.Note, 28, IslandPalette.Secondary),
            IslandUi.Text("No notes yet", 14, FontWeights.SemiBold, IslandPalette.Secondary),
            IslandUi.Text("Type above and press Enter. Notes stay on this PC.", 12, brush: IslandPalette.Tertiary));
        foreach (var child in empty.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Margin = new Thickness(0, Math.Max(8, (height - 110) / 2), 0, 0);
        return empty;
    }

    private static bool Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Copying a note failed: {ex.Message}");
            return false;
        }
    }
}
