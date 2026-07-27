using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleCat.Scraping;
using Xunit;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>Parser tests against the saved pages in Fixtures.</summary>
public class ParserTests
{
    [Fact]
    public void SearchParser_ReadsRowsFromTheRealPage()
    {
        var rows = SearchResultParser.Parse(FixtureFile.Read("search-results.html"));

        Assert.NotEmpty(rows);

        var psa = rows.Single(r => r.Name.Contains("HEVC-PSA_eng_sdh", StringComparison.Ordinal));

        // The href is relative with no leading slash. Keeping it verbatim is what the
        // URL builder relies on.
        Assert.StartsWith("subs/789/", psa.Href, StringComparison.Ordinal);
        Assert.Equal("English", psa.SourceLanguageName);
        Assert.Equal(16, psa.LanguageCount);
        Assert.Equal(16, psa.DownloadCount);
    }

    [Fact]
    public void SearchParser_ReadsTheSourceLanguageFromOutsideTheAnchor()
    {
        var rows = SearchResultParser.Parse(FixtureFile.Read("search-results.html"));

        // "(translated from Italian)" sits in the parent td, not in the link text, so
        // this fails the moment anyone tries to read it off the anchor.
        var italian = rows.Single(r => r.Name.Contains("[1080p AC3].ITA", StringComparison.Ordinal));
        Assert.Equal("Italian", italian.SourceLanguageName);
    }

    [Fact]
    public void SearchParser_ReturnsNothingForMarkupItDoesNotRecognise()
    {
        Assert.Empty(SearchResultParser.Parse("<html><body><p>nothing here</p></body></html>"));
        Assert.Empty(SearchResultParser.Parse(null));
        Assert.Empty(SearchResultParser.Parse(string.Empty));
    }

    [Fact]
    public void DetailParser_ReturnsOnlyLanguagesThatCanActuallyBeDownloaded()
    {
        var html = FixtureFile.Read("detail-page.html");

        var links = DetailPageParser.Parse(html);

        // The page lists ~149 languages but only 16 have been generated. The rest are
        // Translate buttons that run in the visitor's browser and must never be offered.
        Assert.Equal(16, links.Count);
        Assert.True(DetailPageParser.CountBlocks(html) > 100);
        Assert.All(links, link => Assert.EndsWith(".srt", link.Href, StringComparison.Ordinal));
    }

    [Fact]
    public void DetailParser_ReadsLanguageCodesAndNames()
    {
        var links = DetailPageParser.Parse(FixtureFile.Read("detail-page.html"));

        var english = links.Single(l => l.LanguageCode == "en");
        Assert.Equal("English", english.LanguageName);

        // Regional variants keep their full code; normalising happens at match time.
        Assert.Contains(links, l => l.LanguageCode == "pt-BR");
        Assert.Contains(links, l => l.LanguageCode == "zh-CN");
    }

    [Fact]
    public void DetailParser_HandlesAPageWithNoDutchAtAll()
    {
        var links = DetailPageParser.Parse(FixtureFile.Read("detail-page.html"));

        // This release genuinely has no Dutch. An empty result here is the correct
        // answer, not a parser failure, which is why the health panel separates the two.
        Assert.DoesNotContain(links, l => l.LanguageCode == "nl");
    }

    [Fact]
    public void DetailParser_DownloadUrlLivesInADifferentFolderThanThePage()
    {
        var links = DetailPageParser.Parse(FixtureFile.Read("detail-page-with-dutch.html"));

        var dutch = links.Single(l => l.LanguageCode == "nl");

        // The page is /subs/736/ but its Dutch subtitle is served from /subs/757/. This
        // is the tripwire against anyone "optimising" the download URL by rebuilding it
        // from the detail page id, which would 404 on every single download.
        Assert.StartsWith("/subs/757/", dutch.Href, StringComparison.Ordinal);
        Assert.DoesNotContain("/subs/736/", dutch.Href, StringComparison.Ordinal);
        Assert.EndsWith("-nl.srt", dutch.Href, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailParser_SurvivesMarkupThatNoLongerMatches()
    {
        var drifted = FixtureFile.Read("detail-page.html").Replace("sub-single", "sub-item", StringComparison.Ordinal);

        // A layout change has to degrade to zero results without throwing, so the
        // provider returns nothing and the health panel reports it.
        Assert.Empty(DetailPageParser.Parse(drifted));
        Assert.Equal(0, DetailPageParser.CountBlocks(drifted));
    }
}
