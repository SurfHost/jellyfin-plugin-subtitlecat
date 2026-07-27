using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleCat.Matching;

/// <summary>
/// Builds the ordered list of queries to try for one subtitle search.
/// </summary>
internal static class SearchQueryBuilder
{
    private static readonly Regex ResolutionTokenRegex = new(
        @"[\s._-]*\b(2160p|2160i|1080p|1080i|720p|720i|576p|480p|480i|4k|uhd)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SeparatorRegex = new(
        @"[._]+",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// Builds the queries to try, most specific first, deduplicated.
    /// </summary>
    /// <param name="releaseName">The parsed release name of the local file, or null when there is no path.</param>
    /// <param name="rawReleaseName">The release basename as written on disk, or null.</param>
    /// <param name="title">The item title Jellyfin resolved, used for the fallback queries.</param>
    /// <param name="seriesName">The series name for episodes, or null.</param>
    /// <param name="year">The production year, or null.</param>
    /// <param name="season">The season number, or null.</param>
    /// <param name="episode">The episode number, or null.</param>
    /// <param name="includeFallbacks">Whether the title-based fallback queries are included.</param>
    public static IReadOnlyList<string> Build(
        ReleaseName? releaseName,
        string? rawReleaseName,
        string? title,
        string? seriesName,
        int? year,
        int? season,
        int? episode,
        bool includeFallbacks)
    {
        var queries = new List<string>();

        if (!string.IsNullOrWhiteSpace(rawReleaseName))
        {
            var basename = SeparatorRegex.Replace(rawReleaseName, " ").Trim();
            Add(queries, basename);

            // The same query with the resolution dropped. This is what makes a 1080p
            // subtitle reachable for a 2160p file at all: searching the full 2160p
            // release name only ever returns 2160p entries, so without this the
            // cross-resolution match has nothing to score against.
            Add(queries, ResolutionTokenRegex.Replace(basename, " "));
        }

        // Title plus release group finds same-group siblings whose middle tokens differ
        // entirely, which is common between a group's 2160p and 1080p encodes.
        if (releaseName?.Group is not null && releaseName.TitleTokens.Count > 0)
        {
            Add(queries, string.Join(' ', releaseName.TitleTokens) + " " + releaseName.Group);
        }

        if (!includeFallbacks)
        {
            return Trim(queries);
        }

        if (season.HasValue && episode.HasValue)
        {
            var series = !string.IsNullOrWhiteSpace(seriesName) ? seriesName : title;
            if (!string.IsNullOrWhiteSpace(series))
            {
                Add(
                    queries,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} S{1:00}E{2:00}",
                        series.Trim(),
                        season.Value,
                        episode.Value));
                Add(queries, series.Trim());
            }
        }
        else if (!string.IsNullOrWhiteSpace(title))
        {
            var cleanTitle = title.Trim();
            if (year.HasValue)
            {
                Add(
                    queries,
                    string.Format(CultureInfo.InvariantCulture, "{0} {1}", cleanTitle, year.Value));
            }

            Add(queries, cleanTitle);
        }

        return Trim(queries);
    }

    private static void Add(ICollection<string> queries, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var normalised = Regex.Replace(query.Trim(), @"\s{2,}", " ");
        if (normalised.Length < 2)
        {
            return;
        }

        if (!queries.Any(existing => string.Equals(existing, normalised, StringComparison.OrdinalIgnoreCase)))
        {
            queries.Add(normalised);
        }
    }

    /// <summary>
    /// Caps the ladder. Each query is a request, and the later ones are progressively
    /// vaguer, so trying every rung costs the site traffic for results that would score
    /// below the threshold anyway.
    /// </summary>
    private static IReadOnlyList<string> Trim(List<string> queries) =>
        queries.Count > 4 ? queries.GetRange(0, 4) : queries;
}
