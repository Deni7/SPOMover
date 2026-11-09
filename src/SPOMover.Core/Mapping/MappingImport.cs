using System.Text;
using SPOMover.Core.Reports;
using SPOMover.Core.Localization;

namespace SPOMover.Core.Mapping;

/// <summary>
/// MAP-6: mapping rows from CSV, TSV, TXT or XLSX (first sheet). Columns: source, library, path, comment
/// (path and comment may be absent). The separator is a tab, ';', '|' or ',' — the first one a line contains,
/// in that order, so folder names with commas survive in tab- or semicolon-separated files. A header row is skipped.
/// </summary>
public static class MappingImport
{
    public sealed record Result(List<MappingEntry> Entries, List<string> Warnings);

    private static readonly string[] HeaderWords = ["source", "library", "path", "target", "джерело", "бібліотека", "шлях", "куди", "звідки"]; // i18n-ignore: Ukrainian headers of user files are still accepted

    public static Result Parse(string fileName, Stream content)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        List<string[]> rows;
        if (ext is ".xlsx" or ".xlsm")
        {
            rows = SimpleXlsxReader.ReadFirstSheet(content);
        }
        else
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            rows = reader.ReadToEnd().Split('\n').Select(l => SplitLine(l.TrimEnd('\r'))).ToList();
        }
        return FromRows(rows);
    }

    public static Result FromRows(IEnumerable<string[]> rows)
    {
        var entries = new List<MappingEntry>();
        var warnings = new List<string>();
        int line = 0;
        foreach (var raw in rows)
        {
            line++;
            var cells = raw.Select(Cell).ToArray();
            if (cells.All(c => c.Length == 0))
                continue;
            if (entries.Count == 0 && LooksLikeHeader(cells))
                continue;
            if (cells.Length < 2 || cells[0].Length == 0 || cells[1].Length == 0)
            {
                warnings.Add(L.T($"Row {line}: at least a source and a library are required — skipped."));
                continue;
            }
            entries.Add(new MappingEntry
            {
                Source = cells[0],
                Library = cells[1],
                Path = cells.Length > 2 && cells[2].Length > 0 ? cells[2] : null,
                Comment = cells.Length > 3 && cells[3].Length > 0 ? cells[3] : null,
            });
        }
        if (entries.Count == 0)
            warnings.Add(L.T("No mapping rows found."));
        return new Result(entries, warnings);
    }

    /// <summary>A cell without quotes, spaces around and the apostrophe a CSV export puts before "-", "=", "+", "@".</summary>
    private static string Cell(string value)
    {
        string v = value.Trim().Trim('"').Trim();
        return v.Length > 1 && v[0] == '\'' && v[1] is '-' or '=' or '+' or '@' ? v[1..] : v;
    }

    private static bool LooksLikeHeader(string[] cells) =>
        cells.Take(3).Any(c => HeaderWords.Any(w => c.Equals(w, StringComparison.OrdinalIgnoreCase) || c.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
        && !cells[0].Contains('/');

    internal static string[] SplitLine(string line)
    {
        foreach (char sep in new[] { '\t', ';', '|', ',' })
        {
            if (line.Contains(sep))
                return SplitCsv(line, sep);
        }
        return [line];
    }

    private static string[] SplitCsv(string line, char sep)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == sep && !quoted)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        cells.Add(sb.ToString());
        return [.. cells];
    }
}
