// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>Registers the Command Bar: its service, Settings page, Utilities entry and open action.</summary>
public sealed class CommandBarModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(CommandBarKeys.FeatureId, Parity.Partial,
            "Apps (AppsFolder + Start menu), open windows, quit app, every registered tool action and switch, Fieldkit and Windows Settings pages, " +
            "files (Windows Search, folder listing fallback), calculator, units, colours, dates and time zones, emoji, links, saved searches and scripts, " +
            "aliases, pins, hidden rows, row shortcuts, App shortcuts editor and learned ranking all work. Missing: menu commands of the app in front, " +
            "individual clipboard-history items and snippets as rows (their tools are reached through their actions), actions on selected text, " +
            "uninstall/kill-process browsing inside the bar, the ABC-layout switch, the Dynamic Island presentation; scripts run on Enter instead of while typing.");
        r.Service(CommandBarKeys.FeatureId, () => new CommandBarService());
        r.SettingsPage(new SettingsPageDef(CommandBarKeys.FeatureId, SettingsCategory.Utilities, CommandBarStrings.PageTitle, Glyphs.Command, 80,
            [CommandBarKeys.FeatureId], CommandBarSettingsPage.Build,
            ["launcher", "search", "spotlight", "run", "calculator", "emoji", "apps", "files", "shortcuts", "aliases", "scripts"]));
        r.Utility(new UtilityEntry(CommandBarKeys.FeatureId, CommandBarStrings.PageTitle, CommandBarStrings.PanelCaption, Glyphs.Command,
            Open, 80, CommandBarKeys.Shortcut));
        r.Action(new ToolAction("commandBar.open", CommandBarKeys.FeatureId, "Open Command Bar", Glyphs.Command, Toggle,
            ShortcutKey: CommandBarKeys.Shortcut));
    }

    private static void Open() => FeatureRuntime.Service<CommandBarService>()?.Show();

    private static void Toggle() => FeatureRuntime.Service<CommandBarService>()?.Toggle();
}
