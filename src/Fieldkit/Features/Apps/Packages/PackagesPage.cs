// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Packages;

/// <summary>Settings → Utilities → Packages: open the tool, the winget agreement, sources and the shortcut.</summary>
public static class PackagesPage
{
    public static FrameworkElement Build() => Build(PackageManager.Shared);

    public static FrameworkElement Build(PackageManager manager)
    {
        SheetHost? sheet = null;
        var available = WingetCli.IsAvailable;

        var open = Ui.Button(PackagesStrings.OpenPackages, PackagesWindow.Open, accent: true, glyph: Glyphs.Package);
        var managerRow = Ui.Split(
            Ui.Stack(2, Ui.Text(PackagesStrings.ManagerTitle, 13.5, FontWeights.Medium),
                Ui.Caption(available ? PackagesStrings.WingetFound : WingetStrings.MissingBody)),
            available
                ? open
                : Ui.Button(WingetStrings.GetAppInstaller, () =>
                {
                    if (!WingetUi.OpenUri(WingetCommands.AppInstallerStoreUri)) WingetUi.OpenUri(WingetCommands.AppInstallerWebUrl);
                }, glyph: WingetGlyphs.Store));

        var consentHost = new ContentControl();
        void SyncConsent()
        {
            var accepted = WingetConsent.Accepted;
            consentHost.Content = Ui.Split(
                Ui.Stack(2, Ui.Text(PackagesStrings.AgreementsRow, 13.5, FontWeights.Medium),
                    Ui.Caption(accepted ? PackagesStrings.AgreementsAccepted : PackagesStrings.AgreementsNotAccepted)),
                accepted
                    ? Ui.Button(PackagesStrings.Withdraw, WingetConsent.Withdraw)
                    : Ui.Button(PackagesStrings.Review, () => sheet?.Show(sheet.Consent(() => { }))));
        }
        SyncConsent();
        Ui.Track(consentHost, () => Settings.Observe(SyncConsent, WingetConsent.Key));

        var sourcesHost = new ContentControl();
        var update = Ui.Button(PackagesStrings.UpdateSources, () =>
        {
            sheet?.Show(sheet.Confirmation(PackagesStrings.ConfirmUpdateSourcesTitle, PackagesStrings.ConfirmUpdateSourcesBody, null,
                PackagesStrings.UpdateSources, false, () => manager.Perform(WingetAction.UpdateSources, null), WingetGlyphs.Sync));
        }, glyph: WingetGlyphs.Sync);
        void SyncSources()
        {
            update.IsEnabled = available && !manager.IsBusy;
            var rows = new List<UIElement>();
            if (!manager.SourcesLoaded)
            {
                rows.Add(Ui.Caption(available ? PackagesStrings.SourcesLoading : WingetStrings.MissingTitle));
            }
            else if (manager.Sources.Count == 0)
            {
                rows.Add(Ui.Caption(PackagesStrings.SourcesNone));
            }
            else
            {
                foreach (var s in manager.Sources)
                {
                    var name = Ui.Text(s.Name, 13.5, FontWeights.Medium, wrap: false);
                    var url = Ui.Caption(s.Argument, 12);
                    rows.Add(Ui.Split(Ui.Stack(2, name, url), s.Explicit == true ? Ui.Badge(PackagesStrings.Explicit) : null));
                }
            }
            var stack = new StackPanel();
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) stack.Children.Add(Ui.Divider(8));
                stack.Children.Add(rows[i]);
            }
            sourcesHost.Content = stack;
        }
        SyncSources();
        Ui.Track(sourcesHost, () =>
        {
            manager.Changed += SyncSources;
            if (!manager.SourcesLoaded) manager.LoadSources();
            return new ActionDisposable(() => manager.Changed -= SyncSources);
        });

        var page = Ui.Page(PackagesStrings.Name, PackagesStrings.PageIntro,
            Ui.Group(null, managerRow),
            Ui.Group(PackagesStrings.AgreementsTitle, consentHost),
            Ui.Group(PackagesStrings.SourcesTitle, sourcesHost, Ui.Split(Ui.Caption(PackagesStrings.SourcesCaption), update)),
            Ui.Group(PackagesStrings.ShortcutTitle, Ui.ShortcutRow(PackagesStrings.ShortcutRow, PackagesModule.ShortcutKey, string.Empty)));
        sheet = new SheetHost(page);
        return sheet;
    }
}
