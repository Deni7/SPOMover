using System.Text.Json;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Core.Mapping;
using SPOMover.Core.Planning;
using SPOMover.State;

namespace SPOMover.Engine;

/// <summary>What happens to a folder of the source under a plan (GUI-3).</summary>
public enum FolderFate
{
    /// <summary>The folder is a batch: it moves whole in one request.</summary>
    Batch,
    /// <summary>Inside a folder that moves whole.</summary>
    InBatch,
    /// <summary>Moves, but split: its target folder is created, its children and files are planned separately.</summary>
    Split,
    /// <summary>A "stay" row of the mapping covers it.</summary>
    Stays,
    /// <summary>No row of the mapping covers it, nor anything inside it.</summary>
    Unmapped,
    /// <summary>Not moved itself, but a row inside it moves something.</summary>
    Partly,
}

/// <summary>A folder of the source as the plan page shows it.</summary>
/// <param name="Batch">The batch that moves this folder (Batch) or contains it (InBatch).</param>
/// <param name="FileBatches">Files batches of the folder's own files.</param>
public sealed record PlanFolderNode(
    string Path, string Name, long Files, long Bytes, bool HasChildren, FolderFate Fate, int? RuleRow, string? Library, string? Target,
    PlanBatch? Batch, IReadOnlyList<PlanBatch> FileBatches);

/// <summary>
/// GUI-3: a saved plan read for the plan page — the source folder tree of the plan's inventory with the rule and the
/// batch for every folder. Built once per plan (folders and batches in memory), children are asked for lazily.
/// </summary>
public sealed class PlanView
{
    private readonly Dictionary<string, StateStore.FolderStat> _folders = new(StringComparer.Ordinal);
    private readonly ILookup<string, string> _children;
    private readonly Dictionary<string, PlanBatch> _folderBatches = new(StringComparer.Ordinal);
    private readonly ILookup<string, PlanBatch> _fileBatches;
    private readonly MappingSet _mapping;
    private readonly HashSet<string> _innerRules = new(StringComparer.Ordinal);

    public PlanView(StateStore store, long planId)
    {
        Plan = store.GetPlan(planId) ?? throw new KeyNotFoundException(L.T($"Plan {planId} not found."));
        foreach (var s in store.GetFolderStats(Plan.RunId, 0))
            _folders[MappingSet.Key(s.Path)] = s;
        _children = _folders.Keys.Where(k => k.Length > 0).ToLookup(PlanBuilder.Parent, StringComparer.Ordinal);
        Batches = store.GetPlanBatches(planId);
        foreach (var b in Batches.Where(b => b.Kind == BatchKind.Folder))
            _folderBatches[MappingSet.Key(b.Source)] = b;
        _fileBatches = Batches.Where(b => b.Kind == BatchKind.Files).ToLookup(b => MappingSet.Key(b.Source), StringComparer.Ordinal);
        var entries = JsonSerializer.Deserialize<List<MappingEntry>>(Plan.MappingJson, Profile.JsonOptions) ?? [];
        var targets = entries.Select(e => e.Library).Where(l => l is not null && l != MappingEntry.Stay).Select(l => l!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _mapping = MappingSet.Build(entries, null, targets).Set ?? MappingSet.Empty;
        foreach (var rule in _mapping.Rules.Where(r => r.Source.Length > 0))
        {
            for (string k = PlanBuilder.Parent(MappingSet.Key(rule.Source)); ; k = PlanBuilder.Parent(k))
            {
                _innerRules.Add(k);
                if (k.Length == 0)
                    break;
            }
        }
    }

    public StateStore.PlanRow Plan { get; }

    public IReadOnlyList<PlanBatch> Batches { get; }

    /// <summary>The root of the source library.</summary>
    public PlanFolderNode Root => Node(string.Empty);

    /// <summary>Subfolders of a folder ("" — the root), by name.</summary>
    public IReadOnlyList<PlanFolderNode> Children(string path) =>
        _children[MappingSet.Key(path)].Select(Node).OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public PlanFolderNode Node(string key)
    {
        _folders.TryGetValue(key, out var stat);
        string path = stat?.Path ?? key;
        string name = path.Length == 0 ? "/" : path[(path.LastIndexOf('/') + 1)..];
        bool hasChildren = _children[key].Any();
        var match = _mapping.Resolve(path);
        var files = _fileBatches[key].ToList();
        if (_folderBatches.TryGetValue(key, out var own))
            return new(path, name, stat?.Files ?? 0, stat?.Bytes ?? 0, hasChildren, FolderFate.Batch, own.RuleRow, own.Library, own.TargetPath, own, files);
        for (string k = PlanBuilder.Parent(key); key.Length > 0; k = PlanBuilder.Parent(k))
        {
            if (_folderBatches.TryGetValue(k, out var outer))
            {
                string rest = path[(outer.Source.Length == 0 ? 0 : outer.Source.Length + 1)..];
                return new(path, name, stat?.Files ?? 0, stat?.Bytes ?? 0, hasChildren, FolderFate.InBatch, outer.RuleRow, outer.Library,
                    MappingSet.Join(outer.TargetPath, rest), outer, files);
            }
            if (k.Length == 0)
                break;
        }
        FolderFate fate = match switch
        {
            { Stays: false } => FolderFate.Split,
            _ when _innerRules.Contains(key) => FolderFate.Partly,
            { Stays: true } => FolderFate.Stays,
            _ => FolderFate.Unmapped,
        };
        return new(path, name, stat?.Files ?? 0, stat?.Bytes ?? 0, hasChildren, fate, match?.Rule.Row, match?.Library, match?.NewPath, null, files);
    }
}
