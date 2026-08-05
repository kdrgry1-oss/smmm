using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>Ayrıştırılmış tek ekstre satırı (DB'ye yazılmadan önceki ham hali).</summary>
public class ParsedBankLine
{
    public DateTime TransactionDate { get; set; }
    public BankDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public decimal? BalanceAfter { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? CounterpartyName { get; set; }
    public string? CounterpartyIban { get; set; }
    public string? CounterpartyTaxId { get; set; }
    public string? ReferenceNo { get; set; }
    public string? TransactionType { get; set; }
}

/// <summary>Ayrıştırma sonucu — satırlar + hesap bilgisi + kullanıcıya gösterilecek uyarı.</summary>
public class ParsedStatement
{
    public string Format { get; set; } = string.Empty;
    public string? Iban { get; set; }
    public string? CurrencyCode { get; set; }
    public decimal? ClosingBalance { get; set; }
    public List<ParsedBankLine> Lines { get; set; } = new();
    /// <summary>Türkçe uyarı (tanınmayan satır, kısmi okuma vb.). Sessiz kırpma YAPILMAZ.</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Banka ekstresi ayrıştırıcı. BİRİNCİL YOL BUDUR — her banka MT940/CSV/Excel ekstre verir,
/// hiçbir sözleşme veya API anahtarı gerektirmez. Banka kurumsal API'leri geldiğinde
/// IBankStatementProvider adaptörü aynı <see cref="ParsedBankLine"/> sözleşmesini üretir,
/// bu yüzden aşağı akıştaki her şey (mükerrer kontrolü, kodlama, UI) DEĞİŞMEZ.
///
/// Desteklenen biçimler:
///  - MT940 (SWIFT) — Türkiye'de kurumsal internet bankacılığının standart ekstre çıktısı
///  - CSV / Excel   — başlık satırından sütun sezgisi (Türkçe başlıklar tanınır)
/// </summary>
public static class BankStatementParser
{
    /// <summary>Tek dosyada işlenecek azami satır (kötü niyetli/bozuk dosyaya karşı sınır).</summary>
    private const int MaxLines = 20000;

    /// <summary>Dosya içeriğinden biçimi sezip uygun ayrıştırıcıyı çağırır.</summary>
    public static ParsedStatement Parse(Stream stream, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension is ".xlsx" or ".xls")
        {
            var rows = MinimalXlsxReader.ReadFirstSheet(stream, MaxLines);
            return ParseTabular(rows, "XLSX");
        }

        // Metin biçimleri: içeriğe bakarak MT940 mı CSV mi karar ver.
        stream.Position = 0;
        using var reader = new StreamReader(stream, DetectEncoding(), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        if (LooksLikeMt940(text)) return ParseMt940(text);

        var csvRows = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(MaxLines)
            .Select(line => SplitCsvLine(line.TrimEnd('\r')))
            .ToList();

        return ParseTabular(csvRows, "CSV");
    }

    /// <summary>Türkçe ekstrelerde Windows-1254 yaygındır; BOM yoksa UTF-8 denenir.</summary>
    private static Encoding DetectEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.UTF8;
    }

    private static bool LooksLikeMt940(string text) =>
        text.Contains(":61:", StringComparison.Ordinal) &&
        (text.Contains(":20:", StringComparison.Ordinal) || text.Contains(":25:", StringComparison.Ordinal));

    // ---------- MT940 ----------

    /// <summary>
    /// MT940 etiket tabanlı ayrıştırma.
    ///   :25: hesap (IBAN) · :60F/:62F açılış/kapanış bakiyesi
    ///   :61: hareket satırı (tarih, borç/alacak, tutar, tip, referans)
    ///   :86: hareketin açıklaması (karşı taraf adı/IBAN'ı çoğu bankada burada)
    /// </summary>
    private static ParsedStatement ParseMt940(string text)
    {
        var result = new ParsedStatement { Format = "MT940" };

        // Etiketler satır başında gelir; devam satırları bir önceki etikete aittir.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalized.Split('\n');

        var blocks = new List<(string Tag, string Value)>();
        foreach (var raw in rawLines)
        {
            var match = Regex.Match(raw, @"^:(\d{2}[A-Z]?):(.*)$");
            if (match.Success)
            {
                blocks.Add((match.Groups[1].Value, match.Groups[2].Value));
            }
            else if (blocks.Count > 0 && !string.IsNullOrWhiteSpace(raw))
            {
                // Devam satırı — önceki etiketin değerine eklenir.
                var last = blocks[^1];
                blocks[^1] = (last.Tag, last.Value + "\n" + raw.Trim());
            }
        }

        ParsedBankLine? pending = null;
        var unparsed = 0;

        foreach (var (tag, value) in blocks)
        {
            switch (tag)
            {
                case "25":
                    result.Iban = ExtractIban(value) ?? value.Trim();
                    break;

                case "60F":
                case "62F":
                    var balance = ParseMt940Balance(value);
                    if (tag == "62F" && balance.HasValue) result.ClosingBalance = balance;
                    if (result.CurrencyCode is null && value.Length >= 10)
                        result.CurrencyCode = SafeCurrency(value);
                    break;

                case "61":
                    if (pending is not null) result.Lines.Add(pending);
                    pending = ParseMt940Line(value);
                    if (pending is null) unparsed++;
                    break;

                case "86":
                    if (pending is not null) ApplyMt940Description(pending, value);
                    break;
            }

            if (result.Lines.Count >= MaxLines) break;
        }

        if (pending is not null) result.Lines.Add(pending);

        if (unparsed > 0)
        {
            result.Warning = $"{unparsed} hareket satırı tanınmadı ve atlandı. " +
                             "Ekstre biçimi beklenenden farklı olabilir.";
        }

        return result;
    }

    /// <summary>
    /// :61: satırı. Biçim: YYMMDD[MMDD]{C|D|RC|RD}[fon]tutar,kuruş NTRF referans //bankaRef
    /// </summary>
    private static ParsedBankLine? ParseMt940Line(string value)
    {
        var line = value.Split('\n')[0].Trim();

        var match = Regex.Match(
            line,
            @"^(?<vd>\d{6})(?<ed>\d{4})?(?<mark>RC|RD|C|D)(?<fund>[A-Z])?(?<amount>[\d,\.]+)(?<type>[A-Z]{4})?(?<ref>.*)$");

        if (!match.Success) return null;

        var valueDate = ParseYyMmDd(match.Groups["vd"].Value);
        if (valueDate is null) return null;

        var amount = ParseMt940Amount(match.Groups["amount"].Value);
        if (amount is null) return null;

        var mark = match.Groups["mark"].Value;
        // C/RD = alacak (para girdi), D/RC = borç (para çıktı).
        var direction = mark is "C" or "RD" ? BankDirection.Gelen : BankDirection.Giden;

        var reference = match.Groups["ref"].Value.Trim();
        var bankRefIndex = reference.IndexOf("//", StringComparison.Ordinal);
        if (bankRefIndex >= 0) reference = reference[(bankRefIndex + 2)..].Trim();

        return new ParsedBankLine
        {
            TransactionDate = valueDate.Value,
            Direction = direction,
            Amount = Math.Abs(amount.Value),
            TransactionType = match.Groups["type"].Success ? match.Groups["type"].Value : null,
            ReferenceNo = string.IsNullOrWhiteSpace(reference) || reference == "NONREF" ? null : reference,
        };
    }

    /// <summary>
    /// :86: açıklaması. Türk bankaları karşı taraf adını/IBAN'ını buraya yazar; yapılandırılmış
    /// alt alanlar (?20 ?21 ... ) varsa ayıklanır, yoksa serbest metin olarak kullanılır.
    /// </summary>
    private static void ApplyMt940Description(ParsedBankLine line, string value)
    {
        var raw = value.Replace("\n", " ").Trim();

        // Yapılandırılmış alt alanlar: ?20AÇIKLAMA?21DEVAM...
        // Alt alanlar AYRI AYRI tutulur — birleştirilirse karşı taraf adı, sonraki alandaki
        // açıklama/IBAN metnine yapışır ve cari eşleşmesi bozulur.
        var subfields = raw.Contains('?')
            ? Regex.Matches(raw, @"\?(\d{2})([^?]*)")
                .Select(m => m.Groups[2].Value.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList()
            : new List<string> { raw };

        var joined = Collapse(string.Join(" ", subfields));
        line.Description = string.IsNullOrWhiteSpace(joined) ? Collapse(raw) : joined;

        // IBAN/VKN hangi alt alanda olursa olsun bulunur (tüm metinde aranır).
        line.CounterpartyIban = ExtractIban(line.Description);
        line.CounterpartyTaxId = ExtractTaxId(line.Description);

        // Ad ise ÖNCE etiketli alt alanda aranır; yalnız o alanın içeriği kullanılır.
        line.CounterpartyName = ExtractNameFromSubfields(subfields)
                                ?? ExtractCounterpartyName(line.Description);
    }

    /// <summary>
    /// Alt alanlar içinde "GÖNDEREN:/ALICI:/UNVAN:" etiketini taşıyanı bulur ve YALNIZ o alanın
    /// kalanını ad olarak döner. Etiket yoksa, IBAN/rakam içermeyen ilk harf ağırlıklı alan
    /// aday olarak seçilir (bankaların çoğu adı ilk alt alana yazar).
    /// </summary>
    private static string? ExtractNameFromSubfields(List<string> subfields)
    {
        foreach (var field in subfields)
        {
            var labelled = Regex.Match(
                field,
                @"(?:g[öo]nderen|al[ıi]c[ıi]|kar[şs][ıi] taraf|ad[ıi] soyad[ıi]|unvan)\s*[:\-]\s*(?<name>.{3,120})",
                RegexOptions.IgnoreCase);

            if (!labelled.Success) continue;

            var cleaned = StripIdentifiers(labelled.Groups["name"].Value);
            if (cleaned.Length >= 3) return cleaned.Length > 80 ? cleaned[..80].Trim() : cleaned;
        }

        return null;
    }

    private static decimal? ParseMt940Balance(string value)
    {
        var match = Regex.Match(value.Trim(), @"^(?<mark>[CD])(?<date>\d{6})(?<cur>[A-Z]{3})(?<amount>[\d,\.]+)");
        if (!match.Success) return null;

        var amount = ParseMt940Amount(match.Groups["amount"].Value);
        if (amount is null) return null;

        return match.Groups["mark"].Value == "D" ? -amount.Value : amount.Value;
    }

    private static string? SafeCurrency(string value)
    {
        var match = Regex.Match(value, @"[CD]\d{6}([A-Z]{3})");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>MT940 tutarı virgül ondalıklıdır (ör. "1234,56").</summary>
    private static decimal? ParseMt940Amount(string raw)
    {
        var cleaned = raw.Trim().Replace(".", string.Empty).Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? ParseYyMmDd(string raw) =>
        DateTime.TryParseExact(raw, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? AsUtcDate(parsed)
            : null;

    /// <summary>
    /// Tarihi gün başına indirip UTC olarak işaretler. PostgreSQL kolonları
    /// "timestamp with time zone" olduğundan Kind=Unspecified değer yazılamaz
    /// (projedeki DateTime.SpecifyKind(..., Utc) kuralının ekstre tarafındaki karşılığı).
    /// Ekstre tarihleri saat taşımaz; gün bilgisi kaymasın diye dönüşüm DEĞİL, işaretleme yapılır.
    /// </summary>
    private static DateTime AsUtcDate(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    // ---------- CSV / Excel ----------

    /// <summary>
    /// Başlık satırından sütun sezgisiyle tablo ayrıştırma. Türk bankalarının Excel/CSV
    /// ekstreleri farklı başlıklar kullandığı için her alan için birden çok aday aranır.
    /// Tutar tek sütunda (işaretli) ya da ayrı borç/alacak sütunlarında olabilir; ikisi de
    /// desteklenir.
    /// </summary>
    private static ParsedStatement ParseTabular(List<List<string>> rows, string format)
    {
        var result = new ParsedStatement { Format = format };
        if (rows.Count == 0)
        {
            result.Warning = "Dosya boş görünüyor.";
            return result;
        }

        // Başlık satırını bul: "tarih" içeren ilk satır (üstte banka logosu/başlıkları olabilir).
        var headerIndex = rows.FindIndex(r =>
            r.Any(c => Normalize(c).Contains("tarih")) &&
            r.Any(c => { var n = Normalize(c); return n.Contains("tutar") || n.Contains("borc") || n.Contains("alacak"); }));

        if (headerIndex < 0)
        {
            result.Warning = "Ekstre başlık satırı bulunamadı. Dosyada 'Tarih' ve 'Tutar' " +
                             "(veya 'Borç'/'Alacak') sütunları olmalıdır.";
            return result;
        }

        var header = rows[headerIndex].Select(Normalize).ToList();

        int Find(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var index = header.FindIndex(h => h.Contains(candidate));
                if (index >= 0) return index;
            }
            return -1;
        }

        var dateCol = Find("tarih");
        var amountCol = Find("tutar", "islem tutari");
        var debitCol = Find("borc", "cikis");
        var creditCol = Find("alacak", "giris");
        var balanceCol = Find("bakiye");
        var descCol = Find("aciklama", "detay", "islem");
        var nameCol = Find("karsi taraf", "gonderen", "alici", "unvan", "ad soyad");
        var ibanCol = Find("iban", "karsi hesap");
        var refCol = Find("referans", "dekont", "fis no");

        if (dateCol < 0 || (amountCol < 0 && debitCol < 0 && creditCol < 0))
        {
            result.Warning = "Ekstrede tarih veya tutar sütunu tanınamadı.";
            return result;
        }

        var skipped = 0;

        foreach (var row in rows.Skip(headerIndex + 1).Take(MaxLines))
        {
            string Cell(int index) =>
                index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

            var date = ParseFlexibleDate(Cell(dateCol));
            if (date is null) { skipped++; continue; }

            decimal amount;
            BankDirection direction;

            if (amountCol >= 0 && !string.IsNullOrWhiteSpace(Cell(amountCol)))
            {
                var signed = ParseTurkishAmount(Cell(amountCol));
                if (signed is null) { skipped++; continue; }
                direction = signed.Value < 0 ? BankDirection.Giden : BankDirection.Gelen;
                amount = Math.Abs(signed.Value);
            }
            else
            {
                var debit = ParseTurkishAmount(Cell(debitCol)) ?? 0m;
                var credit = ParseTurkishAmount(Cell(creditCol)) ?? 0m;
                if (debit == 0m && credit == 0m) { skipped++; continue; }
                direction = credit > 0 ? BankDirection.Gelen : BankDirection.Giden;
                amount = Math.Abs(credit > 0 ? credit : debit);
            }

            var description = Cell(descCol);
            var counterpartyName = Cell(nameCol);
            var iban = Cell(ibanCol);

            result.Lines.Add(new ParsedBankLine
            {
                TransactionDate = date.Value,
                Direction = direction,
                Amount = amount,
                BalanceAfter = balanceCol >= 0 ? ParseTurkishAmount(Cell(balanceCol)) : null,
                Description = Collapse(description),
                CounterpartyName = string.IsNullOrWhiteSpace(counterpartyName)
                    ? ExtractCounterpartyName(description)
                    : Collapse(counterpartyName),
                CounterpartyIban = string.IsNullOrWhiteSpace(iban)
                    ? ExtractIban(description)
                    : NormalizeIban(iban),
                CounterpartyTaxId = ExtractTaxId($"{description} {counterpartyName}"),
                ReferenceNo = refCol >= 0 ? Cell(refCol) : null,
            });
        }

        if (skipped > 0)
        {
            result.Warning = $"{skipped} satır okunamadı (tarih/tutar boş veya biçimsiz) ve atlandı.";
        }

        return result;
    }

    // ---------- Ortak yardımcılar ----------

    /// <summary>Metinden IBAN ayıklar (TR + 24 hane; boşluklu yazım desteklenir).</summary>
    public static string? ExtractIban(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text.ToUpperInvariant(), @"TR[\s]?(?:\d[\s]?){24}");
        return match.Success ? NormalizeIban(match.Value) : null;
    }

    /// <summary>IBAN'ı boşluksuz büyük harfe çevirir (eşleşme anahtarı).</summary>
    public static string NormalizeIban(string iban) =>
        new(iban.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// Metinden VKN (10) / TCKN (11) ayıklar. IBAN içindeki rakam dizileriyle karışmaması
    /// için IBAN'lar önce metinden çıkarılır.
    /// </summary>
    public static string? ExtractTaxId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var withoutIban = Regex.Replace(text.ToUpperInvariant(), @"TR[\s]?(?:\d[\s]?){24}", " ");
        var match = Regex.Match(withoutIban, @"(?<![\d])(\d{10}|\d{11})(?![\d])");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Açıklamadan karşı taraf adını tahmin eder. Bankalar genelde "... GONDEREN: X ..." veya
    /// "ALICI: X" kalıbı kullanır; bulunamazsa IBAN/rakam temizlenmiş metnin ilk anlamlı
    /// parçası döner. Kesin değildir — SMMM onay ekranında düzeltebilir.
    /// </summary>
    public static string? ExtractCounterpartyName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var labelled = Regex.Match(
            text,
            @"(?:g[öo]nderen|al[ıi]c[ıi]|kar[şs][ıi] taraf|ad[ıi] soyad[ıi]|unvan)\s*[:\-]\s*(?<name>[^,;|]{3,120})",
            RegexOptions.IgnoreCase);

        // Etiketli ad da IBAN/VKN gibi ekleri taşıyabilir (bankalar alt alanları bitişik yazar),
        // bu yüzden her iki yolda da aynı temizlik uygulanır.
        var candidate = labelled.Success ? labelled.Groups["name"].Value : text;
        var cleaned = StripIdentifiers(candidate);

        if (cleaned.Length < 3) return null;
        return cleaned.Length > 80 ? cleaned[..80].Trim() : cleaned;
    }

    /// <summary>
    /// Ad metninden kimlik/teknik ekleri temizler: IBAN, 6+ haneli rakam dizileri (VKN/TCKN,
    /// dekont no), "VKN"/"TCKN" etiketleri ve baştaki-sondaki ayraçlar. Ad alanı bankadan
    /// çoğu zaman IBAN'la bitişik gelir; bu temizlik olmadan cari eşleşmesi tutmaz.
    /// </summary>
    private static string StripIdentifiers(string text)
    {
        var cleaned = Regex.Replace(text.ToUpperInvariant(), @"TR[\s]?(?:\d[\s]?){24}", " ");
        cleaned = Regex.Replace(cleaned, @"\b(?:VKN|TCKN|VD)\b", " ");
        cleaned = Regex.Replace(cleaned, @"\d{6,}", " ");
        return Collapse(cleaned).Trim(' ', '-', ':', '/', '.', ',');
    }

    /// <summary>Türkçe tutar biçimi: "1.234,56" veya "1234.56" veya "-1.234,56".</summary>
    public static decimal? ParseTurkishAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim()
            .Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("₺", string.Empty)
            .Replace(" ", string.Empty);

        if (cleaned.Length == 0) return null;

        var negative = cleaned.StartsWith('-') || (cleaned.StartsWith('(') && cleaned.EndsWith(')'));
        cleaned = cleaned.Trim('-', '(', ')', '+');

        // Hem "1.234,56" hem "1234.56" desteklenir: son ayraç ondalıktır.
        var lastComma = cleaned.LastIndexOf(',');
        var lastDot = cleaned.LastIndexOf('.');

        if (lastComma > lastDot)
        {
            cleaned = cleaned.Replace(".", string.Empty).Replace(',', '.');
        }
        else if (lastDot > lastComma)
        {
            cleaned = cleaned.Replace(",", string.Empty);
        }

        if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return null;

        return negative ? -parsed : parsed;
    }

    /// <summary>Yaygın Türkçe tarih biçimlerini dener.</summary>
    public static DateTime? ParseFlexibleDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        string[] formats =
        {
            "dd.MM.yyyy", "dd/MM/yyyy", "dd-MM-yyyy",
            "yyyy-MM-dd", "yyyy/MM/dd",
            "dd.MM.yyyy HH:mm", "dd/MM/yyyy HH:mm", "dd.MM.yyyy HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss", "dd.MM.yy", "dd/MM/yy",
        };

        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return AsUtcDate(exact);

        // Excel seri tarihi (sayı olarak gelebilir).
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) &&
            serial > 20000 && serial < 80000)
        {
            return AsUtcDate(new DateTime(1899, 12, 30).AddDays(serial));
        }

        return DateTime.TryParse(value, new CultureInfo("tr-TR"), DateTimeStyles.None, out var parsed)
            ? AsUtcDate(parsed)
            : null;
    }

    /// <summary>Ardışık boşlukları teke indirir ve kırpar.</summary>
    private static string Collapse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>Başlık eşleştirmesi için: küçük harf + Türkçe karakter sadeleştirme.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lower = text.ToLowerInvariant().Trim();
        var builder = new StringBuilder(lower.Length);

        foreach (var ch in lower)
        {
            builder.Append(ch switch
            {
                'ı' or 'i' or 'î' => 'i',
                'ğ' => 'g',
                'ü' or 'û' => 'u',
                'ş' => 's',
                'ö' => 'o',
                'ç' => 'c',
                _ => ch,
            });
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ");
    }

    /// <summary>Ayraç sezgili CSV satır bölme (; veya , veya tab) — tırnak desteklidir.</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var separator = line.Count(c => c == ';') >= line.Count(c => c == ',')
            ? ';'
            : (line.Contains('\t') ? '\t' : ',');

        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == separator && !inQuotes) { cells.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }

        cells.Add(current.ToString());
        return cells;
    }
}
