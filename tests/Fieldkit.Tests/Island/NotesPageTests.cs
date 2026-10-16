// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fieldkit.Features.Island;
using Fieldkit.Features.Island.Notes;
using Fieldkit.Features.Island.Views;
using Fieldkit.Tests.Support;

namespace Fieldkit.Tests.Island;

/// <summary>The Notes page driven the way a click would drive it, against notes held in memory.</summary>
public class NotesPageTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);

    private sealed class Harness
    {
        public NotesCollection Notes { get; } = new();
        public FrameworkElement Page { get; }

        public Harness(params string[] existing)
        {
            var minute = 0;
            foreach (var text in existing) Notes.Add(text, Now.AddMinutes(minute++));
            var ctx = new IslandContext
            {
                IsPreview = true,
                Now = () => Now,
                Notes = () => Notes.Items,
                AddNote = text => Notes.Add(text, Now) is not null,
                UpdateNote = (id, text) => Notes.Update(id, text, Now),
                DeleteNote = id => Notes.Delete(id),
                TogglePinNote = id => Notes.TogglePin(id),
            };
            Page = NotesPage.Build(ctx, new IslandNav { Budget = 264 });
        }

        public IEnumerable<T> All<T>() where T : DependencyObject => Walk(Page).OfType<T>();

        public TextBox Composer => All<TextBox>().First();

        public IEnumerable<string> Texts => All<TextBlock>().Select(t => t.Text);

        /// <summary>The note texts as listed, top to bottom.</summary>
        public List<string> Listed() =>
            All<Border>().Where(b => b.Child is StackPanel { Children.Count: 2 } && b.CornerRadius.TopLeft == 12 && b.Margin.Bottom == 6)
                .Select(b => ((StackPanel)b.Child).Children[0]).OfType<TextBlock>().Select(t => t.Text).ToList();

        public Border Button(string tooltip, int index = 0) =>
            All<Border>().Where(b => b.ToolTip as string == tooltip).ElementAt(index);

        public static void Click(UIElement element) =>
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

        private static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var inner in Walk(child))
                yield return inner;
        }
    }

    [Fact]
    public void AnEmptyPageInvitesAFirstNote() => WpfTestHost.Run(() =>
    {
        var h = new Harness();

        Assert.Contains("No notes yet", h.Texts);
        Assert.Empty(h.Listed());
    });

    [Fact]
    public void TypingANoteAndPressingAddKeepsItAndClearsTheBox() => WpfTestHost.Run(() =>
    {
        var h = new Harness();
        h.Composer.Text = "buy milk";

        Harness.Click(h.Button("Add note"));

        Assert.Equal(["buy milk"], h.Notes.Items.Select(n => n.Text));
        Assert.Equal(string.Empty, h.Composer.Text);
        Assert.Equal(["buy milk"], h.Listed());
        Assert.DoesNotContain("No notes yet", h.Texts);
    });

    [Fact]
    public void AddingNothingDoesNothing() => WpfTestHost.Run(() =>
    {
        var h = new Harness();
        h.Composer.Text = "   ";

        Harness.Click(h.Button("Add note"));

        Assert.Equal(0, h.Notes.Count);
        Assert.Contains("No notes yet", h.Texts);
    });

    [Fact]
    public void ExistingNotesAreListedNewestFirst() => WpfTestHost.Run(() =>
    {
        var h = new Harness("first", "second", "third");

        Assert.Equal(["third", "second", "first"], h.Listed());
    });

    [Fact]
    public void PinningMovesANoteToTheTopAndBackAgain() => WpfTestHost.Run(() =>
    {
        var h = new Harness("old", "newer");
        Assert.Equal(["newer", "old"], h.Listed());

        Harness.Click(h.Button("Pin to the top", index: 1)); // the second card's pin: "old"
        Assert.Equal(["old", "newer"], h.Listed());

        Harness.Click(h.Button("Unpin"));
        Assert.Equal(["newer", "old"], h.Listed());
    });

    [Fact]
    public void DeletingRemovesTheNote() => WpfTestHost.Run(() =>
    {
        var h = new Harness("keep", "drop");

        Harness.Click(h.Button("Delete")); // the first card: "drop" (newest)

        Assert.Equal(["keep"], h.Listed());
        Assert.Equal(1, h.Notes.Count);
    });

    [Fact]
    public void ClickingANotesTextOpensItForEditingAndEscapeCancels() => WpfTestHost.Run(() =>
    {
        var h = new Harness("edit me");
        var text = h.All<TextBlock>().First(t => t.Text == "edit me");

        Harness.Click(text);

        var boxes = h.All<TextBox>().ToList();
        Assert.Equal(2, boxes.Count); // the composer and the note's own box
        var editing = boxes[1];
        Assert.Equal("edit me", editing.Text);
        editing.Text = "changed my mind";

        var escape = Assert.IsType<Func<bool>>(editing.Tag);
        Assert.True(escape()); // Esc cancels the edit

        Assert.Single(h.All<TextBox>());
        Assert.Equal(["edit me"], h.Listed());
        Assert.Equal("edit me", h.Notes.Items.Single().Text);
    });

    [Fact]
    public void EscapeClearsTheComposerBeforeAnythingElse() => WpfTestHost.Run(() =>
    {
        var h = new Harness();
        var escape = Assert.IsType<Func<bool>>(h.Composer.Tag);

        Assert.False(escape()); // nothing to clear: the island may close
        h.Composer.Text = "half a thought";
        Assert.True(escape());
        Assert.Equal(string.Empty, h.Composer.Text);
    });
}
