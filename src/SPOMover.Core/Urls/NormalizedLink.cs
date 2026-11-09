using System.Text;

namespace SPOMover.Core.Urls;

/// <summary>
/// A link in the form used for prefix comparison (RUL-3): lower case, percent-encoding decoded
/// (UTF-8, so Cyrillic matches in both spellings), backslashes turned into slashes, the file: scheme
/// dropped. Keeps a map back to the original text so the unmatched tail can be copied verbatim (RUL-5).
/// </summary>
public sealed class NormalizedLink
{
    private readonly int[] _offsets;

    private NormalizedLink(string original, string value, int[] offsets)
    {
        Original = original;
        Value = value;
        _offsets = offsets;
    }

    public string Original { get; }

    public string Value { get; }

    /// <summary>Index in <see cref="Original"/> where normalized char <paramref name="index"/> starts.</summary>
    public int OriginalIndex(int index) => _offsets[index];

    public static NormalizedLink Create(string original)
    {
        var sb = new StringBuilder(original.Length);
        var offsets = new List<int>(original.Length + 1);

        int i = SkipFileScheme(original);
        while (i < original.Length)
        {
            if (original[i] == '%' && TryDecodePercentRun(original, i, sb, offsets, out int next))
            {
                i = next;
                continue;
            }
            Append(sb, offsets, original[i], i);
            i++;
        }
        offsets.Add(original.Length);
        return new NormalizedLink(original, sb.ToString(), offsets.ToArray());
    }

    public static string Normalize(string original) => Create(original).Value;

    private static void Append(StringBuilder sb, List<int> offsets, char c, int position)
    {
        offsets.Add(position);
        sb.Append(c == '\\' ? '/' : char.ToLowerInvariant(c));
    }

    /// <summary>
    /// file:///C:/x → C:/x; file:///\\server\x and file:////server/x → \\server\x; file://server/x → //server/x.
    /// </summary>
    private static int SkipFileScheme(string s)
    {
        if (!s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return 0;

        int i = 5;
        int slashes = 0;
        while (i + slashes < s.Length && LinkClassifier.IsSlash(s[i + slashes]))
            slashes++;

        int afterSlashes = i + slashes;
        bool driveFollows = afterSlashes + 1 < s.Length && char.IsAsciiLetter(s[afterSlashes]) && s[afterSlashes + 1] == ':';
        if (driveFollows)
            return afterSlashes;
        if (slashes >= 4)
            return afterSlashes - 2; // keep two slashes of the UNC prefix
        if (slashes == 2)
            return i; // file://server/share → //server/share
        return afterSlashes - Math.Min(slashes, 2);
    }

    private static bool TryDecodePercentRun(string s, int start, StringBuilder sb, List<int> offsets, out int next)
    {
        var bytes = new List<byte>();
        var positions = new List<int>();
        int j = start;
        while (j + 2 < s.Length && s[j] == '%' && char.IsAsciiHexDigit(s[j + 1]) && char.IsAsciiHexDigit(s[j + 2]))
        {
            bytes.Add(Convert.ToByte(s.Substring(j + 1, 2), 16));
            positions.Add(j);
            j += 3;
        }
        next = j;
        if (bytes.Count == 0)
            return false;

        int k = 0;
        while (k < bytes.Count)
        {
            int len = Utf8SequenceLength(bytes[k]);
            if (len > 0 && k + len <= bytes.Count && IsValidContinuation(bytes, k + 1, len - 1))
            {
                string decoded = Encoding.UTF8.GetString(bytes.GetRange(k, len).ToArray());
                foreach (char c in decoded)
                    Append(sb, offsets, c, positions[k]);
                k += len;
            }
            else
            {
                // Not valid UTF-8: keep the escape as text so it still compares consistently.
                int p = positions[k];
                for (int t = 0; t < 3; t++)
                    Append(sb, offsets, s[p + t], p + t);
                k++;
            }
        }
        return true;
    }

    private static int Utf8SequenceLength(byte lead) => lead switch
    {
        < 0x80 => 1,
        >= 0xC2 and < 0xE0 => 2,
        >= 0xE0 and < 0xF0 => 3,
        >= 0xF0 and < 0xF5 => 4,
        _ => 0,
    };

    private static bool IsValidContinuation(List<byte> bytes, int from, int count)
    {
        for (int i = from; i < from + count; i++)
        {
            if ((bytes[i] & 0xC0) != 0x80)
                return false;
        }
        return true;
    }
}
