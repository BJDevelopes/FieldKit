// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.ClipboardTools;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.BrowserPicker;

/// <summary>An installed browser, from the Windows "StartMenuInternet" registration every browser makes.</summary>
public sealed record BrowserInfo(string Id, string Name, string Exe);

/// <summary>"Always open this site in that browser."</summary>
public sealed record BrowserRule(string Domain, string BrowserId);

/// <summary>Pure rules of the browser picker (unit tested).</summary>
public static class BrowserPickerLogic
{
    /// <summary>Only plain web links are handed to a browser; anything else is refused.</summary>
    public static Uri? SafeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim().Trim('"'), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return string.IsNullOrEmpty(uri.Host) ? null : uri;
    }

    /// <summary>The site a rule is about: lower case, without a leading "www.".</summary>
    public static string Site(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    /// <summary>The most specific rule for a host: "docs.github.com" beats "github.com"; subdomains follow their site.</summary>
    public static BrowserRule? Match(IEnumerable<BrowserRule> rules, string host)
    {
        var site = Site(host);
        return rules
            .Where(r => r.Domain.Length > 0 && (site == r.Domain || site.EndsWith("." + r.Domain, StringComparison.Ordinal)))
            .OrderByDescending(r => r.Domain.Length)
            .FirstOrDefault();
    }

    /// <summary>The program in a registry "shell\open\command" value (quoted or not, with or without arguments).</summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    /// <summary>The single, quoted argument a browser gets: an absolute URI never contains quotes or spaces.</summary>
    public static string Argument(Uri url) => "\"" + url.AbsoluteUri + "\"";

    public static string Store(IEnumerable<BrowserRule> rules) => string.Join("\n", rules.Select(r => r.Domain + "|" + r.BrowserId));

    public static List<BrowserRule> Load(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|', 2))
            .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
            .Select(p => new BrowserRule(Site(p[0]), p[1]))
            .ToList();
}

/// <summary>Installed browsers, and Fieldkit's own registration as one (current user only, no administrator rights).</summary>
public static class Browsers
{
    public const string ProgId = "FieldkitURL";
    private const string ClientKey = @"Software\Clients\StartMenuInternet\Fieldkit";

    /// <summary>Every registered browser except Fieldkit itself and Internet Explorer's leftover entry.</summary>
    public static List<BrowserInfo> Installed()
    {
        var found = new Dictionary<string, BrowserInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var root = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                if (root is null) continue;
                foreach (var id in root.GetSubKeyNames())
                {
                    if (found.ContainsKey(id) || id.Equals("Fieldkit", StringComparison.OrdinalIgnoreCase) || id.Equals("IEXPLORE.EXE", StringComparison.OrdinalIgnoreCase)) continue;
                    using var key = root.OpenSubKey(id);
                    using var command = root.OpenSubKey(id + @"\shell\open\command");
                    var exe = BrowserPickerLogic.ExeFromCommand(command?.GetValue(null) as string);
                    if (exe is null || !File.Exists(exe)) continue;
                    var name = key?.GetValue(null) as string;
                    found[id] = new BrowserInfo(id, string.IsNullOrWhiteSpace(name) ? id : name, exe);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Listing browsers failed: {ex.Message}");
            }
        }
        return found.Values.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command");
            return key?.GetValue(null) is string s && Environment.ProcessPath is { } exe && s.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Whether Windows sends web links to Fieldkit (the user picked it in Default apps).</summary>
    public static bool IsDefault
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return string.Equals(key?.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Registers Fieldkit as a browser for this user so it can be chosen in Settings → Apps → Default apps.</summary>
    public static void Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path");
        var open = $"\"{exe}\" --open-url \"%1\"";
        using (var url = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            url.SetValue(null, "Fieldkit browser picker");
            url.SetValue("URL Protocol", string.Empty);
            using var icon = url.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using var cmd = url.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, open);
        }
        using (var client = Registry.CurrentUser.CreateSubKey(ClientKey))
        {
            client.SetValue(null, "Fieldkit");
            using (var caps = client.CreateSubKey("Capabilities"))
            {
                caps.SetValue("ApplicationName", "Fieldkit");
                caps.SetValue("ApplicationDescription", "Choose a browser for every link.");
                caps.SetValue("ApplicationIcon", $"\"{exe}\",0");
                using var assoc = caps.CreateSubKey("URLAssociations");
                assoc.SetValue("http", ProgId);
                assoc.SetValue("https", ProgId);
                using var start = caps.CreateSubKey("StartMenu");
                start.SetValue("StartMenuInternet", "Fieldkit");
            }
            using (var icon = client.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using var cmd = client.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, $"\"{exe}\"");
        }
        using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue("Fieldkit", ClientKey + @"\Capabilities");
        Log.Info("Registered as a browser");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClientKey, throwOnMissingSubKey: false);
        using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            apps?.DeleteValue("Fieldkit", throwOnMissingValue: false);
        Log.Info("Unregistered as a browser");
    }

    public static void OpenDefaultApps()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Fieldkit") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Opening Default apps failed: {ex.Message}"); }
    }

    /// <summary>Starts <paramref name="browser"/> with the link as its only argument.</summary>
    public static bool Launch(BrowserInfo browser, Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(browser.Exe, BrowserPickerLogic.Argument(url)) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Starting {browser.Name} failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Settings keys of the browser picker.</summary>
public static class BrowserPickerKeys
{
    public const string FeatureId = "browserPicker";
    public const string Rules = "browserPicker.rules";
    public const string Fallback = "browserPicker.fallback";
}

/// <summary>
/// The browser picker (upstream #2861): with Fieldkit chosen as the default browser, every link lands here.
/// A site with a rule opens straight in its browser; anything else shows a small picker at the pointer.
/// A link is never lost: if the picker cannot be used, it opens in the first browser found.
/// </summary>
public static class BrowserPicker
{
    private static PickerWindow? _window;

    public static string Open(string raw)
    {
        if (BrowserPickerLogic.SafeUrl(raw) is not { } url)
        {
            Log.Warn("Browser picker refused a link that is not http or https");
            return "error: not an http(s) link";
        }
        var browsers = Browsers.Installed();
        if (browsers.Count == 0) return "error: no browser installed";
        var rules = BrowserPickerLogic.Load(Settings.GetString(BrowserPickerKeys.Rules, string.Empty));
        var chosen = BrowserPickerLogic.Match(rules, url.Host) is { } rule ? browsers.FirstOrDefault(b => b.Id == rule.BrowserId) : null;
        if (chosen is not null || !FeatureCatalog.IsAvailable(BrowserPickerKeys.FeatureId))
        {
            var target = chosen ?? Fallback(browsers);
            return Browsers.Launch(target, url) ? "ok" : "error: could not start " + target.Name;
        }
        _window?.Close();
        _window = new PickerWindow(url, browsers);
        _window.ShowAtPointer();
        return "ok";
    }

    /// <summary>The browser used when the picker is off: the one chosen in Settings, else the first.</summary>
    public static BrowserInfo Fallback(IReadOnlyList<BrowserInfo> browsers) =>
        browsers.FirstOrDefault(b => b.Id == Settings.GetString(BrowserPickerKeys.Fallback, string.Empty)) ?? browsers[0];

    public static void AddRule(string site, string browserId)
    {
        var rules = BrowserPickerLogic.Load(Settings.GetString(BrowserPickerKeys.Rules, string.Empty))
            .Where(r => r.Domain != BrowserPickerLogic.Site(site)).ToList();
        rules.Add(new BrowserRule(BrowserPickerLogic.Site(site), browserId));
        Settings.Set(BrowserPickerKeys.Rules, BrowserPickerLogic.Store(rules.OrderBy(r => r.Domain, StringComparer.Ordinal)));
    }

    /// <summary>The picker: one row per browser (1–9 pick by keyboard), "Always use for this site", Copy link.</summary>
    private sealed class PickerWindow : FloatingWindow
    {
        private readonly Uri _url;
        private readonly IReadOnlyList<BrowserInfo> _browsers;
        private readonly CheckBox _remember;

        public PickerWindow(Uri url, IReadOnlyList<BrowserInfo> browsers)
        {
            _url = url;
            _browsers = browsers;
            Title = "Open link with";
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = -32000;
            var site = BrowserPickerLogic.Site(url.Host);
            var host = Ui.Text(site, 14, FontWeights.SemiBold, wrap: false);
            var full = Ui.Text(url.AbsoluteUri, 11.5, brush: "TextTertiary", wrap: false);
            full.TextTrimming = TextTrimming.CharacterEllipsis;
            full.MaxWidth = 300;
            full.ToolTip = url.AbsoluteUri;
            var copy = HeaderButton(Glyphs.Copy, "Copy link", () =>
            {
                try { Clipboard.SetText(url.AbsoluteUri); } catch { }
                Close();
            });
            var list = Ui.Stack(2);
            for (var i = 0; i < browsers.Count; i++)
            {
                var browser = browsers[i];
                var number = i < 9 ? Ui.Text((i + 1).ToString(), 11, brush: "TextTertiary", wrap: false) : null;
                var row = Ui.Split(Ui.Row(10, AppIcons.Element(AppIcons.ForPath(browser.Exe), 24), Ui.Text(browser.Name, 13.5, FontWeights.Medium, wrap: false)), number);
                row.Margin = new Thickness(8, 6, 8, 6);
                var button = Ui.Clickable(row, () => Pick(browser));
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                list.Children.Add(button);
            }
            _remember = new CheckBox { Content = Ui.Text($"Always use it for {site}", 12.5, wrap: false), Margin = new Thickness(8, 6, 0, 0) };
            var body = Ui.Stack(8, Ui.Split(Ui.Stack(1, host, full), copy), list, _remember);
            body.Margin = new Thickness(14, 12, 14, 12);
            body.MinWidth = 280;
            Body = body;
            HideOnDeactivate = true;
            PreviewKeyDown += (_, e) =>
            {
                var digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
                if (digit >= 0 && digit < _browsers.Count)
                {
                    e.Handled = true;
                    Pick(_browsers[digit]);
                }
            };
        }

        public override void Dismiss() => Close();

        public void ShowAtPointer()
        {
            Show();
            UpdateLayout();
            Native.GetCursorPos(out var p);
            MoveCardTo(p.X - 20, p.Y - 20);
            Activate();
            WindowHelper.Activate(Handle);
        }

        private void Pick(BrowserInfo browser)
        {
            if (_remember.IsChecked == true) BrowserPicker.AddRule(_url.Host, browser.Id);
            Close();
            Browsers.Launch(browser, _url);
        }
    }
}

/// <summary>Settings → Browser picker.</summary>
internal static class BrowserPickerPage
{
    public static FrameworkElement Build() => Ui.Page("Browser picker",
        "Choose which browser opens each link, or let a site always open in the same one.",
        Setup(),
        InputUi.Live(Rules, BrowserPickerKeys.Rules));

    /// <summary>Registration lives in the registry, not in Settings: this section redraws itself after each button.</summary>
    private static UIElement Setup()
    {
        var host = new ContentControl();
        void Render() => host.Content = SetupContent(Render);
        Render();
        return host;
    }

    private static UIElement SetupContent(Action refresh)
    {
        var registered = Browsers.IsRegistered;
        var isDefault = registered && Browsers.IsDefault;
        var status = isDefault
            ? InputUi.InfoLine(Glyphs.Check, "Fieldkit is your default browser: links show the picker.")
            : InputUi.InfoLine(Glyphs.Info, registered
                ? "Fieldkit is registered. Choose it as the default browser for HTTP and HTTPS in Default apps."
                : "Register Fieldkit as a browser, then choose it in Windows' Default apps. Only your user account is changed.");
        var buttons = Ui.Row(8);
        if (!registered) buttons.Children.Add(Ui.Button("Register Fieldkit as a browser", () => { Browsers.Register(); refresh(); }, accent: true));
        if (registered && !isDefault) buttons.Children.Add(Ui.Button("Open Default apps", Browsers.OpenDefaultApps, accent: true));
        if (registered) buttons.Children.Add(Ui.Button("Unregister", () => { Browsers.Unregister(); refresh(); }));
        var browsers = Browsers.Installed();
        var fallback = browsers.Count > 0
            ? Ui.ChoiceRow("When the picker is off", BrowserPickerKeys.Fallback, browsers.Select(b => (b.Id, b.Name)).ToList(), browsers[0].Id,
                "Links still open, in this browser, if the browser picker is uninstalled while Fieldkit is the default.")
            : null;
        return Ui.Group("Set up", status, buttons, fallback);
    }

    private static UIElement Rules()
    {
        var rules = BrowserPickerLogic.Load(Settings.GetString(BrowserPickerKeys.Rules, string.Empty));
        var browsers = Browsers.Installed().ToDictionary(b => b.Id, b => b.Name);
        var rows = new List<UIElement?>();
        if (rules.Count == 0) rows.Add(Ui.Caption("No sites yet. Tick \"Always use it\" in the picker to add one.", 12));
        foreach (var rule in rules)
        {
            var r = rule;
            rows.Add(Ui.Split(Ui.Row(8, Ui.Text(r.Domain, 13, FontWeights.Medium, wrap: false),
                    Ui.Text("→ " + (browsers.TryGetValue(r.BrowserId, out var n) ? n : r.BrowserId), 13, brush: "TextSecondary", wrap: false)),
                Ui.IconButton(Glyphs.Close, "Remove", () => Settings.Set(BrowserPickerKeys.Rules,
                    BrowserPickerLogic.Store(BrowserPickerLogic.Load(Settings.GetString(BrowserPickerKeys.Rules, string.Empty)).Where(x => x != r))), 11)));
        }
        return Ui.Group("Sites", rows.ToArray());
    }
}

/// <summary>Browser picker (a Fieldkit extra, upstream #2861).</summary>
public sealed class BrowserPickerModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(BrowserPickerKeys.FeatureId, Parity.Full, "Fieldkit extra, from Vorssaint issue #2861.");
        CommandChannel.OpenUrl = BrowserPicker.Open;
        r.SettingsPage(new SettingsPageDef("browserPicker", SettingsCategory.Utilities, "Browser picker", Glyphs.Link, 67,
            [BrowserPickerKeys.FeatureId], BrowserPickerPage.Build, ["browser", "default browser", "links", "chrome", "edge", "firefox", "open with"]));
    }
}
