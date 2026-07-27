using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleCat.Matching;

/// <summary>
/// A scene release name broken into the features that say whether two releases are
/// the same cut of the same thing: title, year, episode, resolution, source, codec,
/// edition and release group.
/// </summary>
internal sealed class ReleaseName
{
    private static readonly Regex EpisodeRegex = new(
        @"\bs(?<season>\d{1,2})[\s._-]*e(?<episode>\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlternateEpisodeRegex = new(
        @"\b(?<season>\d{1,2})x(?<episode>\d{2,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearRegex = new(
        @"\b(?<year>19\d{2}|20\d{2})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Trailing markers subtitlecat and the scene bolt onto a name that are not part
    /// of the release itself. Stripped before the release group is read off the end,
    /// otherwise "HEVC-PSA_eng_sdh" would yield a group of "psa_eng_sdh".
    /// </summary>
    private static readonly string[] TrailingNoise =
    {
        "_eng_sdh", "_eng_forced", "_eng", "_sdh", "_forced", "-orig",
        ".eng.sdh", ".eng", ".sdh", ".forced"
    };

    /// <summary>
    /// Two-part tokens that must survive tokenisation as one word. Applied before the
    /// string is split, because splitting on the hyphen first would turn "web-dl" into
    /// a "web" and a "dl" and lose the source entirely.
    /// </summary>
    private static readonly (string From, string To)[] Joins =
    {
        ("blu-ray", "bluray"),
        ("web-dl", "webdl"),
        ("web dl", "webdl"),
        ("h-264", "h264"),
        ("h-265", "h265"),
        ("x-264", "x264"),
        ("x-265", "x265"),
        ("dvd-rip", "dvdrip"),
        ("hd-tv", "hdtv")
    };

    private static readonly Dictionary<string, int> Resolutions = new(StringComparer.Ordinal)
    {
        ["2160p"] = 2160,
        ["2160i"] = 2160,
        ["4k"] = 2160,
        ["uhd"] = 2160,
        ["1080p"] = 1080,
        ["1080i"] = 1080,
        ["fhd"] = 1080,
        ["720p"] = 720,
        ["720i"] = 720,
        ["576p"] = 480,
        ["480p"] = 480,
        ["480i"] = 480
    };

    /// <summary>
    /// Source tokens mapped onto the groups that actually share subtitle timing.
    /// WEB-DL and WEBRip collapse to one bucket on purpose: they are different encodes
    /// of the same web master and their cuts line up, so treating them as different
    /// sources would reject good matches.
    /// </summary>
    private static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        ["remux"] = "remux",
        ["bluray"] = "bluray",
        ["bdrip"] = "bluray",
        ["brrip"] = "bluray",
        ["bdremux"] = "remux",
        ["webdl"] = "web",
        ["webrip"] = "web",
        ["web"] = "web",
        ["amzn"] = "web",
        ["nf"] = "web",
        ["hdtv"] = "hdtv",
        ["pdtv"] = "hdtv",
        ["dvdrip"] = "dvd",
        ["dvd"] = "dvd",
        ["dvdscr"] = "dvd",
        ["hdrip"] = "hdrip",
        ["cam"] = "cam",
        ["ts"] = "cam",
        ["telesync"] = "cam",
        ["hdcam"] = "cam"
    };

    private static readonly Dictionary<string, string> Codecs = new(StringComparer.Ordinal)
    {
        ["x264"] = "h264",
        ["h264"] = "h264",
        ["avc"] = "h264",
        ["x265"] = "h265",
        ["h265"] = "h265",
        ["hevc"] = "h265",
        ["av1"] = "av1",
        ["xvid"] = "xvid",
        ["divx"] = "xvid"
    };

    private static readonly HashSet<string> Editions = new(StringComparer.Ordinal)
    {
        "repack", "proper", "extended", "uncut", "unrated", "remastered",
        "imax", "criterion", "directors", "theatrical", "rerip"
    };

