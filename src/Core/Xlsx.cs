using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Jellyfin.Plugin.CollectionStudio.Core;

/// <summary>
/// One worksheet of an <see cref="Xlsx"/> workbook: a header row plus appended data rows.
/// </summary>
public sealed class XlsxSheet
{
    private readonly string[] _headers;
    private readonly List<object?[]> _rows = new();
    private string _name;

    /// <summary>
    /// Creates a worksheet with a display name and the header row shown on row 1.
    /// </summary>
    public XlsxSheet(string name, params string[] headers)
    {
        _name = name ?? string.Empty;

        var source = headers ?? Array.Empty<string>();
        var count = source.Length;
        if (count > XlsxXml.MaxColumnIndex)
        {
            count = XlsxXml.MaxColumnIndex;
        }

        _headers = new string[count];
        for (var i = 0; i < count; i++)
        {
            _headers[i] = source[i] ?? string.Empty;
        }
    }

    /// <summary>Gets or sets the display name written into the workbook.</summary>
    public string Name
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    /// <summary>Gets the header row.</summary>
    public IReadOnlyList<string> Headers => _headers;

    /// <summary>Gets the number of appended data rows.</summary>
    public int RowCount => _rows.Count;

    /// <summary>
    /// Appends one data row. Values beyond the 16384-column limit are dropped, and rows beyond the
    /// 1048576-row sheet limit are ignored, so callers never have to guard the sheet size.
    /// </summary>
    public void AddRow(params object?[] cells)
    {
        var source = cells ?? Array.Empty<object?>();
        var maxDataRows = XlsxXml.MaxRowIndex - (HasHeaders ? 1 : 0);
        if (_rows.Count >= maxDataRows)
        {
            return;
        }

        var limit = source.Length > XlsxXml.MaxColumnIndex ? XlsxXml.MaxColumnIndex : source.Length;
        object?[] stored;
        if (limit == 0)
        {
            stored = Array.Empty<object?>();
        }
        else
        {
            stored = new object?[limit];
            Array.Copy(source, stored, limit);
        }

        _rows.Add(stored);
    }

    internal bool HasHeaders => _headers.Length > 0;

    internal string[] HeaderArray => _headers;

    internal object?[] GetRow(int index) => _rows[index];
}

