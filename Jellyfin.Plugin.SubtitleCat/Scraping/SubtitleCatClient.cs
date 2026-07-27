using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleCat.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleCat.Scraping;

/// <summary>
/// Fetches and parses subtitlecat pages, throttled, cached and health-tracked.
/// </summary>
public sealed class SubtitleCatClient
{
    /// <summary>The name of the configured <see cref="HttpClient"/>.</summary>
    public const string ClientName = "SubtitleCat";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubtitleCatClient> _logger;
    private readonly ScrapeHealth _health;

    private readonly TimedCache<string> _pageCache = new(128);
    private readonly SemaphoreSlim _throttleGate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="SubtitleCatClient"/> class.</summary>
    public SubtitleCatClient(
        IHttpClientFactory httpClientFactory,
        ILogger<SubtitleCatClient> logger,
        ScrapeHealth health)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _health = health;
    }

    /// <summary>Runs a search and returns the parsed result rows.</summary>
    /// <param name="query">The text to search for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<SubtitleCatSearchRow>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        var url = string.Concat(
            config.BaseUrl.TrimEnd('/'),
            "/index.php?search=",
            Uri.EscapeDataString(query));

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _health.RecordFailure(query, "The configured base URL is not a valid address.");
            return Array.Empty<SubtitleCatSearchRow>();
        }

        string? html;
        try
        {
            html = await GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SubtitleCat: search request for {Query} failed.", query);
            _health.RecordFailure(query, ex.Message);
            return Array.Empty<SubtitleCatSearchRow>();
        }

        if (html is null)
        {
            return Array.Empty<SubtitleCatSearchRow>();
        }

        var rows = SearchResultParser.Parse(html);
        _health.RecordSearch(query, 200, rows.Count);

        if (rows.Count == 0)
        {
            _logger.LogDebug(
                "SubtitleCat: search for {Query} returned a {Length} byte page with no parsable rows.",
                query,
                html.Length);
        }

        return rows;
    }

    /// <summary>Fetches a detail page and returns the subtitles that can actually be downloaded.</summary>
    /// <param name="href">The detail page href, as it appeared in the search results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<SubtitleCatDownloadLink>> GetDownloadLinksAsync(
        string href,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        var uri = SubtitleCatUrl.Combine(config.BaseUrl, href);
        if (uri is null || !IsOnSite(uri, config.BaseUrl))
        {
            _logger.LogWarning("SubtitleCat: skipping detail link {Href}, which does not resolve onto the site.", href);
            return Array.Empty<SubtitleCatDownloadLink>();
        }

        string? html;
        try
        {
            html = await GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SubtitleCat: could not fetch detail page {Uri}.", uri);
            return Array.Empty<SubtitleCatDownloadLink>();
        }

        if (html is null)
        {
            return Array.Empty<SubtitleCatDownloadLink>();
        }

        var links = DetailPageParser.Parse(html);

        // A page with no language blocks at all means the markup moved. A page with
        // blocks but no links is the ordinary case of a title nobody has translated.
        if (DetailPageParser.CountBlocks(html) == 0)
        {
            _logger.LogDebug("SubtitleCat: detail page {Uri} had no recognisable language blocks.", uri);
        }

        return links;
    }

    /// <summary>Downloads a subtitle file.</summary>
    /// <param name="href">The download href read from a detail page.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file bytes.</returns>
    /// <exception cref="InvalidOperationException">The href does not resolve onto the site.</exception>
    public async Task<byte[]> DownloadAsync(string href, CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        var uri = SubtitleCatUrl.Combine(config.BaseUrl, href);

        // This href travelled here inside a subtitle id, and it originally came out of
        // scraped HTML. Without this check, a compromised or spoofed page could aim an
        // authenticated download at anything the server can reach, which on a home
        // network is every other box on the subnet.
        if (uri is null || !IsOnSite(uri, config.BaseUrl))
        {
            _logger.LogError("SubtitleCat: refusing to download {Href}, which is not on the configured site.", href);
            throw new InvalidOperationException(
                "The subtitle download address is not on subtitlecat.com and was refused.");
        }

        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        using var client = CreateClient(config);
        using var response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Empties the page cache.</summary>
    public void ClearCache() => _pageCache.Clear();

    /// <summary>
    /// Records how many detail pages a search read and how many download links came out
    /// of them, so the health panel can tell a layout change apart from a title nobody
    /// has subtitled yet.
    /// </summary>
    /// <param name="pagesRead">How many detail pages were fetched.</param>
    /// <param name="linksFound">How many download links were recognised across them.</param>
    public void RecordDetailPageOutcome(int pagesRead, int linksFound) =>
        _health.RecordDetailPages(pagesRead, linksFound);

    /// <summary>Gets a snapshot of the scrape health, for the configuration page.</summary>
    public ScrapeHealthReport GetHealth() => _health.Snapshot();

    /// <summary>
    /// Determines whether a URI points at the configured site. Compared on host rather
    /// than on a prefix, so a lookalike host cannot pass by embedding the real one.
    /// </summary>
    private static bool IsOnSite(Uri uri, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var root))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        var key = uri.AbsoluteUri;

        if (_pageCache.TryGet(key, out var cached) && cached is not null)
        {
            return cached;
        }

        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        using var client = CreateClient(config);
        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "SubtitleCat: {Uri} answered {Status}.",
                uri,
                (int)response.StatusCode);
            _health.RecordFailure(
                uri.AbsoluteUri,
                string.Format(CultureInfo.InvariantCulture, "HTTP {0}.", (int)response.StatusCode));
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _pageCache.Set(key, html, TimeSpan.FromMinutes(Math.Max(0, config.CacheMinutes)));
        return html;
    }

    /// <summary>
    /// Holds outbound requests to one every <see cref="Configuration.PluginConfiguration.RequestDelayMs"/>.
    /// A single subtitle search fans out into several detail-page fetches, so without
    /// this the site would see each search arrive as a burst.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, Plugin.Config.RequestDelayMs));
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await _throttleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var since = DateTime.UtcNow - _lastRequestUtc;
            if (since < delay)
            {
                await Task.Delay(delay - since, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _throttleGate.Release();
        }
    }

    private HttpClient CreateClient(Configuration.PluginConfiguration config)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 120));

        if (!string.IsNullOrWhiteSpace(config.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
        }

        return client;
    }
}
