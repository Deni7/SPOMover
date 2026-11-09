using System.Text;
using System.Text.RegularExpressions;
using SPOMover.Core.Localization;

namespace SPOMover.Core.Tests;

/// <summary>
/// The UI text is English in the code and translated through <see cref="L"/>. These tests read the sources: every
/// <c>T("…")</c> / <c>T($"…")</c> key has a Ukrainian translation, translations keep the placeholders of their key,
/// and no Ukrainian text is left outside the dictionaries (a line may opt out with an <c>i18n-ignore</c> comment).
/// </summary>
public sealed partial class LocalizationTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void InterpolatedKeyNumbersHolesAndKeepsFormats()
    {
        long n = 1234;
        string name = "a";
        var text = Build($"{n:N0} files in {name,5} {{x}}");
        Assert.Equal("{0:N0} files in {1,5} {{x}}", text.Key);
        Assert.Equal("1,234 files in     a {x}", text.ToString(L.English));
    }

    private static L.Text Build(L.Text text) => text;

    [Fact]
    public void UkrainianTranslationsKeepPlaceholders()
    {
        var bad = L.Table(L.Ukrainian)
            .Where(p => !Placeholders(p.Key).SequenceEqual(Placeholders(p.Value)))
            .Select(p => $"{p.Key} → {p.Value}")
            .ToList();
        Assert.True(bad.Count == 0, "Placeholders differ:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryKeyInTheSourcesHasAUkrainianTranslation()
    {
        var table = L.Table(L.Ukrainian);
        Assert.NotEmpty(table);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (file, key) in SourceKeys())
        {
            if (!table.ContainsKey(key))
                missing.Add($"{Path.GetRelativePath(Root, file)}: {key}");
        }
        Assert.True(missing.Count == 0, $"{missing.Count} keys without a Ukrainian translation:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void NoUkrainianTextOutsideTheDictionaries()
    {
        var found = new List<string>();
        foreach (string file in SourceFiles())
        {
            int lineNo = 0;
            bool inBlock = false;
            string blockEnd = string.Empty;
            foreach (string line in File.ReadLines(file))
            {
                lineNo++;
                if (line.Contains("i18n-ignore", StringComparison.Ordinal))
                    continue;
                string code = StripComments(line, ref inBlock, ref blockEnd);
                if (Cyrillic().IsMatch(code))
                    found.Add($"{Path.GetRelativePath(Root, file)}:{lineNo}: {line.Trim()}");
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} lines with untranslated text:\n" + string.Join("\n", found.Take(200)));
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> SourceFiles() =>
        new[] { "src", "tools" }
            .Select(d => Path.Combine(Root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<(string File, string Key)> SourceKeys()
    {
        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match m in CallRegex().Matches(text))
                yield return (file, m.Groups[1].Value == "$" ? InterpolatedKey(m.Groups[2].Value) : Unescape(m.Groups[2].Value));
        }
    }

    /// <summary>The key <see cref="L.Text"/> builds for the body of <c>$"…"</c>.</summary>
    internal static string InterpolatedKey(string body)
    {
        var key = new StringBuilder();
        var literal = new StringBuilder();
        int hole = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '{' && i + 1 < body.Length && body[i + 1] == '{')
            {
                literal.Append("{{");
                i++;
            }
            else if (c == '}' && i + 1 < body.Length && body[i + 1] == '}')
            {
                literal.Append("}}");
                i++;
            }
            else if (c == '{')
            {
                key.Append(Unescape(literal.ToString()));
                literal.Clear();
                int depth = 0, j = i + 1, comma = -1, colon = -1;
                for (; j < body.Length; j++)
                {
                    char d = body[j];
                    if (d is '(' or '[' or '{')
                        depth++;
                    else if (d is ')' or ']')
                        depth--;
                    else if (d == '}')
                    {
                        if (depth == 0)
                            break;
                        depth--;
                    }
                    else if (depth == 0 && d == ',' && comma < 0 && colon < 0)
                        comma = j;
                    else if (depth == 0 && d == ':' && colon < 0)
                        colon = j;
                }
                key.Append('{').Append(hole++);
                if (comma >= 0)
                    key.Append(',').Append(body[(comma + 1)..(colon >= 0 ? colon : j)].Trim());
                if (colon >= 0)
                    key.Append(':').Append(Unescape(body[(colon + 1)..j]));
                key.Append('}');
                i = j;
            }
            else
            {
                literal.Append(c);
            }
        }
        key.Append(Unescape(literal.ToString()));
        return key.ToString();
    }

    private static string Unescape(string s) => s.Contains('\\') ? Regex.Unescape(s) : s;

    private static IEnumerable<string> Placeholders(string s) =>
        PlaceholderRegex().Matches(s.Replace("{{", "").Replace("}}", "")).Select(m => m.Value).Order(StringComparer.Ordinal);

    /// <summary>The code of a line without comments (<c>//</c>, <c>/* */</c>, <c>@* *@</c>, <c>&lt;!-- --&gt;</c>).</summary>
    private static string StripComments(string line, ref bool inBlock, ref string blockEnd)
    {
        var sb = new StringBuilder();
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlock)
            {
                int end = line.IndexOf(blockEnd, i, StringComparison.Ordinal);
                if (end < 0)
                    return sb.ToString();
                inBlock = false;
                i = end + blockEnd.Length - 1;
                continue;
            }
            char c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            if (!inString)
            {
                string rest = line[i..];
                if (rest.StartsWith("//", StringComparison.Ordinal))
                    return sb.ToString();
                foreach (var (open, close) in new[] { ("/*", "*/"), ("@*", "*@"), ("<!--", "-->") })
                {
                    if (rest.StartsWith(open, StringComparison.Ordinal))
                    {
                        inBlock = true;
                        blockEnd = close;
                        i += open.Length - 1;
                        break;
                    }
                }
                if (inBlock)
                    continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SPOMover.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SPOMover.sln not found above the test folder.");
    }

    [GeneratedRegex(@"(?<![\w.])(?:L\.)?T\(\s*(\$?)""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"\{\d+[^{}]*\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"[А-Яа-яІіЇїЄєҐґ]")]
    private static partial Regex Cyrillic();
}
