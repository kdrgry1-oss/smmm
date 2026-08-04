using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Harici NuGet paketi OLMADAN geçerli .xlsx üreten minimal yazıcı (yalnızca built-in
/// System.IO.Compression kullanır). OOXML (ECMA-376) paketinin zorunlu asgari parçalarını yazar:
/// [Content_Types].xml, _rels/.rels, xl/workbook.xml, xl/_rels/workbook.xml.rels,
/// xl/worksheets/sheet1.xml ve basit xl/styles.xml.
///
/// Bilinçli sadeleştirmeler:
///  - sharedStrings.xml YOK — metin hücreleri inline string (t="inlineStr") olarak yazılır;
///    daha basittir ve spesifikasyona göre tamamen geçerlidir (Excel/LibreOffice/Luca açar).
///  - Tarihler METİN olarak yazılır ("dd.MM.yyyy") — Excel seri numarası + numFmt riskine girmeden
///    Luca içe aktarmanın kabul ettiği güvenli yol.
///  - Sayılar (Borç/Alacak/Kur/Döviz Tutar) t özniteliksiz NUMERİK hücredir; ondalık ayraç
///    her zaman NOKTA ile (InvariantCulture) yazılır — OOXML böyle ister, Excel ekranda
///    kullanıcının yerel ayarına göre (virgül) gösterir.
/// </summary>
public static class MinimalXlsxWriter
{
    /// <summary>Hücre türü: boş (hiç yazılmaz), metin (inlineStr) veya sayı (numerik).</summary>
    public enum CellKind { Empty = 0, Text = 1, Number = 2 }

    /// <summary>Tek hücre değeri. Cell.Empty ile boş hücre — 0 DEĞİL, gerçekten boş — üretilir.</summary>
    public readonly struct Cell
    {
        public CellKind Kind { get; }
        public string TextValue { get; }
        public decimal NumberValue { get; }

        private Cell(CellKind kind, string text, decimal number)
        {
            Kind = kind;
            TextValue = text;
            NumberValue = number;
        }

        public static readonly Cell Empty = new(CellKind.Empty, string.Empty, 0m);

        /// <summary>Metin hücresi; null/boş metin boş hücreye dönüşür.</summary>
        public static Cell Text(string? value)
            => string.IsNullOrEmpty(value) ? Empty : new Cell(CellKind.Text, value, 0m);

        /// <summary>Numerik hücre; null boş hücreye dönüşür (0 yazılmaz).</summary>
        public static Cell Number(decimal? value)
            => value is null ? Empty : new Cell(CellKind.Number, string.Empty, value.Value);
    }

    /// <summary>
    /// Tek sayfalık .xlsx üretir. Başlık satırı kalın (bold) stille 1. satıra, veri satırları
    /// 2. satırdan itibaren yazılır. sheetName en fazla 31 karakter olabilir (Excel sınırı).
    /// </summary>
    public static byte[] Build(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<Cell>> rows)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
            throw new ArgumentException("Sayfa adı boş olamaz.", nameof(sheetName));
        if (sheetName.Length > 31)
            throw new ArgumentException("Sayfa adı en fazla 31 karakter olabilir (Excel sınırı).", nameof(sheetName));
        if (headers.Count == 0)
            throw new ArgumentException("En az bir başlık kolonu gereklidir.", nameof(headers));

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
            WriteEntry(zip, "_rels/.rels", RootRelsXml);
            WriteEntry(zip, "xl/workbook.xml", BuildWorkbookXml(sheetName));
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            WriteEntry(zip, "xl/styles.xml", StylesXml);
            WriteEntry(zip, "xl/worksheets/sheet1.xml", BuildSheetXml(headers, rows));
        }

        return stream.ToArray();
    }

    // ---------- Sabit paket parçaları ----------

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    /// <summary>
    /// Basit stil tablosu: fontId 0 = normal, fontId 1 = kalın (başlık satırı, cellXfs s="1").
    /// fills/borders/cellStyleXfs OOXML'in zorunlu asgari kayıtlarıdır.
    /// </summary>
    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\">" +
        "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "</fonts>" +
        "<fills count=\"2\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "</fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"2\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private static string BuildWorkbookXml(string sheetName) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        $"<sheets><sheet name=\"{EscapeXml(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    // ---------- Sayfa üretimi ----------

    private static string BuildSheetXml(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<Cell>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetData>");

        // Başlık satırı (kalın stil s="1").
        sb.Append("<row r=\"1\">");
        for (var c = 0; c < headers.Count; c++)
        {
            sb.Append("<c r=\"").Append(CellRef(c, 1)).Append("\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
              .Append(EscapeXml(headers[c]))
              .Append("</t></is></c>");
        }
        sb.Append("</row>");

        // Veri satırları — boş hücreler hiç yazılmaz (Luca "boş" ile "0"ı ayırt eder).
        for (var r = 0; r < rows.Count; r++)
        {
            var rowNumber = r + 2;
            sb.Append("<row r=\"").Append(rowNumber).Append("\">");
            var cells = rows[r];
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];
                switch (cell.Kind)
                {
                    case CellKind.Text:
                        sb.Append("<c r=\"").Append(CellRef(c, rowNumber)).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                          .Append(EscapeXml(cell.TextValue))
                          .Append("</t></is></c>");
                        break;
                    case CellKind.Number:
                        sb.Append("<c r=\"").Append(CellRef(c, rowNumber)).Append("\"><v>")
                          .Append(cell.NumberValue.ToString("0.############", CultureInfo.InvariantCulture))
                          .Append("</v></c>");
                        break;
                    // CellKind.Empty: hücre yazılmaz.
                }
            }
            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    /// <summary>0 tabanlı kolon indeksi + 1 tabanlı satır → "A1" biçimli hücre referansı.</summary>
    private static string CellRef(int columnIndex, int rowNumber)
    {
        var column = string.Empty;
        var i = columnIndex;
        while (i >= 0)
        {
            column = (char)('A' + i % 26) + column;
            i = i / 26 - 1;
        }
        return column + rowNumber.ToString(CultureInfo.InvariantCulture);
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        writer.Write(bytes, 0, bytes.Length);
    }
}
