using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleCat.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the provider returns results.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the base URL of the site. Exposed only as an escape hatch for the
    /// day subtitlecat moves domain; there is no reason to change it otherwise.
    /// </summary>
    public string BaseUrl { get; set; } = "https://www.subtitlecat.com";

    /// <summary>
    /// Gets or sets the User-Agent sent with every request. A real browser string,
    /// not a disguise: the site serves plain HTML to anything, and identifying as a
    /// current browser simply avoids being mistaken for a broken client.
    /// </summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Gets or sets the minimum gap between outbound requests, in milliseconds.
    /// One search can fan out into several detail-page fetches, so this is what
    /// keeps a single subtitle search from arriving as a burst.
    /// </summary>
    public int RequestDelayMs { get; set; } = 1200;

    /// <summary>Gets or sets the per-request timeout, in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets how long fetched pages stay cached, in minutes. Users typically
    /// search, look at the list, and search again; caching keeps that from being
    /// three round trips to the same pages.
    /// </summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets how many detail pages a single search may fetch. Availability of
    /// any given language varies per release entry, so checking several candidates is
    /// what makes the difference between a hit and an empty list. Costs one request each.
    /// </summary>
    public int MaxDetailPagesPerSearch { get; set; } = 8;

    /// <summary>
    /// Gets or sets the score a candidate release must reach to be considered a match.
    /// See <see cref="Matching.ReleaseMatcher"/> for what the points mean.
    /// </summary>
    public int MinimumMatchScore { get; set; } = 40;

    /// <summary>
    /// Gets or sets a value indicating whether a subtitle for a different resolution of
    /// the same release counts as a match. Subtitles are timed to the cut, not the
    /// resolution, so a 1080p entry from the same group usually fits a 2160p file exactly.
    /// </summary>
    public bool AllowResolutionMismatch { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether machine-translated subtitles are returned.
    /// Nearly everything subtitlecat holds in a language other than the original is
    /// machine translated, so turning this off leaves very little.
    /// </summary>
    public bool IncludeMachineTranslated { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a search that finds nothing under the
    /// release name retries with the title and year (or series and episode number).
    /// </summary>
    public bool EnableFallbackTitleSearch { get; set; } = true;
}
