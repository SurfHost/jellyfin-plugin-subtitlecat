using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubtitleCat.Models;

namespace Jellyfin.Plugin.SubtitleCat.Scraping;

/// <summary>
/// Reads the downloadable subtitles off a subtitlecat detail page.
/// </summary>
/// <remarks>
/// A detail page lists every language the site could translate into, which is around
/// 150 blocks, of which only the ones somebody already generated carry an anchor. The
/// rest hold a button whose click handler runs the translation in the visitor's browser
/// against Google Translate. A plugin cannot drive that, so button blocks are dropped
/// here and never reach the caller.
/// </remarks>
internal static class DetailPageParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One language block. Blocks do not nest, so stopping at the first closing div is safe.
    /// </summary>
    private static readonly Regex BlockRegex = new(
        """<div\s+class="sub-single">(?<body>.*?)</div>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>
    /// The download anchor. The href is read rather than reconstructed: each translation
    /// lives in its own numeric folder, which differs from the folder of the page it is
    /// listed on, so there is no pattern to build it from.
    /// </summary>
    private static readonly Regex DownloadRegex = new(
        """<a\s+id="download_(?<code>[^"]+)"[^>]*?href="(?<href>[^"]+)"[^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>
    /// The Translate button's click handler, whose three arguments are the target
    /// language, the original subtitle's file name, and the folder it sits in. Those are
    /// the only place the original's address appears on the page.
    /// </summary>
    private static readonly Regex TranslateCallRegex = new(
        @"translate_from_server_folder\(\s*'(?<code>[^']*)'\s*,\s*'(?<file>[^']*)'\s*,\s*'(?<folder>[^']*)'\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex FlagRegex = new(
        """<img[^>]*\salt="(?<code>[^"]*)"[^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>The plain-text spans, the second of which holds the language name.</summary>
    private static readonly Regex SpanTextRegex = new(
        @"<span>\s*(?<text>[^<>]+?)\s*</span>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>Parses a detail page. Returns an empty list rather than throwing on unexpected markup.</summary>
    /// <param name="html">The raw HTML of the detail page.</param>
    public static IReadOnlyList<SubtitleCatDownloadLink> Parse(string? html)
    {
        var links = new List<SubtitleCatDownloadLink>();
        if (string.IsNullOrEmpty(html))
        {
            return links;
        }

        try
        {
            foreach (Match block in BlockRegex.Matches(html))
            {
                var body = block.Groups["body"].Value;

                var download = DownloadRegex.Match(body);
                if (download.Success)
                {
                    var href = WebUtility.HtmlDecode(download.Groups["href"].Value).Trim();
                    if (href.Length == 0)
                    {
                        continue;
                    }

                    // The anchor id is authoritative for the code; the flag image is the
                    // fallback, since the two have disagreed on regional variants before.
                    var code = download.Groups["code"].Value.Trim();
                    if (code.Length == 0)
                    {
                        code = FlagRegex.Match(body).Groups["code"].Value.Trim();
                    }

                    if (code.Length == 0)
                    {
                        continue;
                    }

                    links.Add(new SubtitleCatDownloadLink
                    {
                        LanguageCode = code,
                        LanguageName = ExtractLanguageName(body, code),
                        Href = href,
                        Kind = SubtitleCatLinkKind.Stored
                    });

                    continue;
                }

                // No stored file, so the block offers a Translate button instead. Its
                // arguments name the original subtitle, which is the same file for every
                // language on the page: only the target differs.
                var translate = TranslateCallRegex.Match(body);
                if (!translate.Success)
                {
                    continue;
                }

                var targetCode = translate.Groups["code"].Value.Trim();
                var originalFile = translate.Groups["file"].Value.Trim();
                var folder = translate.Groups["folder"].Value.Trim();

                if (targetCode.Length == 0 || originalFile.Length == 0 || folder.Length == 0)
                {
                    continue;
                }

                links.Add(new SubtitleCatDownloadLink
                {
                    LanguageCode = targetCode,
                    LanguageName = ExtractLanguageName(body, targetCode),
                    Href = string.Concat(folder.TrimEnd('/'), "/", originalFile),
                    Kind = SubtitleCatLinkKind.Translatable
                });
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return links;
        }

        return links;
    }

    /// <summary>
    /// Counts how many language blocks the page holds in total, including the ones that
    /// only offer a Translate button. A page with blocks but no download links parses
    /// differently from a page with no blocks at all, and only the second means the
    /// markup moved.
    /// </summary>
    /// <param name="html">The raw HTML of the detail page.</param>
    public static int CountBlocks(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return 0;
        }

        try
        {
            return BlockRegex.Matches(html).Count;
        }
        catch (RegexMatchTimeoutException)
        {
            return 0;
        }
    }

    private static string ExtractLanguageName(string body, string fallback)
    {
        foreach (Match span in SpanTextRegex.Matches(body))
        {
            var text = WebUtility.HtmlDecode(span.Groups["text"].Value).Trim();

            // "Download" is the anchor's own label, not a language name.
            if (text.Length > 0 && !string.Equals(text, "Download", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }

        return fallback;
    }
}
