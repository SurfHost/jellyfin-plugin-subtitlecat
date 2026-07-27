using System;
using System.Text;

namespace Jellyfin.Plugin.SubtitleCat.Providers;

/// <summary>
/// The opaque identifier handed to Jellyfin with each search result and handed back on download.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetSubtitles</c> receives this string and nothing else: no request, no item, no
/// language. Everything the download needs has to survive the round trip inside it.
/// </para>
/// <para>
/// The download path is carried rather than the detail-page address, because each
/// translation on subtitlecat lives in its own numeric folder that cannot be derived
/// from the page it is listed on. Carrying the path also spares a second fetch of the
/// detail page at download time.
/// </para>
/// </remarks>
internal sealed class SubtitleCatId
{
    private const string Prefix = "sc1";

    /// <summary>Gets the download path, exactly as it was read from the page.</summary>
    public required string Href { get; init; }

    /// <summary>
    /// Gets the language to report back to Jellyfin, echoed verbatim from the request.
    /// </summary>
    /// <remarks>
    /// Jellyfin names the saved file from this, so a server configured for "dut" must get
    /// back "dut" and one configured for "nld" must get back "nld". Normalising it here
    /// would quietly rename everyone's subtitle files and break the check for whether a
    /// language is already present.
    /// </remarks>
    public required string Language { get; init; }

    /// <summary>Gets a value indicating whether the subtitle is flagged hearing impaired.</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>Gets a value indicating whether the subtitle is flagged forced.</summary>
    public bool Forced { get; init; }

    /// <summary>Renders the identifier.</summary>
    /// <remarks>
    /// The separator is a dot because the base64url alphabet never produces one, so the
    /// parts split apart cleanly. The leading version tag lets the format change later
    /// without an old identifier, still sitting in an open search dialog, being misread
    /// as a new one.
    /// </remarks>
    public string Encode()
    {
        var flags = string.Concat(HearingImpaired ? "h" : string.Empty, Forced ? "f" : string.Empty);
        return string.Join(
            '.',
            Prefix,
            Language,
            flags.Length == 0 ? "n" : flags,
            Base64UrlEncode(Encoding.UTF8.GetBytes(Href)));
    }

    /// <summary>Parses an identifier produced by <see cref="Encode"/>.</summary>
    /// <param name="value">The identifier.</param>
    /// <param name="id">The parsed identifier, when parsing succeeds.</param>
    /// <param name="error">A description of why parsing failed, when it does.</param>
    /// <returns>True when the identifier was understood.</returns>
    public static bool TryDecode(string? value, out SubtitleCatId? id, out string? error)
    {
        id = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The subtitle identifier was empty.";
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            error = "The subtitle identifier is not in the expected format.";
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            // Almost always a result list built by a different version of the plugin and
            // left open across an upgrade, which a fresh search fixes.
            error = "The subtitle identifier was produced by a different version of this plugin. Search again.";
            return false;
        }

        if (parts[1].Length == 0)
        {
            error = "The subtitle identifier carries no language.";
            return false;
        }

        string href;
        try
        {
            href = Encoding.UTF8.GetString(Base64UrlDecode(parts[3]));
        }
        catch (FormatException)
        {
            error = "The subtitle identifier is not valid base64.";
            return false;
        }

        if (href.Length == 0)
        {
            error = "The subtitle identifier carries no download address.";
            return false;
        }

        id = new SubtitleCatId
        {
            Href = href,
            Language = parts[1],
            HearingImpaired = parts[2].Contains('h', StringComparison.Ordinal),
            Forced = parts[2].Contains('f', StringComparison.Ordinal)
        };

        return true;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };

        return Convert.FromBase64String(padded);
    }
}
