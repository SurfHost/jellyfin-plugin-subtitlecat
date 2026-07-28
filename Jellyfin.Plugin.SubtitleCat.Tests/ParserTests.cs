using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleCat.Models;
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
    public void DetailParser_SeparatesStoredSubtitlesFromTranslateOnlyOnes()
    {
        var html = FixtureFile.Read("detail-page.html");

        var links = DetailPageParser.Parse(html);
        var stored = links.Where(l => l.Kind == SubtitleCatLinkKind.Stored).ToList();
        var translatable = links.Where(l => l.Kind == SubtitleCatLinkKind.Translatable).ToList();

        // The page lists ~149 languages but only 16 have been generated. The rest carry
        // a Translate button, which is a different thing to offer and must not be
        // mistaken for a finished subtitle.
        Assert.Equal(16, stored.Count);
        Assert.True(translatable.Count > 100, $"expected many translate-only entries, got {translatable.Count}");
        Assert.True(DetailPageParser.CountBlocks(html) > 100);
        Assert.All(links, link => Assert.EndsWith(".srt", link.Href, StringComparison.Ordinal));
    }

    [Fact]
    public void DetailParser_BuildsTheOriginalSubtitleAddressFromTheTranslateButton()
    {
        var links = DetailPageParser.Parse(FixtureFile.Read("detail-page.html"));

        var afrikaans = links.Single(l => l.LanguageCode == "af");

        Assert.Equal(SubtitleCatLinkKind.Translatable, afrikaans.Kind);

        // Every Translate button on a page points at the same original file, in the
        // page's own folder. Only the target language differs between them.
        Assert.Equal(
            "/subs/789/Dune.Part.Two.2024.REPACK.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA_eng_sdh-orig.srt",
            afrikaans.Href);

        var everyOriginal = links
            .Where(l => l.Kind == SubtitleCatLinkKind.Translatable)
            .Select(l => l.Href)
            .Distinct()
            .ToList();

        Assert.Single(everyOriginal);
    }

    [Fact]
    public void DetailParser_ReadsLanguageCodesAndNames()
    {
        var links = DetailPageParser.Parse(FixtureFile.Read("detail-page.html"))
            .Where(l => l.Kind == SubtitleCatLinkKind.Stored)
            .ToList();

        var english = links.Single(l => l.LanguageCode == "en");
        Assert.Equal("English", english.LanguageName);

        // Regional variants keep their full code; normalising happens at match time.
        Assert.Contains(links, l => l.LanguageCode == "pt-BR");
        Assert.Contains(links, l => l.LanguageCode == "zh-CN");
    }

    [Fact]
    public void DetailParser_OffersDutchAsTranslatableWhenNoStoredOneExists()
    {
        var links = DetailPageParser.Parse(FixtureFile.Read("detail-page.html"));

        // This release has no stored Dutch, which is the ordinary case and not a parser
        // failure. It does offer to translate one, which is what the provider surfaces.
        Assert.DoesNotContain(links, l => l.LanguageCode == "nl" && l.Kind == SubtitleCatLinkKind.Stored);

        var dutch = links.Single(l => l.LanguageCode == "nl");
        Assert.Equal(SubtitleCatLinkKind.Translatable, dutch.Kind);
        Assert.EndsWith("-orig.srt", dutch.Href, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailParser_DownloadUrlLivesInADifferentFolderThanThePage()
    {
        var links = DetailPageParser.Parse(FixtureFile.Read("detail-page-with-dutch.html"));

        var dutch = links.Single(l => l.LanguageCode == "nl" && l.Kind == SubtitleCatLinkKind.Stored);

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
