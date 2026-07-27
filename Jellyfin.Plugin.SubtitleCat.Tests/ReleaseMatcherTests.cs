using System.Linq;
using Jellyfin.Plugin.SubtitleCat.Matching;
using Xunit;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>Tests for release-name parsing and scoring.</summary>
public class ReleaseMatcherTests
{
    [Theory]
    [InlineData("Dune.Part.Two.2024.REPACK.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA", "psa", 1080, "web", "h265", 2024)]
    [InlineData("Dune.Part.Two.2024.1080p.BluRay.x264.AAC5.1-[YTS.MX]", "ytsmx", 1080, "bluray", "h264", 2024)]
    [InlineData("Interstellar.2014.2160p.UHD.BluRay.REMUX.HDR.HEVC.TrueHD.7.1-FGT", "fgt", 2160, "remux", "h265", 2014)]
    public void Parse_ReadsTheFeaturesThatMatter(
        string release,
        string expectedGroup,
        int expectedResolution,
        string expectedSource,
        string expectedCodec,
        int expectedYear)
    {
        var parsed = ReleaseName.Parse(release);

        Assert.Equal(expectedGroup, parsed.Group);
        Assert.Equal(expectedResolution, parsed.Resolution);
        Assert.Equal(expectedSource, parsed.Source);
        Assert.Equal(expectedCodec, parsed.Codec);
        Assert.Equal(expectedYear, parsed.Year);
    }

    [Fact]
    public void Parse_DoesNotMistakeTheHyphenInWebDlForAGroupSeparator()
    {
        var parsed = ReleaseName.Parse("Dune.Part.Two.2024.1080p.WEB-DL.H264.AAC");

        // The final hyphen belongs to WEB-DL, so "DL.H264.AAC" is a fragment, not a group.
        Assert.Null(parsed.Group);
        Assert.Equal("web", parsed.Source);
        Assert.Equal("h264", parsed.Codec);
    }

    [Fact]
    public void Parse_StripsTheSuffixesSubtitlecatAppends()
    {
        var parsed = ReleaseName.Parse("Dune.Part.Two.2024.REPACK.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA_eng_sdh");

        Assert.Equal("psa", parsed.Group);
    }

    [Fact]
    public void Parse_ReadsSeasonAndEpisode()
    {
        var parsed = ReleaseName.Parse("The.Bear.S03E05.1080p.WEB.h264-ETHEL");

        Assert.True(parsed.IsEpisode);
        Assert.Equal(3, parsed.Season);
        Assert.Equal(5, parsed.Episode);
    }

    [Fact]
    public void SameGroupAtADifferentResolutionOutranksADifferentGroupAtTheRightOne()
    {
        var local = ReleaseName.Parse("Interstellar.2014.2160p.UHD.BluRay.REMUX.HDR.HEVC.TrueHD.7.1-FGT");
        var sameGroupLowerRes = ReleaseName.Parse("Interstellar.2014.1080p.BluRay.REMUX.AVC.TrueHD.7.1-FGT");
        var otherGroupSameRes = ReleaseName.Parse("Interstellar.2014.2160p.UHD.BluRay.x265-TERMiNAL");

        var sameGroupScore = ReleaseMatcher.Score(local, sameGroupLowerRes, allowResolutionMismatch: true);
        var otherGroupScore = ReleaseMatcher.Score(local, otherGroupSameRes, allowResolutionMismatch: true);

        // This is the whole point of scoring rather than string-matching: subtitles are
        // timed to the cut, so the same group's 1080p encode fits a 2160p file better
        // than a stranger's 2160p one does.
        Assert.True(
            sameGroupScore > otherGroupScore,
            $"same group at 1080p scored {sameGroupScore}, other group at 2160p scored {otherGroupScore}");
    }

