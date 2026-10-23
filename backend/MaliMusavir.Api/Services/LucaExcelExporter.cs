using System.Globalization;
using System.Text;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;

namespace MaliMusavir.Api.Services;

/// <summary>
/// Fiş Aktarım Şablonu satırı — Luca'nın GERÇEK içe aktarma şablonuyla birebir 14 kolon (A→N).
/// Kullanıcının Luca'ya girmiş dolu örneğine göre: Borç/Alacak'ın boş tarafı ve Miktar/Kur 0
/// yazılır; yalnız DovizTutar null ise hücre boş kalır.
/// </summary>
public sealed record LucaTemplateRow(
    string FisNo,          // A — Fiş No (OPSİYONEL: boş bırakılır, Luca otomatik verir)
    string FisTarihi,      // B — Fiş Tarihi (ZORUNLU, "dd.MM.yyyy" metin)
    string FisAciklama,    // C — Fiş Açıklama (ZORUNLU)
    string HesapKodu,      // D — Hesap Kodu (ZORUNLU)
    string EvrakNo,        // E — Evrak No (ZORUNLU)
    string EvrakTarihi,    // F — Evrak Tarihi (ZORUNLU, "dd.MM.yyyy" metin)
    string DetayAciklama,  // G — Detay Açıklama (OPSİYONEL)
    decimal? Borc,         // H — Borç (ZORUNLU kolon; satırın boş tarafı BOŞ hücre)
    decimal? Alacak,       // I — Alacak (ZORUNLU kolon; satırın boş tarafı BOŞ hücre)
    decimal? Miktar,       // J — Miktar (OPSİYONEL: boş)
    string BelgeTuru,      // K — Belge Türü (ZORUNLU: "Fatura"/"Fiş", parametreden)
    string ParaBirimi,     // L — Para Birimi (ZORUNLU: "TRY", parametreden)
    decimal Kur,           // M — Kur (ZORUNLU: TRY'de 1)
    decimal? DovizTutar);  // N — Döviz Tutar (TRY satırında BOŞ; luca.doviz.tutar.sifir=true ise 0)

