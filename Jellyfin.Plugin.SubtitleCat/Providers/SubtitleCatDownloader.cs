using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleCat.Matching;
using Jellyfin.Plugin.SubtitleCat.Models;
using Jellyfin.Plugin.SubtitleCat.Scraping;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleCat.Providers;

/// <summary>
/// Offers subtitlecat.com as a subtitle source for movies and episodes.
/// </summary>
public sealed class SubtitleCatDownloader : ISubtitleProvider, IHasOrder
{
    private readonly SubtitleCatClient _client;
    private readonly ILogger<SubtitleCatDownloader> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleCatDownloader"/> class.</summary>
    public SubtitleCatDownloader(SubtitleCatClient client, ILogger<SubtitleCatDownloader> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubtitleCat";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => new[]
    {
        VideoContentType.Episode,
        VideoContentType.Movie
    };

    /// <summary>
    /// Gets the position among subtitle providers. Sits behind the defaults, since a
    /// scraped machine translation should not outrank a curated source that has the
    /// same title.
    /// </summary>
    public int Order => 100;

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
        SubtitleSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Plugin.Config.Enabled)
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        try
        {
            return await SearchInternalAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Jellyfin cancels the search when the dialog closes. Swallowing this would
            // turn every closed dialog into a logged error.
            throw;
        }
        catch (Exception ex)
        {
            // A provider that throws aborts the whole multi-provider search in the UI,
            // so a failure here degrades to "no results from this one" instead.
            _logger.LogError(ex, "SubtitleCat: search failed for {Name}.", request.Name);
            return Array.Empty<RemoteSubtitleInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        if (!SubtitleCatId.TryDecode(id, out var decoded, out var error) || decoded is null)
        {
            _logger.LogError("SubtitleCat: could not read the subtitle identifier. {Error}", error);
            throw new ArgumentException(error ?? "Unrecognised subtitle identifier.", nameof(id));
        }

        _logger.LogDebug(
            "SubtitleCat: downloading {Language} subtitle from {Href}.",
            decoded.Language,
            decoded.Href);

        var bytes = await _client.DownloadAsync(decoded.Href, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("subtitlecat.com returned an empty subtitle file.");
        }

        // Re-encoded to UTF-8 rather than passed through: a good share of these files are
        // Windows-1252 with no byte-order mark, and handing those to Jellyfin verbatim is
        // what turns every accented character into mojibake on screen.
        var text = SubtitleTextDecoder.Decode(bytes, out var encodingUsed);
        if (!string.Equals(encodingUsed, "utf-8", StringComparison.Ordinal))
        {
            _logger.LogDebug("SubtitleCat: subtitle decoded as {Encoding}.", encodingUsed);
        }

        return new SubtitleResponse
        {
            Format = "srt",
            Language = decoded.Language,
            IsForced = decoded.Forced,
            IsHearingImpaired = decoded.HearingImpaired,
            Stream = new MemoryStream(new UTF8Encoding(false).GetBytes(text))
        };
    }

