// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Input.Buttons;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Pages;

/// <summary>
/// Mouse button shortcuts (upstream MouseButtonShortcutsSection): the switch, one row per mapped
/// input with its recorded combination, a capture flow that asks for a real press instead of
/// button numbers, and the virtual-desktop drag at the bottom of the same card.
/// </summary>
internal static class MouseButtonsSection
{
    private enum CaptureTarget { None, Shortcut, Drag }

    public static FrameworkElement Build()
    {
        var host = new ContentControl { Focusable = false };
        var capture = CaptureTarget.None;
        string? feedback = null;

        void Render() => host.Content = Content(capture, feedback, StartCapture, StopCapture);

        void StartCapture(CaptureTarget target)
        {
            capture = target;
            feedback = null;
            MouseButtonService.Capturing = true;
            Render();
        }

        void StopCapture()
        {
            if (capture == CaptureTarget.None) return;
            capture = CaptureTarget.None;
            feedback = null;
            MouseButtonService.Capturing = false;
            Render();
        }

        void OnCaptured(string id)
        {
            if (capture == CaptureTarget.Shortcut)
            {
                var buttons = MouseButtonSettings.Buttons();
                if (buttons.Contains(id) || (Settings.GetBool("mouseButtonShortcuts.spacesEnabled") && MouseButtonSettings.SpacesButton() == id))
                {
                    feedback = "That button or direction is already on the list below.";
                    Render();
                    return;
                }
                MouseButtonSettings.SetButtons([.. buttons, id]);
                StopCapture();
            }
            else if (capture == CaptureTarget.Drag)
            {
                if (!MouseInputs.CanHold(id))
                    feedback = "That input cannot be held for a drag. Use the middle button or a side button.";
                else if (MouseButtonSettings.Buttons().Contains(id) && !Shortcut.FromSetting(MouseInputs.ShortcutKey(id)).IsEmpty)
                    feedback = "That button already has a shortcut. Pick another one.";
                else
                {
                    Settings.Set(MouseButtonSettings.SpacesButtonKey, id);
                    StopCapture();
                    return;
                }
                Render();
            }
        }

        Render();
        Ui.Track(host, () =>
        {
            MouseButtonService.Captured += OnCaptured;
            var observer = Settings.Observe(Render, "mouseButtonShortcuts.enabled", "mouseButtonShortcuts.spacesEnabled",
                MouseButtonSettings.ButtonsKey, MouseButtonSettings.SpacesButtonKey);
            return new ActionDisposable(() =>
            {
                MouseButtonService.Captured -= OnCaptured;
                observer.Dispose();
                if (capture != CaptureTarget.None)
                {
                    capture = CaptureTarget.None;
                    MouseButtonService.Capturing = false;
                }
            });
        });
        return host;
    }

    private static FrameworkElement Content(CaptureTarget capture, string? feedback, Action<CaptureTarget> start, Action stop)
    {
        var enabled = Settings.GetBool("mouseButtonShortcuts.enabled");
        var spaces = Settings.GetBool("mouseButtonShortcuts.spacesEnabled");
        var watching = FeatureRuntime.Service<MouseButtonService>()?.IsRunning == true;
        var rows = new List<UIElement?>
        {
            InputUi.Row(Glyphs.Mouse, "Use extra buttons as shortcuts",
                "Each extra button or side-wheel direction can press a key combination for you. While it has a shortcut, it stops doing what it did before.",
                Ui.Toggle("mouseButtonShortcuts.enabled", false, on => { if (!on) stop(); })),
        };

        if (enabled)
        {
            var list = Ui.Stack(10);
            var buttons = MouseButtonSettings.Buttons();
            if (buttons.Count == 0) Ui.Append(list, 10, InputUi.Note("No shortcuts yet. Add a button or side-wheel direction."));
            foreach (var id in buttons)
            {
                var button = id;
                var recorder = InputUi.Recorder(MouseInputs.ShortcutKey(button));
                var remove = Ui.IconButton(Glyphs.Close, "Remove", () =>
                {
                    Settings.Remove(MouseInputs.ShortcutKey(button));
                    MouseButtonSettings.SetButtons(MouseButtonSettings.Buttons().Where(b => b != button));
                }, 11);
                Ui.Append(list, 10, Ui.Split(Ui.Text(MouseInputs.Name(button), 13), Ui.Row(6, recorder, remove)));
            }
            Ui.Append(list, 10, capture == CaptureTarget.Shortcut
                ? CaptureRow("Now press an extra button or move the side wheel.", watching, feedback, stop,
                    "If nothing happens, your mouse’s own software may already be using that control.")
                : AddButton("Add a button or side wheel", () => start(CaptureTarget.Shortcut)));
            rows.Add(InputUi.Indent(list));
        }

        var dragBlock = Ui.Stack(10,
            InputUi.Row(Glyphs.Desktops, "Switch virtual desktops by dragging a button",
                "Hold the chosen button and drag: left or right moves one desktop over, up opens Task View, down shows the desktop. A short click still does what it always did.",
                Ui.Toggle("mouseButtonShortcuts.spacesEnabled", false, on =>
                {
                    if (on) return;
                    stop();
                    // A kept binding would act invisibly while the row is hidden.
                    Settings.Set(MouseButtonSettings.SpacesButtonKey, string.Empty);
                })));
        if (spaces)
        {
            UIElement buttonRow;
            if (capture == CaptureTarget.Drag)
                buttonRow = CaptureRow("Now press the middle button or a side button.", watching, feedback, stop, null);
            else if (MouseButtonSettings.SpacesButton() is { } bound)
                buttonRow = Ui.Split(Ui.Text(MouseInputs.Name(bound), 13),
                    Ui.IconButton(Glyphs.Close, "Remove", () => Settings.Set(MouseButtonSettings.SpacesButtonKey, string.Empty), 11));
            else
                buttonRow = AddButton("Choose a button", () => start(CaptureTarget.Drag));
            Ui.Append(dragBlock, 10, InputUi.Indent(Ui.Stack(10,
                buttonRow,
                Ui.Split(Ui.Stack(2, Ui.Text("Desktops follow the drag", 13),
                        InputUi.Note("Dragging right brings the desktop on the left, the way a touchpad swipe carries it along with your fingers.")),
                    Ui.Toggle("mouseButtonShortcuts.spacesFollowsDrag", false, small: true)))));
        }
        rows.Add(dragBlock);
        if (enabled || spaces)
            rows.Add(InputUi.ExceptionList("mouseButtonShortcuts", "Your extra mouse buttons stay quiet in these apps, and the press reaches them instead."));
        return InputUi.Card("Mouse button shortcuts", rows.ToArray());
    }

    private static Button AddButton(string text, Action onClick)
    {
        var b = Ui.Button(text, onClick, glyph: Glyphs.Add);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    private static FrameworkElement CaptureRow(string waiting, bool watching, string? feedback, Action cancel, string? hint)
    {
        var status = watching
            ? Ui.Row(8, Ui.Icon(Glyphs.Refresh, 13, "TextSecondary"), Ui.Text(waiting, 13))
            : Ui.Row(8, Ui.Icon(Glyphs.Warning, 13, "Orange"), Ui.Text("Fieldkit cannot watch the mouse right now.", 13));
        return Ui.Stack(5,
            Ui.Split(status, Ui.Button("Cancel", cancel)),
            feedback is null ? null : InputUi.Warning(feedback),
            hint is null ? null : InputUi.Note(hint));
    }
}
