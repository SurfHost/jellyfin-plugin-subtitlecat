using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleCat.Subtitles;

/// <summary>
/// The markup taken off a cue before translation, so it can be put back afterwards.
/// </summary>
internal sealed class SrtMarkupShell
{
    /// <summary>Gets the positioning prefix, for example "{\an8}", or an empty string.</summary>
    public required string Prefix { get; init; }

    /// <summary>Gets the tag the whole cue was wrapped in, for example "i", or null.</summary>
    public required string? WrapTag { get; init; }

    /// <summary>Gets the bare text to hand to a translator.</summary>
    public required string Inner { get; init; }

    /// <summary>Gets a value indicating whether inline tags were discarded.</summary>
    public required bool DroppedInlineTags { get; init; }
}

/// <summary>
/// Separates a cue's markup from its words.
/// </summary>
/// <remarks>
/// Translators reorder words, and a tag sent through one comes back in the wrong place,
/// mangled, or split across a word. Rather than trying to track positions, the markup
/// that can be preserved exactly is lifted off first and reapplied after, and the rest
/// is dropped. Losing an inline italic is a much smaller loss than emitting broken
/// markup, which players render as literal angle brackets on screen.
/// </remarks>
internal static class SrtMarkup
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Positioning and style overrides, which always sit at the very front.</summary>
    private static readonly Regex PrefixRegex = new(
        @"^(?:\{\\[^}]*\}\s*)+",
        RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>A cue wrapped entirely in one tag, which is the common case by far.</summary>
    private static readonly Regex FullWrapRegex = new(
        @"^<(?<tag>i|b|u)>(?<inner>.*)</\k<tag>>$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex AnyTagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>Splits a cue into markup and translatable text.</summary>
    /// <param name="text">The cue text, already joined onto one line.</param>
    public static SrtMarkupShell Extract(string? text)
    {
        var working = (text ?? string.Empty).Trim();

        var prefixMatch = PrefixRegex.Match(working);
        var prefix = prefixMatch.Success ? prefixMatch.Value : string.Empty;
        working = working[prefix.Length..].Trim();

        string? wrapTag = null;
        var wrap = FullWrapRegex.Match(working);
        if (wrap.Success)
        {
            wrapTag = wrap.Groups["tag"].Value.ToLowerInvariant();
            working = wrap.Groups["inner"].Value.Trim();
        }

        var stripped = AnyTagRegex.Replace(working, string.Empty);
        var dropped = !string.Equals(stripped, working, StringComparison.Ordinal);

        return new SrtMarkupShell
        {
            Prefix = prefix,
            WrapTag = wrapTag,
            Inner = stripped.Trim(),
            DroppedInlineTags = dropped
        };
    }

    /// <summary>Puts the markup back around a translated line.</summary>
    /// <param name="shell">The markup taken off by <see cref="Extract"/>.</param>
    /// <param name="translated">The translated text.</param>
    public static string Restore(SrtMarkupShell shell, string? translated)
    {
        ArgumentNullException.ThrowIfNull(shell);

        var body = (translated ?? string.Empty).Trim();
        if (body.Length == 0)
        {
            return string.Empty;
        }

        if (shell.WrapTag is not null)
        {
            body = string.Concat("<", shell.WrapTag, ">", body, "</", shell.WrapTag, ">");
        }

        return string.Concat(shell.Prefix, body);
    }
}
