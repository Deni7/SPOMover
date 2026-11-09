using SPOMover.Core.Mapping;
using SPOMover.Core.Localization;

namespace SPOMover.Core.Planning;

/// <summary>A folder of a library as the inventory saw it (INV-2): path relative to the library root ("" — the root).</summary>
public sealed record PlanFolder(string Path, long Files, long Bytes);

/// <summary>A file of the source: id in the state store, library-relative path, size, who holds it checked out.</summary>
public sealed record PlanFile(long Id, string Path, long Size, string? CheckedOutBy);

public enum BatchKind
{
    /// <summary>A folder with its whole subtree, into a folder of the target that does not exist yet (MoveFolderByPath).</summary>
    Folder,

    /// <summary>Files of one source folder into one target folder (MoveFileByPath each).</summary>
    Files,
}

/// <summary>PLN-1: one unit of the move.</summary>
/// <param name="Seq">1-based number in the plan, stable for the same inputs (P-3).</param>
/// <param name="Wave">PLN-3: wave number (1-based).</param>
/// <param name="Source">Folder batch: the moved folder. Files batch: the folder the files are in.</param>
/// <param name="TargetPath">Folder batch: the new path of the folder. Files batch: the folder the files go into.</param>
/// <param name="Items">Elements SharePoint counts against the list view threshold: files and folders of the subtree (folder batch), files (files batch).</param>
public sealed record PlanBatch(
    int Seq,
    int Wave,
    BatchKind Kind,
    int RuleRow,
    string Library,
    string Source,
    string TargetPath,
    long Files,
    long Bytes,
    long Items,
    int CheckedOut,
    IReadOnlyList<PlanBatchFile> FileList);

/// <summary>A file of a files batch and the path it gets in the target (a file rule may rename it).</summary>
public sealed record PlanBatchFile(long FileId, string Path, string NewPath, long Size);

/// <summary>EXE-3: a folder to create in a target library before the batches that need it.</summary>
public sealed record PlanTargetFolder(string Library, string Path);

/// <summary>A moved file that cannot be planned: its new path is taken (a file in the target or another moved file).</summary>
public sealed record PlanConflict(long FileId, string Path, string Library, string NewPath, string Reason);

public sealed record PlanWave(int Number, int RuleRow, string Title, int Batches, long Files, long Bytes);

public sealed record PlanResult(
    IReadOnlyList<PlanBatch> Batches,
    IReadOnlyList<PlanTargetFolder> Folders,
    IReadOnlyList<PlanConflict> Conflicts,
    IReadOnlyList<PlanWave> Waves,
    long StayFiles,
    long StayBytes);

/// <summary>Limits of the plan (CFG-2, <c>plan</c> in the profile).</summary>
public sealed record PlanLimits(int MaxBatchItems);

/// <summary>
/// PLN-1, PLN-2, PLN-3: turns the inventory and the mapping into batches. A folder moves whole when its subtree fits
/// into the batch limit, no rule inside it changes where anything goes, and its target folder neither exists nor is
/// needed by another batch. Otherwise its target folder is created and its children are planned one by one; the
/// folder's own files become files batches. The result does not depend on the order of the inputs (P-3).
/// </summary>
public static class PlanBuilder
{
    private enum Decision { Whole, Split, Skip }

    /// <param name="sourceFolders">All folders of the source with totals of their subtree, the root included.</param>
    /// <param name="sourceFiles">
    /// Files of the source, read once. In path order for a stable result: when two moved files would get the same new
    /// path, the first one is planned and the second is a conflict.
    /// </param>
    /// <param name="targetFolders">Existing folders of every target library, by library name.</param>
    /// <param name="targetFiles">Existing files of every target library, by library name.</param>
    public static PlanResult Build(
        MappingSet mapping,
        IReadOnlyCollection<PlanFolder> sourceFolders,
        IEnumerable<PlanFile> sourceFiles,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> targetFolders,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> targetFiles,
        PlanLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxBatchItems, 1);
        var tree = Tree.Create(sourceFolders);
        var innerRule = InnerRules(mapping);
        var existingFolders = Keys(targetFolders);
        var forceSplit = new HashSet<string>(StringComparer.Ordinal);

