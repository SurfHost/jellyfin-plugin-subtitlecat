using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleCat.Subtitles;
using Jellyfin.Plugin.SubtitleCat.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>
/// Tests the translation pipeline against a stand-in backend.
/// </summary>
/// <remarks>
/// These deliberately model backends behaving badly, because the real ones do: a batch
/// endpoint that returns a different number of lines than it was sent is the failure that
/// silently shifts every subsequent subtitle onto the wrong timing.
/// </remarks>
public class TranslationServiceTests
{
    private const string Sample =
        "1\r\n00:00:01,000 --> 00:00:02,000\r\nFirst line\r\n\r\n" +
        "2\r\n00:00:03,000 --> 00:00:04,000\r\n<i>Second line</i>\r\n\r\n" +
        "3\r\n00:00:05,000 --> 00:00:06,000\r\n♪\r\n\r\n" +
        "4\r\n00:00:07,000 --> 00:00:08,000\r\nFourth line\r\n";

    private static SubtitleTranslationService Service() =>
        new(NullLogger<SubtitleTranslationService>.Instance);

    private static async Task<SrtDocument> RunAsync(FakeTranslator translator, int batchSize = 2)
    {
        var output = await Service()
            .TranslateAsync(Sample, translator, "en", "nl", batchSize, CancellationToken.None)
            .ConfigureAwait(false);

        return SrtDocument.Parse(output);
    }

    [Fact]
    public async Task TranslatesEveryCueAndKeepsTheTimings()
    {
        var result = await RunAsync(new FakeTranslator(s => "[" + s + "]"));

        var original = SrtDocument.Parse(Sample);
        Assert.Equal(original.Cues.Count, result.Cues.Count);
        for (var i = 0; i < original.Cues.Count; i++)
        {
            Assert.Equal(original.Cues[i].Start, result.Cues[i].Start);
            Assert.Equal(original.Cues[i].End, result.Cues[i].End);
        }

        Assert.Equal("[First line]", result.Cues[0].Text);
    }

    [Fact]
    public async Task KeepsTheWrappingItalicTag()
    {
        var result = await RunAsync(new FakeTranslator(s => "[" + s + "]"));

        Assert.Equal("<i>[Second line]</i>", result.Cues[1].Text);
    }

    [Fact]
    public async Task DoesNotSendCuesWithNoWords()
    {
        var translator = new FakeTranslator(s => "[" + s + "]");

        var result = await RunAsync(translator);

        // The musical note cue has nothing a translator could act on, so it should never
        // have gone out, and it should come back exactly as it was.
        Assert.DoesNotContain(translator.Seen, s => s.Contains('♪', StringComparison.Ordinal));
        Assert.Equal("♪", result.Cues[2].Text);
        Assert.Equal(3, translator.Seen.Count);
    }

    [Fact]
    public async Task ABackendThatReturnsTheWrongNumberOfLinesDoesNotShiftTheSubtitle()
    {
        // Drops one line from every multi-line batch, which is exactly the shape of
        // failure that would otherwise put each translation on the following cue.
        var translator = new FakeTranslator(s => "[" + s + "]")
        {
            MangleBatch = results => results.Count > 1 ? results.Take(results.Count - 1).ToList() : results
        };

        var result = await RunAsync(translator);

        Assert.Equal("[First line]", result.Cues[0].Text);
        Assert.Equal("<i>[Second line]</i>", result.Cues[1].Text);
        Assert.Equal("♪", result.Cues[2].Text);
        Assert.Equal("[Fourth line]", result.Cues[3].Text);
        Assert.True(translator.SingleCalls > 0, "expected a fallback to one line at a time");
    }

    [Fact]
    public async Task ABackendThatReturnsExtraLinesIsAlsoHandled()
    {
        var translator = new FakeTranslator(s => "[" + s + "]")
        {
            MangleBatch = results => results.Concat(new[] { "spurious" }).ToList()
        };

        var result = await RunAsync(translator);

        Assert.Equal("[First line]", result.Cues[0].Text);
        Assert.Equal("[Fourth line]", result.Cues[3].Text);
        Assert.DoesNotContain(result.Cues, c => c.Text.Contains("spurious", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALineThatComesBackEmptyKeepsItsOriginalText()
    {
        var result = await RunAsync(new FakeTranslator(s => s == "First line" ? string.Empty : "[" + s + "]"));

        // Better an untranslated line than a blank one where dialogue should be.
        Assert.Equal("First line", result.Cues[0].Text);
        Assert.Equal("[Fourth line]", result.Cues[3].Text);
    }

    [Fact]
    public async Task ATransientFailureFallsBackInsteadOfLosingTheBatch()
    {
        var translator = new FakeTranslator(s => "[" + s + "]") { ThrowOnBatch = true };

        var result = await RunAsync(translator);

        Assert.Equal("[First line]", result.Cues[0].Text);
        Assert.Equal("[Fourth line]", result.Cues[3].Text);
    }

    [Fact]
    public async Task AnExhaustedQuotaIsReportedRatherThanSilentlyReturningEnglish()
    {
        var translator = new FakeTranslator(_ => "unused") { RefuseEverything = true };

        // Handing back the untranslated file would look like success and leave the user
        // with an English subtitle labelled Dutch.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(translator));
    }

    [Fact]
    public async Task AFileWithNothingToTranslateIsReportedRatherThanReturnedEmpty()
    {
        var service = Service();
        var translator = new FakeTranslator(s => s);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(
            "1\r\n00:00:01,000 --> 00:00:02,000\r\n♪\r\n",
            translator,
            "en",
            "nl",
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task AnUnreadableSourceIsReported()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().TranslateAsync(
            "<html>not a subtitle at all</html>",
            new FakeTranslator(s => s),
            "en",
            "nl",
            10,
            CancellationToken.None));
    }

    /// <summary>A translator whose misbehaviour can be dialled in per test.</summary>
    private sealed class FakeTranslator : ISubtitleTranslator
    {
        private readonly Func<string, string> _translate;

        public FakeTranslator(Func<string, string> translate)
        {
            _translate = translate;
        }

        public string Name => "Fake";

        public TranslatorKind Kind => TranslatorKind.None;

        public bool IsConfigured => true;

        public List<string> Seen { get; } = new();

        public int SingleCalls { get; private set; }

        public bool RefuseEverything { get; init; }

        public bool ThrowOnBatch { get; init; }

        public Func<IReadOnlyList<string>, IReadOnlyList<string>>? MangleBatch { get; init; }

        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> segments,
            string? sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            if (RefuseEverything)
            {
                throw new InvalidOperationException("quota exhausted");
            }

            if (segments.Count == 1)
            {
                SingleCalls++;
            }
            else if (ThrowOnBatch)
            {
                throw new TimeoutException("transient");
            }

            Seen.AddRange(segments);

            IReadOnlyList<string> results = segments.Select(_translate).ToList();
            if (segments.Count > 1 && MangleBatch is not null)
            {
                results = MangleBatch(results);
            }

            return Task.FromResult(results);
        }
    }
}
