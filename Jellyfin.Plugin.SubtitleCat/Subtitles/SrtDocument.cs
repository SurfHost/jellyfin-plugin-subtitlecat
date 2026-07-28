using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleCat.Subtitles;

/// <summary>
/// A parsed SubRip subtitle file.
/// </summary>
/// <remarks>
/// This type exists so that translation only ever touches cue text. Indices and
/// timestamps are parsed here, held here, and written back here; they never reach a
/// translation service, so no amount of reformatting or reordering on that side can
/// desynchronise the result from the video.
/// </remarks>
internal sealed class SrtDocument
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A cue's time range. Accepts a comma or a full stop before the milliseconds, since
    /// some sources write the latter, and one or two digits for the hour.
    /// </summary>
    private static readonly Regex TimingRegex = new(
        @"^\s*(?<sh>\d{1,3}):(?<sm>\d{1,2}):(?<ss>\d{1,2})[,.](?<sf>\d{1,3})\s*-->\s*(?<eh>\d{1,3}):(?<em>\d{1,2}):(?<es>\d{1,2})[,.](?<ef>\d{1,3})",
        RegexOptions.Compiled,
        MatchTimeout);

    private SrtDocument(IReadOnlyList<SrtCue> cues)
    {
        Cues = cues;
    }

    /// <summary>Gets the cues, in file order.</summary>
    public IReadOnlyList<SrtCue> Cues { get; }

    /// <summary>Parses a subtitle file. Unparseable blocks are skipped rather than throwing.</summary>
    /// <param name="text">The decoded file contents.</param>
    public static SrtDocument Parse(string? text)
    {
        var cues = new List<SrtCue>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SrtDocument(cues);
        }

        // Normalise line endings and drop a byte-order mark that survived decoding, so
        // the block splitting below does not depend on where the file came from.
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimStart('﻿');

        var lines = normalised.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            while (i < lines.Length && lines[i].Trim().Length == 0)
            {
                i++;
            }

            if (i >= lines.Length)
            {
                break;
            }

            // The index line is optional and its value is ignored: files in the wild
            // skip numbers, repeat them and start from zero, and the order in the file
            // is what actually matters. Serialising renumbers from one.
            if (IsIndexLine(lines[i]) && i + 1 < lines.Length && TimingRegex.IsMatch(lines[i + 1]))
            {
                i++;
            }

            var timing = i < lines.Length ? TimingRegex.Match(lines[i]) : Match.Empty;
            if (!timing.Success)
            {
                // Not a cue start. Skip to the next blank line and try again.
                while (i < lines.Length && lines[i].Trim().Length > 0)
                {
                    i++;
                }

                continue;
            }

            i++;

            var body = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                body.Add(lines[i].TrimEnd());
                i++;
            }

            cues.Add(new SrtCue(ReadTime(timing, "s"), ReadTime(timing, "e"), body));
        }

        return new SrtDocument(cues);
    }

    /// <summary>
    /// Returns a copy with each cue's text replaced, keeping every timing untouched.
    /// </summary>
    /// <param name="replacements">One replacement per cue, in the same order.</param>
    /// <exception cref="ArgumentException">The count does not match the cue count.</exception>
    public SrtDocument WithTexts(IReadOnlyList<string> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);

        // A count mismatch means the replacements no longer line up with the cues, which
        // would shift every subsequent line onto the wrong timing. Refusing loudly is the
        // only safe response; the caller retries rather than shipping a broken file.
        if (replacements.Count != Cues.Count)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Expected {0} replacement texts to match the cues, got {1}.",
                    Cues.Count,
                    replacements.Count),
                nameof(replacements));
        }

        var rebuilt = new List<SrtCue>(Cues.Count);
        for (var i = 0; i < Cues.Count; i++)
        {
            var text = replacements[i];
            rebuilt.Add(Cues[i].WithLines(
                string.IsNullOrWhiteSpace(text) ? Cues[i].Lines : SplitForDisplay(text)));
        }

        return new SrtDocument(rebuilt);
    }

    /// <summary>Writes the file back out, renumbered from one and with CRLF endings.</summary>
    public string Serialize()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < Cues.Count; i++)
        {
            var cue = Cues[i];
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            builder.Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(cue.End)).Append("\r\n");

            foreach (var line in cue.Lines)
            {
                builder.Append(line).Append("\r\n");
            }

            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Breaks a translated line for display, at most two lines, preferring a break near
    /// the middle so the two halves are balanced.
    /// </summary>
    /// <param name="text">The full cue text on one line.</param>
    /// <param name="maxLength">The length above which the text is broken.</param>
    internal static List<string> SplitForDisplay(string text, int maxLength = 42)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength || !trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return new List<string> { trimmed };
        }

        var middle = trimmed.Length / 2;
        var before = trimmed.LastIndexOf(' ', Math.Min(middle, trimmed.Length - 1));
        var after = trimmed.IndexOf(' ', middle);

        var split = before < 0 ? after
            : after < 0 ? before
            : (middle - before <= after - middle ? before : after);

        return split <= 0
            ? new List<string> { trimmed }
            : new List<string> { trimmed[..split].TrimEnd(), trimmed[(split + 1)..].TrimStart() };
    }

    private static bool IsIndexLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit);
    }

    private static TimeSpan ReadTime(Match match, string prefix)
    {
        var hours = int.Parse(match.Groups[prefix + "h"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[prefix + "m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[prefix + "s"].Value, CultureInfo.InvariantCulture);

        // "1" in the fraction field means a tenth of a second, not a millisecond, so a
        // short group is padded rather than parsed as-is.
        var fraction = match.Groups[prefix + "f"].Value.PadRight(3, '0');

        return new TimeSpan(
            0,
            hours,
            minutes,
            seconds,
            int.Parse(fraction, CultureInfo.InvariantCulture));
    }

    private static string FormatTime(TimeSpan value) => string.Format(
        CultureInfo.InvariantCulture,
        "{0:00}:{1:00}:{2:00},{3:000}",
        (int)value.TotalHours,
        value.Minutes,
        value.Seconds,
        value.Milliseconds);
}
