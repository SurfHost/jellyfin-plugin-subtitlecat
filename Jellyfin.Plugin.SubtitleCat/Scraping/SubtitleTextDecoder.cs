using System;
using System.Text;

namespace Jellyfin.Plugin.SubtitleCat.Scraping;

/// <summary>
/// Turns downloaded subtitle bytes into text, guessing the encoding when it is not declared.
/// </summary>
/// <remarks>
/// Subtitles are old files from everywhere and a good share of them are Windows-1252
/// with no byte-order mark. Decoding those as UTF-8 is what produces the classic
/// mojibake where an accented character arrives as two. Windows-1252 itself is not in
/// the shared framework, and pulling in System.Text.Encoding.CodePages just for it would
/// mean shipping a second assembly, so it is reconstructed here from Latin-1 plus the
/// 32 code points where the two differ.
/// </remarks>
internal static class SubtitleTextDecoder
{
    /// <summary>
    /// The Windows-1252 assignments for 0x80 to 0x9F, where ISO-8859-1 has control
    /// characters instead. Everything outside this range is identical in both.
    /// The five slots Windows-1252 leaves undefined (0x81, 0x8D, 0x8F, 0x90, 0x9D) keep
    /// their raw code point rather than being dropped, so a file that is neither
    /// encoding still round-trips instead of losing bytes.
    /// </summary>
    private static readonly char[] Cp1252Overrides =
    {
        '€', '', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '', 'Ž', '',
        '', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '', 'ž', 'Ÿ'
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes subtitle bytes to text.</summary>
    /// <param name="bytes">The downloaded file.</param>
    /// <param name="encodingUsed">The name of the encoding that was applied.</param>
    public static string Decode(byte[] bytes, out string encodingUsed)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
        {
            encodingUsed = "empty";
            return string.Empty;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingUsed = "utf-8 (bom)";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingUsed = "utf-16le (bom)";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingUsed = "utf-16be (bom)";
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            var text = StrictUtf8.GetString(bytes);
            encodingUsed = "utf-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
            encodingUsed = "windows-1252";
            return DecodeCp1252(bytes);
        }
    }

    private static string DecodeCp1252(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i] = b is >= 0x80 and <= 0x9F ? Cp1252Overrides[b - 0x80] : (char)b;
        }

        return new string(chars);
    }
}
