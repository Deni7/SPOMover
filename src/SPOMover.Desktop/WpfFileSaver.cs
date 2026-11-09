using System.IO;
using System.Windows;
using Microsoft.Win32;
using SPOMover.Core.Localization;
using SPOMover.Ui.Services;

namespace SPOMover.Desktop;

/// <summary>GUI-10 in the desktop app: the standard "Save as" dialog.</summary>
internal sealed class WpfFileSaver(Func<Window?> owner) : IFileSaver
{
    private static string? _lastFolder;

    public async Task<string?> SaveAsync(string suggestedName, Func<Stream, Task> write)
    {
        string? path = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            string ext = Path.GetExtension(suggestedName).TrimStart('.').ToLowerInvariant();
            var dialog = new SaveFileDialog
            {
                FileName = suggestedName,
                AddExtension = true,
                OverwritePrompt = true,
                Filter = ext switch
                {
                    "xlsx" => L.T("Excel workbook") + " (*.xlsx)|*.xlsx",
                    "csv" => "CSV (*.csv)|*.csv",
                    "json" => "JSON (*.json)|*.json",
                    "txt" => L.T("Text file") + " (*.txt)|*.txt",
                    _ => L.T("All files") + " (*.*)|*.*",
                },
                InitialDirectory = _lastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
        });
        if (path is null)
            return null;
        _lastFolder = Path.GetDirectoryName(path);
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            await write(stream);
        return path;
    }
}
