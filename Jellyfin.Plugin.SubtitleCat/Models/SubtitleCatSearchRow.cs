namespace Jellyfin.Plugin.SubtitleCat.Models;

/// <summary>
/// One row of a subtitlecat search result table.
/// </summary>
public sealed class SubtitleCatSearchRow
{
    /// <summary>Gets the detail page path, as written in the href and still relative.</summary>
    public required string Href { get; init; }

    /// <summary>Gets the release name shown as the link text.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the language the subtitle was originally written in, as the English name
    /// subtitlecat prints after the link ("English" from "(translated from English)").
    /// Null when the row carries no such note.
    /// </summary>
    public string? SourceLanguageName { get; init; }

    /// <summary>
    /// Gets the number of languages this entry is available in. Doubles as a
    /// popularity signal: the more languages already generated, the more likely the
    /// one being searched for is among them.
    /// </summary>
    public int LanguageCount { get; init; }

    /// <summary>Gets the download count shown on the row.</summary>
    public int DownloadCount { get; init; }
}
