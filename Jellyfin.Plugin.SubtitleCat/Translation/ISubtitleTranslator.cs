using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleCat.Translation;

/// <summary>Which translation service the plugin sends text to.</summary>
public enum TranslatorKind
{
    /// <summary>Translation is off. Only subtitles the site already hosts are offered.</summary>
    None = 0,

    /// <summary>A LibreTranslate instance, typically self-hosted on the local network.</summary>
    LibreTranslate = 1,

    /// <summary>The public Google endpoint that subtitlecat's own page calls.</summary>
    GoogleTranslate = 2,

    /// <summary>The DeepL API, free or pro tier.</summary>
    DeepL = 3
}

/// <summary>
/// Translates lines of subtitle text.
/// </summary>
/// <remarks>
/// Implementations receive text and nothing else. Cue numbering and timings never reach
/// them, so a service that reflows, reorders or mangles what it is given can spoil the
/// wording but cannot desynchronise the subtitle from the video.
/// </remarks>
public interface ISubtitleTranslator
{
    /// <summary>Gets the name shown in results and logs.</summary>
    string Name { get; }

    /// <summary>Gets which backend this is.</summary>
    TranslatorKind Kind { get; }

    /// <summary>
    /// Gets a value indicating whether this translator has everything it needs to run,
    /// for example an API key or a reachable address.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Translates a batch of lines.</summary>
    /// <param name="segments">The lines to translate.</param>
    /// <param name="sourceLanguage">The two-letter source language, or null to auto-detect.</param>
    /// <param name="targetLanguage">The two-letter target language.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// Exactly as many strings as were passed in, in the same order. A line that could
    /// not be translated comes back as its original text rather than as null or empty:
    /// a partly translated subtitle is still watchable, a misaligned one is not.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The service refused the whole batch, for example on an exhausted quota or a
    /// rejected key. Callers surface this rather than silently returning the original.
    /// </exception>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> segments,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
