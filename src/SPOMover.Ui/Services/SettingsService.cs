using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Engine;

namespace SPOMover.Ui.Services;

/// <summary>
/// CFG-1, GUI-5: stored settings for the GUI and the REST API alike,
/// so validation and JSON format are the same everywhere.
/// </summary>
public sealed class SettingsService(RunService runs, SpoBrowser browser)
{
    public IReadOnlyList<(string Name, DateTimeOffset UpdatedAt)> List(string kind) =>
        runs.Store.ListSettings(kind).Select(s => (s.Name, s.UpdatedAt)).ToList();

    public string? GetJson(string kind, string name) => runs.Store.GetSetting(kind, name);

    public Profile? GetProfile(string name) => GetJson(SettingKinds.Profile, name) is { } j ? Profile.Parse(j) : null;

    public Connection? GetConnection(string name) => GetJson(SettingKinds.Connection, name) is { } j ? Connection.Parse(j) : null;

    /// <summary>Parses, names and saves a setting given as JSON (import); returns validation issues (saved anyway).</summary>
    public IReadOnlyList<ValidationIssue> SaveJson(string kind, string name, string json) => kind switch
    {
        SettingKinds.Profile => Save(name, Profile.Parse(json)),
        SettingKinds.Connection => Save(name, Connection.Parse(json)),
        _ => throw new KeyNotFoundException(L.T($"Unknown section “{kind}”.")),
    };

    public IReadOnlyList<ValidationIssue> Save(string name, Profile profile)
    {
        CheckName(name);
        profile.Name = name;
        runs.Store.PutSetting(SettingKinds.Profile, name, profile.ToJson(), DateTimeOffset.UtcNow);
        return runs.ValidateProfile(profile).Issues;
    }

    public IReadOnlyList<ValidationIssue> Save(string name, Connection connection)
    {
        CheckName(name);
        connection.Name = name;
        runs.Store.PutSetting(SettingKinds.Connection, name, connection.ToJson(), DateTimeOffset.UtcNow);
        browser.Forget(name);
        return connection.Validate().ToList();
    }

    public bool Delete(string kind, string name)
    {
        if (kind == SettingKinds.Connection)
            browser.Forget(name);
        return runs.Store.DeleteSetting(kind, name, DateTimeOffset.UtcNow);
    }

    /// <summary>copy under a new name.</summary>
    public void Clone(string kind, string name, string newName)
    {
        CheckName(newName);
        if (GetJson(kind, newName) is not null)
            throw new InvalidOperationException(L.T($"“{newName}” already exists."));
        string json = GetJson(kind, name) ?? throw new KeyNotFoundException(L.T($"“{name}” not found."));
        SaveJson(kind, newName, json);
    }

    /// <summary>
    /// New name for a profile or a connection. Profiles that refer to a renamed connection
    /// are updated; finished runs keep the name they had (their snapshot does not change).
    /// </summary>
    /// <returns>Profiles whose references were updated.</returns>
    public IReadOnlyList<string> Rename(string kind, string name, string newName)
    {
        CheckName(newName);
        if (string.Equals(name, newName, StringComparison.Ordinal))
            return [];
        string json = GetJson(kind, name) ?? throw new KeyNotFoundException(L.T($"“{name}” not found."));
        // A change of letter case only is the same key in the store.
        if (!string.Equals(name, newName, StringComparison.OrdinalIgnoreCase) && GetJson(kind, newName) is not null)
            throw new InvalidOperationException(L.T($"“{newName}” already exists."));

        var updated = new List<string>();
        if (kind == SettingKinds.Connection)
        {
            foreach (var (profileName, profileJson, _) in runs.Store.ListSettings(SettingKinds.Profile))
            {
                var profile = Profile.Parse(profileJson);
                var list = profile.Connections;
                int i = list.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                if (i < 0)
                    continue;
                list[i] = newName;
                runs.Store.PutSetting(SettingKinds.Profile, profileName, profile.ToJson(), DateTimeOffset.UtcNow);
                updated.Add(profileName);
            }
        }
        runs.Store.DeleteSetting(kind, name, DateTimeOffset.UtcNow);
        SaveJson(kind, newName, json);
        if (kind == SettingKinds.Connection)
            browser.Forget(name);
        return updated;
    }

    /// <summary>Profiles that use a connection — shown before deleting it.</summary>
    public IReadOnlyList<string> UsedBy(string kind, string name) => runs.Store.ListSettings(SettingKinds.Profile)
        .Select(p => Profile.Parse(p.Json))
        .Where(p => kind == SettingKinds.Connection && p.Connections.Contains(name, StringComparer.OrdinalIgnoreCase))
        .Select(p => p.Name!)
        .ToList();

    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0)
            throw new ArgumentException(L.T("Name: 1–100 characters, without / \\ ? # %."));
    }
}
