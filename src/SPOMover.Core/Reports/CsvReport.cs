namespace SPOMover.Core.Reports;

/// <summary>CSV helpers for reports. Files are written as UTF-8 with BOM so Excel shows Cyrillic correctly.</summary>
public static class CsvReport
{
    public static string Escape(string? value, char delimiter)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        // A leading = + - @ would be run as a formula when the report is opened in Excel.
        if (value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        bool quote = value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0;
        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
