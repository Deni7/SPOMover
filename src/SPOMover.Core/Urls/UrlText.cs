using System.Text;

namespace SPOMover.Core.Urls;

public static class UrlText
{
    public static bool ContainsPercentEscape(string s)
    {
        for (int i = 0; i + 2 < s.Length; i++)
        {
            if (s[i] == '%' && char.IsAsciiHexDigit(s[i + 1]) && char.IsAsciiHexDigit(s[i + 2]))
                return true;
        }
        return false;
    }

    /// <summary>True if the text has characters that a well-formed URL would carry percent-encoded.</summary>
    public static bool ContainsRawUnsafe(string s)
    {
        foreach (char c in s)
        {
            if (IsUnsafe(c))
                return true;
        }
        return false;
    }

    /// <summary>Percent-decodes UTF-8 escapes; invalid escapes stay as they are.</summary>
    public static string Decode(string s) => Uri.UnescapeDataString(s);

    /// <summary>
    /// Percent-encodes spaces, non-ASCII and other characters not allowed in a URL.
    /// Existing escapes and URL delimiters (/ ? # &amp; = etc.) are left alone.
    /// </summary>
    public static string EncodeUnsafe(string s) => Encode(s, encodeAscii: true, encodeNonAscii: true);

    /// <param name="encodeAscii">Encode spaces and ASCII characters not allowed in a URL.</param>
    /// <param name="encodeNonAscii">Encode Cyrillic and other non-ASCII characters (as UTF-8).</param>
    public static string Encode(string s, bool encodeAscii, bool encodeNonAscii)
    {
        var sb = new StringBuilder(s.Length + 16);
        Span<byte> buffer = stackalloc byte[4];
        foreach (Rune rune in s.EnumerateRunes())
        {
            bool encode = rune.IsAscii ? encodeAscii && IsUnsafe((char)rune.Value) : encodeNonAscii;
            if (!encode)
            {
                sb.Append(rune.ToString());
                continue;
            }
            int n = rune.EncodeToUtf8(buffer);
            for (int i = 0; i < n; i++)
                sb.Append('%').Append(buffer[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// How a link spells special characters, so the new prefix is written the same way (RUL-5).
    /// Excel itself writes spaces as %20 and Cyrillic as is; that is the default when the link shows neither.
    /// </summary>
    public static (bool EncodeAscii, bool EncodeNonAscii) DetectStyle(string link)
    {
        bool rawAscii = false, rawNonAscii = false, encodedNonAscii = false;
        for (int i = 0; i < link.Length; i++)
        {
            char c = link[i];
            if (c == '%' && i + 2 < link.Length && char.IsAsciiHexDigit(link[i + 1]) && char.IsAsciiHexDigit(link[i + 2]))
            {
                encodedNonAscii |= link[i + 1] is >= '8' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
                i += 2;
            }
            else if (c >= 0x80)
            {
                rawNonAscii = true;
            }
            else if (IsUnsafe(c))
            {
                rawAscii = true;
            }
        }
        return (!rawAscii, encodedNonAscii && !rawNonAscii);
    }

    internal static bool IsUnsafe(char c) =>
        c <= 0x20 || c >= 0x7F || c is '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}';
}
