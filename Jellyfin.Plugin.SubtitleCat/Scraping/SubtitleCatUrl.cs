using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.SubtitleCat.Scraping;

/// <summary>
/// Builds absolute URLs from the hrefs subtitlecat writes.
/// </summary>
/// <remarks>
/// The site is inconsistent about escaping. Search results give a percent-encoded path
/// with no leading slash ("subs/1518/%20Dune%20Part%20Two..."), while download anchors
/// give a rooted path with raw brackets and spaces
/// ("/subs/757/Dune...AAC5.1-[YTS.MX]-nl.srt"). Both have to end up as the same valid
/// URL, so an already-encoded triplet must be left alone while a raw bracket is encoded,
/// which rules out escaping the whole string in one pass.
/// </remarks>
internal static class SubtitleCatUrl
{
    /// <summary>
    /// Characters RFC 3986 allows in a path without escaping. Everything else is
    /// percent-encoded, except an existing percent-triplet, which is passed through.
    /// </summary>
    private const string PathSafe = "-._~!$&'()*+,;=:@/";

    /// <summary>Combines a base URL with an href from a page.</summary>
    /// <param name="baseUrl">The configured site base, for example "https://www.subtitlecat.com".</param>
    /// <param name="href">The href exactly as it appeared in the HTML.</param>
    /// <returns>An absolute URI, or null when the href cannot form one.</returns>
    public static Uri? Combine(string baseUrl, string href)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var trimmed = href.Trim();

        // The site occasionally emits an absolute URL. Take it as-is if it parses.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var root))
        {
            return null;
        }

        var path = Encode(trimmed.TrimStart('/'));
        return Uri.TryCreate(root, "/" + path, out var combined) ? combined : null;
    }

    /// <summary>
    /// Percent-encodes a path, preserving sequences that are already encoded.
    /// </summary>
    /// <param name="path">The raw or partly encoded path.</param>
    internal static string Encode(string path)
    {
        var builder = new StringBuilder(path.Length + 16);

        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];

            if (c == '%' && i + 2 < path.Length && IsHex(path[i + 1]) && IsHex(path[i + 2]))
            {
                builder.Append(path, i, 3);
                i += 2;
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c) || PathSafe.Contains(c, StringComparison.Ordinal))
            {
                builder.Append(c);
                continue;
            }

            foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
            {
                builder.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return builder.ToString();
    }

    private static bool IsHex(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
