using System.Linq;
using Jellyfin.Plugin.SubtitleCat.Matching;
using Xunit;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>
/// Regression tests for the title gate.
/// </summary>
/// <remarks>
/// These cover a class of title whose tokens all disappeared during parsing: dotted
/// acronyms such as "S.W.A.T." tokenise to single letters, and a title whose first word
/// also appears in the technical-metadata vocabulary ("Dual Survival") stopped the title
/// scan before it started. An empty token list then meant the title could not be checked
/// at all, which silently disabled the only guard against matching a different title.
/// </remarks>
public class TitleGateTests
{
    [Theory]
    [InlineData("S.W.A.T.S05E12.1080p.WEB.h264-GOSSIP")]
    [InlineData("S.W.A.T.2017.S05E12.1080p.WEB.H264-CAKES")]
    [InlineData("Dual.Survival.S01E01.1080p.WEB.h264-GRP")]
    [InlineData("2.Broke.Girls.S01E01.1080p.WEB.h264-GRP")]
    [InlineData("Vision.Quest.S01E01.1080p.WEB.h264-GRP")]
    public void TitlesThatUsedToVanishStillProduceComparableTokens(string release)
    {
        var parsed = ReleaseName.Parse(release);

        Assert.NotEmpty(parsed.SignificantTitleTokens);
    }

    [Theory]
    // The exact cross-series pairs the review reproduced, which scored 115 and 95
    // against a threshold of 40 and would have downloaded another show's subtitle.
    [InlineData("S.W.A.T.S05E12.1080p.WEB.h264-GOSSIP", "The.Rookie.S05E12.1080p.WEB.h264-NTb")]
    [InlineData("S.W.A.T.2017.S05E12.1080p.WEB.H264-CAKES", "Yellowstone.S05E12.1080p.WEB.H264-CAKES")]
    [InlineData("Dual.Survival.S01E01.1080p.WEB.h264-GRP", "Breaking.Bad.S01E01.1080p.BluRay.x264-GRP")]
    [InlineData("2.Broke.Girls.S01E01.1080p.WEB.h264-GRP", "Breaking.Bad.S01E01.1080p.WEB.h264-GRP")]
    public void ADifferentSeriesWithTheSameEpisodeNumberIsRejected(string local, string other)
    {
        var score = ReleaseMatcher.Score(
            ReleaseName.Parse(local), ReleaseName.Parse(other), allowResolutionMismatch: true);

        Assert.Equal(ReleaseMatcher.Rejected, score);
    }

    [Theory]
    [InlineData("S.W.A.T.S05E12.1080p.WEB.h264-GOSSIP", "S.W.A.T.S05E12.1080p.WEB.h264-NTb")]
    [InlineData("Dual.Survival.S01E01.1080p.WEB.h264-GRP", "Dual.Survival.S01E01.720p.HDTV.x264-XYZ")]
    public void TheSameSeriesStillMatches(string local, string other)
    {
        var score = ReleaseMatcher.Score(
            ReleaseName.Parse(local), ReleaseName.Parse(other), allowResolutionMismatch: true);

        Assert.True(score >= 40, $"the correct series scored only {score}");
    }

    [Theory]
    // The mirror image of the same defect: for films these titles produced an empty
    // token list too, which was read as "no title in common" and rejected outright, so
    // they could never receive a subtitle from any release.
    [InlineData("S.W.A.T.2003.1080p.BluRay.x264-GRP")]
    [InlineData("R.I.P.D.2013.1080p.BluRay.x264-GRP")]
    [InlineData("Dual.2022.1080p.BluRay.x264-GRP")]
    public void AFilmMatchesItsOwnReleaseName(string release)
    {
        var score = ReleaseMatcher.Score(
            ReleaseName.Parse(release), ReleaseName.Parse(release), allowResolutionMismatch: true);

        Assert.True(score >= 40, $"a film scored {score} against its own release name");
    }

    [Fact]
    public void AFilmStillDoesNotMatchADifferentFilm()
    {
        var score = ReleaseMatcher.Score(
            ReleaseName.Parse("S.W.A.T.2003.1080p.BluRay.x264-GRP"),
            ReleaseName.Parse("Interstellar.2014.1080p.BluRay.x264-GRP"),
            allowResolutionMismatch: true);

        Assert.Equal(ReleaseMatcher.Rejected, score);
    }

    [Fact]
    public void ANameWithNoTitleAtAllIsNeverTreatedAsAPerfectMatch()
    {
        // A file called just "S05E12.mkv" carries no title. The provider seeds the
        // series name from the Jellyfin request for exactly this case, but if that ever
        // fails the matcher must refuse rather than fall back to matching on the
        // episode number alone.
        var local = ReleaseName.Parse("S05E12");
        Assert.Empty(local.SignificantTitleTokens);

        var score = ReleaseMatcher.Score(
            local, ReleaseName.Parse("The.Rookie.S05E12.1080p.WEB.h264-NTb"), allowResolutionMismatch: true);

        Assert.Equal(ReleaseMatcher.Rejected, score);
    }

    [Fact]
    public void TechnicalTokensAreStillKeptOutOfTheTitleWhenTheyFollowIt()
    {
        // The relaxation must not swing the other way: once a title has been collected,
        // a metadata word still ends it.
        var parsed = ReleaseName.Parse("Some.Show.S01E01.1080p.WEB.DD5.1.Atmos.Dual.Audio.x264-GRP");

        Assert.Equal(new[] { "some", "show" }, parsed.SignificantTitleTokens.ToArray());
        Assert.Equal("grp", parsed.Group);
    }
}
