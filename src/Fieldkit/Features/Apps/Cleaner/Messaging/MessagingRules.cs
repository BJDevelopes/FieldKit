// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;

namespace Fieldkit.Features.Apps.Cleaner.Messaging;

/// <summary>User-facing buckets for confirmed messaging downloads (raw values persisted: append only).</summary>
public enum MessagingCategory { Image, Video, Audio, Document, Archive, Other }

public enum OrganizerLayout { Flat, Category, Month }

public enum DuplicateAction { TrashNew, KeepBoth, ReplaceExisting }

/// <summary>Which messenger a download is attributed to.</summary>
public enum MessagingApp { WhatsApp, Telegram }

/// <summary>Where a candidate was found.</summary>
public enum MessagingSource
{
    /// <summary>Top level of Downloads, attributed by its Zone.Identifier (the counterpart of macOS quarantine data).</summary>
    Downloads,
    /// <summary>Downloads\Telegram Desktop, where Telegram Desktop saves files.</summary>
    TelegramFolder,
    /// <summary>WhatsApp Desktop's own media folder (LocalState\shared\transfers), only while WhatsApp is closed.</summary>
    WhatsAppMedia,
    /// <summary>Filed away by the organizer: reviewable by hand, never cleaned automatically.</summary>
    Organized,
}

/// <summary>Settings keys of messaging downloads (part of the cleaner feature).</summary>
public static class MessagingKeys
{
    public const string Enabled = "cleaner.messaging.enabled";
    public const string Automatic = "cleaner.messaging.automatic";
    public const string Categories = "cleaner.messaging.categories";
    public const string RetentionDays = "cleaner.messaging.retentionDays";
    public const string Notify = "cleaner.messaging.notify";
    public const string IncludeExisting = "cleaner.messaging.includeExisting";
    public const string AutomaticStart = "cleaner.messaging.automaticStart";
    public const string Exclusions = "cleaner.messaging.exclusions";
    public const string SourceWhatsApp = "cleaner.messaging.sourceWhatsApp";
    public const string SourceTelegram = "cleaner.messaging.sourceTelegram";
    public const string LastCleanup = "cleaner.messaging.lastCleanup";
    public const string LastCleanupCount = "cleaner.messaging.lastCleanupCount";
    public const string LastCleanupBytes = "cleaner.messaging.lastCleanupBytes";
    public const string LastCleanupFailed = "cleaner.messaging.lastCleanupFailed";
    public const string LastAutoRun = "cleaner.messaging.lastAutoRun";

    public const string OrganizerEnabled = "cleaner.messaging.organizer.enabled";
    public const string OrganizerDestination = "cleaner.messaging.organizer.destination";
    public const string OrganizerDelay = "cleaner.messaging.organizer.delayMinutes";
    public const string OrganizerCategories = "cleaner.messaging.organizer.categories";
    public const string OrganizerLayout = "cleaner.messaging.organizer.layout";
    public const string OrganizerDuplicateAction = "cleaner.messaging.organizer.duplicateAction";
    public const string OrganizerLastRun = "cleaner.messaging.organizer.lastRun";
    public const string OrganizerLastMoved = "cleaner.messaging.organizer.lastMoved";
    public const string OrganizerLastDuplicates = "cleaner.messaging.organizer.lastDuplicates";
    public const string OrganizerLastFailed = "cleaner.messaging.organizer.lastFailed";

    public const string DefaultCategories = "image,video,audio";
    public const string AllCategories = "image,video,audio,document,archive,other";
}

/// <summary>
/// Pure classification and retention rules for messaging downloads (upstream
/// WhatsAppDownloadSupport). Keeping every judgment here makes the destructive boundary easy to
/// test without touching a real Downloads folder.
/// </summary>
public static class MessagingRules
{
    public static readonly int[] AllowedRetentionDays = [1, 2, 7, 14, 30];
    public static readonly int[] AllowedOrganizerDelayMinutes = [1, 5, 15, 60];
    public static readonly TimeSpan OrganizerUndoLifetime = TimeSpan.FromDays(7);
    public static readonly IReadOnlySet<MessagingCategory> DefaultCategories =
        new HashSet<MessagingCategory> { MessagingCategory.Image, MessagingCategory.Video, MessagingCategory.Audio };

