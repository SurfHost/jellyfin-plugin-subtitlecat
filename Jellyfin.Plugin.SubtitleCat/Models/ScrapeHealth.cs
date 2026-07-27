using System;
using System.Globalization;

namespace Jellyfin.Plugin.SubtitleCat.Models;

/// <summary>
/// The verdict shown on the configuration page.
/// </summary>
public enum ScrapeHealthState
{
    /// <summary>No search has run yet since the server started.</summary>
    Unknown = 0,

    /// <summary>The last search fetched and parsed pages as expected.</summary>
    Healthy = 1,

    /// <summary>Pages were fetched but nothing could be parsed out of them.</summary>
    ParserStale = 2,

    /// <summary>The site could not be reached, or answered with an error.</summary>
    Unreachable = 3
}

/// <summary>
/// Records what the last scrape did, so a change to subtitlecat's HTML shows up as a
/// red panel on the configuration page instead of an unexplained empty result list.
/// </summary>
/// <remarks>
/// The distinction that matters is between "the site is down" and "the site answered
/// normally but nothing parsed". The second means the page structure moved and the
/// regexes need updating, and without this it is invisible: the provider simply returns
/// no results, exactly as it does for a title nobody has subtitled.
/// </remarks>
public sealed class ScrapeHealth
{
    private readonly object _gate = new();

    private DateTime? _lastAttemptUtc;
    private string? _lastQuery;
    private int _lastStatusCode;
    private int _lastRowsParsed;
    private int _lastDetailPagesParsed;
    private int _lastDownloadLinksFound;
    private string? _lastError;
    private ScrapeHealthState _state = ScrapeHealthState.Unknown;

    /// <summary>Records a search that completed, whether or not it found anything.</summary>
    /// <param name="query">The query that was sent.</param>
    /// <param name="statusCode">The HTTP status of the search request.</param>
    /// <param name="rowsParsed">How many result rows the parser recognised.</param>
    public void RecordSearch(string query, int statusCode, int rowsParsed)
    {
        lock (_gate)
        {
            _lastAttemptUtc = DateTime.UtcNow;
            _lastQuery = query;
            _lastStatusCode = statusCode;
            _lastRowsParsed = rowsParsed;
            _lastDetailPagesParsed = 0;
            _lastDownloadLinksFound = 0;
            _lastError = null;

            // A 200 with zero rows is the tell-tale of a layout change. A genuinely
            // empty result set also lands here, which is why this is a warning rather
            // than an error: one obscure title proves nothing, a run of them does.
            _state = statusCode == 200
                ? (rowsParsed > 0 ? ScrapeHealthState.Healthy : ScrapeHealthState.ParserStale)
                : ScrapeHealthState.Unreachable;
        }
    }

    /// <summary>Records the outcome of parsing the detail pages for the last search.</summary>
    /// <param name="pagesParsed">How many detail pages were fetched and read.</param>
    /// <param name="downloadLinksFound">How many download links were recognised across them.</param>
    public void RecordDetailPages(int pagesParsed, int downloadLinksFound)
    {
        lock (_gate)
        {
            _lastDetailPagesParsed = pagesParsed;
            _lastDownloadLinksFound = downloadLinksFound;

            // Detail pages always list something. Pages that fetch but yield no links at
            // all means the block markup moved, which the search parser would not catch.
            if (_state == ScrapeHealthState.Healthy && pagesParsed > 0 && downloadLinksFound == 0)
            {
                _state = ScrapeHealthState.ParserStale;
            }
        }
    }

    /// <summary>Records a failure to reach the site.</summary>
    /// <param name="query">The query that was being attempted.</param>
    /// <param name="error">A short description of what went wrong.</param>
    public void RecordFailure(string query, string error)
    {
        lock (_gate)
        {
            _lastAttemptUtc = DateTime.UtcNow;
            _lastQuery = query;
            _lastError = error;
            _lastStatusCode = 0;
            _state = ScrapeHealthState.Unreachable;
        }
    }

    /// <summary>Takes a snapshot for the configuration page.</summary>
    public ScrapeHealthReport Snapshot()
    {
        lock (_gate)
        {
            return new ScrapeHealthReport
            {
                State = _state.ToString(),
                Summary = Describe(),
                LastAttemptUtc = _lastAttemptUtc,
                LastQuery = _lastQuery,
                LastStatusCode = _lastStatusCode,
                RowsParsed = _lastRowsParsed,
                DetailPagesParsed = _lastDetailPagesParsed,
                DownloadLinksFound = _lastDownloadLinksFound,
                LastError = _lastError
            };
        }
    }

    private string Describe() => _state switch
    {
        ScrapeHealthState.Healthy => string.Format(
            CultureInfo.InvariantCulture,
            "Last search parsed {0} result{1} and found {2} download link{3} across {4} detail page{5}.",
            _lastRowsParsed,
            _lastRowsParsed == 1 ? string.Empty : "s",
            _lastDownloadLinksFound,
            _lastDownloadLinksFound == 1 ? string.Empty : "s",
            _lastDetailPagesParsed,
            _lastDetailPagesParsed == 1 ? string.Empty : "s"),

        ScrapeHealthState.ParserStale =>
            "subtitlecat answered normally but nothing could be read out of the page. "
            + "That usually means the site changed its HTML and the plugin needs updating. "
            + "If this was a search for an obscure title it may simply have no results.",

        ScrapeHealthState.Unreachable => string.Format(
            CultureInfo.InvariantCulture,
            "Could not reach subtitlecat. {0}",
            _lastError ?? "HTTP " + _lastStatusCode.ToString(CultureInfo.InvariantCulture)),

        _ => "No search has run yet. Use Download subtitles on any movie or episode to test it."
    };
}

/// <summary>A point-in-time view of <see cref="ScrapeHealth"/>, serialised to the config page.</summary>
public sealed class ScrapeHealthReport
{
    /// <summary>Gets the state name, one of the <see cref="ScrapeHealthState"/> values.</summary>
    public required string State { get; init; }

    /// <summary>Gets a sentence describing the state, ready to display.</summary>
    public required string Summary { get; init; }

    /// <summary>Gets when the last search ran, or null if none has.</summary>
    public DateTime? LastAttemptUtc { get; init; }

    /// <summary>Gets the last query sent to the site.</summary>
    public string? LastQuery { get; init; }

    /// <summary>Gets the HTTP status of the last search request.</summary>
    public int LastStatusCode { get; init; }

    /// <summary>Gets how many search result rows were parsed.</summary>
    public int RowsParsed { get; init; }

    /// <summary>Gets how many detail pages were fetched and read.</summary>
    public int DetailPagesParsed { get; init; }

    /// <summary>Gets how many download links were recognised.</summary>
    public int DownloadLinksFound { get; init; }

    /// <summary>Gets the last error message, if the site could not be reached.</summary>
    public string? LastError { get; init; }
}
