using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubtitleCat.Models;

namespace Jellyfin.Plugin.SubtitleCat.Scraping;

/// <summary>
/// Reads the result rows out of a subtitlecat search page.
/// </summary>
/// <remarks>
/// Deliberately regex rather than a DOM parser: it keeps the plugin a single assembly
/// with no third-party dependency, which is what lets the release package stay one dll.
/// The trade is brittleness if the site restructures, which is what
/// <see cref="ScrapeHealth"/> exists to make visible.
/// </remarks>
internal static class SearchResultParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One result row. The anchor is matched rather than the table, because the surrounding
    /// markup carries responsive-layout classes that have changed before and would take
    /// the whole parse down with them.
    /// </summary>
    private static readonly Regex RowRegex = new(
        """<tr>\s*<td>\s*<a\s+href="(?<href>/?subs/\d+/[^"]+\.html)"[^>]*>(?<name>.*?)</a>(?<rest>.*?)</tr>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>The "(translated from English)" note, which sits outside the anchor.</summary>
    private static readonly Regex SourceLanguageRegex = new(
        @"\(\s*translated\s+from\s+(?<language>[^)]+?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex DownloadsRegex = new(
        @"(?<count>\d+)\s*downloads?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex LanguagesRegex = new(
        @"(?<count>\d+)\s*languages?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex TagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>Parses a search page. Returns an empty list rather than throwing on unexpected markup.</summary>
    /// <param name="html">The raw HTML of the search page.</param>
    public static IReadOnlyList<SubtitleCatSearchRow> Parse(string? html)
    {
        var rows = new List<SubtitleCatSearchRow>();
        if (string.IsNullOrEmpty(html))
        {
            return rows;
        }

        try
        {
            foreach (Match match in RowRegex.Matches(html))
            {
                var name = CleanText(match.Groups["name"].Value);
                if (name.Length == 0)
                {
                    continue;
                }

                var rest = match.Groups["rest"].Value;
                var sourceLanguage = SourceLanguageRegex.Match(rest);

                rows.Add(new SubtitleCatSearchRow
                {
                    Href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim(),
                    Name = name,
                    SourceLanguageName = sourceLanguage.Success
                        ? CleanText(sourceLanguage.Groups["language"].Value)
                        : null,
                    DownloadCount = ParseCount(DownloadsRegex, rest),
                    LanguageCount = ParseCount(LanguagesRegex, rest)
                });
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A page big or malformed enough to stall the matcher is not worth retrying.
            // The caller reports zero rows, which surfaces as a stale-parser warning.
            return rows;
        }

        return rows;
    }

    private static int ParseCount(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success
            && int.TryParse(match.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
    }

    private static string CleanText(string value) =>
        WebUtility.HtmlDecode(TagRegex.Replace(value, string.Empty)).Trim();
}
