using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleCat.Translation;

/// <summary>
/// Translates through the DeepL API.
/// </summary>
/// <remarks>
/// Noticeably better Dutch than the alternatives, at the cost of a key and a quota. The
/// free tier allows 500,000 characters a month, and a feature-length film is roughly
/// 75,000, so about six films before it stops until the month turns over. The API takes
/// an array and answers in the same order, so batching is straightforward.
/// </remarks>
internal sealed class DeepLTranslator : ISubtitleTranslator
{
    /// <summary>The name of the configured <see cref="HttpClient"/>.</summary>
    public const string ClientName = "SubtitleCat.DeepL";

    /// <summary>DeepL caps a single request at 50 texts.</summary>
    private const int MaxTextsPerRequest = 50;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TranslationThrottle _throttle = new();

    /// <summary>Initializes a new instance of the <see cref="DeepLTranslator"/> class.</summary>
    public DeepLTranslator(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "DeepL";

    /// <inheritdoc />
    public TranslatorKind Kind => TranslatorKind.DeepL;

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Config.DeepLApiKey);

    /// <summary>
    /// Gets the API host. A free-tier key ends in ":fx" and must go to the free host;
    /// sending it to the pro host is rejected as an invalid key, which reads like a
    /// mistyped key rather than a wrong endpoint.
    /// </summary>
    private static string Host => Plugin.Config.DeepLApiKey.TrimEnd().EndsWith(":fx", StringComparison.Ordinal)
        ? "https://api-free.deepl.com"
        : "https://api.deepl.com";

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

        if (!IsConfigured)
        {
            throw new InvalidOperationException("No DeepL API key is configured.");
        }

        var config = Plugin.Config;
        var output = new List<string>(segments.Count);

        // Split rather than fail when the caller's batch is larger than DeepL allows, so
        // the batch size on the config page does not have to know each backend's limit.
        for (var offset = 0; offset < segments.Count; offset += MaxTextsPerRequest)
        {
            var slice = segments.Skip(offset).Take(MaxTextsPerRequest).ToList();
            output.AddRange(await SendAsync(slice, sourceLanguage, targetLanguage, config.TranslationRequestDelayMs, cancellationToken)
                .ConfigureAwait(false));
        }

        return output;
    }

    private async Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> segments,
        string? sourceLanguage,
        string targetLanguage,
        int delayMs,
        CancellationToken cancellationToken)
    {
        await _throttle
            .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, delayMs)), cancellationToken)
            .ConfigureAwait(false);

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["text"] = segments,
            ["target_lang"] = targetLanguage.ToUpperInvariant(),

            // The markup is already off, but xml handling also stops DeepL treating a
            // stray angle bracket in dialogue as a tag to be preserved and moved.
            ["tag_handling"] = "xml"
        };

        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            payload["source_lang"] = sourceLanguage.ToUpperInvariant();
        }

        using var client = _httpClientFactory.CreateClient(ClientName);
        client.Timeout = TimeSpan.FromSeconds(120);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            "DeepL-Auth-Key " + Plugin.Config.DeepLApiKey.Trim());

        using var response = await client
            .PostAsJsonAsync(Host + "/v2/translate", payload, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw MapFailure(response.StatusCode, body);
        }

        var result = await response.Content
            .ReadFromJsonAsync<DeepLResponse>(cancellationToken)
            .ConfigureAwait(false);

        return result?.Translations?.Select(t => t.Text ?? string.Empty).ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private static InvalidOperationException MapFailure(HttpStatusCode status, string body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : " " + body.Trim();

        return (int)status switch
        {
            403 => new InvalidOperationException(
                "DeepL rejected the API key. Check it in the plugin settings." + detail),

            // DeepL's own code for a used-up quota, which is the one a free-tier user
            // actually hits, so it is worth naming rather than reporting as "456".
            456 => new InvalidOperationException(
                "The DeepL quota for this month is used up. It resets at the start of next month." + detail),

            429 => new InvalidOperationException(
                "DeepL is rate limiting this server. Increase the delay between requests." + detail),
            413 => new InvalidOperationException(
                "DeepL refused the batch as too large. Lower the batch size in the plugin settings." + detail),
            _ => new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "DeepL answered {0}.{1}", (int)status, detail))
        };
    }

    private sealed class DeepLResponse
    {
        [JsonPropertyName("translations")]
        public IReadOnlyList<DeepLTranslation>? Translations { get; set; }
    }

    private sealed class DeepLTranslation
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
