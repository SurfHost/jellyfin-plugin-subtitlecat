using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleCat.Subtitles;

/// <summary>
/// One subtitle entry: when it appears, when it goes, and the lines shown.
/// </summary>
internal sealed class SrtCue
{
    /// <summary>Initializes a new instance of the <see cref="SrtCue"/> class.</summary>
    /// <param name="start">When the cue appears.</param>
    /// <param name="end">When the cue disappears.</param>
    /// <param name="lines">The display lines, without trailing newlines.</param>
    public SrtCue(TimeSpan start, TimeSpan end, IReadOnlyList<string> lines)
    {
        Start = start;
        End = end;
        Lines = lines;
    }

    /// <summary>Gets the time the cue appears.</summary>
    public TimeSpan Start { get; }

    /// <summary>Gets the time the cue disappears.</summary>
    public TimeSpan End { get; }

    /// <summary>Gets the display lines.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>
    /// Gets the cue's text as a single line, which is the form a translator wants.
    /// </summary>
    /// <remarks>
    /// A cue's two display lines are one sentence broken to fit the screen, not two
    /// sentences. Translating them separately produces two half-sentences translated
    /// out of context, so they are joined before going out and re-broken afterwards.
    /// </remarks>
    public string Text => string.Join(' ', Lines);

    /// <summary>Returns a copy of this cue with different text, keeping the timings.</summary>
    /// <param name="lines">The replacement display lines.</param>
    public SrtCue WithLines(IReadOnlyList<string> lines) => new(Start, End, lines);
}