        // Whole folders whose target is needed by another batch are split, then the plan is made again.
        while (true)
        {
            var decisions = Decide(mapping, tree, innerRule, existingFolders, forceSplit, limits);
            var claims = decisions.Where(d => d.Value.Decision == Decision.Whole)
                .Select(d => (Source: d.Key, d.Value.Library!, Target: MappingSet.Key(d.Value.TargetPath!)))
                .ToList();
            var needed = NeededFolders(decisions);
            var demote = claims.Where(c => Conflicts(c, claims, needed)).Select(c => c.Source).ToList();
            if (demote.Count == 0)
                return Assemble(mapping, tree, decisions, sourceFiles, existingFolders, Keys(targetFiles), limits);
            forceSplit.UnionWith(demote);
        }
    }

    private sealed record Choice(Decision Decision, int? RuleRow, string? Library, string? TargetPath);

    private static Dictionary<string, Choice> Decide(MappingSet mapping, Tree tree, HashSet<string> innerRule,
        Dictionary<string, HashSet<string>> existingFolders, HashSet<string> forceSplit, PlanLimits limits)
    {
        var result = new Dictionary<string, Choice>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(string.Empty);
        while (stack.Count > 0)
        {
            string key = stack.Pop();
            var node = tree.Nodes[key];
            var match = mapping.Resolve(node.Path);
            bool moves = match is { Stays: false };
            bool inner = innerRule.Contains(key);
            if (!moves && !inner)
            {
                result[key] = new Choice(Decision.Skip, match?.Rule.Row, null, null);
                continue;
            }
            bool whole = moves && key.Length > 0 && !inner && !forceSplit.Contains(key)
                && node.Items <= limits.MaxBatchItems
                && !Exists(existingFolders, match!.Library!, match.NewPath!);
            if (whole)
            {
                result[key] = new Choice(Decision.Whole, match!.Rule.Row, match.Library, match.NewPath);
                continue;
            }
            result[key] = new Choice(Decision.Split, match?.Rule.Row, moves ? match!.Library : null, moves ? match!.NewPath : null);
            foreach (string child in node.Children)
                stack.Push(child);
        }
        return result;
    }

    /// <summary>Target folders that must exist for the plan: what split folders turn into and the parents of whole folders.</summary>
    private static HashSet<(string Library, string Key)> NeededFolders(Dictionary<string, Choice> decisions)
    {
        var needed = new HashSet<(string, string)>();
        foreach (var c in decisions.Values)
        {
            if (c.Library is null)
                continue;
            if (c.Decision == Decision.Split)
                AddChain(needed, c.Library, c.TargetPath!);
            else if (c.Decision == Decision.Whole)
                AddChain(needed, c.Library, Parent(c.TargetPath!));
        }
        return needed;
    }

    private static void AddChain(HashSet<(string, string)> set, string library, string path)
    {
        string key = MappingSet.Key(path);
        while (key.Length > 0)
        {
            set.Add((library.ToLowerInvariant(), key));
            key = Parent(key);
        }
    }

    /// <summary>A whole folder cannot move into a path another batch also uses: its target or a folder under it.</summary>
    private static bool Conflicts((string Source, string Library, string Target) claim, List<(string Source, string Library, string Target)> claims,
        HashSet<(string Library, string Key)> needed)
    {
        string lib = claim.Library.ToLowerInvariant();
        if (needed.Any(n => n.Library == lib && Under(n.Key, claim.Target)))
            return true;
        // A nested whole target is caught above (its parents are needed); here — two whole folders into the same path.
        return claims.Any(o => o.Source != claim.Source
            && string.Equals(o.Library, claim.Library, StringComparison.OrdinalIgnoreCase) && o.Target == claim.Target);
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it (keys).</summary>
    private static bool Under(string path, string folder) =>
        folder.Length == 0 || path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static PlanResult Assemble(MappingSet mapping, Tree tree, Dictionary<string, Choice> decisions, IEnumerable<PlanFile> sourceFiles,
        Dictionary<string, HashSet<string>> existingFolders, Dictionary<string, HashSet<string>> existingFiles, PlanLimits limits)
    {
        // Whole folders: the subtree totals come from the folder stats; check-outs from the files below.
        var wholes = decisions.Where(d => d.Value.Decision == Decision.Whole).ToDictionary(d => d.Key, d => (Choice: d.Value, CheckedOut: 0));
        var groups = new Dictionary<(string Folder, int Rule, string Library, string Target), List<PlanBatchFile>>();
        var checkedOutIds = new HashSet<long>();
        var conflicts = new List<PlanConflict>();
        var claimedFiles = new Dictionary<(string Library, string Key), long>();
        long stayFiles = 0, stayBytes = 0;

        foreach (var file in sourceFiles)
        {
            string dirKey = MappingSet.Key(Parent(file.Path));
            string? owner = OwnerOf(decisions, dirKey);
            if (owner is null || decisions[owner].Decision == Decision.Skip)
            {
                stayFiles++;
                stayBytes += file.Size;
                continue;
            }
            if (decisions[owner].Decision == Decision.Whole)
            {
                if (file.CheckedOutBy is not null)
                {
                    var w = wholes[owner];
                    wholes[owner] = (w.Choice, w.CheckedOut + 1);
                }
                continue;
            }
            // The file's own folder is split: the file is planned on its own.
            var match = mapping.Resolve(file.Path);
            if (match is null || match.Stays)
            {
                stayFiles++;
                stayBytes += file.Size;
                continue;
            }
            string lib = match.Library!;
            var newKey = (lib.ToLowerInvariant(), MappingSet.Key(match.NewPath!));
            if (existingFiles.TryGetValue(newKey.Item1, out var files) && files.Contains(newKey.Item2))
            {
                conflicts.Add(new PlanConflict(file.Id, file.Path, lib, match.NewPath!, L.T("the target library already has a file with this path")));
                continue;
            }
            if (claimedFiles.TryGetValue(newKey, out long other))
            {
                conflicts.Add(new PlanConflict(file.Id, file.Path, lib, match.NewPath!, L.T($"another source file lands on the same path (id {other})")));
                continue;
            }
            claimedFiles[newKey] = file.Id;
            var group = (Folder: Parent(file.Path), match.Rule.Row, lib, Parent(match.NewPath!));
            if (!groups.TryGetValue(group, out var list))
                groups[group] = list = [];
            list.Add(new PlanBatchFile(file.Id, file.Path, match.NewPath!, file.Size));
            if (file.CheckedOutBy is not null)
                checkedOutIds.Add(file.Id);
        }

        // Batches in a fixed order: by wave (rule row), then by source path.
        var raw = new List<(int Rule, string Source, PlanBatch Batch)>();
        foreach (var (key, (c, checkedOut)) in wholes)
        {
            var node = tree.Nodes[key];
            raw.Add((c.RuleRow!.Value, node.Path, new PlanBatch(0, 0, BatchKind.Folder, c.RuleRow.Value, c.Library!, node.Path, c.TargetPath!,
                node.Files, node.Bytes, node.Items, checkedOut, [])));
        }
        foreach (var (g, list) in groups)
        {
            var ordered = list.OrderBy(f => MappingSet.Key(f.Path), StringComparer.Ordinal).ToList();
            for (int i = 0; i < ordered.Count; i += limits.MaxBatchItems)
            {
                var chunk = ordered.Skip(i).Take(limits.MaxBatchItems).ToList();
                raw.Add((g.Rule, g.Folder, new PlanBatch(0, 0, BatchKind.Files, g.Rule, g.Library, g.Folder, g.Target,
                    chunk.Count, chunk.Sum(f => f.Size), chunk.Count, chunk.Count(f => checkedOutIds.Contains(f.FileId)), chunk)));
            }
        }
        var rules = mapping.Rules.ToDictionary(r => r.Row);
        var waveOf = raw.Select(r => r.Rule).Distinct().Order().Select((rule, i) => (rule, i + 1)).ToDictionary(x => x.rule, x => x.Item2);
        var batches = raw
            .OrderBy(r => r.Rule)
            .ThenBy(r => MappingSet.Key(r.Source), StringComparer.Ordinal)
            .ThenBy(r => r.Batch.Kind)
            .ThenBy(r => r.Batch.FileList.Count == 0 ? string.Empty : MappingSet.Key(r.Batch.FileList[0].Path), StringComparer.Ordinal)
            .Select((r, i) => r.Batch with { Seq = i + 1, Wave = waveOf[r.Rule] })
            .ToList();

        // EXE-3: folders to create — every needed folder that is not in the target yet, parents first.
        var needed = new SortedSet<(string Library, string Path)>(Comparer<(string Library, string Path)>.Create((a, b) =>
        {
            int c = StringComparer.OrdinalIgnoreCase.Compare(a.Library, b.Library);
            return c != 0 ? c : StringComparer.Ordinal.Compare(MappingSet.Key(a.Path), MappingSet.Key(b.Path));
        }));
        void Need(string library, string path)
        {
            for (string p = path; p.Length > 0; p = Parent(p))
            {
                if (!Exists(existingFolders, library, p))
                    needed.Add((library, p));
            }
        }
        foreach (var (_, c) in decisions.Where(d => d.Value.Decision == Decision.Split && d.Value.Library is not null).OrderBy(d => d.Key, StringComparer.Ordinal))
            Need(c.Library!, c.TargetPath!);
        foreach (var b in batches)
            Need(b.Library, b.Kind == BatchKind.Folder ? Parent(b.TargetPath) : b.TargetPath);

        var waves = batches.GroupBy(b => b.Wave).OrderBy(g => g.Key).Select(g =>
        {
            var rule = rules[g.First().RuleRow];
            return new PlanWave(g.Key, rule.Row, rule.ToString(), g.Count(), g.Sum(b => b.Files), g.Sum(b => b.Bytes));
        }).ToList();
        return new PlanResult(batches, needed.Select(n => new PlanTargetFolder(n.Library, n.Path)).ToList(),
            conflicts.OrderBy(c => MappingSet.Key(c.Path), StringComparer.Ordinal).ToList(), waves, stayFiles, stayBytes);
    }

    /// <summary>
    /// The closest folder at or above <paramref name="dirKey"/> the planner looked at. Folders below a whole or skipped
    /// folder are not looked at; a split folder is looked at with all its children.
    /// </summary>
    private static string? OwnerOf(Dictionary<string, Choice> decisions, string dirKey)
    {
        for (string k = dirKey; ; k = Parent(k))
        {
            if (decisions.ContainsKey(k))
                return k;
            if (k.Length == 0)
                return null;
        }
    }

    /// <summary>
    /// Folders (keys) inside which a rule changes where something goes, compared with the rule above it: such a
    /// folder cannot move whole. A rule that only repeats its parent does not count.
    /// </summary>
    private static HashSet<string> InnerRules(MappingSet mapping)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in mapping.Rules.Where(r => r.Source.Length > 0))
        {
            var parent = mapping.ParentOf(rule);
            bool same;
            if (parent is null)
            {
                same = rule.Stays;
            }
            else
            {
                string rest = rule.Source[(parent.Source.Length == 0 ? 0 : parent.Source.Length + 1)..];
                same = string.Equals(parent.Library, rule.Library, StringComparison.OrdinalIgnoreCase)
                    && (rule.Stays || MappingSet.Key(MappingSet.Join(parent.TargetPath, rest)) == MappingSet.Key(rule.TargetPath));
            }
            if (same)
                continue;
            for (string k = Parent(MappingSet.Key(rule.Source)); ; k = Parent(k))
            {
                result.Add(k);
                if (k.Length == 0)
                    break;
            }
        }
        return result;
    }

    private static Dictionary<string, HashSet<string>> Keys(IReadOnlyDictionary<string, IReadOnlyCollection<string>> byLibrary) =>
        byLibrary.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value.Select(MappingSet.Key).ToHashSet(StringComparer.Ordinal));

    private static bool Exists(Dictionary<string, HashSet<string>> folders, string library, string path) =>
        MappingSet.Key(path).Length == 0 || (folders.TryGetValue(library.ToLowerInvariant(), out var set) && set.Contains(MappingSet.Key(path)));

    public static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>The source folder tree with subtree item counts (files and folders).</summary>
    private sealed class Tree
    {
        public sealed class Node(string path, long files, long bytes)
        {
            public string Path { get; } = path;
            public long Files { get; } = files;
            public long Bytes { get; } = bytes;
            public long Items { get; set; }
            public List<string> Children { get; } = [];
        }

        public Dictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);

        public static Tree Create(IReadOnlyCollection<PlanFolder> folders)
        {
            var tree = new Tree();
            foreach (var f in folders)
                tree.Nodes.TryAdd(MappingSet.Key(f.Path), new Node(f.Path, f.Files, f.Bytes));
            tree.Nodes.TryAdd(string.Empty, new Node(string.Empty, folders.Sum(f => f.Path.Length == 0 ? f.Files : 0), folders.Sum(f => f.Path.Length == 0 ? f.Bytes : 0)));
            // Parents the stats may lack (a listing limited to a folder): added with the totals of their children later.
            foreach (string key in tree.Nodes.Keys.ToList())
            {
                for (string p = Parent(key); key.Length > 0; p = Parent(p))
                {
                    if (!tree.Nodes.ContainsKey(p))
                        tree.Nodes[p] = new Node(ParentPath(tree.Nodes[key].Path, key, p), 0, 0);
                    if (p.Length == 0)
                        break;
                }
            }
            var subfolders = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string key in tree.Nodes.Keys.Where(k => k.Length > 0).OrderBy(k => k, StringComparer.Ordinal))
            {
                tree.Nodes[Parent(key)].Children.Add(key);
                for (string p = Parent(key); ; p = Parent(p))
                {
                    subfolders[p] = subfolders.GetValueOrDefault(p) + 1;
                    if (p.Length == 0)
                        break;
                }
            }
            foreach (var (key, node) in tree.Nodes)
            {
                node.Items = node.Files + subfolders.GetValueOrDefault(key);
                node.Children.Sort(StringComparer.Ordinal);
            }
            return tree;
        }

        /// <summary>The original-case path of an ancestor, cut from a descendant's path.</summary>
        private static string ParentPath(string childPath, string childKey, string parentKey) =>
            parentKey.Length == 0 ? string.Empty : childPath[..parentKey.Length];
    }
}
