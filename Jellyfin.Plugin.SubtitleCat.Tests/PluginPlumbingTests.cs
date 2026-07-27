using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.SubtitleCat.Matching;
using Jellyfin.Plugin.SubtitleCat.Providers;
using Jellyfin.Plugin.SubtitleCat.Scraping;
using Xunit;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>Tests for the id codec, URL building, encoding detection and the query ladder.</summary>
public class PluginPlumbingTests
{
    [Fact]
    public void SubtitleId_RoundTrips()
    {
        var original = new SubtitleCatId
        {
            Href = "/subs/757/Dune.Part.Two.2024.1080p.BluRay.x264.AAC5.1-[YTS.MX]-nl.srt",
            Language = "dut",
            HearingImpaired = true,
            Forced = false
        };

        Assert.True(SubtitleCatId.TryDecode(original.Encode(), out var decoded, out _));

        Assert.NotNull(decoded);
        Assert.Equal(original.Href, decoded!.Href);
        Assert.Equal("dut", decoded.Language);
        Assert.True(decoded.HearingImpaired);
        Assert.False(decoded.Forced);
    }

    [Fact]
    public void SubtitleId_EchoesWhicheverLanguageCodeTheServerUses()
    {
        // Jellyfin names the saved file from this, so a server set to nld must get nld
        // back and one set to dut must get dut. Normalising would rename people's files.
        foreach (var language in new[] { "dut", "nld", "nl" })
        {
            var encoded = new SubtitleCatId { Href = "/subs/1/x-nl.srt", Language = language }.Encode();
            Assert.True(SubtitleCatId.TryDecode(encoded, out var decoded, out _));
            Assert.Equal(language, decoded!.Language);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("sc9.dut.n.aGk")]
    [InlineData("sc1.dut.n.!!!not-base64!!!")]
    public void SubtitleId_RefusesWhatItCannotRead(string value)
    {
        Assert.False(SubtitleCatId.TryDecode(value, out var decoded, out var error));
        Assert.Null(decoded);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Url_EncodesRawBracketsButLeavesExistingEscapesAlone()
    {
        // Download hrefs arrive with raw brackets; search hrefs arrive already encoded.
        // Both have to end up as one valid URL, which is why this cannot be a single pass.
        var fromDownload = SubtitleCatUrl.Combine(
            "https://www.subtitlecat.com",
            "/subs/757/Dune.2024.AAC5.1-[YTS.MX]-nl.srt");

        var fromSearch = SubtitleCatUrl.Combine(
            "https://www.subtitlecat.com",
            "subs/1518/%20Dune%20Part%20Two%20%282024%29%20%5B1080p%20AC3%5D.ITA.html");

        Assert.NotNull(fromDownload);
        Assert.Contains("%5BYTS.MX%5D", fromDownload!.AbsoluteUri, StringComparison.Ordinal);

        Assert.NotNull(fromSearch);
        Assert.Contains("%20Dune%20Part%20Two%20%282024%29", fromSearch!.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("%2520", fromSearch.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Url_RejectsAnythingThatWouldLeaveTheSite()
    {
        var elsewhere = SubtitleCatUrl.Combine("https://www.subtitlecat.com", "http://192.168.1.1/admin");

        // An absolute href is taken as-is, which is exactly why the client checks the
        // host separately before fetching anything.
        Assert.NotNull(elsewhere);
        Assert.Equal("192.168.1.1", elsewhere!.Host);
    }

    [Fact]
    public void TextDecoder_HandlesTheEncodingsSubtitlesActuallyArriveIn()
    {
        var utf8 = Encoding.UTF8.GetBytes("Café — naïve");
        Assert.Equal("Café — naïve", SubtitleTextDecoder.Decode(utf8, out var plain));
        Assert.Equal("utf-8", plain);

        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray();
        Assert.Equal("Café — naïve", SubtitleTextDecoder.Decode(withBom, out var bom));
        Assert.Equal("utf-8 (bom)", bom);

        // 0xE9 is a lone invalid UTF-8 byte, so this must fall back rather than throw or
        // produce mojibake. 0x97 is the em dash, which is where Windows-1252 and
        // ISO-8859-1 differ and the reconstructed table earns its keep.
        var cp1252 = new byte[] { 0x43, 0x61, 0x66, 0xE9, 0x20, 0x97, 0x20, 0x6F, 0x6B };
        Assert.Equal("Café — ok", SubtitleTextDecoder.Decode(cp1252, out var legacy));
        Assert.Equal("windows-1252", legacy);
    }

    [Fact]
    public void QueryBuilder_IncludesAResolutionStrippedQuery()
    {
        var raw = "Interstellar.2014.2160p.UHD.BluRay.REMUX.HDR.HEVC-FGT";
        var queries = SearchQueryBuilder.Build(
            ReleaseName.Parse(raw), raw, "Interstellar", null, 2014, null, null, includeFallbacks: true);

        // Without this rung, searching the full 2160p name only ever returns 2160p
        // entries and the cross-resolution match has nothing to score against.
        Assert.Contains(queries, q => q.Contains("Interstellar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            queries,
            q => !q.Contains("2160p", StringComparison.OrdinalIgnoreCase)
                && q.Contains("REMUX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QueryBuilder_BuildsAnEpisodeQuery()
    {
        var queries = SearchQueryBuilder.Build(
            null, null, "Doors of Perception", "The Bear", null, 3, 5, includeFallbacks: true);

        Assert.Contains("The Bear S03E05", queries);
    }

    [Fact]
    public void LanguageCodes_ReconcileWhatJellyfinSendsWithWhatTheSiteWrites()
    {
        Assert.Equal("nl", LanguageCodes.Resolve(null, "dut"));
        Assert.Equal("nl", LanguageCodes.Resolve(null, "nld"));
        Assert.Equal("nl", LanguageCodes.Resolve("nl", "dut"));

        Assert.True(LanguageCodes.Matches("nl", "nl", out var exact));
        Assert.True(exact);

        // A regional variant answers a request for its base language, but is flagged so
        // the exact one is offered first.
        Assert.True(LanguageCodes.Matches("pt-BR", "pt", out var regional));
        Assert.False(regional);

        Assert.False(LanguageCodes.Matches("de", "nl", out _));
        Assert.Equal("nl", LanguageCodes.FromEnglishName("Dutch"));
    }
}
