// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Core;

/// <summary>Lets surfaces close the tray panel (e.g. before opening a tool window).</summary>
public static class PanelHost
{
    internal static Action? HideAction;
    internal static Action<string?>? ShowAction;
    internal static Action<string>? OpenSettingsAction;

    public static void Hide() => HideAction?.Invoke();

    /// <summary>Opens the tray panel, optionally on a given section id.</summary>
    public static void Show(string? sectionId = null) => ShowAction?.Invoke(sectionId);

    /// <summary>Opens the Settings window on a page id.</summary>
    public static void OpenSettings(string pageId) => OpenSettingsAction?.Invoke(pageId);
}