    private async Task<IEnumerable<RemoteSubtitleInfo>> SearchInternalAsync(
        SubtitleSearchRequest request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Config;

        var wantedLanguage = LanguageCodes.Resolve(request.TwoLetterISOLanguageName, request.Language);
        if (wantedLanguage.Length == 0)
        {
            _logger.LogDebug("SubtitleCat: the request carried no usable language, skipping.");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var rawReleaseName = GetReleaseBasename(request.MediaPath);
        var local = ReleaseName.Parse(rawReleaseName ?? BuildNameFromMetadata(request));

        var queries = SearchQueryBuilder.Build(
            local,
            rawReleaseName,
            request.Name,
            request.SeriesName,
            request.ProductionYear,
            request.ParentIndexNumber,
            request.IndexNumber,
            config.EnableFallbackTitleSearch);

        if (queries.Count == 0)
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var candidates = await CollectCandidatesAsync(queries, local, config, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var results = new List<ScoredResult>();
        var linksFound = 0;
        var pagesRead = 0;

        foreach (var candidate in candidates.Take(Math.Clamp(config.MaxDetailPagesPerSearch, 1, 20)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var links = await _client
                .GetDownloadLinksAsync(candidate.Row.Href, cancellationToken)
                .ConfigureAwait(false);

            pagesRead++;
            linksFound += links.Count;

            foreach (var link in links)
            {
                if (!LanguageCodes.Matches(link.LanguageCode, wantedLanguage, out var isExact))
                {
                    continue;
                }

                // subtitlecat generates everything but the original by machine, so a
                // subtitle counts as native only when the entry was authored in the
                // language being asked for.
                var sourceLanguage = LanguageCodes.FromEnglishName(candidate.Row.SourceLanguageName);
                var machineTranslated = sourceLanguage is null
                    || !string.Equals(sourceLanguage, wantedLanguage, StringComparison.Ordinal);

                if (machineTranslated && !config.IncludeMachineTranslated)
                {
                    continue;
                }

                results.Add(new ScoredResult(candidate.Row, candidate.Score, link, machineTranslated, isExact));
            }
        }

        _client.RecordDetailPageOutcome(pagesRead, linksFound);

        if (results.Count == 0)
        {
            _logger.LogDebug(
                "SubtitleCat: {Count} candidate release(s) matched but none offered {Language}.",
                candidates.Count,
                wantedLanguage);
            return Array.Empty<RemoteSubtitleInfo>();
        }

        return ReleaseMatcher
            .Rank(
                results,
                r => r.MachineTranslated,
                r => r.Score + (r.IsExactLanguage ? 0 : -5),
                r => r.Row.LanguageCount)
            .Select(r => ToRemoteSubtitleInfo(r, request))
            .ToList();
    }

    /// <summary>
    /// Walks the query ladder until one rung produces matches, then returns them ordered
    /// by score. Stopping at the first productive rung keeps the vaguer queries, which
    /// return hundreds of unrelated rows, from being issued at all in the common case.
    /// </summary>
    private async Task<IReadOnlyList<ScoredRow>> CollectCandidatesAsync(
        IReadOnlyList<string> queries,
        ReleaseName local,
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scored = new List<ScoredRow>();

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rows = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (!seen.Add(row.Href))
                {
                    continue;
                }

                var candidate = ReleaseName.Parse(row.Name);
                var score = ReleaseMatcher.Score(local, candidate, config.AllowResolutionMismatch);

                if (score >= config.MinimumMatchScore)
                {
                    scored.Add(new ScoredRow(row, score));
                }
            }

            if (scored.Count > 0)
            {
                break;
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Row.LanguageCount)
            .ToList();
    }

    private RemoteSubtitleInfo ToRemoteSubtitleInfo(ScoredResult result, SubtitleSearchRequest request)
    {
        var name = result.Row.Name;
        var comment = string.Format(
            CultureInfo.InvariantCulture,
            "Release match score {0}. Available in {1} language{2}.",
            result.Score,
            result.Row.LanguageCount,
            result.Row.LanguageCount == 1 ? string.Empty : "s");

        if (result.MachineTranslated)
        {
            var from = result.Row.SourceLanguageName;
            var note = from is null
                ? "machine translated"
                : string.Format(CultureInfo.InvariantCulture, "machine translated from {0}", from);

            name = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name, note);
            comment = string.Format(
                CultureInfo.InvariantCulture,
                "{0} Generated by subtitlecat's automatic translation, not written by a person.",
                comment);
        }

        var id = new SubtitleCatId
        {
            Href = result.Link.Href,

            // Echoed rather than normalised, because Jellyfin names the saved file from
            // it and the server's own configuration decides whether that is dut or nld.
            Language = string.IsNullOrWhiteSpace(request.Language)
                ? result.Link.LanguageCode
                : request.Language
        };

        return new RemoteSubtitleInfo
        {
            Id = id.Encode(),
            Name = name,
            ProviderName = Name,
            Format = "srt",
            ThreeLetterISOLanguageName = id.Language,
            Comment = comment,
            DownloadCount = result.Row.DownloadCount,
            MachineTranslated = result.MachineTranslated,
            AiTranslated = false,
            IsHashMatch = false
        };
    }

    /// <summary>
    /// Gets the release name from a media path. Splits on both separators rather than
    /// using the platform's, because the server runs on Linux while this is developed
    /// and tested on Windows, and the tests have to exercise container paths.
    /// </summary>
    private static string? GetReleaseBasename(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        var lastSeparator = mediaPath.LastIndexOfAny(new[] { '/', '\\' });
        var fileName = lastSeparator >= 0 ? mediaPath[(lastSeparator + 1)..] : mediaPath;

        var lastDot = fileName.LastIndexOf('.');
        if (lastDot > 0)
        {
            fileName = fileName[..lastDot];
        }

        return fileName.Length > 0 ? fileName : null;
    }

    /// <summary>
    /// Builds a stand-in release name from the request metadata, for the case where
    /// Jellyfin supplies no media path.
    /// </summary>
    private static string BuildNameFromMetadata(SubtitleSearchRequest request)
    {
        var builder = new StringBuilder();

        builder.Append(
            !string.IsNullOrWhiteSpace(request.SeriesName) ? request.SeriesName : request.Name);

        if (request.ParentIndexNumber.HasValue && request.IndexNumber.HasValue)
        {
            builder.Append(
                CultureInfo.InvariantCulture,
                $" S{request.ParentIndexNumber.Value:00}E{request.IndexNumber.Value:00}");
        }
        else if (request.ProductionYear.HasValue)
        {
            builder.Append(CultureInfo.InvariantCulture, $" {request.ProductionYear.Value}");
        }

        return builder.ToString().Trim();
    }

    private sealed record ScoredRow(SubtitleCatSearchRow Row, int Score);

    private sealed record ScoredResult(
        SubtitleCatSearchRow Row,
        int Score,
        SubtitleCatDownloadLink Link,
        bool MachineTranslated,
        bool IsExactLanguage);
}
