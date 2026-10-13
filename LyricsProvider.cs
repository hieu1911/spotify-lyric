using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyLyric;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed class Lyrics
{
    public List<LyricLine> Lines { get; init; } = new();
    public bool Synced => Lines.Count > 0;
    public bool Instrumental { get; init; }

    /// <summary>Index of the last line whose time is &lt;= pos, or -1.</summary>
    public int IndexAt(TimeSpan pos)
    {
        int lo = 0, hi = Lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Time <= pos) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }
}

/// <summary>Fetches synced lyrics from LRCLIB (https://lrclib.net), free and keyless.</summary>
public sealed class LyricsProvider
{
    private static readonly HttpClient Http = CreateClient();
    private readonly ConcurrentDictionary<string, Lyrics?> _cache = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyLyric/1.0 (taskbar lyrics overlay)");
        return c;
    }

    /// <summary>Returns null when no lyrics exist. Throws on network errors.</summary>
    public async Task<Lyrics?> GetAsync(TrackInfo track, CancellationToken ct)
    {
        if (_cache.TryGetValue(track.Key, out var cached)) return cached;

        var result = await FetchAsync(track, ct);
        _cache[track.Key] = result;
        return result;
    }

    private static async Task<Lyrics?> FetchAsync(TrackInfo t, CancellationToken ct)
    {
        // 1. Exact match by signature.
        var url = $"api/get?track_name={E(t.Title)}&artist_name={E(t.Artist)}&album_name={E(t.Album)}";
        if (t.DurationSec > 0) url += $"&duration={Math.Round(t.DurationSec).ToString(CultureInfo.InvariantCulture)}";
        using (var resp = await Http.GetAsync(url, ct))
        {
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var lyr = FromJson(doc.RootElement);
                if (lyr != null && (lyr.Synced || lyr.Instrumental)) return lyr;
            }
            else if (resp.StatusCode != HttpStatusCode.NotFound)
            {
                resp.EnsureSuccessStatusCode();
            }
        }

        // 2. Fuzzy search with a cleaned title and the first artist.
        var title = CleanTitle(t.Title);
        var artist = t.Artist.Split(',', '&')[0].Trim();
        var best = await SearchAsync($"api/search?track_name={E(title)}&artist_name={E(artist)}", t, ct)
                   ?? await SearchAsync($"api/search?q={E(title + " " + artist)}", t, ct);
        return best;
    }

    private static async Task<Lyrics?> SearchAsync(string url, TrackInfo t, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var candidates = doc.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
            .Select(e => new
            {
                Elem = e,
                Diff = t.DurationSec > 0 && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? Math.Abs(d.GetDouble() - t.DurationSec)
                    : 0
            })
            .OrderBy(x => x.Diff)
            .ToList();

        var pick = candidates.FirstOrDefault(x => x.Diff <= 5) ?? candidates.FirstOrDefault(x => x.Diff <= 20);
        return pick == null ? null : FromJson(pick.Elem);
    }

    private static Lyrics? FromJson(JsonElement e)
    {
        bool instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        string? synced = e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(synced)) return instrumental ? new Lyrics { Instrumental = true } : new Lyrics();
        return new Lyrics { Lines = ParseLrc(synced), Instrumental = instrumental };
    }

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);

    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = TimeTag.Matches(raw);
            if (matches.Count == 0) continue;
            var text = TimeTag.Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static readonly Regex FeatPattern = new(@"\s*[\(\[](feat|ft|with)\.?\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DashSuffix = new(@"\s+-\s+.*$", RegexOptions.Compiled);

    private static string CleanTitle(string title)
    {
        var t = FeatPattern.Replace(title, "");
        t = DashSuffix.Replace(t, "");
        return t.Trim().Length > 0 ? t.Trim() : title;
    }

    private static string E(string s) => Uri.EscapeDataString(s ?? "");
}
