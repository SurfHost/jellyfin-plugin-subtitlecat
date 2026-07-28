using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleCat.Subtitles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleCat.Translation;

/// <summary>
/// Turns a subtitle file in one language into the same file in another.
/// </summary>
/// <remarks>
/// The whole job of this class is to keep a subtitle intact while its words are replaced.
/// It parses the file, sends only cue text out in batches, and rebuilds the file from the
/// original timings. Two invariants make that safe: a batch's reply must contain exactly
/// as many lines as were sent, and the finished list must contain exactly as many lines
/// as there are cues. Either mismatch shifts every later line onto the wrong timing, so
/// both are checked rather than assumed.
/// </remarks>
public sealed class SubtitleTranslationService
{
    private readonly ILogger<SubtitleTranslationService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleTranslationService"/> class.</summary>
    public SubtitleTranslationService(ILogger<SubtitleTranslationService> logger)
    {
        _logger = logger;
    }

    /// <summary>Translates a subtitle file.</summary>
    /// <param name="srtText">The decoded source subtitle.</param>
    /// <param name="translator">The backend to send text to.</param>
    /// <param name="sourceLanguage">The two-letter source language, or null to auto-detect.</param>
    /// <param name="targetLanguage">The two-letter target language.</param>
    /// <param name="batchSize">How many cues to send per request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The translated subtitle, ready to write out.</returns>
    /// <exception cref="InvalidOperationException">The source could not be parsed as a subtitle.</exception>
    public async Task<string> TranslateAsync(
        string srtText,
        ISubtitleTranslator translator,
        string? sourceLanguage,
        string targetLanguage,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(translator);

        var document = SrtDocument.Parse(srtText);
        if (document.Cues.Count == 0)
        {
            throw new InvalidOperationException(
                "The original subtitle could not be read, so there was nothing to translate.");
        }

        // Markup comes off before anything goes out, and goes back on afterwards.
        var shells = document.Cues.Select(cue => SrtMarkup.Extract(cue.Text)).ToList();

        // Cues that are only markup, only punctuation or empty are passed straight
        // through. They translate to nothing useful and would waste a slice of the
        // request budget, which on a metered backend is real money.
        var pending = new List<int>();
        for (var i = 0; i < shells.Count; i++)
        {
            if (HasWords(shells[i].Inner))
            {
                pending.Add(i);
            }
        }

        if (pending.Count == 0)
        {
            throw new InvalidOperationException("The original subtitle contained no translatable text.");
        }

        var translated = new string[shells.Count];
        for (var i = 0; i < shells.Count; i++)
        {
            translated[i] = shells[i].Inner;
        }

        var size = Math.Clamp(batchSize, 1, 200);
        var batches = (pending.Count + size - 1) / size;
        var droppedTags = shells.Count(s => s.DroppedInlineTags);
        var batchNumber = 0;

        _logger.LogInformation(
            "SubtitleCat: translating {Cues} cue(s) to {Language} via {Translator} in {Batches} batch(es).",
            pending.Count,
            targetLanguage,
            translator.Name,
            batches);

        for (var offset = 0; offset < pending.Count; offset += size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batchNumber++;

            var slice = pending.Skip(offset).Take(size).ToList();
            var inputs = slice.Select(index => shells[index].Inner).ToList();

            var results = await TranslateBatchAsync(
                translator, inputs, sourceLanguage, targetLanguage, batchNumber, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < slice.Count; i++)
            {
                var value = results[i];
                translated[slice[i]] = string.IsNullOrWhiteSpace(value) ? shells[slice[i]].Inner : value;
            }
        }

        var rebuilt = new List<string>(shells.Count);
        for (var i = 0; i < shells.Count; i++)
        {
            rebuilt.Add(SrtMarkup.Restore(shells[i], translated[i]));
        }

        if (droppedTags > 0)
        {
            _logger.LogDebug(
                "SubtitleCat: dropped inline formatting from {Count} cue(s) so the markup would not be mangled.",
                droppedTags);
        }

        // WithTexts throws if this list and the cue list ever disagree, which is the
        // last line of defence against a shifted subtitle.
        return document.WithTexts(rebuilt).Serialize();
    }

    /// <summary>
    /// Translates one batch, falling back to one line at a time if the reply does not
    /// line up with the request.
    /// </summary>
    /// <remarks>
    /// A batch reply with the wrong number of lines cannot be matched back to its inputs.
    /// Zipping them anyway would put each translation on the wrong cue from that point
    /// on, which reads as a subtitle that slowly stops making sense. Sending the batch
    /// again one line at a time is slower and removes the ambiguity entirely.
    /// </remarks>
    private async Task<IReadOnlyList<string>> TranslateBatchAsync(
        ISubtitleTranslator translator,
        IReadOnlyList<string> inputs,
        string? sourceLanguage,
        string targetLanguage,
        int batchNumber,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? results = null;

        try
        {
            results = await translator
                .TranslateAsync(inputs, sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // A refusal of the whole batch, such as an exhausted quota, is not something
            // retrying line by line will fix.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SubtitleCat: batch {Batch} failed, retrying one line at a time.", batchNumber);
        }

        if (results is not null && results.Count == inputs.Count)
        {
            return results;
        }

        if (results is not null)
        {
            _logger.LogWarning(
                "SubtitleCat: batch {Batch} returned {Got} line(s) for {Sent} sent, so it cannot be matched up. Retrying individually.",
                batchNumber,
                results.Count,
                inputs.Count);
        }

        var individually = new List<string>(inputs.Count);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var single = await translator
                    .TranslateAsync(new[] { input }, sourceLanguage, targetLanguage, cancellationToken)
                    .ConfigureAwait(false);

                individually.Add(single.Count == 1 && !string.IsNullOrWhiteSpace(single[0]) ? single[0] : input);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SubtitleCat: a line could not be translated and was left as-is.");
                individually.Add(input);
            }
        }

        return individually;
    }

    /// <summary>
    /// Determines whether a line contains anything a translator could act on. Musical
    /// notes, dashes and stray punctuation come back unchanged at best.
    /// </summary>
    private static bool HasWords(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Any(char.IsLetter);

    /// <summary>Describes a translation for the result list and the log.</summary>
    /// <param name="translatorName">The backend's display name.</param>
    /// <param name="sourceLanguage">The source language, or null when auto-detected.</param>
    public static string DescribeSource(string translatorName, string? sourceLanguage)
    {
        var builder = new StringBuilder("translated here by ");
        builder.Append(translatorName);

        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            builder.Append(CultureInfo.InvariantCulture, $" from {sourceLanguage}");
        }

        return builder.ToString();
    }
}
