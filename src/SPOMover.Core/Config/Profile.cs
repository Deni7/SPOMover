using System.Text.Json;
using System.Text.Json.Serialization;
using SPOMover.Core.Mapping;
using SPOMover.Core.Localization;

namespace SPOMover.Core.Config;

/// <summary>
/// Run profile (CFG-2): the source library, the target libraries of the same site, the mapping, connections,
/// performance and retries. The same JSON is stored by the host, exported from the GUI and accepted by the CLI.
/// </summary>
public sealed class Profile
{
    public string? Name { get; set; }

    /// <summary>The library to split: site, library and, optionally, a folder the inventory is limited to.</summary>
    public Target? Source { get; set; }

    /// <summary>Libraries of the same site the mapping may send files to (they must exist).</summary>
    public List<string> TargetLibraries { get; set; } = [];

    /// <summary>MAP-1: where each part of the source goes.</summary>
    public List<MappingEntry> Mapping { get; set; } = [];

    /// <summary>Names of connections (app registrations) stored by the host. More than one needs <see cref="Pool"/>.</summary>
    public List<string> Connections { get; set; } = [];

    public PoolOptions Pool { get; set; } = new();

    public PerformanceOptions Performance { get; set; } = new();

    public RetryOptions Retry { get; set; } = new();

    /// <summary>PLN-2, PLN-3: batch limit and the forecast speed.</summary>
    public PlanOptions Plan { get; set; } = new();

    /// <summary>EXE-1: how batches are moved.</summary>
    public MoveOptions Move { get; set; } = new();

    /// <summary>
    /// Libraries a run lists, in a fixed order: the source first (index 0), then the target libraries (INV-4).
    /// Run state refers to them by this index.
    /// </summary>
    public IReadOnlyList<Target> Libraries()
    {
        if (Source is null)
            return [];
        return [Source, .. TargetLibraries.Select(l => new Target { SiteUrl = Source.SiteUrl, Library = l })];
    }

    /// <summary>MAP-4: the mapping checked against the source and target libraries of this profile.</summary>
    public MappingBuild BuildMapping() => MappingSet.Build(Mapping, Source?.Library, TargetLibraries);

    /// <summary>CFG-6: libraries, mapping, connections, limits. Mapping issues carry <see cref="MappingSet.Area"/>.</summary>
    public ProfileValidation ValidateForRun(IReadOnlyCollection<Connection> connections)
    {
        var issues = new List<ValidationIssue>();
        if (Source is null)
            issues.Add(new(IssueSeverity.Error, L.T("source: no source library set.")));
        else
            issues.AddRange(Source.Validate("source"));
        var names = TargetLibraries.Select(t => (t ?? string.Empty).Trim()).ToList();
        if (names.Count == 0)
            issues.Add(new(IssueSeverity.Error, L.T("targetLibraries: no target library set.")));
        if (names.Any(n => n.Length == 0))
            issues.Add(new(IssueSeverity.Error, L.T("targetLibraries: empty library name.")));
        foreach (var dup in names.Where(n => n.Length > 0).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new(IssueSeverity.Error, L.T($"targetLibraries: «{dup.Key}» is listed more than once.")));
        if (Source?.Library is { } src && names.Contains(src.Trim(), StringComparer.OrdinalIgnoreCase))
            issues.Add(new(IssueSeverity.Error, L.T($"targetLibraries: «{src}» is the source library.")));
        issues.AddRange(BuildMapping().Issues);
        if (Connections.Count == 0)
            issues.Add(new(IssueSeverity.Error, L.T("connections: no connection set.")));
        foreach (string name in Connections.Where(n => connections.All(c => !string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))))
            issues.Add(new(IssueSeverity.Error, L.T($"connections: connection «{name}» not found.")));
        foreach (var c in connections)
            issues.AddRange(c.Validate());
        if (Connections.Count > 1 && !Pool.Enabled)
            issues.Add(new(IssueSeverity.Error,
                L.T("Several connections work only as a pool: turn on pool.enabled. Microsoft advises against several apps for one job — the pool shares the tenant budget with the users.")));
        if (Pool.Enabled && Connections.Count > 1)
            issues.Add(new(IssueSeverity.Warning,
                L.T($"A pool of {Connections.Count} apps is on. The total load is limited to {Pool.TenantCeilingPercent}% of the tenant budget.")));
        issues.AddRange(Pool.Validate());
        issues.AddRange(Performance.Validate());
        issues.AddRange(Retry.Validate());
        issues.AddRange(Plan.Validate());
        issues.AddRange(Move.Validate());
        return new ProfileValidation(issues);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Profile Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Profile>(json, JsonOptions)
                ?? throw new ProfileException(L.T("The profile is empty."));
        }
        catch (JsonException ex)
        {
            if (ex.LineNumber is long line)
                throw new ProfileException(L.T($"The profile could not be parsed (line {line + 1}): {ex.Message}"), ex);
            throw new ProfileException(L.T($"The profile could not be parsed: {ex.Message}"), ex);
        }
    }

    public static Profile Load(string path)
    {
        if (!File.Exists(path))
            throw new ProfileException(L.T($"Profile file not found: {path}"));
        return Parse(File.ReadAllText(path));
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

}

public sealed record ProfileValidation(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);

    /// <summary>
    /// Inventory does not need a correct mapping: it is what the mapping is checked against. Moving (stage 4) needs
    /// <see cref="IsValid"/>.
    /// </summary>
    public bool IsValidForInventory => Issues.All(i => i.Severity != IssueSeverity.Error || i.Area == MappingSet.Area);
}

public sealed class ProfileException(string message, Exception? inner = null) : Exception(message, inner);
