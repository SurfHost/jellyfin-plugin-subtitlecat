using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleCat.Scraping;

/// <summary>
/// Reconciles the language codes Jellyfin asks with the ones subtitlecat writes.
/// </summary>
/// <remarks>
/// Jellyfin hands the provider an ISO 639-2 code such as "dut" plus a two-letter code
/// such as "nl". subtitlecat labels its flags with two-letter codes, with a handful of
/// regional variants ("pt-BR", "zh-CN") and a few codes that were renamed decades ago
/// but survive in HTML ("iw" for Hebrew, "in" for Indonesian).
/// </remarks>
internal static class LanguageCodes
{
    /// <summary>
    /// ISO 639-2 to two-letter, covering both the bibliographic and terminological
    /// forms, since Jellyfin may send either depending on where the request came from.
    /// </summary>
    private static readonly Dictionary<string, string> ThreeToTwo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dut"] = "nl",
        ["nld"] = "nl",
        ["eng"] = "en",
        ["ger"] = "de",
        ["deu"] = "de",
        ["fre"] = "fr",
        ["fra"] = "fr",
        ["spa"] = "es",
        ["ita"] = "it",
        ["por"] = "pt",
        ["rus"] = "ru",
        ["pol"] = "pl",
        ["swe"] = "sv",
        ["nor"] = "no",
        ["nob"] = "no",
        ["nno"] = "no",
        ["dan"] = "da",
        ["fin"] = "fi",
        ["ice"] = "is",
        ["isl"] = "is",
        ["cze"] = "cs",
        ["ces"] = "cs",
        ["slo"] = "sk",
        ["slk"] = "sk",
        ["hun"] = "hu",
        ["rum"] = "ro",
        ["ron"] = "ro",
        ["bul"] = "bg",
        ["gre"] = "el",
        ["ell"] = "el",
        ["tur"] = "tr",
        ["ara"] = "ar",
        ["heb"] = "he",
        ["per"] = "fa",
        ["fas"] = "fa",
        ["hin"] = "hi",
        ["ind"] = "id",
        ["jpn"] = "ja",
        ["kor"] = "ko",
        ["chi"] = "zh",
        ["zho"] = "zh",
        ["tha"] = "th",
        ["vie"] = "vi",
        ["ukr"] = "uk",
        ["srp"] = "sr",
        ["hrv"] = "hr",
        ["slv"] = "sl",
        ["est"] = "et",
        ["lav"] = "lv",
        ["lit"] = "lt",
        ["cat"] = "ca",
        ["baq"] = "eu",
        ["eus"] = "eu",
        ["glg"] = "gl",
        ["fil"] = "tl",
        ["tgl"] = "tl",
        ["may"] = "ms",
        ["msa"] = "ms"
    };

    /// <summary>Codes subtitlecat still writes in their pre-1989 form.</summary>
    private static readonly Dictionary<string, string> Deprecated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iw"] = "he",
        ["in"] = "id",
        ["ji"] = "yi",
        ["fil"] = "tl",
        ["sh"] = "sr"
    };

    /// <summary>
    /// Reduces a subtitlecat language code to the base two-letter code used for matching,
    /// so "pt-BR" and "pt" both answer a request for Portuguese.
    /// </summary>
    /// <param name="code">A code as written in a flag's alt attribute.</param>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var trimmed = code.Trim();
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        var baseCode = (dash > 0 ? trimmed[..dash] : trimmed).ToLowerInvariant();

        return Deprecated.TryGetValue(baseCode, out var modern) ? modern : baseCode;
    }

    /// <summary>
    /// Gets the two-letter code to search for, preferring the one Jellyfin supplied and
    /// falling back to a lookup when only the three-letter form is present.
    /// </summary>
    /// <param name="twoLetter">The value of the request's TwoLetterISOLanguageName.</param>
    /// <param name="threeLetter">The value of the request's Language.</param>
    public static string Resolve(string? twoLetter, string? threeLetter)
    {
        if (!string.IsNullOrWhiteSpace(twoLetter))
        {
            return Normalize(twoLetter);
        }

        if (!string.IsNullOrWhiteSpace(threeLetter)
            && ThreeToTwo.TryGetValue(threeLetter.Trim(), out var mapped))
        {
            return mapped;
        }

        // An unmapped three-letter code is more useful passed through than dropped:
        // a few subtitlecat codes are three letters already.
        return Normalize(threeLetter);
    }

    /// <summary>
    /// Determines whether a subtitlecat code answers a request for the wanted language.
    /// </summary>
    /// <param name="siteCode">The code from the flag's alt attribute.</param>
    /// <param name="wanted">The normalised two-letter code being searched for.</param>
    /// <param name="isExact">
    /// Set to true when the codes match outright, false when this is a regional variant
    /// such as "pt-BR" answering a request for "pt". Exact matches are offered first.
    /// </param>
    public static bool Matches(string? siteCode, string wanted, out bool isExact)
    {
        isExact = false;
        if (string.IsNullOrWhiteSpace(siteCode) || string.IsNullOrWhiteSpace(wanted))
        {
            return false;
        }

        var normalizedSite = Normalize(siteCode);
        if (!string.Equals(normalizedSite, wanted, StringComparison.Ordinal))
        {
            return false;
        }

        isExact = string.Equals(siteCode.Trim(), wanted, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// Maps an English language name, as printed in "(translated from Dutch)", onto a
    /// two-letter code. Used to tell a subtitle written in the target language apart
    /// from one machine translated into it.
    /// </summary>
    /// <param name="name">The English name of the language.</param>
    public static string? FromEnglishName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "dutch" => "nl",
            "english" => "en",
            "german" => "de",
            "french" => "fr",
            "spanish" => "es",
            "italian" => "it",
            "portuguese" => "pt",
            "portuguese (brazil)" => "pt",
            "russian" => "ru",
            "polish" => "pl",
            "swedish" => "sv",
            "norwegian" => "no",
            "danish" => "da",
            "finnish" => "fi",
            "czech" => "cs",
            "slovak" => "sk",
            "hungarian" => "hu",
            "romanian" => "ro",
            "bulgarian" => "bg",
            "greek" => "el",
            "turkish" => "tr",
            "arabic" => "ar",
            "hebrew" => "he",
            "persian" => "fa",
            "hindi" => "hi",
            "indonesian" => "id",
            "japanese" => "ja",
            "korean" => "ko",
            "chinese" => "zh",
            "chinese (simplified)" => "zh",
            "chinese (traditional)" => "zh",
            "thai" => "th",
            "vietnamese" => "vi",
            "ukrainian" => "uk",
            "serbian" => "sr",
            "croatian" => "hr",
            "slovenian" => "sl",
            _ => null
        };
    }
}