/// <summary>
/// Builds dependency-free Office Open XML (.xlsx) workbooks: BCL only, no NuGet packages.
/// </summary>
public static class Xlsx
{
    /// <summary>Builds a complete .xlsx package and returns its bytes.</summary>
    public static byte[] Build(IReadOnlyList<XlsxSheet> sheets)
    {
        var list = new List<XlsxSheet>();
        var names = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (sheets != null)
        {
            foreach (var sheet in sheets)
            {
                if (sheet == null)
                {
                    continue;
                }

                list.Add(sheet);
                names.Add(XlsxXml.UniqueSheetName(sheet.Name, used));
            }
        }

        if (list.Count == 0)
        {
            var fallback = new XlsxSheet("Sheet1");
            list.Add(fallback);
            names.Add(XlsxXml.UniqueSheetName(fallback.Name, used));
        }

        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            XlsxXml.AddEntry(zip, "[Content_Types].xml", XlsxXml.ContentTypesXml(names.Count));
            XlsxXml.AddEntry(zip, "_rels/.rels", XlsxXml.RootRelsXml());
            XlsxXml.AddEntry(zip, "xl/workbook.xml", XlsxXml.WorkbookXml(names));
            XlsxXml.AddEntry(zip, "xl/_rels/workbook.xml.rels", XlsxXml.WorkbookRelsXml(names.Count));
            XlsxXml.AddEntry(zip, "xl/styles.xml", XlsxXml.StylesXml());
            for (var i = 0; i < list.Count; i++)
            {
                var path = "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml";
                XlsxXml.AddEntry(zip, path, XlsxXml.SheetXml(list[i]));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Convenience overload for a single sheet.</summary>
    public static byte[] BuildSingle(
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows)
    {
        string[] headerArray;
        if (headers == null)
        {
            headerArray = Array.Empty<string>();
        }
        else
        {
            headerArray = new string[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                headerArray[i] = headers[i] ?? string.Empty;
            }
        }

        var sheet = new XlsxSheet(sheetName ?? string.Empty, headerArray);
        if (rows != null)
        {
            foreach (var row in rows)
            {
                if (row == null)
                {
                    sheet.AddRow();
                    continue;
                }

                var cells = new object?[row.Count];
                for (var i = 0; i < row.Count; i++)
                {
                    cells[i] = row[i];
                }

                sheet.AddRow(cells);
            }
        }

        return Build(new[] { sheet });
    }
}

/// <summary>
/// XML writers and OOXML limits shared by <see cref="Xlsx"/>.
/// </summary>
internal static class XlsxXml
{
    internal const int MaxRowIndex = 1048576;
    internal const int MaxColumnIndex = 16384;
    internal const int MaxCellTextLength = 32767;
    internal const int MaxSheetNameLength = 31;

    private const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly long DateEpochTicks =
        new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified).Ticks;

    private static string[]? _columnRefs;

    internal static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static string ContentTypesXml(int sheetCount)
    {
        var sb = new StringBuilder(512);
        sb.Append(XmlHeader);
        sb.Append("<Types xmlns=\"").Append(ContentTypesNs).Append("\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append("<Override PartName=\"/xl/worksheets/sheet")
                .Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        sb.Append("</Types>");
        return sb.ToString();
    }

    internal static string RootRelsXml()
    {
        var sb = new StringBuilder(256);
        sb.Append(XmlHeader);
        sb.Append("<Relationships xmlns=\"").Append(PackageRelationshipNs).Append("\">");
        sb.Append("<Relationship Id=\"rId1\" Type=\"").Append(RelationshipNs).Append("/officeDocument\" Target=\"xl/workbook.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    internal static string WorkbookXml(IReadOnlyList<string> sheetNames)
    {
        var sb = new StringBuilder(512);
        sb.Append(XmlHeader);
        sb.Append("<workbook xmlns=\"").Append(SpreadsheetNs).Append("\" xmlns:r=\"").Append(RelationshipNs).Append("\">");
        sb.Append("<bookViews><workbookView activeTab=\"0\"/></bookViews>");
        sb.Append("<sheets>");
        for (var i = 0; i < sheetNames.Count; i++)
        {
            var id = (i + 1).ToString(CultureInfo.InvariantCulture);
            sb.Append("<sheet name=\"");
            AppendEscapedText(sb, sheetNames[i], MaxSheetNameLength);
            sb.Append("\" sheetId=\"").Append(id).Append("\" r:id=\"rId").Append(id).Append("\"/>");
        }

        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    internal static string WorkbookRelsXml(int sheetCount)
    {
        var sb = new StringBuilder(512);
        sb.Append(XmlHeader);
        sb.Append("<Relationships xmlns=\"").Append(PackageRelationshipNs).Append("\">");
        for (var i = 1; i <= sheetCount; i++)
        {
            var id = i.ToString(CultureInfo.InvariantCulture);
            sb.Append("<Relationship Id=\"rId").Append(id)
                .Append("\" Type=\"").Append(RelationshipNs).Append("/worksheet\" Target=\"worksheets/sheet")
                .Append(id).Append(".xml\"/>");
        }

        sb.Append("<Relationship Id=\"rId").Append((sheetCount + 1).ToString(CultureInfo.InvariantCulture))
            .Append("\" Type=\"").Append(RelationshipNs).Append("/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    internal static string StylesXml()
    {
        var sb = new StringBuilder(768);
        sb.Append(XmlHeader);
        sb.Append("<styleSheet xmlns=\"").Append(SpreadsheetNs).Append("\">");
        sb.Append("<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm:ss\"/></numFmts>");
        sb.Append("<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>");
        sb.Append("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>");
        sb.Append("<borders count=\"1\"><border/></borders>");
        sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        sb.Append("<cellXfs count=\"2\">");
        sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
        sb.Append("<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>");
        sb.Append("</cellXfs>");
        sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
        sb.Append("</styleSheet>");
        return sb.ToString();
    }

    internal static string SheetXml(XlsxSheet sheet)
    {
        var sb = new StringBuilder(16384);
        sb.Append(XmlHeader);
        sb.Append("<worksheet xmlns=\"").Append(SpreadsheetNs).Append("\">");

        var columnCount = sheet.HeaderArray.Length;
        for (var i = 0; i < sheet.RowCount; i++)
        {
            var width = sheet.GetRow(i).Length;
            if (width > columnCount)
            {
                columnCount = width;
            }
        }

        if (columnCount > MaxColumnIndex)
        {
            columnCount = MaxColumnIndex;
        }

        if (columnCount < 1)
        {
            columnCount = 1;
        }

        var totalRows = sheet.RowCount + (sheet.HasHeaders ? 1 : 0);
        if (totalRows < 1)
        {
            totalRows = 1;
        }

        sb.Append("<dimension ref=\"A1:").Append(ColumnRefs()[columnCount])
            .Append(totalRows.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
        sb.Append("<sheetData>");

        var rowIndex = 1;
        if (sheet.HasHeaders)
        {
            AppendRow(sb, rowIndex, sheet.HeaderArray, columnCount);
            rowIndex++;
        }

        for (var i = 0; i < sheet.RowCount; i++)
        {
            AppendRow(sb, rowIndex, sheet.GetRow(i), columnCount);
            rowIndex++;
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, int rowIndex, object?[] cells, int columnCount)
    {
        sb.Append("<row r=\"").Append(rowIndex.ToString(CultureInfo.InvariantCulture)).Append("\">");
        var limit = cells.Length < columnCount ? cells.Length : columnCount;
        for (var i = 0; i < limit; i++)
        {
            var value = cells[i];
            if (value == null)
            {
                continue;
            }

            AppendCell(sb, rowIndex, i, value);
        }

        sb.Append("</row>");
    }

    private static void AppendCell(StringBuilder sb, int rowIndex, int columnIndex, object value)
    {
        var reference = ColumnRefs()[columnIndex + 1] + rowIndex.ToString(CultureInfo.InvariantCulture);
        switch (value)
        {
            case string text:
                AppendInlineTextCell(sb, reference, text);
                return;
            case bool flag:
                sb.Append("<c r=\"").Append(reference).Append("\" t=\"b\"><v>")
                    .Append(flag ? "1" : "0").Append("</v></c>");
                return;
            case DateTime date:
                AppendDateCell(sb, reference, date);
                return;
            case DateTimeOffset dateOffset:
                AppendDateCell(sb, reference, dateOffset.DateTime);
                return;
            case sbyte or byte or short or ushort or int or uint:
                AppendNumberCell(sb, reference, Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            case long number:
                AppendNumberCell(sb, reference, number.ToString(CultureInfo.InvariantCulture));
                return;
            case ulong number:
                AppendNumberCell(sb, reference, number.ToString(CultureInfo.InvariantCulture));
                return;
            case decimal number:
                AppendNumberCell(sb, reference, number.ToString(CultureInfo.InvariantCulture));
                return;
            case double number:
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    AppendInlineTextCell(sb, reference, number.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                AppendNumberCell(sb, reference, FormatNumber(number));
                return;
            case float number:
                if (float.IsNaN(number) || float.IsInfinity(number))
                {
                    AppendInlineTextCell(sb, reference, number.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                AppendNumberCell(sb, reference, FormatNumber(number));
                return;
            default:
                AppendInlineTextCell(sb, reference, value.ToString() ?? string.Empty);
                return;
        }
    }

    private static void AppendInlineTextCell(StringBuilder sb, string reference, string text)
    {
        sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
        AppendEscapedText(sb, text, MaxCellTextLength);
        sb.Append("</t></is></c>");
    }

    private static void AppendNumberCell(StringBuilder sb, string reference, string number)
    {
        sb.Append("<c r=\"").Append(reference).Append("\" t=\"n\"><v>").Append(number).Append("</v></c>");
    }

    private static void AppendDateCell(StringBuilder sb, string reference, DateTime value)
    {
        sb.Append("<c r=\"").Append(reference).Append("\" t=\"n\" s=\"1\"><v>")
            .Append(FormatNumber(DateToSerial(value))).Append("</v></c>");
    }

    internal static double DateToSerial(DateTime value)
    {
        var serial = (value.Ticks - DateEpochTicks) / (double)TimeSpan.TicksPerDay;
        if (value < new DateTime(1900, 3, 1, 0, 0, 0, DateTimeKind.Unspecified))
        {
            serial -= 1.0;
        }

        return Math.Round(serial, 10, MidpointRounding.AwayFromZero);
    }

    internal static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static void AppendEscapedText(StringBuilder sb, string? value, int maxLength)
    {
        if (value == null)
        {
            return;
        }

        var text = Truncate(value, maxLength);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                case '\'':
                    sb.Append("&apos;");
                    break;
                default:
                    if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c != 0x7F && c != 0xFFFE && c != 0xFFFF))
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }
    }

    internal static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value.Substring(0, cut);
    }

    internal static string UniqueSheetName(string? raw, HashSet<string> used)
    {
        var baseName = SanitizeSheetName(raw);
        var candidate = baseName;
        var index = 1;
        while (!used.Add(candidate.ToUpperInvariant()))
        {
            index++;
            var suffix = "_" + index.ToString(CultureInfo.InvariantCulture);
            var room = MaxSheetNameLength - suffix.Length;
            var head = baseName.Length > room ? Truncate(baseName, room) : baseName;
            candidate = head + suffix;
        }

        return candidate;
    }

    private static string SanitizeSheetName(string? raw)
    {
        var source = raw ?? string.Empty;
        var sb = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\' || c == '/' || c == '?' || c == '*' || c == '[' || c == ']' || c == ':')
            {
                sb.Append('_');
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    sb.Append(c).Append(source[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            if (c < 0x20 || c == 0x7F || c == 0xFFFE || c == 0xFFFF)
            {
                continue;
            }

            sb.Append(c);
        }

        var name = sb.ToString().Trim();
        if (name.Length == 0)
        {
            name = "Sheet";
        }

        name = Truncate(name, MaxSheetNameLength);
        if (name.Length > 0 && name[0] == '\'')
        {
            name = "_" + name.Substring(1);
        }

        if (name.Length > 0 && name[name.Length - 1] == '\'')
        {
            name = name.Substring(0, name.Length - 1) + "_";
        }

        return name;
    }

    private static string[] ColumnRefs()
    {
        var cached = _columnRefs;
        if (cached != null)
        {
            return cached;
        }

        var refs = new string[MaxColumnIndex + 1];
        refs[0] = "A";
        for (var i = 1; i <= MaxColumnIndex; i++)
        {
            var sb = new StringBuilder(3);
            var value = i;
            while (value > 0)
            {
                var remainder = (value - 1) % 26;
                sb.Insert(0, (char)('A' + remainder));
                value = (value - 1) / 26;
            }

            refs[i] = sb.ToString();
        }

        _columnRefs = refs;
        return refs;
    }
}
