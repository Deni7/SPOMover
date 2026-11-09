using System.Text;
using MudBlazor;
using SPOMover.Core.Localization;

namespace SPOMover.Ui.Services;

/// <summary>"Save as" for exports from pages, with a message where the file went.</summary>
public static class SaveFile
{
    public static async Task Run(IFileSaver saver, ISnackbar snackbar, string suggestedName, Action<Stream> write)
    {
        try
        {
            // Large exports are built off the UI thread.
            string? path = await saver.SaveAsync(suggestedName, stream => Task.Run(() => write(stream)));
            if (path is not null)
                snackbar.Add(L.T($"Saved: {path}"), Severity.Success);
        }
        // A report that cannot be built (unknown run, damaged settings snapshot) is a message, not a broken page.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Core.Config.ProfileException)
        {
            snackbar.Add(L.T($"Not saved: {ex.Message}"), Severity.Error);
        }
    }

    public static Task Text(IFileSaver saver, ISnackbar snackbar, string suggestedName, string text) =>
        Run(saver, snackbar, suggestedName, s => s.Write(Encoding.UTF8.GetBytes(text)));

    public static Task Bytes(IFileSaver saver, ISnackbar snackbar, string suggestedName, byte[] bytes) =>
        Run(saver, snackbar, suggestedName, s => s.Write(bytes));

    /// <summary>A safe file name from a setting name.</summary>
    public static string Name(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
