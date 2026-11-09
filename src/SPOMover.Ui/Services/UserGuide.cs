using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using SPOMover.Core.Localization;

namespace SPOMover.Ui.Services;

/// <summary>
/// docs/USER_GUIDE.md (English) and docs/USER_GUIDE.uk.md (Ukrainian), embedded in the app and split into sections
/// for the help pages: parts (#) group the sections (##), a section with subsections (###) gets one page for its
/// introduction and one per subsection. Section ids are the GitHub anchors of the headings, so links inside the
/// guide lead to the right page. Both files have the same structure and the same heading numbers ("3.16.", "S1."),
/// so a section can be referred to by its number in either language.
/// </summary>
public sealed partial class UserGuide
{
    public sealed record Section(string Id, string Title, int Level, string Group, string? ParentId, string Html, string Text)
    {
        public List<Section> Children { get; } = [];
    }

    public sealed record Part(string Title, IReadOnlyList<Section> Sections);

    private static readonly Lazy<UserGuide> EnglishGuide = new(() => Load(L.English));
    private static readonly Lazy<UserGuide> UkrainianGuide = new(() => Load(L.Ukrainian));

    /// <summary>The guide in the language of the UI.</summary>
    public static UserGuide Instance => For(L.Language);

    /// <summary>The guide in a given language (no global state): Ukrainian for "uk", English otherwise.</summary>
    public static UserGuide For(string language) => language == L.Ukrainian ? UkrainianGuide.Value : EnglishGuide.Value;

    /// <summary>Name of the embedded resource (and of the file in docs/) with the guide in a language.</summary>
    public static string FileName(string language) => language == L.Ukrainian ? "USER_GUIDE.uk.md" : "USER_GUIDE.md";

    private static UserGuide Load(string language)
    {
        using var stream = typeof(UserGuide).Assembly.GetManifestResourceStream(FileName(language))
            ?? throw new InvalidOperationException(L.Translate(language, "The user guide is not embedded in the app."));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new UserGuide(reader.ReadToEnd(), language);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    private readonly Dictionary<string, Section> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Section> _byNumber = new(StringComparer.Ordinal);
    private readonly List<Section> _ordered = []; // every section in document order: the same positions in both languages

    /// <param name="markdown">The guide.</param>
    /// <param name="language">Language of the texts the app adds (the introduction's title); none — the UI language.</param>
    public UserGuide(string markdown, string? language = null)
    {
        Language = language ?? L.Language;
        var parts = new List<(string Title, List<Section> Sections)>();
        var body = new StringBuilder();
        string? docTitle = null;
        (string Title, int Level)? current = null; // null — the text before the first section (introduction)
        bool skip = false; // the table of contents: the app has its own navigation
        bool inCode = false;
        Section? parent = null;

        void Flush()
        {
            string text = body.ToString().Trim().Trim('-').Trim();
            body.Clear();
            if (skip)
                return;
            if (current is not { } h)
            {
                if (text.Length > 0)
                    Intro = new Section("intro", L.Translate(Language, "About this guide"), 2, string.Empty, null, ToHtml(text), Plain(text));
                return;
            }
            string group = h.Level == 1 ? h.Title : parts.Count > 0 ? parts[^1].Title : string.Empty;
            var section = new Section(Slug(h.Title), h.Title, h.Level, group, h.Level == 3 ? parent?.Id : null, ToHtml(text), Plain(h.Title + "\n" + text));
            if (h.Level == 1)
            {
                parts.Add((h.Title, []));
                // A part's own text (before its first section) becomes its first page.
                if (text.Length > 0)
                    parts[^1].Sections.Add(section);
            }
            else if (h.Level == 2)
            {
                if (parts.Count == 0)
                    parts.Add((string.Empty, []));
                parts[^1].Sections.Add(section);
                parent = section;
            }
            else
            {
                parent?.Children.Add(section);
            }
            _byId.TryAdd(section.Id, section);
            _ordered.Add(section);
            if (Number(h.Title) is { } number)
                _byNumber.TryAdd(number, section);
        }

        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;
            if (!inCode && HeadingRegex().Match(raw.TrimEnd()) is { Success: true } m)
            {
                int level = m.Groups[1].Length;
                string text = m.Groups[2].Value.Trim();
                if (level == 1 && docTitle is null && current is null && body.ToString().Trim().Length == 0)
                {
                    docTitle = text;
                    continue;
                }
                Flush();
                current = (text, level);
                skip = level == 2 && IsContents(text);
                continue;
            }
            // The link to the guide in the other language: the app has its own language switch.
            if (!inCode && LanguageLinkRegex().IsMatch(raw))
                continue;
            body.AppendLine(raw);
        }
        Flush();

        Title = docTitle ?? L.Translate(Language, "User guide");
        if (Intro is not null)
        {
            _byId[Intro.Id] = Intro;
            _ordered.Insert(0, Intro);
        }
        Parts = parts.Select(p => new Part(p.Title, p.Sections)).ToList();
        Pages = (Intro is null ? [] : new[] { Intro })
            .Concat(Parts.SelectMany(p => p.Sections.SelectMany(s => s.Children.Count == 0 || HasOwnText(s) ? new[] { s }.Concat(s.Children) : s.Children)))
            .ToList();
    }

    /// <summary>Language of the guide ("en" or "uk").</summary>
    public string Language { get; }

    public string Title { get; }
    public Section? Intro { get; private set; }

    public IReadOnlyList<Part> Parts { get; }

    /// <summary>Pages in reading order (for "previous / next").</summary>
    public IReadOnlyList<Section> Pages { get; }

