namespace SPOMover.Core.Urls;

/// <summary>How a link is written, which decides how it is resolved before rules apply.</summary>
public enum LinkForm
{
    /// <summary>http:// or https://</summary>
    Http,
    /// <summary>\\server\share or //server/share</summary>
    Unc,
    /// <summary>C:\path or C:/path</summary>
    Drive,
    /// <summary>file:... in any of its spellings</summary>
    File,
    /// <summary>/sites/x/... — absolute path without host</summary>
    ServerRelative,
    /// <summary>../a.xlsx, Folder\b.xlsx</summary>
    Relative,
    /// <summary>#Sheet1!A1 — inside the same workbook</summary>
    Internal,
    /// <summary>mailto:, tel:, and other schemes</summary>
    OtherScheme,
    Empty,
}

public static class LinkClassifier
{
    public static LinkForm Classify(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
            return LinkForm.Empty;

        var s = link.AsSpan();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return LinkForm.Http;
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return LinkForm.File;
        if (s.Length >= 2 && IsSlash(s[0]) && IsSlash(s[1]))
            return LinkForm.Unc;
        if (s.Length >= 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && IsSlash(s[2]))
            return LinkForm.Drive;
        if (IsSlash(s[0]))
            return LinkForm.ServerRelative;
        if (s[0] == '#')
            return LinkForm.Internal;

        int colon = s.IndexOf(':');
        if (colon > 0)
        {
            int sep = s.IndexOfAny('/', '\\', '?');
            if (sep < 0 || colon < sep)
                return LinkForm.OtherScheme;
        }
        return LinkForm.Relative;
    }

    /// <summary>Forms that identify a location on their own, without a base.</summary>
    public static bool IsAbsolute(LinkForm form) =>
        form is LinkForm.Http or LinkForm.Unc or LinkForm.Drive or LinkForm.File;

    internal static bool IsSlash(char c) => c is '/' or '\\';
}
