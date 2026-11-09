using System.Diagnostics;
using System.Text.Json;

namespace SPOMover.Core.Hosting;

/// <summary>
/// Where a running app listens and the key its local API expects. Written to the data folder on start (which is per
/// user by default), so several users of one machine each get their own instance and port, and the CLI of a user finds
/// that user's instance without knowing the port.
/// </summary>
public sealed record ApiEndpoint(string Url, string Key, int ProcessId)
{
    public const string FileName = "api.json";
    public const string KeyHeader = "X-SPO-RL-Key";
    public const string DefaultUrl = "http://localhost:5090";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>%LOCALAPPDATA%\SPOMover unless a folder is given.</summary>
    public static string DataDirectory(string? configured) =>
        configured ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPOMover");

    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    public void Write(string dataDir)
    {
        string path = PathIn(dataDir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The endpoint of a running instance on these data, or null when none is running.</summary>
    public static ApiEndpoint? Read(string dataDir)
    {
        try
        {
            var endpoint = JsonSerializer.Deserialize<ApiEndpoint>(File.ReadAllText(PathIn(dataDir)), Json);
            return endpoint is { Url.Length: > 0, Key.Length: > 0 } && IsAlive(endpoint.ProcessId) ? endpoint : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Removes the file if it still describes this process (a newer instance may have replaced it).</summary>
    public static void Delete(string dataDir, int processId)
    {
        try
        {
            string path = PathIn(dataDir);
            var endpoint = JsonSerializer.Deserialize<ApiEndpoint>(File.ReadAllText(path), Json);
            if (endpoint?.ProcessId == processId)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // exists, but belongs to an account whose processes we may not query
        }
    }
}
