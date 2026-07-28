using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleCat.Translation;

/// <summary>
/// Translates through a LibreTranslate instance, typically self-hosted.
/// </summary>
/// <remarks>
/// The best-behaved of the three for this job: it takes an array of strings and returns
/// an array in the same order, so a batch needs no separator tricks. Self-hosted it has
/// no quota, no key and no third party seeing what is being watched.
/// </remarks>
internal sealed class LibreTranslateTranslator : ISubtitleTranslator
{
    /// <summary>The name of the configured <see cref="HttpClient"/>.</summary>
    public const string ClientName = "SubtitleCat.LibreTranslate";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LibreTranslateTranslator> _logger;
    private readonly TranslationThrottle _throttle = new();

    /// <summary>Initializes a new instance of the <see cref="LibreTranslateTranslator"/> class.</summary>
    public LibreTranslateTranslator(IHttpClientFactory httpClientFactory, ILogger<LibreTranslateTranslator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LibreTranslate";

    /// <inheritdoc />
    public TranslatorKind Kind => TranslatorKind.LibreTranslate;

    /// <inheritdoc />
    public bool IsConfigured =>
        Uri.TryCreate(Plugin.Config.LibreTranslateUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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

        var config = Plugin.Config;
        if (!Uri.TryCreate(config.LibreTranslateUrl.TrimEnd('/') + "/translate", UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "The LibreTranslate address is not a valid URL. Check the plugin settings.");
        }

        await _throttle
            .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, config.TranslationRequestDelayMs)), cancellationToken)
            .ConfigureAwait(false);

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["q"] = segments,
            ["source"] = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage,
            ["target"] = targetLanguage,

            // "text" rather than "html": the markup has already been lifted off, and the
            // html mode re-escapes characters that then show up literally on screen.
            ["format"] = "text"
        };

        if (!string.IsNullOrWhiteSpace(config.LibreTranslateApiKey))
        {
            payload["api_key"] = config.LibreTranslateApiKey;
        }

        using var client = _httpClientFactory.CreateClient(ClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds * 4, 30, 300));

        using var response = await client.PostAsJsonAsync(uri, payload, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw MapFailure(response.StatusCode, body);
        }

        var result = await response.Content
            .ReadFromJsonAsync<LibreTranslateResponse>(cancellationToken)
            .ConfigureAwait(false);

        var translated = result?.ReadTranslations();
        if (translated is null)
        {
            _logger.LogWarning("SubtitleCat: LibreTranslate returned a reply that could not be read.");
            return Array.Empty<string>();
        }

        return translated;
    }

    /// <summary>
    /// Turns a failure into either a fatal error or a retryable one.
    /// </summary>
    /// <remarks>
    /// The distinction matters for 400. LibreTranslate applies its character limit per
    /// array element but rejects the whole batch when any single one exceeds it, so a
    /// 400 is often one long line rather than anything wrong with the request. Throwing
    /// something retryable lets the caller resend the batch line by line, which isolates
    /// the offending cue instead of losing the other thirty-nine.
    /// </remarks>
    private static Exception MapFailure(HttpStatusCode status, string body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : " " + body.Trim();

        return status switch
        {
            HttpStatusCode.Forbidden => new InvalidOperationException(
                "LibreTranslate rejected the API key." + detail),
            HttpStatusCode.TooManyRequests => new InvalidOperationException(
                "LibreTranslate is rate limiting this server. Raise its limits or slow the plugin down." + detail),
            HttpStatusCode.BadRequest => new HttpRequestException(
                "LibreTranslate rejected the batch, most likely one line over its character limit." + detail),
            _ => new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "LibreTranslate answered {0}.{1}", (int)status, detail))
        };
    }

    /// <summary>
    /// The reply shape. LibreTranslate answers an array request with an array and a
    /// single request with a string, in the same field, so both are accepted.
    /// </summary>
    private sealed class LibreTranslateResponse
    {
        [JsonPropertyName("translatedText")]
        public JsonElement TranslatedText { get; set; }

        public IReadOnlyList<string>? ReadTranslations() => TranslatedText.ValueKind switch
        {
            JsonValueKind.Array => TranslatedText.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .ToList(),
            JsonValueKind.String => new List<string> { TranslatedText.GetString() ?? string.Empty },
            _ => null
        };
    }
}
