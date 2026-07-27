using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleCat.Matching;
using Jellyfin.Plugin.SubtitleCat.Scraping;
using Xunit;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>
/// Runs parsing and scoring together over the saved pages, so the pieces are checked
/// against the shape of the real site rather than only in isolation.
/// </summary>
public class EndToEndMatchingTests
{
    /// <summary>
    /// The scenario the plugin exists for: a 2160p file, and subtitlecat has no 2160p
    /// entry at all for it.
    /// </summary>
    private const string LocalFile = "Dune.Part.Two.2024.2160p.UHD.BluRay.x265.10bit.HDR.TrueHD.7.1.Atmos-SWTYBLZ";

    [Fact]
    public void A2160pFileFindsCandidatesAmongTheReal1080pResults()
    {
        var local = ReleaseName.Parse(LocalFile);
        var rows = SearchResultParser.Parse(FixtureFile.Read("search-results.html"));

        var accepted = rows
            .Select(row => new { row.Name, Score = ReleaseMatcher.Score(local, ReleaseName.Parse(row.Name), true) })
            .Where(x => x.Score >= 40)
            .OrderByDescending(x => x.Score)
            .ToList();

        // Every result on this page is 1080p or lower. Without the resolution-mismatch
        // allowance the list would be empty and the plugin would be useless for 4K.
        Assert.NotEmpty(accepted);
        Assert.All(accepted, x => Assert.Contains("Dune", x.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheAcceptedCandidatesIncludeTheOneThatActuallyHasDutch()
    {
        var local = ReleaseName.Parse(LocalFile);
        var rows = SearchResultParser.Parse(FixtureFile.Read("search-results.html"));

        // detail-page-with-dutch.html is this row's page, and it does carry a Dutch
        // subtitle. If scoring ever stops accepting it, 4K files silently lose Dutch.
        var ytsBluRay = rows.Single(r =>
            r.Name.Equals("Dune.Part.Two.2024.1080p.BluRay.x264.AAC5.1-[YTS.MX]", StringComparison.Ordinal));

        var score = ReleaseMatcher.Score(local, ReleaseName.Parse(ytsBluRay.Name), allowResolutionMismatch: true);

        Assert.True(score >= 40, $"the release that has Dutch scored only {score}");

        var dutch = DetailPageParser
            .Parse(FixtureFile.Read("detail-page-with-dutch.html"))
            .SingleOrDefault(l => l.LanguageCode == "nl");

        Assert.NotNull(dutch);
    }

    [Fact]
    public void UnrelatedReleasesOnThePageAreNotAccepted()
    {
        var unrelated = ReleaseName.Parse("The.Matrix.1999.2160p.UHD.BluRay.x265-GROUP");
        var rows = SearchResultParser.Parse(FixtureFile.Read("search-results.html"));

        var accepted = rows
            .Select(row => ReleaseMatcher.Score(unrelated, ReleaseName.Parse(row.Name), true))
            .Count(score => score >= 40);

        // A page full of Dune releases must produce nothing for a different film, or the
        // fallback title search would attach any old subtitle to anything.
        Assert.Equal(0, accepted);
    }
}
