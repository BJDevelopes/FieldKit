// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>A small themed confirmation (the counterpart of a SwiftUI alert): title, message, Cancel and an action.</summary>
public static class ConfirmDialog
{
    /// <summary>Shows the dialog modally over <paramref name="owner"/> (or centered) and returns true when confirmed.</summary>
    public static bool Show(Window? owner, string title, string message, string confirmText, string cancelText = "Cancel", bool destructive = true)
    {
        var dialog = new FloatingWindow { HideOnDeactivate = false, Width = 380, SizeToContent = SizeToContent.Height, Title = title };
        var confirmed = false;
        var cancel = Ui.Button(cancelText, () => dialog.Close());
        var confirm = Ui.Button(confirmText, () =>
        {
            confirmed = true;
            dialog.Close();
        }, accent: !destructive);
        if (destructive) confirm.SetResourceReference(Control.ForegroundProperty, "Red");
        cancel.IsCancel = true;
        confirm.IsDefault = true;
        cancel.MinWidth = confirm.MinWidth = 96;
        var buttons = Ui.Row(8, cancel, confirm);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var body = Ui.Stack(10, Ui.Title(title, 15), Ui.Caption(message, 13), buttons);
        body.Margin = new Thickness(20, 18, 20, 16);
        dialog.Body = body;
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                confirmed = true;
                dialog.Close();
            }
        };
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
        return confirmed;
    }
}
