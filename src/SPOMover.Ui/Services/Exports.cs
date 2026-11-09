using SPOMover.Engine;
using SPOMover.Core.Reports;
using SPOMover.State;

namespace SPOMover.Ui.Services;

/// <summary>
/// Where the GUI puts a file the user exports (GUI-4): the desktop app asks with "Save as";
/// tests and other hosts may write elsewhere.
/// </summary>
public interface IFileSaver
{
    /// <param name="suggestedName">File name offered in the dialog, e.g. run-12-files.xlsx.</param>
    /// <returns>Where the file was saved, or null if the user cancelled.</returns>
    Task<string?> SaveAsync(string suggestedName, Func<Stream, Task> write);
}

/// <summary>Exports shared by the GUI and the REST API.</summary>
public static class Exports
{
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>GUI-4: the files table with a filter, as CSV or XLSX. Read in id order with keyset paging.</summary>
    public static void WriteFiles(StateStore store, long runId, FileFilter filter, string format, Stream output)
    {
        var rows = ReportExporter.FileRows(AllFiles(store, runId, filter));
        if (format == "xlsx")
            SimpleXlsxWriter.Write(output, "files", rows);
        else
            ReportExporter.WriteCsv(output, rows);
    }

    /// <summary>A run report (files, attention, summary) as CSV/TXT or XLSX.</summary>
    public static void WriteReport(StateStore store, long runId, string name, string format, Stream output)
    {
        if (format == "xlsx")
            ReportExporter.WriteXlsx(store, runId, name, output);
        else
            ReportExporter.Write(store, runId, name, output);
    }

    /// <summary>MAP-5: a report of the mapping check (coverage, unmapped, problems, preview) as CSV or XLSX.</summary>
    public static void WriteMappingReport(StateStore store, Core.Config.Profile profile, MappingReport report, string name, string format, Stream output)
    {
        if (format == "xlsx")
            MappingCheck.WriteXlsx(store, profile, report, name, output);
        else
            MappingCheck.WriteCsv(store, profile, report, name, output);
    }

    /// <summary>PLN-5: a report of a plan (batches, waves, folders, conflicts, files) as CSV or XLSX.</summary>
    public static void WritePlanReport(StateStore store, long planId, string name, string format, Stream output)
    {
        if (format == "xlsx")
            Planner.WriteXlsx(store, planId, name, output);
        else
            Planner.WriteCsv(store, planId, name, output);
    }

    public static string PlanFileName(string profile, long planId, string name, string format) =>
        Planner.FileName(SaveFile.Name(profile), planId, name, format == "xlsx" ? "xlsx" : "csv");

    public static string MappingFileName(string profile, string name, string format) =>
        $"{SaveFile.Name(profile)}-mapping-{name}.{(format == "xlsx" ? "xlsx" : "csv")}";

    public static string ReportFileName(long runId, string name, string format) =>
        $"run-{runId}-{name}.{(format == "xlsx" ? "xlsx" : ReportExporter.IsText(name) ? "txt" : "csv")}";

    public static IEnumerable<FileRow> AllFiles(StateStore store, long runId, FileFilter filter)
    {
        long after = 0;
        while (true)
        {
            var page = store.QueryFilesAfter(runId, filter, after, 5000);
            foreach (var f in page)
                yield return f;
            if (page.Count < 5000)
                yield break;
            after = page[^1].Id;
        }
    }
}
