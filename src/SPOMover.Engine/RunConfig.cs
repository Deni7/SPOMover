using System.Text.Json;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;

namespace SPOMover.Engine;

/// <summary>
/// CFG-5: everything a run works with, frozen at its start — the profile and the connections it uses.
/// Later edits of the profile do not affect the run.
/// </summary>
public sealed class RunConfig
{
    public Profile Profile { get; set; } = new();

    public List<Connection> Connections { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, Profile.JsonOptions);

    public static RunConfig Parse(string json) =>
        JsonSerializer.Deserialize<RunConfig>(json, Profile.JsonOptions) ?? throw new ProfileException(L.T("Empty settings snapshot."));
}