    [Fact]
    public void ADifferentResolutionIsRejectedWhenTheOptionIsOff()
    {
        var local = ReleaseName.Parse("Interstellar.2014.2160p.UHD.BluRay.REMUX.HDR.HEVC-FGT");
        var candidate = ReleaseName.Parse("Interstellar.2014.1080p.BluRay.REMUX.AVC-FGT");

        Assert.Equal(ReleaseMatcher.Rejected, ReleaseMatcher.Score(local, candidate, allowResolutionMismatch: false));
        Assert.NotEqual(ReleaseMatcher.Rejected, ReleaseMatcher.Score(local, candidate, allowResolutionMismatch: true));
    }

    [Fact]
    public void TheWrongEpisodeIsNeverAMatch()
    {
        var local = ReleaseName.Parse("The.Bear.S03E05.1080p.WEB.h264-ETHEL");
        var neighbour = ReleaseName.Parse("The.Bear.S03E06.1080p.WEB.h264-ETHEL");
        var right = ReleaseName.Parse("The.Bear.S03E05.2160p.WEB.h265-NTb");

        // An off-by-one episode subtitle looks entirely plausible in the results list and
        // is completely wrong on screen, so this gate is absolute rather than weighted.
        Assert.Equal(ReleaseMatcher.Rejected, ReleaseMatcher.Score(local, neighbour, allowResolutionMismatch: true));
        Assert.NotEqual(ReleaseMatcher.Rejected, ReleaseMatcher.Score(local, right, allowResolutionMismatch: true));
    }

    [Fact]
    public void AMovieDoesNotMatchAnEpisodeOrTheReverse()
    {
        var movie = ReleaseName.Parse("Dune.Part.Two.2024.1080p.BluRay.x264-YTS");
        var episode = ReleaseName.Parse("Dune.Part.Two.S01E01.1080p.WEB.h264-YTS");

        Assert.Equal(ReleaseMatcher.Rejected, ReleaseMatcher.Score(movie, episode, allowResolutionMismatch: true));
        Assert.Equal(ReleaseMatcher.Rejected, ReleaseMatcher.Score(episode, movie, allowResolutionMismatch: true));
    }

    [Fact]
    public void ARemakeWithTheSameTitleIsRejectedOnTheYear()
    {
        var local = ReleaseName.Parse("Dune.2021.2160p.BluRay.x265-GROUP");
        var remake = ReleaseName.Parse("Dune.1984.1080p.BluRay.x264-GROUP");

        Assert.Equal(ReleaseMatcher.Rejected, ReleaseMatcher.Score(local, remake, allowResolutionMismatch: true));
    }

    [Fact]
    public void AYearOffByOneIsStillAccepted()
    {
        var local = ReleaseName.Parse("Some.Film.2019.1080p.BluRay.x264-GROUP");
        var candidate = ReleaseName.Parse("Some.Film.2020.1080p.BluRay.x264-GROUP");

        // Festival run against general release is a routine one-year disagreement between
        // a scene release and the metadata Jellyfin holds.
        Assert.NotEqual(ReleaseMatcher.Rejected, ReleaseMatcher.Score(local, candidate, allowResolutionMismatch: true));
    }

    [Fact]
    public void APartialTitleMatchIsRejected()
    {
        var dune = ReleaseName.Parse("Dune.2021.1080p.BluRay.x264-GROUP");
        var partTwo = ReleaseName.Parse("Dune.Part.Two.2024.1080p.BluRay.x264-GROUP");

        // "Dune" is a subset of "Dune Part Two" but they are different films, so a
        // partial title overlap must not be enough on its own.
        Assert.Equal(ReleaseMatcher.Rejected, ReleaseMatcher.Score(partTwo, dune, allowResolutionMismatch: true));
    }

    [Fact]
    public void NativeResultsAreRankedAboveMachineTranslatedOnes()
    {
        var items = new[]
        {
            new { Machine = true, Score = 500, Languages = 40 },
            new { Machine = false, Score = 50, Languages = 2 }
        };

        var ranked = ReleaseMatcher.Rank(items, i => i.Machine, i => i.Score, i => i.Languages).ToList();

        // Ordering is grouped, not weighted, so a high-scoring machine translation can
        // never displace a native subtitle no matter how good its release match is.
        Assert.False(ranked[0].Machine);
        Assert.True(ranked[1].Machine);
    }
}