/// <summary>Şablon üretim sonucu: satırlar + ön-kontrol hataları (Türkçe, belge/alan bazlı).</summary>
public sealed class LucaTemplateBuildResult
{
    public List<LucaTemplateRow> Rows { get; } = new();
    /// <summary>Zorunlu alanı eksik belgelerin Türkçe listesi. Doluysa export ENGELLENİR.</summary>
    public List<string> Errors { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Luca "Fiş Aktarım Şablon" dışa aktarma servisi (blueprint §0 madde 3: garantili yol dosya formatı).
/// Kullanıcının verdiği GERÇEK Luca fiş aktarım şablonuna birebir uyar: sayfa adı
/// "Fiş Aktarım Şablon", 14 kolon (A→N): Fiş No, Fiş Tarihi, Fiş Açıklama, Hesap Kodu,
/// Evrak No, Evrak Tarihi, Detay Açıklama, Borç, Alacak, Miktar, Belge Türü, Para Birimi,
/// Kur, Döviz Tutar. Ana çıktı .xlsx'tir (MinimalXlsxWriter — harici paket YOK); ikinci
/// format seçeneği olarak AYNI kolon sırası/başlıklarla ;-ayraçlı UTF-8 BOM'lu CSV üretilir.
///
/// Dengeli fiş mantığı (Faz 2/8) AYNEN korunur, yalnızca kolon düzeni şablona taşındı:
/// her belge (fatura veya onaylı fiş) tek dengeli fiş olur — borç = kalem hesapları
/// (+ KKEG bölünmesi) + oran bazında gruplanmış 191 KDV, alacak = cari (320.xx) / kasa (100).
/// </summary>
public class LucaExcelExporter
{
    public const string SheetName = "Fiş Aktarım Şablon";

    /// <summary>Şablonun başlık satırı — kolon sırası KESİNLİKLE değiştirilmez (A→N).</summary>
    public static readonly string[] TemplateHeaders =
    {
        "Fiş No", "Fiş Tarihi", "Fiş Açıklama", "Hesap Kodu", "Evrak No", "Evrak Tarihi",
        "Detay Açıklama", "Borç", "Alacak", "Miktar", "Belge Türü", "Para Birimi", "Kur", "Döviz Tutar"
    };

    /// <summary>Cari kodu olmayan fişlerde alacak tarafı (nakit ödeme varsayımı — Kasa).</summary>
    private const string FallbackCashAccount = "100";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// KDV oranı → indirilecek KDV hesabı VARSAYILAN eşlemesi: %20→191.01, %10→191.02, %1→191.03;
    /// %0 için KDV satırı yazılmaz. Faz 7 (E4) itibarıyla asıl kaynak parametre merkezidir
    /// ("kdv.hesap.20/10/1/diger"); bu sözlük parametre tablosu boşsa davranışı birebir korur.
    /// </summary>
    private static readonly Dictionary<decimal, string> DefaultVatAccountMap = new()
    {
        [20m] = "191.01",
        [10m] = "191.02",
        [1m] = "191.03",
    };

    private const string FallbackVatAccount = "191";

    /// <summary>Faz 8 (B3): KKEG satırlarının varsayılan borç hesabı; asıl kaynak "kkeg.hesap" parametresi.</summary>
    private const string FallbackKkegAccount = "689";

    // ---------- e-SMM (serbest meslek makbuzu) gelir fişi ----------

    /// <summary>
    /// e-SMM makbuzunu tanıtan satır adı öneki — UyumsoftProvider e-SMM kayıtlarının tek
    /// satırına bu öneki koyar; MergeDocuments bu önekli TEK satırlı faturaları alış fişi
    /// yerine GELİR FİŞİ kalıbıyla yazar. Değiştirilirse UyumsoftProvider da güncellenmelidir.
    /// </summary>
    public const string EsmmItemPrefix = "e-SMM Makbuzu";

    /// <summary>e-SMM: cari kodu yoksa borç tarafındaki karşı hesap (alıcılar).</summary>
    private const string FallbackEsmmReceivableAccount = "120";
    /// <summary>e-SMM: hesap kodu atanmamışsa alacak tarafındaki gelir hesabı.</summary>
    private const string FallbackEsmmIncomeAccount = "600";
    /// <summary>e-SMM: stopaj satırının borç hesabı (Peşin Ödenen Vergiler ve Fonlar).</summary>
    private const string FallbackEsmmWithholdingAccount = "193.01";
    /// <summary>
    /// e-SMM: hesaplanan KDV alacak hesabı. "kdv.hesap.*" parametre ailesi 191 (indirilecek)
    /// tarafını eşler ve 391 karşılığı yoktur → varsayılan düz "391"; istenirse
    /// "kdv.hesaplanan.hesap" parametresiyle değiştirilebilir.
    /// </summary>
    private const string FallbackCalculatedVatAccount = "391";

    // ---------- Faz 16: Z raporu (yazar kasa günlük kapanış) GELİR fişi ----------
    /// <summary>Z raporu: gelir alacak hesabı; asıl kaynak "zrapor.gelir.hesap" parametresi.</summary>
    private const string FallbackZReportIncomeAccount = "600";
    /// <summary>Z raporu: kart tahsilatının borç hesabı; asıl kaynak "zrapor.kart.hesap" parametresi.</summary>
    private const string FallbackZReportCardAccount = "108";

    // Belge Türü / Para Birimi varsayılanları — asıl kaynak parametre merkezi (DbSeeder ekler).
    // CANLI DOĞRULANDI (kullanıcının Luca'ya girmiş gerçek dosyası, fis_listesi_60548614):
    // Belge Türü "EF" (e-fatura) / "PS" (perakende satış fişi); Para Birimi BOŞ bırakılır.
    private const string FallbackDocTypeInvoice = "EF";
    private const string FallbackDocTypeReceipt = "PS";
    private const string FallbackCurrency = "";

    private readonly ParameterService _parameters;

    public LucaExcelExporter(ParameterService parameters)
    {
        _parameters = parameters;
    }

    /// <summary>
    /// KDV oranı için indirilecek KDV hesabı: önce parametre merkezi ("kdv.hesap.{oran}"),
    /// yoksa varsayılan eşleme, o da yoksa "kdv.hesap.diger" (varsayılan 191).
    /// </summary>
    private string ResolveVatAccount(decimal rate)
    {
        var defaultAccount = DefaultVatAccountMap.TryGetValue(rate, out var mapped)
            ? mapped
            : _parameters.Get("kdv.hesap.diger", FallbackVatAccount);

        return _parameters.Get($"kdv.hesap.{rate.ToString("0.##", CultureInfo.InvariantCulture)}", defaultAccount);
    }

    /// <summary>
    /// Fatura + onaylı fişlerden şablon satırlarını üretir ve ZORUNLU ALAN ÖN-KONTROLÜNÜ yapar.
    /// Errors doluysa export engellenmelidir (hangi belgenin hangi alanı eksik, Türkçe listelenir).
    /// Doldurma kuralları — kullanıcının Luca'ya GİRMİŞ gerçek dosyasından doğrulandı
    /// (fis_listesi_60548614; TODO-VERIFY'lar kapandı):
    ///  - Fiş No: BOŞ (Luca otomatik numara verir).
    ///  - Fiş/Evrak Tarihi: belge tarihi "dd/MM/yyyy" (eğik çizgili metin).
    ///  - Fiş Açıklama: fatura → "{BelgeNo} numaralı alış faturası"; fiş → "{Unvan} fiş gideri".
    ///  - Detay Açıklama: HER satırda satıcı unvanı (KKEG satırında gerekçe korunur).
    ///  - Evrak No: fatura InvoiceNumber / fiş DocumentNumber (yoksa "FIS-{id kısa}").
    ///  - Borç/Alacak: dengeli fiş; satırın boş tarafı 0 YAZILIR (boş hücre değil).
    ///  - Miktar: 0. Belge Türü: EF/PS (parametreden). Para Birimi: boş. Kur: 0.
    ///  - Döviz Tutar: BOŞ (luca.doviz.tutar.sifir=true yapılırsa 0 yazılır).
    /// </summary>
    public async Task<LucaTemplateBuildResult> BuildTemplateAsync(
        IReadOnlyList<EInvoice> invoices, IReadOnlyList<Receipt>? receipts = null, CancellationToken ct = default)
    {
        await _parameters.EnsureLoadedAsync(ct);
        var kkegAccount = _parameters.Get("kkeg.hesap", FallbackKkegAccount);
        var docTypeInvoice = _parameters.Get("luca.belge.turu.fatura", FallbackDocTypeInvoice);
        var docTypeReceipt = _parameters.Get("luca.belge.turu.fis", FallbackDocTypeReceipt);
        var currency = _parameters.Get("luca.para.birimi", FallbackCurrency);
        var writeZeroFx = string.Equals(_parameters.Get("luca.doviz.tutar.sifir", "false").Trim(), "true",
            StringComparison.OrdinalIgnoreCase);
        decimal? fxAmount = writeZeroFx ? 0m : null;
        // e-SMM gelir fişi: hesaplanan KDV hesabı (kdv.hesap.* ailesinin 391 karşılığı yok).
        var calculatedVatAccount = _parameters.Get("kdv.hesaplanan.hesap", FallbackCalculatedVatAccount);
        // TODO-VERIFY: Luca'da e-SMM için ayrı belge türü kodu olup olmadığı doğrulanacak;
        // şimdilik fatura belge türü (EF) kullanılır, "luca.belge.turu.smm" ile değiştirilebilir.
        var docTypeEsmm = _parameters.Get("luca.belge.turu.smm", docTypeInvoice);
        // Faz 16: Z raporu gelir fişi hesapları (parametre merkezinden; yoksa 600 / 108).
        var zIncomeAccount = _parameters.Get("zrapor.gelir.hesap", FallbackZReportIncomeAccount);
        var zCardAccount = _parameters.Get("zrapor.kart.hesap", FallbackZReportCardAccount);

        var result = new LucaTemplateBuildResult();

        foreach (var doc in MergeDocuments(invoices, receipts, kkegAccount, calculatedVatAccount,
                     zIncomeAccount, zCardAccount, result.Errors))
        {
            // Gerçek Luca dosyası kalıbı: tarih "dd/MM/yyyy"; fiş açıklama fatura için
            // "{No} numaralı alış faturası"; detay HER satırda satıcı unvanı.
            var date = doc.Date.ToString("dd/MM/yyyy", Turkish);

            // ---------- GELİR FİŞİ (ExplicitRows dolu — alış kalıbına GİRMEZ) ----------
            // e-SMM: Borç = karşı hesap (tahsil edilen) + stopaj (193.01); Alacak = gelir (600) + 391 KDV.
            // Z raporu (Faz 16): Borç = 100 Kasa (nakit) + kart hesabı; Alacak = oran bazlı gelir + 391.
            if (doc.ExplicitRows is { Count: > 0 } incomeRows)
            {
                var isZReport = doc.Source == DocumentSource.Receipt;
                var incomeAciklama = isZReport
                    ? $"{date} Z Raporu #{doc.DocumentNo}"
                    : $"{doc.DocumentNo} numaralı e-SMM makbuzu";
                var incomeDocType = isZReport ? docTypeReceipt : docTypeEsmm;

                decimal incomeDebit = 0m, incomeCredit = 0m;
                foreach (var entry in incomeRows)
                {
                    result.Rows.Add(Row(date, incomeAciklama, entry.Account, doc.DocumentNo,
                        string.IsNullOrWhiteSpace(entry.Detail) ? incomeAciklama : entry.Detail,
                        borc: entry.Debit is decimal d ? Round2(d) : null,
                        alacak: entry.Credit is decimal c ? Round2(c) : null,
                        incomeDocType, currency, fxAmount));
                    incomeDebit += entry.Debit ?? 0m;
                    incomeCredit += entry.Credit ?? 0m;
                }

                // ---------- Ön-kontrol (gelir fişi) ----------
                var incomeLabel = isZReport
                    ? $"Z Raporu #{doc.DocumentNo} ({date})"
                    : $"e-SMM Makbuzu {doc.DocumentNo} ({date})";
                if (string.IsNullOrWhiteSpace(doc.DocumentNo))
                    result.Errors.Add($"{incomeLabel}: evrak numarası üretilemedi.");
                if (incomeCredit <= 0)
                    result.Errors.Add($"{incomeLabel}: tutar okunamadı (alacak toplamı 0) — belge dışa aktarılamaz.");
                if (Round2(incomeDebit) != Round2(incomeCredit))
                    result.Errors.Add(
                        $"{incomeLabel}: fiş dengesi sağlanamadı (borç {Round2(incomeDebit)} ≠ alacak {Round2(incomeCredit)}).");
                continue;
            }

            var fisAciklama = doc.Source == DocumentSource.Invoice
                ? $"{doc.DocumentNo} numaralı alış faturası"
                : string.IsNullOrWhiteSpace(doc.Title)
                    ? $"{doc.DocumentNo} numaralı gider fişi"
                    : $"{doc.Title.Trim()} fiş gideri";
            var detay = string.IsNullOrWhiteSpace(doc.Title) ? fisAciklama : doc.Title.Trim();
            var belgeTuru = doc.Source == DocumentSource.Invoice ? docTypeInvoice : docTypeReceipt;

            decimal totalDebit = 0m;
            var docRows = new List<LucaTemplateRow>();

            // Borç: her kalem kendi (onaylı) hesabına; KKEG satırında gerekçe metni korunur.
            foreach (var debit in doc.Debits)
            {
                docRows.Add(Row(date, fisAciklama, debit.Account, doc.DocumentNo,
                    debit.IsKkeg ? debit.Detail : detay,
                    borc: Round2(debit.Amount), alacak: null, belgeTuru, currency, fxAmount));
                totalDebit += debit.Amount;
            }

            // Borç: KDV, oran bazında gruplanır (%0 → satır yok); hesap eşlemesi parametreden (E4).
            foreach (var vat in doc.VatByRate.Where(v => v.Rate > 0 && v.Amount > 0).OrderByDescending(v => v.Rate))
            {
                docRows.Add(Row(date, fisAciklama, ResolveVatAccount(vat.Rate), doc.DocumentNo, detay,
                    borc: Round2(vat.Amount), alacak: null, belgeTuru, currency, fxAmount));
                totalDebit += vat.Amount;
            }

            // Alacak: cari/kasa hesabı. Denge garantisi için alacak = borç toplamı yazılır
            // (satır toplamları ile belge başlığı arasında kuruş farkı olsa bile fiş dengeli kalır).
            docRows.Add(Row(date, fisAciklama, doc.CreditAccount, doc.DocumentNo, detay,
                borc: null, alacak: Round2(totalDebit), belgeTuru, currency, fxAmount));

            // ---------- Ön-kontrol (çift validasyon: UI + burada) ----------
            var label = doc.Source == DocumentSource.Invoice
                ? $"Fatura {doc.DocumentNo} ({date})"
                : $"Fiş {(string.IsNullOrWhiteSpace(doc.Title) ? doc.DocumentNo : doc.Title.Trim())} ({date})";

            if (doc.Debits.Any(d => string.IsNullOrWhiteSpace(d.Account)))
                result.Errors.Add($"{label}: hesap kodu atanmamış satır var. Onay ekranından hesap kodunu girin.");
            if (string.IsNullOrWhiteSpace(doc.DocumentNo))
                result.Errors.Add($"{label}: evrak numarası üretilemedi.");
            if (totalDebit <= 0)
                result.Errors.Add($"{label}: tutar okunamadı (borç toplamı 0) — belge dışa aktarılamaz.");

            result.Rows.AddRange(docRows);
        }

        return result;
    }

    /// <summary>Şablon satırlarını .xlsx'e yazar (sayfa adı "Fiş Aktarım Şablon", başlıklar kalın).</summary>
    public byte[] ToXlsx(IReadOnlyList<LucaTemplateRow> rows)
    {
        var cellRows = rows
            .Select(r => (IReadOnlyList<MinimalXlsxWriter.Cell>)new[]
            {
                MinimalXlsxWriter.Cell.Text(r.FisNo),
                MinimalXlsxWriter.Cell.Text(r.FisTarihi),
                MinimalXlsxWriter.Cell.Text(r.FisAciklama),
                MinimalXlsxWriter.Cell.Text(r.HesapKodu),
                MinimalXlsxWriter.Cell.Text(r.EvrakNo),
                MinimalXlsxWriter.Cell.Text(r.EvrakTarihi),
                MinimalXlsxWriter.Cell.Text(r.DetayAciklama),
                MinimalXlsxWriter.Cell.Number(r.Borc),
                MinimalXlsxWriter.Cell.Number(r.Alacak),
                MinimalXlsxWriter.Cell.Number(r.Miktar),
                MinimalXlsxWriter.Cell.Text(r.BelgeTuru),
                MinimalXlsxWriter.Cell.Text(r.ParaBirimi),
                MinimalXlsxWriter.Cell.Number(r.Kur),
                MinimalXlsxWriter.Cell.Number(r.DovizTutar),
            })
            .ToList();

        return MinimalXlsxWriter.Build(SheetName, TemplateHeaders, cellRows);
    }

    /// <summary>
    /// İkinci format: AYNI kolon sırası/başlıklarla ;-ayraçlı UTF-8 BOM'lu CSV
    /// (Türkçe Excel varsayılanı; tutarlar virgül ondalıklı, boş taraf boş alan).
    /// </summary>
    public byte[] ToCsv(IReadOnlyList<LucaTemplateRow> rows)
    {
        var sb = new StringBuilder();
        AppendCsvRow(sb, TemplateHeaders);

        foreach (var r in rows)
        {
            AppendCsvRow(sb,
                r.FisNo, r.FisTarihi, r.FisAciklama, r.HesapKodu, r.EvrakNo, r.EvrakTarihi, r.DetayAciklama,
                CsvMoney(r.Borc), CsvMoney(r.Alacak), CsvMoney(r.Miktar),
                r.BelgeTuru, r.ParaBirimi, CsvMoney(r.Kur) ?? "0", CsvMoney(r.DovizTutar));
        }

        return WithBom(sb.ToString());
    }

    private static LucaTemplateRow Row(
        string date, string fisAciklama, string account, string evrakNo, string detay,
        decimal? borc, decimal? alacak, string belgeTuru, string currency, decimal? fxAmount)
        => new(
            FisNo: string.Empty,           // Luca otomatik verir
            FisTarihi: date,
            FisAciklama: fisAciklama,
            HesapKodu: account,
            EvrakNo: evrakNo,
            EvrakTarihi: date,
            DetayAciklama: detay,
            Borc: borc ?? 0m,              // gerçek Luca dosyası: boş taraf 0 yazılır
            Alacak: alacak ?? 0m,
            Miktar: 0m,                    // gerçek Luca dosyası: 0
            BelgeTuru: belgeTuru,
            ParaBirimi: currency,
            Kur: 0m,                       // gerçek Luca dosyası: 0 (TRY satırı)
            DovizTutar: fxAmount);

    private enum DocumentSource { Invoice = 0, Receipt = 1 }

    /// <summary>
    /// Fatura + fişleri tek belge modeline indirger ve tarih + belge no sırasına dizer.
    /// Fiş evrak no'su: OCR'dan gelen DocumentNumber; yoksa "FIS-" + Id'nin ilk 8 hanesi.
    /// Faz 8 (B3): KKEG işaretli satırlar borç tarafında İKİYE bölünür — indirilebilir kısım kendi
    /// hesabına, KKEG kısmı (matrah payı + İNDİRİLEMEYEN KDV payı aynı satırda) kkeg.hesap'a
    /// (varsayılan 689) gerekçe açıklamasıyla yazılır; 191 satırı da indirilemeyen KDV kadar azalır.
    /// Faz 8 (B2): tevkifatlı kalemlerde Detay Açıklama'ya tevkifat notu eklenir (tutar mantığı değişmez).
    /// </summary>
    private static IEnumerable<ExportDocument> MergeDocuments(
        IReadOnlyList<EInvoice> invoices, IReadOnlyList<Receipt>? receipts, string kkegAccount,
        string calculatedVatAccount, string zIncomeAccount, string zCardAccount, List<string> errors)
    {
        var docs = new List<ExportDocument>();

        foreach (var invoice in invoices)
        {
            var lines = invoice.Lines.OrderBy(l => l.LineNumber).ToList();

            // ---------- e-SMM makbuzu → GELİR FİŞİ (alış kalıbı DEĞİL) ----------
            // Ayırt edici işaret: TEK satır + ItemName "e-SMM Makbuzu" öneki (UyumsoftProvider
            // koyar) — şema değişikliği olmadan gelir belgesi işareti. Mevcut alış akışına dokunulmaz.
            if (lines.Count == 1 &&
                lines[0].ItemName.TrimStart().StartsWith(EsmmItemPrefix, StringComparison.OrdinalIgnoreCase))
            {
                docs.Add(BuildEsmmDocument(invoice, lines[0], calculatedVatAccount));
                continue;
            }

            var debits = new List<ExportDebit>();
            var vatByRate = new Dictionary<decimal, decimal>();

            foreach (var l in lines)
            {
                var account = l.AccountCode ?? l.SuggestedAccountCode ?? string.Empty;
                var kkegNet = l.IsKkeg && l.KkegAmount is > 0 ? Math.Min(l.KkegAmount.Value, l.LineAmount) : 0m;
                var disallowedVat = KkegAnalyzer.DisallowedVat(l.IsKkeg, l.KkegAmount, l.LineAmount, l.VatAmount);

                // Faz 8 (B2): tevkifat notu — yalnızca Detay Açıklama'ya bilgi olarak yazılır.
                var withholdingNote = string.IsNullOrWhiteSpace(l.WithholdingCode)
                    ? string.Empty
                    : $" (Tevkifat {l.WithholdingCode}{(string.IsNullOrWhiteSpace(l.WithholdingRate) ? "" : $" {l.WithholdingRate}")})";

                if (kkegNet <= 0)
                {
                    // Eski davranış: satırın tamamı kendi hesabına.
                    debits.Add(new ExportDebit(account, l.ItemName.Trim() + withholdingNote, l.LineAmount));
                }
                else
                {
                    var deductibleNet = l.LineAmount - kkegNet;
                    if (deductibleNet > 0)
                        debits.Add(new ExportDebit(account, l.ItemName.Trim() + withholdingNote, deductibleNet));

                    // KKEG satırı: matrah payı + indirilemeyen KDV aynı satırda, gerekçe açıklamada.
                    var reason = string.IsNullOrWhiteSpace(l.KkegReason) ? "KKEG" : l.KkegReason!.Trim();
                    debits.Add(new ExportDebit(kkegAccount, $"KKEG - {reason} - {l.ItemName.Trim()}", kkegNet + disallowedVat, IsKkeg: true));
                }

                // KDV: oran bazında gruplanır; KKEG'e isabet eden (indirilemeyen) kısım 191'e yazılmaz.
                var deductibleVat = l.VatAmount - disallowedVat;
                if (l.VatRate > 0 && deductibleVat > 0)
                    vatByRate[l.VatRate] = vatByRate.TryGetValue(l.VatRate, out var sum) ? sum + deductibleVat : deductibleVat;
            }

            docs.Add(new ExportDocument(
                Date: invoice.IssueDate,
                DocumentNo: invoice.InvoiceNumber,
                Title: invoice.SenderTitle,
                Source: DocumentSource.Invoice,
                Debits: debits,
                VatByRate: vatByRate.Select(kv => new ExportVat(kv.Key, kv.Value)).ToList(),
                CreditAccount: lines.Select(l => l.CariCode).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "320"));
        }

        foreach (var receipt in receipts ?? Array.Empty<Receipt>())
        {
            // Ön-kontrol: tarihi olmayan fiş şablona yazılamaz (Fiş/Evrak Tarihi zorunlu).
            if (receipt.ReceiptDate is null)
            {
                var vendorLabel = string.IsNullOrWhiteSpace(receipt.VendorName) ? receipt.FileName : receipt.VendorName!.Trim();
                errors.Add($"Fiş {vendorLabel}: belge tarihi yok — onay ekranından tarihi girip yeniden deneyin.");
                continue;
            }

            var rateRows = ReceiptVatMath.RowsOrLegacy(receipt.VatBreakdowns, receipt.VatRate, receipt.NetAmount, receipt.VatAmount);
            var vatError = ReceiptVatMath.Validate(receipt.TotalAmount, receipt.NetAmount, receipt.VatAmount, rateRows);
            if (vatError is not null)
            {
                errors.Add($"Fiş {receipt.DocumentNumber ?? receipt.FileName}: {vatError}");
                continue;
            }

            // Evrak No: OCR/onaydan gelen belge no; yoksa deterministik "FIS-{id kısa}".
            var documentNo = string.IsNullOrWhiteSpace(receipt.DocumentNumber)
                ? $"FIS-{receipt.Id.ToString("N")[..8].ToUpperInvariant()}"
                : receipt.DocumentNumber!.Trim();

            // ---------- Faz 16: Z raporu → GELİR FİŞİ (gider kalıbına GİRMEZ) ----------
            if (receipt.DocumentKind == ReceiptDocumentKind.ZRaporu)
            {
                docs.Add(BuildZReportDocument(receipt, documentNo, zIncomeAccount, zCardAccount, calculatedVatAccount));
                continue;
            }

            var account = receipt.AccountCode ?? receipt.SuggestedAccountCode ?? string.Empty;
            var net = receipt.NetAmount ?? 0m;
            var vatRate = receipt.VatRate ?? 0m;
            var vatAmount = receipt.VatAmount ?? 0m;
            var description = string.IsNullOrWhiteSpace(receipt.VendorName)
                ? "Fiş gideri"
                : $"{receipt.VendorName!.Trim()} fişi";

            // Faz 8 (B3): KKEG işaretli fişte borç tarafı bölünür (fatura satırıyla aynı kural).
            var kkegNet = receipt.IsKkeg && receipt.KkegAmount is > 0 ? Math.Min(receipt.KkegAmount.Value, net) : 0m;
            var disallowedVat = KkegAnalyzer.DisallowedVat(receipt.IsKkeg, receipt.KkegAmount, net, vatAmount);

            var receiptDebits = new List<ExportDebit>();
            if (kkegNet <= 0)
            {
                receiptDebits.Add(new ExportDebit(account, description, net));
            }
            else
            {
                var deductibleNet = net - kkegNet;
                if (deductibleNet > 0)
                    receiptDebits.Add(new ExportDebit(account, description, deductibleNet));

                var reason = string.IsNullOrWhiteSpace(receipt.KkegReason) ? "KKEG" : receipt.KkegReason!.Trim();
                receiptDebits.Add(new ExportDebit(kkegAccount, $"KKEG - {reason} - {description}", kkegNet + disallowedVat, IsKkeg: true));
            }

            var deductibleVat = vatAmount - disallowedVat;
            if (deductibleVat < 0) deductibleVat = 0m;

            // ---------- Faz 16: KDV satırları ORAN BAZINDA (fatura dalıyla aynı mantık) ----------
            // Kırılım varsa her oran ayrı 191 satırı olur (ResolveVatAccount(rate) hesabı eşler);
            // kırılımsız eski kayıtlarda tekil oran/tutardan tek satır üretilir (davranış birebir).
            var vatRows = (receipt.VatBreakdowns ?? new List<ReceiptVatBreakdown>())
                .Where(b => b.VatRate > 0 && b.VatAmount > 0)
                .OrderByDescending(b => b.VatRate)
                .Select(b => new ExportVat(b.VatRate, b.VatAmount))
                .ToList();
            if (vatRows.Count == 0 && vatRate > 0 && vatAmount > 0)
                vatRows.Add(new ExportVat(vatRate, vatAmount));

            // KKEG'in indirilemeyen KDV'si kırılım TOPLAMI üzerinden hesaplandı (yukarıda);
            // 191 satırları orantılı azaltılır, kuruş farkı en büyük satırda düzeltilir — denge bozulmaz.
            var vatRowSum = vatRows.Sum(v => v.Amount);
            if (disallowedVat > 0m && vatRowSum > 0m)
            {
                var scaled = vatRows
                    .Select(v => new ExportVat(v.Rate, Round2(v.Amount * deductibleVat / vatRowSum)))
                    .ToList();
                var roundingDiff = Round2(Math.Min(deductibleVat, vatRowSum)) - scaled.Sum(v => v.Amount);
                if (roundingDiff != 0m && scaled.Count > 0)
                {
                    var maxIdx = 0;
                    for (var i = 1; i < scaled.Count; i++)
                        if (scaled[i].Amount > scaled[maxIdx].Amount) maxIdx = i;
                    scaled[maxIdx] = scaled[maxIdx] with { Amount = Round2(scaled[maxIdx].Amount + roundingDiff) };
                }
                vatRows = scaled.Where(v => v.Amount > 0m).ToList();
            }

            docs.Add(new ExportDocument(
                Date: receipt.ReceiptDate.Value,
                DocumentNo: documentNo,
                Title: receipt.VendorName ?? string.Empty,
                Source: DocumentSource.Receipt,
                Debits: receiptDebits,
                VatByRate: vatRows,
                CreditAccount: string.IsNullOrWhiteSpace(receipt.CariCode) ? FallbackCashAccount : receipt.CariCode!));
        }

        return docs.OrderBy(d => d.Date).ThenBy(d => d.DocumentNo, StringComparer.Ordinal);
    }

    private sealed record ExportDebit(string Account, string Detail, decimal Amount, bool IsKkeg = false);
    private sealed record ExportVat(decimal Rate, decimal Amount);

    /// <summary>
    /// e-SMM gelir fişi gibi standart alış kalıbına uymayan belgeler için serbest fiş satırı
    /// (Debit veya Credit'ten yalnız biri dolu olmalıdır).
    /// </summary>
    private sealed record ExportEntry(string Account, string Detail, decimal? Debit, decimal? Credit);

    private sealed record ExportDocument(
        DateTime Date,
        string DocumentNo,
        string Title,
        DocumentSource Source,
        List<ExportDebit> Debits,
        List<ExportVat> VatByRate,
        string CreditAccount,
        List<ExportEntry>? ExplicitRows = null);

    /// <summary>
    /// e-SMM makbuzu GELİR FİŞİ (madde 4 — alış kalıbı DEĞİL):
    ///  - Borç: karşı hesap (satırın CariCode'u, yoksa 120) = tahsil edilen (brüt+KDV−stopaj);
    ///  - Borç: stopaj &gt; 0 ise 193.01 "Peşin Ödenen Vergiler — SMM stopajı";
    ///  - Alacak: gelir hesabı (satırın AccountCode'u, yoksa 600) = brüt ücret;
    ///  - Alacak: hesaplanan KDV (391) = KDV tutarı.
    /// Stopaj satırdan türetilir: brüt + KDV − tahsil edilen (negatifse 0). Denge garantisi:
    /// karşı hesap = alacak toplamı − stopaj yazılır (yuvarlama farkı karşı hesaba gider).
    /// </summary>
    private static ExportDocument BuildEsmmDocument(EInvoice invoice, EInvoiceLine line, string calculatedVatAccount)
    {
        var gross = invoice.TaxExclusiveAmount != 0m ? invoice.TaxExclusiveAmount : line.LineAmount;
        var vat = invoice.TaxTotal != 0m ? invoice.TaxTotal : line.VatAmount;
        var collected = invoice.PayableAmount;

        var withholding = Round2(gross + vat - collected);
        if (withholding < 0m) withholding = 0m;

        var creditTotal = Round2(gross + vat);
        // Denge: borç toplamı = alacak toplamı (karşı hesap kalan tutarı üstlenir).
        var receivable = creditTotal - withholding;

        var receivableAccount = string.IsNullOrWhiteSpace(line.CariCode)
            ? FallbackEsmmReceivableAccount
            : line.CariCode!.Trim();
        var incomeAccount = string.IsNullOrWhiteSpace(line.AccountCode)
            ? line.SuggestedAccountCode
            : line.AccountCode;
        if (string.IsNullOrWhiteSpace(incomeAccount)) incomeAccount = FallbackEsmmIncomeAccount;

        var title = invoice.SenderTitle?.Trim() ?? string.Empty;

        var rows = new List<ExportEntry>
        {
            new(receivableAccount, title, Debit: receivable, Credit: null)
        };
        if (withholding > 0m)
        {
            rows.Add(new ExportEntry(
                FallbackEsmmWithholdingAccount, "Peşin Ödenen Vergiler — SMM stopajı",
                Debit: withholding, Credit: null));
        }
        rows.Add(new ExportEntry(incomeAccount!, title, Debit: null, Credit: Round2(gross)));
        if (vat > 0m)
        {
            // Alacak toplamının Round2(gross)+Round2(vat) ile birebir tutması için KDV satırı
            // kalan tutar olarak yazılır (kuruş farkı KDV yerine karşı hesapta DEĞİL, burada
            // sıfırlanır: creditTotal − Round2(gross)).
            rows.Add(new ExportEntry(
                calculatedVatAccount, "Hesaplanan KDV", Debit: null, Credit: creditTotal - Round2(gross)));
        }

        return new ExportDocument(
            Date: invoice.IssueDate,
            DocumentNo: invoice.InvoiceNumber,
            Title: invoice.SenderTitle,
            Source: DocumentSource.Invoice,
            Debits: new List<ExportDebit>(),
            VatByRate: new List<ExportVat>(),
            CreditAccount: receivableAccount,
            ExplicitRows: rows);
    }

    /// <summary>
    /// Faz 16: Z raporu (yazar kasa günlük kapanış) GELİR FİŞİ — e-SMM kalıbına benzer:
    ///  - Borç: 100 Kasa = nakit tahsilat; kart hesabı ("zrapor.kart.hesap", varsayılan 108) = kart.
    ///    Nakit/kart kırılımı okunamadıysa tamamı kasaya yazılır.
    ///  - Alacak: ORAN BAZINDA gelir hesabı (fişin onaylı hesabı, yoksa "zrapor.gelir.hesap",
    ///    varsayılan 600) = matrah; hesaplanan KDV ("kdv.hesaplanan.hesap", varsayılan 391) = KDV.
    /// Denge garantisi: kart = min(kart, alacak toplamı), nakit = alacak toplamı − kart
    /// (yuvarlama/okuma farkı kasa satırında sıfırlanır — borç toplamı = alacak toplamı).
    /// Fiş açıklama: "{tarih} Z Raporu #{zNo}" (BuildTemplateAsync gelir dalı üretir).
    /// </summary>
    private static ExportDocument BuildZReportDocument(
        Receipt receipt, string documentNo, string zIncomeAccount, string zCardAccount, string calculatedVatAccount)
    {
        var title = string.IsNullOrWhiteSpace(receipt.VendorName)
            ? $"Z Raporu #{documentNo}"
            : receipt.VendorName!.Trim();

        // Gelir hesabı: SMMM onayda hesap girdiyse o; yoksa parametre (600).
        var incomeAccount = string.IsNullOrWhiteSpace(receipt.AccountCode) ? zIncomeAccount : receipt.AccountCode!.Trim();

        // Alacak tarafı oran bazında: kırılım varsa satır satır; yoksa tekil toplamlardan tek blok.
        var breakdown = (receipt.VatBreakdowns ?? new List<ReceiptVatBreakdown>())
            .Where(b => b.NetAmount != 0m || b.VatAmount != 0m)
            .OrderByDescending(b => b.VatRate)
            .Select(b => (b.VatRate, Net: Round2(b.NetAmount), Vat: Round2(b.VatAmount)))
            .ToList();
        if (breakdown.Count == 0)
        {
            var fallbackNet = Round2(receipt.NetAmount ?? 0m);
            var fallbackVat = Round2(receipt.VatAmount ?? 0m);
            if (fallbackNet != 0m || fallbackVat != 0m)
                breakdown.Add((receipt.VatRate ?? 0m, fallbackNet, fallbackVat));
        }

        var creditRows = new List<ExportEntry>();
        decimal creditTotal = 0m;
        foreach (var (rate, netPart, vatPart) in breakdown)
        {
            if (netPart > 0m)
            {
                creditRows.Add(new ExportEntry(
                    incomeAccount, $"%{rate:0.##} satış geliri — {title}", Debit: null, Credit: netPart));
                creditTotal += netPart;
            }
            if (vatPart > 0m)
            {
                creditRows.Add(new ExportEntry(
                    calculatedVatAccount, $"Hesaplanan KDV %{rate:0.##}", Debit: null, Credit: vatPart));
                creditTotal += vatPart;
            }
        }
        creditTotal = Round2(creditTotal);

        // Borç tarafı: kart (varsa) + nakit = kalan → borç toplamı alacak toplamına eşitlenir.
        var card = Round2(Math.Clamp(receipt.CardTotal ?? 0m, 0m, creditTotal));
        var cash = Round2(creditTotal - card);

        var rows = new List<ExportEntry>();
        if (cash > 0m)
            rows.Add(new ExportEntry(FallbackCashAccount, $"Nakit tahsilat — {title}", Debit: cash, Credit: null));
        if (card > 0m)
            rows.Add(new ExportEntry(zCardAccount, $"Kart tahsilatı — {title}", Debit: card, Credit: null));
        rows.AddRange(creditRows);

        return new ExportDocument(
            Date: receipt.ReceiptDate!.Value,
            DocumentNo: documentNo,
            Title: title,
            Source: DocumentSource.Receipt,
            Debits: new List<ExportDebit>(),
            VatByRate: new List<ExportVat>(),
            CreditAccount: FallbackCashAccount,
            ExplicitRows: rows);
    }

    /// <summary>Dosya adı için güvenli mükellef kısaltması (Türkçe karakterler sadeleştirilir).</summary>
    public static string Slugify(string title)
    {
        var map = new Dictionary<char, char>
        {
            ['ç'] = 'c', ['Ç'] = 'c', ['ğ'] = 'g', ['Ğ'] = 'g', ['ı'] = 'i', ['I'] = 'i',
            ['İ'] = 'i', ['ö'] = 'o', ['Ö'] = 'o', ['ş'] = 's', ['Ş'] = 's', ['ü'] = 'u', ['Ü'] = 'u'
        };

        var sb = new StringBuilder();
        foreach (var ch in title.Trim())
        {
            var c = map.TryGetValue(ch, out var mapped) ? mapped : char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = "mukellef";
        return slug.Length <= 40 ? slug : slug[..40];
    }

    /// <summary>
    /// Toplu ZIP export için zip İÇİ dosya adı temizleyici: Türkçe karakterler KORUNUR;
    /// yalnızca dosya sisteminde geçersiz karakterler (/ \ : * ? " &lt; &gt; |) ve kontrol
    /// karakterleri "_" yapılır, sonuç 80 karaktere kırpılır. Boş kalırsa "Mukellef" döner.
    /// (Slugify'dan farkı: zip içinde okunaklı, Türkçe unvan istenir.)
    /// </summary>
    public static string SanitizeFileName(string title)
    {
        var invalid = new HashSet<char> { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var sb = new StringBuilder();
        foreach (var ch in title.Trim())
            sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);

        var name = sb.ToString().Trim();
        if (name.Length == 0) name = "Mukellef";
        return name.Length <= 80 ? name : name[..80].TrimEnd();
    }

    // ---------- Biçim yardımcıları ----------

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>CSV tutarı: Türkçe Excel uyumu için virgül ondalıklı; null → boş alan (0 değil).</summary>
    private static string? CsvMoney(decimal? value) => value?.ToString("0.00", Turkish);

    private static void AppendCsvRow(StringBuilder sb, params string?[] fields)
        => sb.Append(string.Join(';', fields.Select(f => EscapeField(f ?? string.Empty)))).Append("\r\n");

    /// <summary>Ayraç (;), tırnak veya satır sonu içeren alanlar çift tırnakla korunur.</summary>
    private static string EscapeField(string field)
    {
        if (field.Contains(';') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }

    /// <summary>UTF-8 BOM ekler: Türkçe Excel, BOM'suz UTF-8 CSV'de karakterleri bozar.</summary>
    private static byte[] WithBom(string content)
    {
        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(content);
        var result = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(body, 0, result, bom.Length, body.Length);
        return result;
    }
}