    /// <summary>The section with this id (heading anchor).</summary>
    public Section? Find(string? id) => id is not null && _byId.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// The section whose heading starts with this number: "3.1" (or "3.1.") finds "3.1. …" but not "3.15. …";
    /// "S1" finds the scenario "S1. …" in either language (the Ukrainian guide writes it with a Cyrillic letter).
    /// A subsection without a number of its own is found by its position: "3.3.2" — the second subsection of 3.3.
    /// </summary>
    public Section? FindByNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return null;
        string key = NormalizeNumber(number);
        if (_byNumber.TryGetValue(key, out var s))
            return s;
        int dot = key.LastIndexOf('.');
        if (dot > 0 && int.TryParse(key[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n >= 1 && _byNumber.TryGetValue(key[..dot], out var parent) && n <= parent.Children.Count)
            return parent.Children[n - 1];
        return null;
    }

    /// <summary>
    /// The section a help link means: an id of this guide, a section number, or an id of the guide in the other
    /// language (the same section here) — so a link keeps working after the language is switched.
    /// </summary>
    public Section? Resolve(string? idOrNumber)
    {
        if (string.IsNullOrWhiteSpace(idOrNumber))
            return null;
        if ((Find(idOrNumber) ?? FindByNumber(idOrNumber)) is { } s)
            return s;
        foreach (var (code, _) in L.Languages)
        {
            if (code == Language)
                continue;
            var other = For(code);
            if (!ReferenceEquals(other, this) && other.Find(idOrNumber) is { } there && Equivalent(other, there) is { } here)
                return here;
        }
        return null;
    }

    /// <summary>The section of this guide at the same place as a section of another guide (same structure).</summary>
    private Section? Equivalent(UserGuide other, Section section)
    {
        int i = other._ordered.IndexOf(section);
        return i >= 0 && other._ordered.Count == _ordered.Count ? _ordered[i] : null;
    }

    public IEnumerable<string> Ids => _byId.Keys;

    /// <summary>Numbers of the numbered sections ("1.1", "S1", "3.16"…).</summary>
    public IEnumerable<string> Numbers => _byNumber.Keys;

    /// <summary>Sections whose title or text contains all the words of the query, titles first.</summary>
    public IReadOnlyList<Section> Search(string query)
    {
        var words = query.ToLower(CultureInfo.CurrentCulture).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return [];
        return Pages
            .Where(s => words.All(w => s.Text.Contains(w, StringComparison.Ordinal)))
            .OrderByDescending(s => words.Count(w => s.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static bool HasOwnText(Section s) => s.Html.Length > 0;

    // The table of contents: "Contents" in the English guide, its Ukrainian title in the Ukrainian one.
    private static bool IsContents(string heading) =>
        heading == "Contents" || heading == "Зміст"; // i18n-ignore

    /// <summary>"3.16. Move plan" → "3.16"; "S1. …" (also with a Cyrillic S) → "S1"; a heading without a number → null.</summary>
    private static string? Number(string heading)
    {
        var m = NumberRegex().Match(heading);
        return m.Success ? NormalizeNumber(m.Groups[1].Value) : null;
    }

    // U+0421 / U+0441 — the Cyrillic letter that looks like S, used for scenarios in the Ukrainian guide.
    private static string NormalizeNumber(string number) =>
        number.Trim().TrimEnd('.').Replace('С', 'S').Replace('с', 'S').ToUpperInvariant(); // i18n-ignore: Cyrillic "С" of the Ukrainian scenario numbers

    /// <summary>GitHub heading anchor: lower case, punctuation removed, spaces to hyphens.</summary>
    public static string Slug(string heading)
    {
        var sb = new StringBuilder();
        foreach (char c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
        }
        return sb.ToString();
    }

    private static string ToHtml(string markdown)
    {
        string html = Markdown.ToHtml(markdown.Trim().TrimEnd('-').Trim(), Pipeline);
        // Links inside the guide open the help page of the section; links to other documents of the repository are
        // not in the app, so only their text is kept.
        html = AnchorRegex().Replace(html, m => $"href=\"help/{m.Groups[1].Value}\"");
        html = RepoLinkRegex().Replace(html, "$1");
        html = ExternalLinkRegex().Replace(html, m => $"{m.Value} target=\"_blank\"");
        return html;
    }

    private static string Plain(string markdown) =>
        Regex.Replace(markdown, @"[`*|#>\[\]]|\(#[^)]*\)", " ").ToLower(CultureInfo.CurrentCulture);

    [GeneratedRegex(@"^(#{1,3})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    // "1.1.", "3.16.", "S1." (Latin or Cyrillic S) at the start of a heading.
    [GeneratedRegex(@"^((?:\d+|[SС]\d+)(?:\.\d+)*)\.(?:\s|$)")] // i18n-ignore: Cyrillic "С" of the Ukrainian scenario numbers
    private static partial Regex NumberRegex();

    // "English version: [USER_GUIDE.md](USER_GUIDE.md)" — the line linking the two languages.
    [GeneratedRegex(@"^\s*[^\[\]]*\[[^\]]*\]\(USER_GUIDE(\.[a-z]{2})?\.md\)\s*$")]
    private static partial Regex LanguageLinkRegex();

    [GeneratedRegex("href=\"#([^\"]+)\"")]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("<a href=\"\\.\\./[^\"]*\"[^>]*>(.*?)</a>")]
    private static partial Regex RepoLinkRegex();

    [GeneratedRegex("href=\"https?://[^\"]+\"")]
    private static partial Regex ExternalLinkRegex();
}
