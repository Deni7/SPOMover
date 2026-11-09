using System.IO.Compression;
using System.Text;
using System.Xml;
using SPOMover.Core.Localization;

namespace SPOMover.Core.Reports;

/// <summary>
/// Minimal xlsx writer for exports (GUI-10): one sheet, text cells as inline strings, numbers as numbers,
/// the first row bold and frozen. Opens in Excel without repair.
/// </summary>
public static class SimpleXlsxWriter
{
    public static void Write(Stream output, string sheetName, IEnumerable<IReadOnlyList<string?>> rows)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Part(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }

        Part("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Part("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Part("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
            $"<sheet name=\"{Escape(SheetName(sheetName))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Part("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

        using var sheet = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        sheet.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetData>");
        int r = 0;
        foreach (var row in rows)
        {
            r++;
            sheet.Write($"<row r=\"{r}\">");
            for (int c = 0; c < row.Count; c++)
            {
                string? v = row[c];
                if (string.IsNullOrEmpty(v))
                    continue;
                string reference = Column(c) + r;
                string style = r == 1 ? " s=\"1\"" : string.Empty;
                if (r > 1 && v.Length < 16 && long.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
                    sheet.Write($"<c r=\"{reference}\"{style}><v>{v}</v></c>");
                else
                    sheet.Write($"<c r=\"{reference}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Escape(Truncate(v))}</t></is></c>");
            }
            sheet.Write("</row>");
        }
        sheet.Write("</sheetData></worksheet>");
    }

    public static string Column(int index)
    {
        var sb = new StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + ((n - 1) % 26)));
        return sb.ToString();
    }

    // Excel allows 32 767 characters per cell.
    private static string Truncate(string v) => v.Length > 32_000 ? v[..32_000] + "…" : v;

    private static string SheetName(string name)
    {
        var clean = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        return clean.Length == 0 ? "Sheet1" : clean.Length > 31 ? clean[..31] : clean;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
                continue; // not allowed in XML 1.0
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }
        return sb.ToString();
    }
}

/// <summary>Reads the first sheet of an xlsx as rows of text (for importing rule tables, GUI-23).</summary>
public static class SimpleXlsxReader
{
    public static List<string[]> ReadFirstSheet(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
        {
            using var r = XmlReader.Create(sst.Open(), Settings);
            while (r.ReadToFollowing("si", Ns))
                shared.Add(InnerText(r.ReadSubtree()));
        }

        string sheetPath = FirstSheetPath(zip);
        var entry = zip.GetEntry(sheetPath) ?? throw new InvalidDataException(L.T("The workbook has no sheet."));
        var rows = new List<string[]>();
        using var reader = XmlReader.Create(entry.Open(), Settings);
        while (reader.ReadToFollowing("row", Ns))
        {
            var cells = new SortedDictionary<int, string>();
            using var row = reader.ReadSubtree();
            while (row.ReadToFollowing("c", Ns))
            {
                string reference = row.GetAttribute("r") ?? string.Empty;
                string? type = row.GetAttribute("t");
                using var cell = row.ReadSubtree();
                string? value = null;
                while (cell.Read())
                {
                    if (cell.NodeType != XmlNodeType.Element)
                        continue;
                    if (cell.LocalName == "v")
                        value = cell.ReadElementContentAsString();
                    else if (cell.LocalName == "is")
                        value = InnerText(cell.ReadSubtree());
                }
                if (value is null)
                    continue;
                if (type == "s" && int.TryParse(value, out int i) && i < shared.Count)
                    value = shared[i];
                cells[ColumnIndex(reference, cells.Count == 0 ? 0 : cells.Keys.Max() + 1)] = value;
            }
            if (cells.Count == 0)
                continue;
            var arr = new string[cells.Keys.Max() + 1];
            for (int k = 0; k < arr.Length; k++)
                arr[k] = cells.TryGetValue(k, out var v) ? v : string.Empty;
            rows.Add(arr);
        }
        return rows;
    }

    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };

    private static string FirstSheetPath(ZipArchive zip)
    {
        var wb = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException(L.T("This is not an Excel workbook (no xl/workbook.xml)."));
        string? rid = null;
        using (var r = XmlReader.Create(wb.Open(), Settings))
        {
            if (r.ReadToFollowing("sheet", Ns))
                rid = r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        }
        if (rid is not null && zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            using var r = XmlReader.Create(rels.Open(), Settings);
            while (r.ReadToFollowing("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships"))
            {
                if (r.GetAttribute("Id") == rid)
                {
                    string target = r.GetAttribute("Target")!;
                    return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                }
            }
        }
        return "xl/worksheets/sheet1.xml";
    }

    private static string InnerText(XmlReader r)
    {
        var sb = new StringBuilder();
        using (r)
        {
            while (r.Read())
            {
                // Phonetic runs (rPh) are not part of the value.
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "rPh")
                    r.Skip();
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t")
                    sb.Append(r.ReadElementContentAsString());
            }
        }
        return sb.ToString();
    }

    private static int ColumnIndex(string reference, int fallback)
    {
        int n = 0;
        foreach (char ch in reference)
        {
            if (!char.IsAsciiLetter(ch))
                break;
            n = (n * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n == 0 ? fallback : n - 1;
    }
}
