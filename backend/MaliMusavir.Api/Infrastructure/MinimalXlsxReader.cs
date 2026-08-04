using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Harici NuGet paketi OLMADAN .xlsx okuyan minimal okuyucu (MinimalXlsxWriter'ın karşılığı;
/// yalnızca built-in System.IO.Compression + XDocument kullanır). İlk sayfayı
/// (xl/worksheets/sheet1.xml) satır satır okur ve her satırı 0 tabanlı kolon indeksiyle
/// hizalanmış string listesine çevirir.
///
/// Desteklenen hücre türleri:
///  - t="s"        → sharedStrings.xml'deki paylaşılan metin (indeksle çözülür),
///  - t="inlineStr" → satır içi metin (is/t — MinimalXlsxWriter'ın yazdığı biçim),
///  - t="str"      → formül sonucu metin (v),
///  - t özniteliksiz → numerik hücre; InvariantCulture ile okunur ve nokta ondalıklı
///    string olarak döner (VKN gibi tam sayılar bilimsel gösterimden arındırılır).
/// Kolon konumu r özniteliğinden ("C5" → kolon 2) çözülür; r yoksa sıradaki kolon kullanılır.
/// </summary>
public static class MinimalXlsxReader
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>
    /// İlk sayfanın satırlarını döndürür. Her satır, en soldaki kolondan (A) itibaren
    /// hizalanmış hücre metinleri listesidir (boş hücreler boş string). Sayfa bulunamazsa
    /// veya paket geçersizse InvalidDataException fırlatır (çağıran Türkçe mesaja çevirir).
    /// </summary>
    public static List<List<string>> ReadFirstSheet(Stream stream, int maxRows = 5000)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        if (sheetEntry is null)
            throw new InvalidDataException("Çalışma sayfası bulunamadı (xl/worksheets/sheet1.xml yok).");

        var sharedStrings = ReadSharedStrings(zip);

        XDocument sheetDoc;
        using (var sheetStream = sheetEntry.Open())
        {
            sheetDoc = XDocument.Load(sheetStream);
        }

        var rows = new List<List<string>>();

        foreach (var rowEl in sheetDoc.Descendants(Ns + "row"))
        {
            if (rows.Count >= maxRows) break;

            var cells = new List<string>();
            var nextColumn = 0; // r özniteliksiz hücreler için sıradaki kolon

            foreach (var cellEl in rowEl.Elements(Ns + "c"))
            {
                var reference = (string?)cellEl.Attribute("r");
                var columnIndex = reference is null ? nextColumn : ColumnIndexFromReference(reference);
                nextColumn = columnIndex + 1;

                // Aradaki boş kolonlar boş string ile doldurulur (kolon hizası korunur).
                while (cells.Count < columnIndex) cells.Add(string.Empty);

                var value = ReadCellValue(cellEl, sharedStrings);
                if (cells.Count == columnIndex) cells.Add(value);
                else cells[columnIndex] = value;
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>sharedStrings.xml varsa paylaşılan metin tablosunu okur (yoksa boş liste).</summary>
    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();

        XDocument doc;
        using (var stream = entry.Open())
        {
            doc = XDocument.Load(stream);
        }

        // Her <si> içindeki tüm <t> parçaları birleştirilir (zengin metin runs desteği).
        return doc.Descendants(Ns + "si")
            .Select(si => string.Concat(si.Descendants(Ns + "t").Select(t => (string)t)))
            .ToList();
    }

    /// <summary>Tek hücrenin metin değerini türüne göre çözer.</summary>
    private static string ReadCellValue(XElement cellEl, List<string> sharedStrings)
    {
        var type = (string?)cellEl.Attribute("t");

        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            var isEl = cellEl.Element(Ns + "is");
            return isEl is null
                ? string.Empty
                : string.Concat(isEl.Descendants(Ns + "t").Select(t => (string)t)).Trim();
        }

        var raw = ((string?)cellEl.Element(Ns + "v") ?? string.Empty).Trim();
        if (raw.Length == 0) return string.Empty;

        if (string.Equals(type, "s", StringComparison.Ordinal))
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                   && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index].Trim()
                : string.Empty;
        }

        if (string.Equals(type, "str", StringComparison.Ordinal) ||
            string.Equals(type, "b", StringComparison.Ordinal))
        {
            return raw;
        }

        // Numerik hücre: "6.120034142E9" gibi bilimsel gösterim düz metne çevrilir
        // (VKN kolonuna sayı yazan Excel kullanıcıları için kritik).
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number == decimal.Truncate(number)
                ? decimal.Truncate(number).ToString("0", CultureInfo.InvariantCulture)
                : number.ToString(CultureInfo.InvariantCulture);
        }

        return raw;
    }

    /// <summary>"C5" → 2 (0 tabanlı kolon indeksi). Harf bulunamazsa 0 döner.</summary>
    private static int ColumnIndexFromReference(string reference)
    {
        var index = 0;
        var hasLetter = false;

        foreach (var ch in reference)
        {
            var upper = char.ToUpperInvariant(ch);
            if (upper is >= 'A' and <= 'Z')
            {
                index = index * 26 + (upper - 'A' + 1);
                hasLetter = true;
            }
            else
            {
                break; // rakamlar satır numarasıdır
            }
        }

        return hasLetter ? index - 1 : 0;
    }
}
