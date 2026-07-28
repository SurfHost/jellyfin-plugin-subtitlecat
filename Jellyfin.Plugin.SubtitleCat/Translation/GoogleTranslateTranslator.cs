using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleCat.Translation;

/// <summary>
/// Translates through the public Google endpoint that subtitlecat's own page calls.
/// </summary>
/// <remarks>
/// <para>
/// Free and needs no account, which is why it is here, but it is undocumented and
/// unsanctioned. It can change or disappear without notice, and its rate limiting is per
/// address: subtitlecat pushes this work into each visitor's browser precisely so the
/// limit lands on them, and running it server-side concentrates every translation in the
/// house onto one address instead.
/// </para>
/// <para>
/// It has no batch parameter. Lines are joined with newlines and split apart again, and
/// the result is only used when the split gives back exactly as many lines as went in.
/// When it does not, the caller falls back to one line per request, which is slow but
/// unambiguous.
/// </para>
/// <para>
/// Do not be tempted to repeat the q parameter instead. Tested against the live endpoint:
/// <c>q=apple&amp;q=banana&amp;q=cherry</c> returns a single translation of "apple" with
/// HTTP 200 and no warning, and reversing the order returns only "cherry". Every q after
/// the first is silently discarded, so a client that zipped the reply back onto its
/// inputs would mislabel every cue. The advice online that q may be repeated describes
/// Google's paid Cloud Translation API, which is a different service on a similar host.
/// </para>
/// </remarks>
internal sealed class GoogleTranslateTranslator : ISubtitleTranslator
{
    /// <summary>The name of the configured <see cref="HttpClient"/>.</summary>
    public const string ClientName = "SubtitleCat.GoogleTranslate";

    /// <summary>
    /// Roughly the point where the request line gets long enough to be refused, once the
    /// text is percent-encoded. Kept well clear of it.
    /// </summary>
    private const int MaxCharactersPerRequest = 1200;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleTranslateTranslator> _logger;
    private readonly TranslationThrottle _throttle = new();

    /// <summary>Initializes a new instance of the <see cref="GoogleTranslateTranslator"/> class.</summary>
    public GoogleTranslateTranslator(IHttpClientFactory httpClientFactory, ILogger<GoogleTranslateTranslator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Google Translate";

    /// <inheritdoc />
    public TranslatorKind Kind => TranslatorKind.GoogleTranslate;

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> segments,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            return Array.Empty<string>();
        }

        var output = new List<string>(segments.Count);
        var chunk = new List<string>();
        var chunkLength = 0;

        foreach (var segment in segments)
        {
            // A single line longer than the budget still has to go out on its own.
            if (chunk.Count > 0 && chunkLength + segment.Length + 1 > MaxCharactersPerRequest)
            {
                output.AddRange(await SendAsync(chunk, sourceLanguage, targetLanguage, cancellationToken)
                    .ConfigureAwait(false));
                chunk = new List<string>();
                chunkLength = 0;
            }

            chunk.Add(segment);
            chunkLength += segment.Length + 1;
        }

        if (chunk.Count > 0)
        {
            output.AddRange(await SendAsync(chunk, sourceLanguage, targetLanguage, cancellationToken)
                .ConfigureAwait(false));
        }

        return output;
    }

    private async Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> segments,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Config;

        await _throttle
            .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, config.TranslationRequestDelayMs)), cancellationToken)
            .ConfigureAwait(false);

        var joined = string.Join('\n', segments);
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "https://translate.googleapis.com/translate_a/single?client=gtx&sl={0}&tl={1}&dt=t&q={2}",
            string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : Uri.EscapeDataString(sourceLanguage),
            Uri.EscapeDataString(targetLanguage),
            Uri.EscapeDataString(joined));

        using var client = _httpClientFactory.CreateClient(ClientName);
        client.Timeout = TimeSpan.FromSeconds(60);

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                "Google is rate limiting this server's address. Wait an hour, raise the delay between "
                + "requests, or use a different translation backend.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "The Google translation endpoint answered {0}.",
                (int)response.StatusCode));
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var translated = ExtractTranslation(body);

        if (translated is null)
        {
            _logger.LogWarning("SubtitleCat: the Google reply could not be read; falling back to single lines.");
            return Array.Empty<string>();
        }

        var lines = translated.Split('\n');

        // Only trusted when the count survives the round trip. When it does not, an
        // empty list tells the caller to retry unambiguously rather than guess which
        // translation belongs to which cue.
        if (lines.Length != segments.Count)
        {
            _logger.LogDebug(
                "SubtitleCat: Google returned {Got} line(s) for {Sent} sent; retrying them individually.",
                lines.Length,
                segments.Count);
            return Array.Empty<string>();
        }

        return lines;
    }

    /// <summary>
    /// Pulls the translated text out of the reply.
    /// </summary>
    /// <remarks>
    /// The reply is a deeply nested, unlabelled array. The first element holds a list of
    /// chunks, each of which has the translated text at index 0; Google splits long input
    /// into its own chunks, so they have to be concatenated to get the text back whole.
    /// </remarks>
    private static string? ExtractTranslation(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var chunks = document.RootElement[0];
            if (chunks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var chunk in chunks.EnumerateArray())
            {
                if (chunk.ValueKind == JsonValueKind.Array
                    && chunk.GetArrayLength() > 0
                    && chunk[0].ValueKind == JsonValueKind.String)
                {
                    builder.Append(chunk[0].GetString());
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