    /// <summary>
    /// Everything that is metadata rather than part of a title. Doubles as the veto
    /// list for release-group detection: a trailing segment containing any of these is
    /// a stray fragment, not a group.
    /// </summary>
    private static readonly HashSet<string> MetadataTokens = new(StringComparer.Ordinal)
    {
        "10bit", "8bit", "12bit", "hdr", "hdr10", "dv", "dolby", "vision", "sdr",
        "aac", "ac3", "eac3", "ddp", "dd", "dts", "hd", "ma", "truehd", "atmos", "flac", "opus",
        "6ch", "2ch", "8ch", "5", "1", "7", "2", "0",
        "multi", "dual", "audio", "subs", "subbed", "dubbed",
        "complete", "season", "series"
    };

    /// <summary>Words too common to count as evidence that two titles are the same.</summary>
    private static readonly HashSet<string> TitleStopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "or", "in", "on", "at", "to", "de", "het", "een"
    };

    private ReleaseName(
        string original,
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> titleTokens,
        int? year,
        int? season,
        int? episode,
        int? resolution,
        string? source,
        string? codec,
        IReadOnlySet<string> editions,
        string? group)
    {
        Original = original;
        Tokens = tokens;
        TitleTokens = titleTokens;
        Year = year;
        Season = season;
        Episode = episode;
        Resolution = resolution;
        Source = source;
        Codec = codec;
        EditionFlags = editions;
        Group = group;
    }

    /// <summary>Gets the name this was parsed from, unmodified.</summary>
    public string Original { get; }

    /// <summary>Gets every token in the name, lowercased.</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>Gets the leading tokens that precede the first metadata token.</summary>
    public IReadOnlyList<string> TitleTokens { get; }

    /// <summary>Gets the four-digit year, if the name carries one.</summary>
    public int? Year { get; }

    /// <summary>Gets the season number, if the name looks like an episode.</summary>
    public int? Season { get; }

    /// <summary>Gets the episode number, if the name looks like an episode.</summary>
    public int? Episode { get; }

    /// <summary>Gets the vertical resolution, normalised to 2160, 1080, 720 or 480.</summary>
    public int? Resolution { get; }

    /// <summary>Gets the normalised source, for example "bluray", "web" or "hdtv".</summary>
    public string? Source { get; }

    /// <summary>Gets the normalised video codec, for example "h264" or "h265".</summary>
    public string? Codec { get; }

    /// <summary>Gets the edition markers found, for example "repack" or "extended".</summary>
    public IReadOnlySet<string> EditionFlags { get; }

    /// <summary>Gets the release group, lowercased and stripped of punctuation.</summary>
    public string? Group { get; }

    /// <summary>Gets a value indicating whether this name carries a season and episode number.</summary>
    public bool IsEpisode => Season.HasValue && Episode.HasValue;

    /// <summary>
    /// Gets the title tokens worth comparing, dropping stop words and single characters.
    /// </summary>
    public IReadOnlyList<string> SignificantTitleTokens =>
        TitleTokens.Where(t => t.Length > 1 && !TitleStopWords.Contains(t)).ToList();

    /// <summary>Parses a release name. Never throws; an unparseable name yields empty features.</summary>
    public static ReleaseName Parse(string? value)
    {
        var original = value ?? string.Empty;
        var working = StripTrailingNoise(original).ToLowerInvariant();

        foreach (var (from, to) in Joins)
        {
            working = working.Replace(from, to, StringComparison.Ordinal);
        }

        var group = ExtractGroup(working);

        var tokens = Regex.Split(working, "[^a-z0-9]+")
            .Where(t => t.Length > 0)
            .ToList();

        var (season, episode) = ExtractEpisode(working);
        var year = ExtractYear(working, season.HasValue);
        var titleTokens = ExtractTitleTokens(tokens);

        int? resolution = null;
        string? source = null;
        string? codec = null;
        var editions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (resolution is null && Resolutions.TryGetValue(token, out var res))
            {
                resolution = res;
            }

            // Later source tokens do not overwrite earlier ones: "BluRay.REMUX" should
            // read as a remux, but "WEBRip" following a stray "web" should not flip.
            if (Sources.TryGetValue(token, out var src) && (source is null || src == "remux"))
            {
                source = src;
            }

            if (codec is null && Codecs.TryGetValue(token, out var cod))
            {
                codec = cod;
            }

            if (Editions.Contains(token))
            {
                editions.Add(token);
            }
        }

        return new ReleaseName(
            original, tokens, titleTokens, year, season, episode,
            resolution, source, codec, editions, group);
    }

    private static string StripTrailingNoise(string value)
    {
        var result = value.Trim();
        bool stripped;
        do
        {
            stripped = false;
            foreach (var suffix in TrailingNoise)
            {
                if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    result = result[..^suffix.Length];
                    stripped = true;
                }
            }
        }
        while (stripped);

        return result.Trim();
    }

    /// <summary>
    /// Reads the release group off the end of the name. The group is whatever follows
    /// the final hyphen, but only when that segment is free of metadata tokens: in
    /// "1080p.WEB-DL.H264.AAC" the final hyphen belongs to WEB-DL, and the "dl h264 aac"
    /// that follows it is a fragment rather than a group.
    /// </summary>
    private static string? ExtractGroup(string normalised)
    {
        var hyphen = normalised.LastIndexOf('-');
        if (hyphen < 0 || hyphen == normalised.Length - 1)
        {
            return null;
        }

        var candidate = normalised[(hyphen + 1)..].Trim();
        var parts = Regex.Split(candidate, "[^a-z0-9]+").Where(p => p.Length > 0).ToList();
        if (parts.Count == 0 || parts.Count > 3)
        {
            return null;
        }

        foreach (var part in parts)
        {
            if (MetadataTokens.Contains(part)
                || Resolutions.ContainsKey(part)
                || Sources.ContainsKey(part)
                || Codecs.ContainsKey(part)
                || Editions.Contains(part))
            {
                return null;
            }
        }

        var group = string.Concat(parts);

        // A bare number after a hyphen is a part number or a chapter, not a group.
        return group.Length is > 1 and <= 20 && !group.All(char.IsDigit) ? group : null;
    }

    private static (int? Season, int? Episode) ExtractEpisode(string normalised)
    {
        var match = EpisodeRegex.Match(normalised);
        if (!match.Success)
        {
            match = AlternateEpisodeRegex.Match(normalised);
        }

        if (!match.Success)
        {
            return (null, null);
        }

        return (
            int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture));
    }

    private static int? ExtractYear(string normalised, bool isEpisode)
    {
        // An episode's air year is unreliable and often absent, so it is not used as
        // evidence either way. For movies the year is one of the strongest signals.
        if (isEpisode)
        {
            return null;
        }

        var matches = YearRegex.Matches(normalised);
        if (matches.Count == 0)
        {
            return null;
        }

        // The last year wins: "2012.2009.1080p" is the film 2012 released in 2009.
        return int.Parse(matches[^1].Groups["year"].Value, CultureInfo.InvariantCulture);
    }

    private static List<string> ExtractTitleTokens(IReadOnlyList<string> tokens)
    {
        var title = new List<string>();
        foreach (var token in tokens)
        {
            if (Resolutions.ContainsKey(token)
                || Sources.ContainsKey(token)
                || Codecs.ContainsKey(token)
                || Editions.Contains(token)
                || MetadataTokens.Contains(token)
                || EpisodeRegex.IsMatch(token)
                || (token.Length == 4 && YearRegex.IsMatch(token)))
            {
                break;
            }

            title.Add(token);
        }

        return title;
    }

    /// <summary>Renders the parsed features, for log messages and test failures.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(' ', TitleTokens));
        if (Year.HasValue)
        {
            builder.Append(CultureInfo.InvariantCulture, $" ({Year})");
        }

        if (IsEpisode)
        {
            builder.Append(CultureInfo.InvariantCulture, $" S{Season:00}E{Episode:00}");
        }

        builder.Append(CultureInfo.InvariantCulture, $" [{Resolution?.ToString(CultureInfo.InvariantCulture) ?? "?"}p");
        builder.Append(CultureInfo.InvariantCulture, $" {Source ?? "?"} {Codec ?? "?"} {Group ?? "?"}]");
        return builder.ToString();
    }
}
