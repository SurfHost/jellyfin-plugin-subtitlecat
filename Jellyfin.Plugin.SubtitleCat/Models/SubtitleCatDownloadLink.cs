namespace Jellyfin.Plugin.SubtitleCat.Models;

/// <summary>How a language on a detail page can be obtained.</summary>
public enum SubtitleCatLinkKind
{
    /// <summary>Somebody already generated it and the site hosts the file.</summary>
    Stored = 0,

    /// <summary>
    /// Only the original subtitle exists. The site offers a Translate button that runs
    /// in the visitor's browser, so getting this language means fetching the original
    /// and translating it here instead.
    /// </summary>
    Translatable = 1
}

/// <summary>
/// A language listed on a detail page, and the address of the file behind it.
/// </summary>
/// <remarks>
/// Detail pages list every language the site could produce, around 150 of them, but only
/// a handful have actually been generated. Both kinds carry a fetchable address; the
/// difference is what happens after the fetch.
/// </remarks>
public sealed class SubtitleCatDownloadLink
{
    /// <summary>Gets the language code as subtitlecat writes it, for example "nl" or "pt-BR".</summary>
    public required string LanguageCode { get; init; }

    /// <summary>Gets the language name shown next to the flag, for example "Dutch".</summary>
    public required string LanguageName { get; init; }

    /// <summary>
    /// Gets the path to fetch: the finished subtitle for <see cref="SubtitleCatLinkKind.Stored"/>,
    /// or the original that needs translating for <see cref="SubtitleCatLinkKind.Translatable"/>.
    /// </summary>
    /// <remarks>
    /// For a stored subtitle this is read from the anchor and never reconstructed: each
    /// translation lives in its own numeric folder, which differs from the folder of the
    /// page it is listed on. The original, by contrast, always sits in the page's own
    /// folder and is assembled from the Translate button's arguments.
    /// </remarks>
    public required string Href { get; init; }

    /// <summary>Gets whether the file at <see cref="Href"/> is finished or needs translating.</summary>
    public required SubtitleCatLinkKind Kind { get; init; }
}