    private static readonly HashSet<string> IncompleteExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "download", "part", "partial", "crdownload", "tmp" };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "7z", "bz", "bz2", "cab", "dmg", "gz", "iso", "rar", "tar", "tgz", "xz", "zip" };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "doc", "docm", "docx", "epub", "key", "md", "numbers", "odp", "ods", "odt", "pages", "pdf", "ppt", "pptm",
        "pptx", "rtf", "tex", "txt", "xls", "xlsm", "xlsx",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "tif", "tiff", "avif", "svg", "jfif" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mov", "m4v", "avi", "mkv", "webm", "3gp", "wmv", "mpg", "mpeg" };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "m4a", "aac", "wav", "ogg", "oga", "opus", "flac", "wma", "amr" };

    /// <summary>Hosts whose downloads are attributed to a messenger (subdomains included).</summary>
    private static readonly (string Host, MessagingApp App)[] Hosts =
    [
        ("whatsapp.com", MessagingApp.WhatsApp), ("whatsapp.net", MessagingApp.WhatsApp),
        ("telegram.org", MessagingApp.Telegram), ("t.me", MessagingApp.Telegram), ("telegram.me", MessagingApp.Telegram),
    ];

    public static int SanitizedRetentionDays(int value) => AllowedRetentionDays.Contains(value) ? value : 7;

    public static int SanitizedOrganizerDelayMinutes(int value) => AllowedOrganizerDelayMinutes.Contains(value) ? value : 5;

    public static string EncodeCategories(IEnumerable<MessagingCategory> categories)
    {
        var set = categories.ToHashSet();
        return string.Join(",", Enum.GetValues<MessagingCategory>().Where(set.Contains).Select(c => c.ToString().ToLowerInvariant()));
    }

    public static HashSet<MessagingCategory> DecodeCategories(string? raw)
    {
        if (raw is null) return [.. DefaultCategories];
        var result = new HashSet<MessagingCategory>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<MessagingCategory>(part, ignoreCase: true, out var c) && Enum.IsDefined(c)) result.Add(c);
        }
        return result;
    }

    public static OrganizerLayout ParseLayout(string? raw) =>
        Enum.TryParse<OrganizerLayout>(raw, true, out var l) && Enum.IsDefined(l) ? l : OrganizerLayout.Flat;

    public static DuplicateAction ParseDuplicateAction(string? raw) =>
        Enum.TryParse<DuplicateAction>(raw, true, out var a) && Enum.IsDefined(a) ? a : DuplicateAction.TrashNew;

    public static bool IsIncompleteFile(string extension) => IncompleteExtensions.Contains(extension.TrimStart('.'));

    public static MessagingCategory Category(string extension)
    {
        var ext = extension.TrimStart('.');
        if (ArchiveExtensions.Contains(ext)) return MessagingCategory.Archive;
        if (DocumentExtensions.Contains(ext)) return MessagingCategory.Document;
        if (ImageExtensions.Contains(ext)) return MessagingCategory.Image;
        if (VideoExtensions.Contains(ext)) return MessagingCategory.Video;
        if (AudioExtensions.Contains(ext)) return MessagingCategory.Audio;
        return MessagingCategory.Other;
    }

    /// <summary>Old enough only when both the download date and the latest edit are beyond the
    /// retention window: editing a document in place postpones cleanup instead of losing work.</summary>
    public static bool IsOldEnough(DateTime downloadedAt, DateTime modifiedAt, DateTime now, int retentionDays)
    {
        var cutoff = now.AddDays(-SanitizedRetentionDays(retentionDays));
        return downloadedAt <= cutoff && modifiedAt <= cutoff;
    }

    public static bool IsEligibleForRules(MessagingCategory category, DateTime downloadedAt, DateTime modifiedAt, DateTime now,
        int retentionDays, IReadOnlySet<MessagingCategory> enabled) =>
        enabled.Contains(category) && IsOldEnough(downloadedAt, modifiedAt, now, retentionDays);

    public static bool IsEligibleForAutomaticCleanup(MessagingCategory category, DateTime downloadedAt, DateTime modifiedAt, DateTime now,
        int retentionDays, IReadOnlySet<MessagingCategory> enabled, bool includeExisting, DateTime? automaticStart)
    {
        if (!IsEligibleForRules(category, downloadedAt, modifiedAt, now, retentionDays, enabled)) return false;
        if (includeExisting) return true;
        return automaticStart is { } start && downloadedAt >= start;
    }

    /// <summary>The automatic pass runs once a day at 9:00 local time.</summary>
    public static DateTime NextAutomaticCheck(DateTime after)
    {
        var today = after.Date.AddHours(9);
        return today > after ? today : today.AddDays(1);
    }

    public static bool MissedAutomaticCheck(DateTime now, DateTime? lastRun)
    {
        if (lastRun is null) return false;
        var today = now.Date.AddHours(9);
        return now >= today && lastRun < today;
    }

    public static bool IsStableForOrganization(DateTime downloadedAt, DateTime modifiedAt, DateTime now, int delayMinutes)
    {
        var latest = downloadedAt > modifiedAt ? downloadedAt : modifiedAt;
        return now - latest >= TimeSpan.FromMinutes(SanitizedOrganizerDelayMinutes(delayMinutes));
    }

    public static bool OrganizerUndoIsValid(DateTime createdAt, DateTime now)
    {
        var age = now - createdAt;
        return age >= TimeSpan.Zero && age < OrganizerUndoLifetime;
    }

    public static string OrganizerCategoryFolder(MessagingCategory category) => category switch
    {
        MessagingCategory.Image => "Images",
        MessagingCategory.Video => "Videos",
        MessagingCategory.Audio => "Audio",
        MessagingCategory.Document => "Documents",
        MessagingCategory.Archive => "Archives",
        _ => "Other",
    };

    public static string[] OrganizerRelativeComponents(OrganizerLayout layout, MessagingCategory category, DateTime date) => layout switch
    {
        OrganizerLayout.Category => [OrganizerCategoryFolder(category)],
        OrganizerLayout.Month => [date.Year.ToString("0000", CultureInfo.InvariantCulture), date.Month.ToString("00", CultureInfo.InvariantCulture)],
        _ => [],
    };

    /// <summary>Reads HostUrl / ReferrerUrl from a Zone.Identifier stream ("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=…").</summary>
    public static (string? HostUrl, string? ReferrerUrl) ParseZoneIdentifier(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (null, null);
        string? host = null, referrer = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) host = value;
            else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrer = value;
        }
        return (host, referrer);
    }

    /// <summary>The messenger a Zone.Identifier attributes a download to (WhatsApp Desktop and
    /// Telegram Web save through a browser engine that records the page they came from).</summary>
    public static MessagingApp? AttributedApp(string? zoneIdentifier)
    {
        var (host, referrer) = ParseZoneIdentifier(zoneIdentifier);
        return AppForUrl(referrer) ?? AppForUrl(host);
    }

    public static MessagingApp? AppForUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) u = u[5..];
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var hostName = uri.Host.ToLowerInvariant();
        foreach (var (h, app) in Hosts)
        {
            if (hostName == h || hostName.EndsWith("." + h, StringComparison.Ordinal)) return app;
        }
        return null;
    }

    /// <summary>"photo.jpg" → "photo (2).jpg", "photo (3).jpg", … until <paramref name="exists"/> says no.</summary>
    public static string UniqueName(string directory, string fileName, Func<string, bool> exists)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static string CategoryName(MessagingCategory c) => c switch
    {
        MessagingCategory.Image => S.MsgImage,
        MessagingCategory.Video => S.MsgVideo,
        MessagingCategory.Audio => S.MsgAudio,
        MessagingCategory.Document => S.MsgDocument,
        MessagingCategory.Archive => S.MsgArchive,
        _ => S.MsgOther,
    };

    public static string CategoryGlyph(MessagingCategory c) => c switch
    {
        MessagingCategory.Image => CleanerGlyphs.Image,
        MessagingCategory.Video => CleanerGlyphs.Film,
        MessagingCategory.Audio => CleanerGlyphs.Audio,
        MessagingCategory.Document => CleanerGlyphs.Document,
        MessagingCategory.Archive => CleanerGlyphs.Zip,
        _ => CleanerGlyphs.Page,
    };
}
