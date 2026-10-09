using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AccountingSystem.Core.Export;

/// <summary>یک برگه‌ی Excel: عنوان ستون‌ها و سطرها. مقدار null خانه‌ی خالی می‌شود.</summary>
public sealed record WorkbookSheet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>
/// سازنده‌ی ساده‌ی فایل xlsx بدون کتابخانه‌ی خارجی: یک بسته‌ی OpenXML با System.IO.Compression.
/// عددها به‌صورت عدد و بقیه به‌صورت متن ذخیره می‌شوند. برگه‌ها راست‌به‌چپ و سطر عنوان ثابت است.
/// </summary>
public static class XlsxWriter
{
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int MaxSheetNameLength = 31;
    private const int MaxWidthSampleRows = 500;

    private const string Styles =
        XmlDeclaration +
        "<styleSheet xmlns=\"" + SpreadsheetNamespace + "\">" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private const string RootRelationships =
        XmlDeclaration +
        "<Relationships xmlns=\"" + PackageRelationshipNamespace + "\">" +
        "<Relationship Id=\"rId1\" Type=\"" + RelationshipNamespace + "/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    /// <summary>بایت‌های یک فایل xlsx شامل برگه‌های داده‌شده.</summary>
    public static byte[] Build(IReadOnlyList<WorkbookSheet> sheets)
    {
        if (sheets.Count == 0)
        {
            throw new ArgumentException("حداقل یک برگه لازم است.", nameof(sheets));
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypes(sheets.Count));
            WriteEntry(archive, "_rels/.rels", RootRelationships);
            WriteEntry(archive, "xl/workbook.xml", Workbook(sheets));
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
            WriteEntry(archive, "xl/styles.xml", Styles);
            for (var i = 0; i < sheets.Count; i++)
            {
                WriteEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i]));
            }
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ContentTypes(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append(XmlDeclaration);
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i)
              .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string Workbook(IReadOnlyList<WorkbookSheet> sheets)
    {
        var sb = new StringBuilder();
        sb.Append(XmlDeclaration);
        sb.Append("<workbook xmlns=\"").Append(SpreadsheetNamespace).Append("\" xmlns:r=\"").Append(RelationshipNamespace).Append("\"><sheets>");
        for (var i = 0; i < sheets.Count; i++)
        {
            var name = Escape(SafeSheetName(sheets[i].Name, i));
            sb.Append("<sheet name=\"").Append(name).Append("\" sheetId=\"").Append(i + 1)
              .Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
        }
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private static string WorkbookRelationships(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append(XmlDeclaration);
        sb.Append("<Relationships xmlns=\"").Append(PackageRelationshipNamespace).Append("\">");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append("<Relationship Id=\"rId").Append(i).Append("\" Type=\"").Append(RelationshipNamespace)
              .Append("/worksheet\" Target=\"worksheets/sheet").Append(i).Append(".xml\"/>");
        }
        sb.Append("<Relationship Id=\"rId").Append(sheetCount + 1).Append("\" Type=\"").Append(RelationshipNamespace)
          .Append("/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string Worksheet(WorkbookSheet sheet)
    {
        var columnCount = sheet.Headers.Count;
        var sb = new StringBuilder();
        sb.Append(XmlDeclaration);
        sb.Append("<worksheet xmlns=\"").Append(SpreadsheetNamespace).Append("\">");
        sb.Append("<sheetViews><sheetView rightToLeft=\"1\" workbookViewId=\"0\">");
        sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        sb.Append("</sheetView></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");

        if (columnCount > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < columnCount; c++)
            {
                var width = ColumnWidth(sheet, c);
                sb.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1)
                  .Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        AppendRow(sb, 1, sheet.Headers.Select(h => (object?)h).ToList(), isHeader: true);
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            AppendRow(sb, r + 2, sheet.Rows[r], isHeader: false);
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, int rowNumber, IReadOnlyList<object?> values, bool isHeader)
    {
        sb.Append("<row r=\"").Append(rowNumber).Append("\">");
        for (var c = 0; c < values.Count; c++)
        {
            var value = values[c];
            if (value is null)
            {
                continue;
            }

            var reference = ColumnName(c) + rowNumber.ToString(CultureInfo.InvariantCulture);
            var style = isHeader ? " s=\"1\"" : string.Empty;
            switch (value)
            {
                case bool flag:
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(style).Append(" t=\"b\"><v>")
                      .Append(flag ? "1" : "0").Append("</v></c>");
                    break;
                case decimal or int or long or double or float or short or byte:
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(style).Append("><v>")
                      .Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</v></c>");
                    break;
                default:
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (text.Length == 0)
                    {
                        break;
                    }
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(style).Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(Escape(text)).Append("</t></is></c>");
                    break;
            }
        }
        sb.Append("</row>");
    }

    private static int ColumnWidth(WorkbookSheet sheet, int column)
    {
        var longest = sheet.Headers.Count > column ? sheet.Headers[column].Length : 0;
        foreach (var row in sheet.Rows.Take(MaxWidthSampleRows))
        {
            if (row.Count > column && row[column] is { } value)
            {
                var length = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Length;
                longest = Math.Max(longest, length);
            }
        }
        return Math.Clamp(longest + 4, 10, 60);
    }

    private static string SafeSheetName(string name, int index)
    {
        var cleaned = new string((name ?? string.Empty).Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Sheet" + (index + 1).ToString(CultureInfo.InvariantCulture);
        }
        return cleaned.Length > MaxSheetNameLength ? cleaned[..MaxSheetNameLength] : cleaned;
    }

    /// <summary>نام ستون به سبک Excel: 0 → A، 25 → Z، 26 → AA.</summary>
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        var number = index + 1;
        while (number > 0)
        {
            var remainder = (number - 1) % 26;
            name = (char)('A' + remainder) + name;
            number = (number - 1) / 26;
        }
        return name;
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
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
                case '\t':
                case '\n':
                case '\r':
                    sb.Append(ch);
                    break;
                default:
                    // کاراکترهای کنترلی و ناسازگار با XML جایگزین فاصله می‌شوند.
                    sb.Append((ch < 0x20 || ch is '\uFFFE' or '\uFFFF') ? ' ' : ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
