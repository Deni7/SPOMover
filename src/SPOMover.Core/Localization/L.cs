using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SPOMover.Core.Localization;

/// <summary>
/// User-facing text in the language the user picked. The source text in the code is English and is the key;
/// other languages are JSON dictionaries "English → translation" embedded from <c>Localization/&lt;lang&gt;/*.json</c>.
/// A text missing from a dictionary is shown in English.
/// </summary>
/// <remarks>
/// Use <c>T("Save")</c>, <c>T($"{n} files found")</c> (the holes become <c>{0}</c>, <c>{1:N0}</c>… in the key) or
/// <c>T("{0} files", n)</c>. Holes must not contain string literals, so that the key can be read from the source
/// (LocalizationTests does that). An interpolated text whose holes are all <c>const string</c> is folded by the
/// compiler into one constant and reaches <c>T(string)</c> with the value inside the key — pass such values as
/// arguments instead: <c>T("enter «{0}»", Entry.Stay)</c>. The language is process-wide: the app is a single-user
/// desktop tool.
/// </remarks>
public static class L
{
    public const string English = "en";
    public const string Ukrainian = "uk";

    /// <summary>Languages offered in the UI, each in its own language.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
        [(English, "English"), (Ukrainian, "Українська")]; // i18n-ignore

    public const string EnvironmentVariable = "SPO_MOVER_LANG";
    public const string FileName = "language.txt";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables = LoadTables();
    private static readonly ConcurrentDictionary<string, byte> MissingKeys = new(StringComparer.Ordinal);
    private static volatile string _language = English;

    /// <summary>Raised after the language changes (the UI re-renders, the window renames itself).</summary>
    public static event Action? Changed;

    public static string Language => _language;

    public static CultureInfo Culture => CultureOf(_language);

    public static CultureInfo CultureOf(string language) =>
        CultureInfo.GetCultureInfo(language == Ukrainian ? "uk-UA" : "en-US");

    public static bool IsSupported(string? code) => code is not null && Languages.Any(l => l.Code == code);

    /// <summary>Switches the language; an unknown code is ignored.</summary>
    public static void Set(string? code)
    {
        code = Normalize(code);
        if (code is null || code == _language)
            return;
        _language = code;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Changed?.Invoke();
    }

    /// <summary>"uk-UA", "UK", "ukrainian" → "uk"; null when not supported.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        string c = code.Trim().ToLowerInvariant();
        if (c.Length > 2)
            c = c[..2];
        return IsSupported(c) ? c : null;
    }

    /// <summary>
    /// The language for a process started on these data: <c>SPO_MOVER_LANG</c>, else the choice saved in the data
    /// folder, else Ukrainian on a Ukrainian Windows and English elsewhere.
    /// </summary>
    public static string Preferred(string? dataDir)
    {
        if (Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable)) is { } env)
            return env;
        if (dataDir is not null)
        {
            try
            {
                string path = Path.Combine(dataDir, FileName);
                if (File.Exists(path) && Normalize(File.ReadAllText(path)) is { } saved)
                    return saved;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Ukrainian ? Ukrainian : English;
    }

    private static string? _dataDir;

    /// <summary>Applies <see cref="Preferred"/>; <see cref="Choose"/> later saves to the same folder.</summary>
    public static void Load(string? dataDir)
    {
        _dataDir = dataDir;
        Set(Preferred(dataDir));
    }

    /// <summary>The user's choice: switches and remembers it in the data folder given to <see cref="Load"/>.</summary>
    public static void Choose(string code)
    {
        Set(code);
        if (_dataDir is not null)
            Save(_dataDir);
    }

    /// <summary>Remembers the current language in the data folder (the CLI and the next start read it).</summary>
    public static void Save(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, FileName), _language);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The text in the current language.</summary>
    public static string T(string text) => Translate(_language, text);

    /// <summary>An interpolated text: the key is the text with holes numbered, e.g. "{0} of {1:N0}".</summary>
    public static string T(Text text) => text.ToString(_language);

    /// <summary>A composite format: the format is translated, then filled with the culture of the language.</summary>
    public static string T(string format, params object?[] args) =>
        string.Format(Culture, Translate(_language, format), args);

    /// <summary>The text in a given language (no global state — for tests and reports in a fixed language).</summary>
    public static string Translate(string language, string text)
    {
        if (language == English || string.IsNullOrEmpty(text))
            return text;
        if (Tables.TryGetValue(language, out var table) && table.TryGetValue(text, out string? value))
            return value;
        MissingKeys.TryAdd(text, 0);
        return text;
    }

    /// <summary>Keys asked for that a dictionary does not have (diagnostics).</summary>
    public static IReadOnlyCollection<string> Missing => MissingKeys.Keys.ToList();

    /// <summary>The dictionary of a language (empty for English).</summary>
    public static IReadOnlyDictionary<string, string> Table(string language) =>
        Tables.TryGetValue(language, out var t) ? t : new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadTables()
    {
        var assembly = typeof(L).Assembly;
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (code, _) in Languages)
        {
            if (code == English)
                continue;
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            string prefix = $"i18n/{code}/";
            foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                Read(assembly, name, table);
            result[code] = table;
        }
        return result;
    }

    private static void Read(Assembly assembly, string resource, Dictionary<string, string> table)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String)
                table.TryAdd(p.Name, p.Value.GetString()!);
        }
    }

    /// <summary>Builds the key of an interpolated text and keeps the values of its holes.</summary>
    [InterpolatedStringHandler]
    public struct Text
    {
        private readonly StringBuilder _key;
        private readonly List<object?> _args;

        public Text(int literalLength, int formattedCount)
        {
            _key = new StringBuilder(literalLength + formattedCount * 4);
            _args = new List<object?>(formattedCount);
        }

        public readonly void AppendLiteral(string s) => _key.Append(s.Replace("{", "{{").Replace("}", "}}"));

        public readonly void AppendFormatted<TValue>(TValue value) => AppendFormatted(value, 0, null);

        public readonly void AppendFormatted<TValue>(TValue value, string? format) => AppendFormatted(value, 0, format);

        public readonly void AppendFormatted<TValue>(TValue value, int alignment) => AppendFormatted(value, alignment, null);

        public readonly void AppendFormatted<TValue>(TValue value, int alignment, string? format)
        {
            _key.Append('{').Append(_args.Count);
            if (alignment != 0)
                _key.Append(',').Append(alignment);
            if (!string.IsNullOrEmpty(format))
                _key.Append(':').Append(format);
            _key.Append('}');
            _args.Add(value);
        }

        public readonly string Key => _key.ToString();

        public readonly string ToString(string language) =>
            string.Format(CultureOf(language), Translate(language, Key), _args.ToArray());

        public override readonly string ToString() => ToString(_language);
    }
}
