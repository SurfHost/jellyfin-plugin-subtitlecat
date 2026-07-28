using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleCat.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>
/// Tests for SubRip parsing, rewriting and markup handling.
/// </summary>
/// <remarks>
/// The point of every test here is that timings survive. A translated file whose text is
/// merely mediocre is usable; one whose cues have shifted is not.
/// </remarks>
public class SrtTests
{
    private const string Sample =
        "1\r\n" +
        "00:00:03,188 --> 00:00:04,842\r\n" +
        "First line of dialogue\r\n" +
        "\r\n" +
        "2\r\n" +
        "00:00:05,016 --> 00:00:07,627\r\n" +
        "Second cue, first line\r\n" +
        "second cue, second line\r\n" +
        "\r\n" +
        "3\r\n" +
        "00:01:07,801 --> 00:01:12,850\r\n" +
        "Third cue\r\n";

    [Fact]
    public void ParsesCuesAndTimings()
    {
        var doc = SrtDocument.Parse(Sample);

        Assert.Equal(3, doc.Cues.Count);
        Assert.Equal(new TimeSpan(0, 0, 0, 3, 188), doc.Cues[0].Start);
        Assert.Equal(new TimeSpan(0, 0, 0, 4, 842), doc.Cues[0].End);
        Assert.Equal(new TimeSpan(0, 0, 1, 7, 801), doc.Cues[2].Start);
        Assert.Equal(2, doc.Cues[1].Lines.Count);
    }

    [Fact]
    public void JoinsTheTwoDisplayLinesIntoOneSentenceForTranslation()
    {
        var doc = SrtDocument.Parse(Sample);

        // A cue's two lines are one sentence broken to fit the screen. Sending them
        // separately would translate two half-sentences with no context.
        Assert.Equal("Second cue, first line second cue, second line", doc.Cues[1].Text);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void AcceptsAnyLineEnding(string newLine)
    {
        var doc = SrtDocument.Parse(Sample.Replace("\r\n", newLine, StringComparison.Ordinal));

        Assert.Equal(3, doc.Cues.Count);
    }

    [Fact]
    public void AcceptsAByteOrderMarkAFullStopSeparatorAndMissingIndices()
    {
        var awkward =
            "﻿00:00:01.500 --> 00:00:02.000\n" +
            "No index line, full stop separator\n" +
            "\n\n\n" +
            "99\n" +
            "00:00:03,000 --> 00:00:04,000\n" +
            "Index out of sequence\n";

        var doc = SrtDocument.Parse(awkward);

        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(new TimeSpan(0, 0, 0, 1, 500), doc.Cues[0].Start);
    }

    [Fact]
    public void SerialisesRenumberedFromOne()
    {
        var doc = SrtDocument.Parse("7\n00:00:01,000 --> 00:00:02,000\nOnly cue\n");

        var text = doc.Serialize();

        Assert.StartsWith("1\r\n00:00:01,000 --> 00:00:02,000\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripsWithoutDrift()
    {
        var once = SrtDocument.Parse(Sample).Serialize();
        var twice = SrtDocument.Parse(once).Serialize();

        Assert.Equal(once, twice);
    }

    [Fact]
    public void ReplacingTextKeepsEveryTiming()
    {
        var doc = SrtDocument.Parse(Sample);
        var replaced = doc.WithTexts(new[] { "Eerste regel", "Tweede cue", "Derde cue" });

        Assert.Equal(doc.Cues.Count, replaced.Cues.Count);
        for (var i = 0; i < doc.Cues.Count; i++)
        {
            Assert.Equal(doc.Cues[i].Start, replaced.Cues[i].Start);
            Assert.Equal(doc.Cues[i].End, replaced.Cues[i].End);
        }

        Assert.Equal("Eerste regel", replaced.Cues[0].Text);
    }

    [Fact]
    public void ReplacingWithTheWrongNumberOfTextsIsRefused()
    {
        var doc = SrtDocument.Parse(Sample);

        // This is the guard that matters most. A short or long list would shift every
        // later line onto someone else's timing, and the file would look fine while
        // being wrong from that point on.
        var ex = Assert.Throws<ArgumentException>(() => doc.WithTexts(new[] { "one", "two" }));
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyTranslationLeavesTheOriginalLineAlone()
    {
        var doc = SrtDocument.Parse(Sample);

        var replaced = doc.WithTexts(new[] { "Eerste regel", string.Empty, "Derde cue" });

        Assert.Equal(doc.Cues[1].Text, replaced.Cues[1].Text);
    }

    [Fact]
    public void LongLinesAreBrokenInTwoNearTheMiddle()
    {
        var lines = SrtDocument.SplitForDisplay(
            "This is a fairly long line of dialogue that will not fit on one row", 42);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.DoesNotContain("  ", l, StringComparison.Ordinal));
        Assert.Equal(
            "This is a fairly long line of dialogue that will not fit on one row",
            string.Join(' ', lines));
    }

    [Fact]
    public void ShortLinesAreLeftAlone()
    {
        Assert.Single(SrtDocument.SplitForDisplay("Short enough", 42));
    }

    [Fact]
    public void MarkupPrefixAndWrapSurviveTheRoundTrip()
    {
        var shell = SrtMarkup.Extract("{\\an8}<i>Spoken from off screen</i>");

        Assert.Equal("{\\an8}", shell.Prefix);
        Assert.Equal("i", shell.WrapTag);
        Assert.Equal("Spoken from off screen", shell.Inner);
        Assert.False(shell.DroppedInlineTags);

        Assert.Equal("{\\an8}<i>Gesproken buiten beeld</i>", SrtMarkup.Restore(shell, "Gesproken buiten beeld"));
    }

    [Fact]
    public void InlineTagsAreDroppedRatherThanSentThrough()
    {
        var shell = SrtMarkup.Extract("Some <b>bold</b> words");

        // A tag sent through a translator comes back moved, mangled or split mid-word,
        // and players show the broken markup on screen. Losing the emphasis is the
        // smaller loss, and the flag lets the caller count how often it happens.
        Assert.Equal("Some bold words", shell.Inner);
        Assert.True(shell.DroppedInlineTags);
        Assert.Null(shell.WrapTag);
        Assert.Equal("Iets vetgedrukt", SrtMarkup.Restore(shell, "Iets vetgedrukt"));
    }

    [Fact]
    public void PlainTextIsUntouched()
    {
        var shell = SrtMarkup.Extract("Nothing special here");

        Assert.Equal(string.Empty, shell.Prefix);
        Assert.Null(shell.WrapTag);
        Assert.False(shell.DroppedInlineTags);
        Assert.Equal("Nothing special here", SrtMarkup.Restore(shell, "Nothing special here"));
    }

    [Fact]
    public void AnEmptyTranslationRestoresToNothingRatherThanBareTags()
    {
        var shell = SrtMarkup.Extract("<i>Something</i>");

        // Re-wrapping an empty string would emit "<i></i>", which some players render
        // as a visible empty box.
        Assert.Equal(string.Empty, SrtMarkup.Restore(shell, string.Empty));
    }

    [Fact]
    public void ATranslatedFileKeepsItsCueCount()
    {
        var doc = SrtDocument.Parse(Sample);
        var texts = doc.Cues.Select(c => SrtMarkup.Extract(c.Text).Inner).ToList();

        Assert.Equal(doc.Cues.Count, texts.Count);

        var rebuilt = SrtDocument.Parse(doc.WithTexts(texts).Serialize());
        Assert.Equal(doc.Cues.Count, rebuilt.Cues.Count);
    }
}
