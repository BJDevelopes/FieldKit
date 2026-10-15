// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// "Clean URLs as you copy them": on every clipboard change (listener, no polling) a copy that is a
/// single link carrying tracking parameters is replaced by the cleaned link. A copy with nothing to
/// remove, a picture, a file, a private copy or HTML that shows more than the link is left exactly
/// as it was. Runs before the clipboard history, which then records the cleaned link.
/// </summary>
public sealed class UrlCleanerService : FeatureService
{
    public const string Id = "urlCleaner";
    public const string EnabledKey = "urlCleaner.enabled";
    public const string CustomParametersKey = "urlCleaner.customParameters";
    public const string SiteParametersKey = "urlCleaner.siteParameters";
    public const string DisabledParametersKey = "urlCleaner.disabledParameters";

    private IDisposable? _subscription;

    /// <summary>The last link the automatic clean produced and the names it took out (Settings shows them).</summary>
    public static (string Url, IReadOnlyList<string> Removed)? LastCleaned { get; private set; }

    public static event Action? Cleaned;

    public override string FeatureId => Id;

    public override IReadOnlyList<string> EnableKeys => [EnabledKey];

    public override bool EnabledByDefault => false;

    public static UrlCleaning.Rules CurrentRules => UrlCleaning.RulesFrom(
        Settings.GetString(CustomParametersKey),
        Settings.GetString(SiteParametersKey),
        Settings.GetString(DisabledParametersKey));

    protected override void OnStart() => _subscription = ClipboardLane.Subscribe(OnChange, priority: 100);

    protected override void OnStop()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    // Lane thread.
    private static void OnChange(ClipboardChange change, ClipboardAccess access)
    {
        if (change.OwnWrite is not null) return;
        var formats = access.FormatNames();
        if (!UrlCleaning.CanRewriteClipboard(formats) || access.IsPrivate()) return;
        var text = access.ReadText() ?? access.ReadUtf16("UniformResourceLocatorW", 8192);
        if (string.IsNullOrEmpty(text)) return;
        var cleaned = UrlCleaning.Clean(text, CurrentRules);
        if (cleaned is null || cleaned.Removed.Count == 0) return;
        if (formats.Contains("HTML Format"))
        {
            var html = access.ReadUtf8("HTML Format", 72 * 1024);
            if (html is null || !UrlCleaning.HtmlAllowsRewrite(html, text)) return;
        }
        // Another app may have copied since the read; this narrows the window, it cannot close it.
        if (access.Sequence != change.Sequence) return;
        if (!access.WriteText(cleaned.Url, OwnWriteKind.Rewrite, source: change.SourcePath)) return;
        Ui.OnUi(() =>
        {
            LastCleaned = (cleaned.Url, cleaned.Removed);
            Cleaned?.Invoke();
        });
    }

    /// <summary>Copies a link Fieldkit cleaned on request (Clean URL tool, Settings).</summary>
    public static Task<bool> CopyAsync(string url) => ClipboardLane.Run(access => access.WriteText(url, OwnWriteKind.Other));

    /// <summary>The clipboard's text, for the Paste buttons.</summary>
    public static Task<string> ReadClipboardTextAsync() => ClipboardLane.Run(access => access.ReadText() ?? string.Empty);
}
