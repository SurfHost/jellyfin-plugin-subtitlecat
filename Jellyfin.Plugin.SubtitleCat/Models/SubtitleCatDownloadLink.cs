namespace Jellyfin.Plugin.SubtitleCat.Models;

/// <summary>
/// A subtitle on a detail page that has actually been generated and can be downloaded.
/// </summary>
/// <remarks>
/// Detail pages list every language subtitlecat could translate into, but only the ones
/// somebody already generated carry a download link. The rest are buttons that run a
/// translation in the visitor's browser, which a server-side plugin cannot do, so those
/// never become instances of this type.
/// </remarks>
public sealed class SubtitleCatDownloadLink
{
    /// <summary>Gets the language code as subtitlecat writes it, for example "nl" or "pt-BR".</summary>
    public required string LanguageCode { get; init; }

    /// <summary>Gets the language name shown next to the flag, for example "Dutch".</summary>
    public required string LanguageName { get; init; }

    /// <summary>
    /// Gets the download path exactly as written in the href.
    /// </summary>
    /// <remarks>
    /// Each translation lives in its own numeric folder, which is not the folder of the
    /// detail page it is listed on, so this path cannot be reconstructed and has to be
    /// carried through from the page.
    /// </remarks>
    public required string Href { get; init; }
}
