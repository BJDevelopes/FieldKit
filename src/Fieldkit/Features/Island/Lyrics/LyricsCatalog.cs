// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fieldkit.Core;

namespace Fieldkit.Features.Island.Lyrics;

/// <summary>The recording lyrics are looked up for.</summary>
public sealed record LyricsTrack(string Title, string Artist, string Album, double Duration);

/// <summary>
/// LRCLIB request building and reply validation (port of upstream <c>NotchLyricsSupport</c>):
/// the exact <c>/api/get</c> lookup first, then up to three <c>/api/search</c> queries whose results
/// must be the same recording (title, credits, live/remix kind, duration within 2 s).
/// </summary>
public static partial class LyricsCatalog
{
    public const string GetEndpoint = "https://lrclib.net/api/get";
    public const string SearchEndpoint = "https://lrclib.net/api/search";
    public const int MaximumSearchBytes = 1024 * 1024;

    /// <summary>A trailing " - Single" / " - EP" is the release type, not the album name.</summary>
    public static string CatalogAlbum(string album)
    {
        var a = album.Trim();
        foreach (var suffix in new[] { " - Single", " - EP" })
            if (a.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return a[..^suffix.Length].Trim();
        return a;
    }

    public static string? LookupUrl(LyricsTrack track)
    {
        var album = CatalogAlbum(track.Album);
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return null;
        if (Encoding.UTF8.GetByteCount(track.Title) > 1024 || Encoding.UTF8.GetByteCount(track.Artist) > 1024 || Encoding.UTF8.GetByteCount(album) > 1024) return null;
        if (!double.IsFinite(track.Duration) || track.Duration < 1 || track.Duration > 3600) return null;
        var sb = new StringBuilder(GetEndpoint);
        sb.Append("?track_name=").Append(Uri.EscapeDataString(track.Title));
        sb.Append("&artist_name=").Append(Uri.EscapeDataString(track.Artist));
        if (album.Length > 0) sb.Append("&album_name=").Append(Uri.EscapeDataString(album));
        sb.Append("&duration=").Append(Math.Round(track.Duration).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static List<string> SearchUrls(LyricsTrack track)
    {
        if (LookupUrl(track) is null) return [];
        var primary = ArtistNames(track.Artist).FirstOrDefault() ?? track.Artist;
        var variants = new[] { (track.Title, track.Artist), (track.Title, primary), (SearchText(track.Title), SearchText(primary)) };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var urls = new List<string>();
        foreach (var (title, artist) in variants)
        {
            if (title.Length == 0 || artist.Length == 0 || !seen.Add(title.ToLowerInvariant() + "\n" + artist.ToLowerInvariant())) continue;
            urls.Add($"{SearchEndpoint}?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}");
        }
        return urls;
    }

    /// <summary>Validates an <c>/api/get</c> reply strictly against the track.</summary>
    public static LyricsPayload? DecodeGet(string json, LyricsTrack track)
    {
        if (json.Length > LrcParser.MaximumBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            var title = Str(o, "trackName");
            var artist = Str(o, "artistName");
            var album = Str(o, "albumName");
            var titleOk = Equal(title, track.Title) || (TitleKey(title) == TitleKey(track.Title) && TitleKey(track.Title).Length > 0);
            var artistOk = Equal(artist, track.Artist) || CreditsMatch(artist ?? string.Empty, track.Artist);
            var trackAlbum = CatalogAlbum(track.Album);
            var albumOk = trackAlbum.Length == 0 || Key(CatalogAlbum(album ?? string.Empty)) == Key(trackAlbum);
            if (!titleOk || !artistOk || !albumOk) return null;
            if (!o.TryGetProperty("duration", out var d) || d.ValueKind != JsonValueKind.Number || Math.Abs(d.GetDouble() - track.Duration) > 2) return null;
            return Payload(o, track.Duration, catalog: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Candidate(LyricsPayload Lyrics, int Score, double Distance, long? Id, string StableKey);

    /// <summary>Picks the best matching record of an <c>/api/search</c> reply, or null.</summary>
    public static LyricsPayload? BestFromSearch(string json, LyricsTrack track)
    {
        if (json.Length > MaximumSearchBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > 20) return null;
            var candidates = new List<Candidate>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                var title = Str(o, "trackName");
                var artist = Str(o, "artistName");
                var album = Str(o, "albumName");
                if (title is null || artist is null || album is null) continue;
                if (TitleKey(title).Length == 0 || TitleKey(title) != TitleKey(track.Title)) continue;
                if (!SearchCreditsMatch(artist, track.Artist)) continue;
                if (!RecordingKinds(album).SetEquals(RecordingKinds(track.Album))) continue;
                if (!o.TryGetProperty("duration", out var d) || d.ValueKind != JsonValueKind.Number) continue;
                var distance = Math.Abs(d.GetDouble() - track.Duration);
                if (distance > 2) continue;
                var payload = Payload(o, track.Duration, catalog: true);
                if (payload is null) continue;
                var score = (Key(CatalogAlbum(album)) == Key(CatalogAlbum(track.Album)) ? 8 : 0)
                            + (Equal(title, track.Title) ? 2 : 0)
                            + (CreditsMatch(artist, track.Artist) ? 4 : 0)
                            + (payload.IsSynced && !payload.Instrumental ? 1 : 0);
                long? id = o.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var v) && v > 0 ? v : null;
                var stable = (payload.Plain ?? string.Empty) + "\n" + string.Join("\n", payload.Lines.Select(l => $"{l.Time}:{l.Text}"));
                candidates.Add(new Candidate(payload, score, distance, id, stable));
            }
            return candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Distance)
                .ThenBy(c => c.Id ?? long.MaxValue)
                .ThenBy(c => c.StableKey, StringComparer.Ordinal)
                .FirstOrDefault()?.Lyrics;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LyricsPayload? Payload(JsonElement o, double duration, bool catalog)
    {
        var plain = (Str(o, "plainLyrics") ?? string.Empty).Trim();
        var instrumental = o.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True;
        var synced = Str(o, "syncedLyrics") ?? string.Empty;
        List<LyricLine> lines;
        if (catalog)
        {
            // A catalog entry whose timeline runs past the recording is another cut: keep its plain text only.
            lines = LrcParser.Parse(synced, Math.Max(duration, 604_800));
            if (lines.Count > 0 && lines[^1].Time > duration + 2) lines = [];
            else lines = lines.Where(l => l.Time <= duration).ToList();
        }
        else
        {
            lines = LrcParser.Parse(synced, duration);
        }
        if (!instrumental && plain.Length == 0 && lines.Count == 0) return null;
        return new LyricsPayload(instrumental ? [] : lines, instrumental ? string.Empty : plain, instrumental);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Equal(string? a, string b) =>
        a is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folds Latin accents (not Cyrillic й), ё→е, lowercases and keeps letters and digits only.</summary>
    public static string Key(string text) => new(Fold(text).Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Accent-folded, punctuation as spaces, single-spaced (search query text).</summary>
    public static string SearchText(string text)
    {
        var folded = Fold(text);
        var sb = new StringBuilder();
        foreach (var c in folded) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return MultiSpace().Replace(sb.ToString(), " ").Trim();
    }

    private static string Fold(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormC))
        {
            if (ch < 0x0250)
            {
                foreach (var d in ch.ToString().Normalize(NormalizationForm.FormD))
                    if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(d));
            }
            else
            {
                var lower = char.ToLowerInvariant(ch);
                sb.Append(lower == 'ё' ? 'е' : lower);
            }
        }
        return sb.ToString();
    }

    /// <summary>Featured credits are artist metadata and "Original Mix" is the default version.</summary>
    private static string CatalogTitle(string title)
    {
        var t = FeatRegex().Replace(title, string.Empty);
        t = OriginalMixRegex().Replace(t, string.Empty);
        return t.Trim();
    }

    public static string TitleKey(string? title) => title is null ? string.Empty : Key(CatalogTitle(title));

    /// <summary>Artist names in a credit: split on , ; ， 、, on " / ", " & ", " • ", and on feat./ft./featuring.</summary>
    public static List<string> ArtistNames(string credit)
    {
        var parts = ArtistSplit().Split(credit);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static bool CreditsMatch(string artist, string expected)
    {
        var a = ArtistNames(artist).Select(Key).Where(k => k.Length > 0).ToHashSet();
        var e = ArtistNames(expected).Select(Key).Where(k => k.Length > 0).ToHashSet();
        return a.Count > 0 && a.SetEquals(e);
    }

    /// <summary>Search results may credit only the primary artist, or a subset of the player's credits.</summary>
    private static bool SearchCreditsMatch(string artist, string expected)
    {
        var a = ArtistNames(artist).Select(Key).Where(k => k.Length > 0).ToHashSet();
        var e = ArtistNames(expected).Select(Key).Where(k => k.Length > 0).ToHashSet();
        return a.Count > 0 && a.IsSubsetOf(e);
    }

    private static readonly string[] Kinds = ["live", "remix", "acoustic", "instrumental", "karaoke"];

    private static HashSet<string> RecordingKinds(string album)
    {
        var lower = album.ToLowerInvariant();
        return Kinds.Where(k => Regex.IsMatch(lower, $@"\b{k}\b")).ToHashSet();
    }

    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring)\s+[^\)\]]+[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();

    [GeneratedRegex(@"\s*(?:[\(\[]\s*original mix\s*[\)\]]|[-–—]\s*original mix)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMixRegex();

    [GeneratedRegex(@"\s*[,;，、]\s*|\s+[/&•]\s+|\s+(?:feat\.?|ft\.?|featuring)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}

public enum LyricsLookupStatus { Found, Missing, Failed }

/// <summary>
/// Fetches lyrics from LRCLIB, only on demand (the Lyrics panel is open and "Find lyrics online" is on).
/// One track is kept in memory; "not found" is remembered for 60 s and failures for 15 s.
/// </summary>
public sealed class LyricsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, (DateTime Until, LyricsLookupStatus Status)> _negative = new(StringComparer.Ordinal);
    private (string Key, LyricsPayload Payload)? _memory;

    public LyricsClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        // Bodies are read with ResponseHeadersRead and a counting limit below; the buffer cap is a second guard.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = LyricsCatalog.MaximumSearchBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version} (+{AppInfo.RepositoryUrl})");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static string TrackKey(LyricsTrack t) => $"{t.Title}\u001f{t.Artist}\u001f{t.Album}\u001f{Math.Round(t.Duration)}";

    public void ForgetFailures() => _negative.Clear();

    public async Task<(LyricsLookupStatus Status, LyricsPayload? Payload)> LookupAsync(LyricsTrack track, CancellationToken ct)
    {
        var key = TrackKey(track);
        if (_memory is { } m && m.Key == key) return (LyricsLookupStatus.Found, m.Payload);
        if (_negative.TryGetValue(key, out var neg) && neg.Until > DateTime.UtcNow) return (neg.Status, null);
        var url = LyricsCatalog.LookupUrl(track);
        if (url is null) return Remember(key, LyricsLookupStatus.Missing);

        var (code, body) = await GetAsync(url, LrcParser.MaximumBytes, ct);
        if (code == HttpStatusCode.OK && body is not null && LyricsCatalog.DecodeGet(body, track) is { } exact)
            return Found(key, exact);
        // Network failures and client errors do not fan out into more requests; 404 and 5xx may still search.
        var canSearch = code == HttpStatusCode.NotFound || code == HttpStatusCode.OK || (int?)code >= 500;
        if (!canSearch) return Remember(key, LyricsLookupStatus.Failed);
        foreach (var searchUrl in LyricsCatalog.SearchUrls(track))
        {
            var (sc, sb) = await GetAsync(searchUrl, LyricsCatalog.MaximumSearchBytes, ct);
            if (sc != HttpStatusCode.OK || sb is null) continue;
            if (LyricsCatalog.BestFromSearch(sb, track) is { } best) return Found(key, best);
        }
        return Remember(key, (int?)code >= 500 ? LyricsLookupStatus.Failed : LyricsLookupStatus.Missing);
    }

    private (LyricsLookupStatus, LyricsPayload?) Found(string key, LyricsPayload payload)
    {
        _memory = (key, payload);
        return (LyricsLookupStatus.Found, payload);
    }

    private (LyricsLookupStatus, LyricsPayload?) Remember(string key, LyricsLookupStatus status)
    {
        _negative[key] = (DateTime.UtcNow.AddSeconds(status == LyricsLookupStatus.Missing ? 60 : 15), status);
        return (status, null);
    }

    private async Task<(HttpStatusCode? Code, string? Body)> GetAsync(string url, int limit, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[limit + 1];
            var total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token)) > 0)
            {
                total += read;
                if (total > limit) return (HttpStatusCode.RequestEntityTooLarge, null);
            }
            return (HttpStatusCode.OK, Encoding.UTF8.GetString(buffer, 0, total));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Lyrics request failed: {ex.Message}");
            return (null, null);
        }
    }

    public void Dispose() => _http.Dispose();
}
