using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleCat.Matching;

/// <summary>
/// Scores a candidate release from subtitlecat against the local media file.
/// </summary>
/// <remarks>
/// Subtitles are timed to a particular cut, so the signals that predict whether a
/// subtitle will stay in sync are the release group and the source, not the
/// resolution. A same-group encode at a different resolution is almost always the
/// same cut with the same timings, which is why a 1080p entry is a good answer for a
/// 2160p file and scores well above an unrelated 2160p one.
/// </remarks>
internal static class ReleaseMatcher
{
    /// <summary>Returned instead of a score when a candidate cannot be the same release.</summary>
    public const int Rejected = -1;

    private const int GroupPoints = 40;
    private const int EpisodePoints = 40;
    private const int SourcePoints = 20;
    private const int YearPoints = 15;
    private const int ResolutionExactPoints = 15;
    private const int ResolutionDifferentPoints = 8;
    private const int CodecPoints = 10;
    private const int EditionPoints = 5;
    private const int TitlePoints = 30;

    /// <summary>
    /// Scores <paramref name="candidate"/> against <paramref name="local"/>, or returns
    /// <see cref="Rejected"/> when the two cannot be the same release.
    /// </summary>
    /// <param name="local">The release name of the file in the library.</param>
    /// <param name="candidate">The release name of the subtitlecat entry.</param>
    /// <param name="allowResolutionMismatch">
    /// Whether a different resolution of the same release still counts as a match.
    /// </param>
    public static int Score(ReleaseName local, ReleaseName candidate, bool allowResolutionMismatch)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(candidate);

        var titleOverlap = TitleOverlap(local, candidate);
        if (titleOverlap <= 0)
        {
            return Rejected;
        }

        // A wrong episode is a different piece of content, never a near miss. This is
        // the one check that has to be absolute: an off-by-one episode subtitle looks
        // plausible in the list and is completely wrong on screen.
        if (local.IsEpisode)
        {
            if (!candidate.IsEpisode || local.Season != candidate.Season || local.Episode != candidate.Episode)
            {
                return Rejected;
            }
        }
        else if (candidate.IsEpisode)
        {
            return Rejected;
        }

        // Remakes share a title, so a year that is off by more than one is a different film.
        // One year of slack absorbs the festival-run and release-year disagreements that
        // are common between a scene release and the metadata Jellyfin holds.
        if (local.Year.HasValue && candidate.Year.HasValue && Math.Abs(local.Year.Value - candidate.Year.Value) > 1)
        {
            return Rejected;
        }

        var score = (int)Math.Round(titleOverlap * TitlePoints);

        if (local.IsEpisode)
        {
            score += EpisodePoints;
        }

        if (local.Group is not null && string.Equals(local.Group, candidate.Group, StringComparison.Ordinal))
        {
            score += GroupPoints;
        }

        if (local.Source is not null && string.Equals(local.Source, candidate.Source, StringComparison.Ordinal))
        {
            score += SourcePoints;
        }

        if (local.Year.HasValue && local.Year == candidate.Year)
        {
            score += YearPoints;
        }

        if (local.Resolution.HasValue && candidate.Resolution.HasValue)
        {
            if (local.Resolution == candidate.Resolution)
            {
                score += ResolutionExactPoints;
            }
            else if (allowResolutionMismatch)
            {
                score += ResolutionDifferentPoints;
            }
            else
            {
                return Rejected;
            }
        }

        if (local.Codec is not null && string.Equals(local.Codec, candidate.Codec, StringComparison.Ordinal))
        {
            score += CodecPoints;
        }

        // Only the cut-changing editions matter, and only when they agree. A REPACK
        // subtitle on a non-REPACK file is usually still fine, so this is a small bonus
        // rather than a penalty for disagreeing.
        if (local.EditionFlags.Count > 0 && local.EditionFlags.SetEquals(candidate.EditionFlags))
        {
            score += EditionPoints;
        }

        return score;
    }

    /// <summary>
    /// Gets the fraction of the local title's significant words that the candidate also
    /// carries. Compared against the candidate's full token list rather than only its
    /// title, because a candidate whose name starts with a language or group prefix
    /// still names the title further along.
    /// </summary>
    private static double TitleOverlap(ReleaseName local, ReleaseName candidate)
    {
        var localTitle = local.SignificantTitleTokens;
        if (localTitle.Count == 0)
        {
            // Nothing to compare on. Let the other signals decide rather than rejecting,
            // which is what happens for a file named only by episode code.
            return local.IsEpisode ? 1d : 0d;
        }

        var candidateTokens = new HashSet<string>(candidate.Tokens, StringComparer.Ordinal);
        var matched = localTitle.Count(token => candidateTokens.Contains(token));

        // Every significant word has to be present. Partial title matches are how
        // "Dune" ends up matching "Dune Part Two", which is a different film.
        return matched == localTitle.Count ? 1d : 0d;
    }

    /// <summary>
    /// Orders scored candidates the way the subtitle dialog should present them: native
    /// subtitles before machine translations, and inside each group the closest release
    /// first, then the entry offering the most languages as a tie-break, since that is
    /// the most actively maintained upload.
    /// </summary>
    public static IEnumerable<T> Rank<T>(
        IEnumerable<T> items,
        Func<T, bool> isMachineTranslated,
        Func<T, int> score,
        Func<T, int> languageCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isMachineTranslated);
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(languageCount);

        return items
            .OrderBy(item => isMachineTranslated(item) ? 1 : 0)
            .ThenByDescending(score)
            .ThenByDescending(languageCount);
    }
}
