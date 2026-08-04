using System.Globalization;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Mizan satırı: hesap bazlı borç/alacak toplamı ve bakiyesi.</summary>
public class TrialBalanceRow
{
    public string HesapKodu { get; set; } = string.Empty;
    /// <summary>ChartAccount'ta tanımlıysa oradan; yoksa bilinen tekdüzen hesap adları sözlüğünden.</summary>
    public string HesapAdi { get; set; } = string.Empty;
    public decimal Borc { get; set; }
    public decimal Alacak { get; set; }
    public decimal BorcBakiye { get; set; }
    public decimal AlacakBakiye { get; set; }
}

/// <summary>Uyum kontrol satırı (E8): mizan ↔ KDV taslağı / KKEG karşılaştırmaları.</summary>
public class TrialBalanceCheck
{
    public string Ad { get; set; } = string.Empty;
    /// <summary>"Ok" / "Uyari" (PeriodCheckStatus ile aynı sözleşme; burada Hata üretilmez).</summary>
    public PeriodCheckStatus Durum { get; set; }
    public string Detay { get; set; } = string.Empty;
}

/// <summary>Mizan sonucu (E8).</summary>
public class TrialBalanceResult
{
    public Guid ClientId { get; set; }
    public string Period { get; set; } = string.Empty;
    public List<TrialBalanceRow> Rows { get; set; } = new();
    public decimal ToplamBorc { get; set; }
    public decimal ToplamAlacak { get; set; }
    /// <summary>|Borç − Alacak| ≤ 0,01 TL → denk.</summary>
    public bool Denk { get; set; }
    /// <summary>Borç − Alacak farkı (denk ise 0).</summary>
    public decimal Fark { get; set; }
    public List<TrialBalanceCheck> UyumKontrolleri { get; set; } = new();
    public int FaturaSayisi { get; set; }
    public int FisSayisi { get; set; }
    /// <summary>Şablon üretiminde çıkan eksik alan uyarıları (hesapsız satır vb. — bilgi).</summary>
    public List<string> Uyarilar { get; set; } = new();
}

/// <summary>
/// Faz 11 — E8: Mizan &amp; uyum kontrolü. Dönemdeki ONAYLI fatura + fişlerden hesap bazlı
/// mizan üretir. KOD TEKRARI YOKTUR: dengeli fiş satırları LucaExcelExporter.BuildTemplateAsync
/// (Luca dışa aktarmayla AYNI çekirdek — KKEG bölünmesi, oran bazlı 191 gruplaması, cari/kasa
/// alacak satırı, parametre merkezi hesap eşlemeleri) ile üretilir ve burada yalnızca hesap
/// koduna göre TOPLANIR. Böylece mizan, Luca'ya aktarılan fişlerle birebir aynı dağılımı verir.
/// </summary>
public class TrialBalanceService
{
    /// <summary>Denklik ve uyum karşılaştırma toleransı (kuruş yuvarlamaları).</summary>
    public const decimal Tolerance = 0.05m;
    private const decimal BalanceTolerance = 0.01m;

    /// <summary>Hesap kodu atanmamış satırların mizanda toplandığı kova.</summary>
    public const string UncodedBucket = "(kodsuz)";

    /// <summary>Bilinen tekdüzen hesap adları — ChartAccount kaydı yoksa buradan etiketlenir.</summary>
    private static readonly Dictionary<string, string> WellKnownAccountNames = new(StringComparer.Ordinal)
    {
        ["100"] = "Kasa",
        ["153"] = "Ticari Mallar",
        ["191"] = "İndirilecek KDV",
        ["320"] = "Satıcılar",
        ["689"] = "Diğer Olağandışı Gider ve Zararlar (KKEG)",
        ["740"] = "Hizmet Üretim Maliyeti",
        ["760"] = "Pazarlama Satış ve Dağıtım Giderleri",
        ["770"] = "Genel Yönetim Giderleri",
    };

    private readonly AppDbContext _db;
    private readonly LucaExcelExporter _exporter;
    private readonly ParameterService _parameters;

    public TrialBalanceService(AppDbContext db, LucaExcelExporter exporter, ParameterService parameters)
    {
        _db = db;
        _exporter = exporter;
        _parameters = parameters;
    }

    /// <summary>
    /// Dönem mizanını üretir. Dönemde onaylı belge yoksa boş mizan döner (Rows boş, Denk true) —
    /// UI boş durumu gösterir. Geçersiz dönem biçiminde ArgumentException (Türkçe) fırlatır.
    /// </summary>
    public async Task<TrialBalanceResult> BuildAsync(Guid clientId, string period, CancellationToken ct)
    {
        if (!DeclarationService.TryParsePeriod(period, out var periodStart))
            throw new ArgumentException("Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).", nameof(period));

        var periodEnd = periodStart.AddMonths(1);

        // Dönemdeki ONAYLI belgeler — Luca export'la aynı seçim kuralları (CodingStatus/ReceiptStatus.Approved).
        var invoices = await _db.EInvoices.AsNoTracking()
            .Include(e => e.Lines)
            .Where(e => e.ClientId == clientId
                        && e.Direction == InvoiceDirection.Incoming
                        && e.CodingStatus == CodingStatus.Approved
                        && e.IssueDate >= periodStart && e.IssueDate < periodEnd)
            .OrderBy(e => e.IssueDate)
            .ToListAsync(ct);

        var receipts = await _db.Receipts.AsNoTracking()
            .Include(r => r.VatBreakdowns) // Faz 16: KDV satırları oran bazında üretilir (mizan da aynı çekirdek)
            .Where(r => r.ClientId == clientId
                        && r.Status == ReceiptStatus.Approved
                        && r.ReceiptDate != null
                        && r.ReceiptDate >= periodStart && r.ReceiptDate < periodEnd)
            .OrderBy(r => r.ReceiptDate)
            .ToListAsync(ct);

        // ORTAK ÇEKİRDEK: dengeli fiş satırları exporter'dan (davranış birebir Luca dosyasıyla aynı).
        var build = await _exporter.BuildTemplateAsync(invoices, receipts, ct);

        // Hesap bazlı toplama.
        var buckets = new Dictionary<string, TrialBalanceRow>(StringComparer.Ordinal);
        foreach (var row in build.Rows)
        {
            var code = string.IsNullOrWhiteSpace(row.HesapKodu) ? UncodedBucket : row.HesapKodu.Trim();
            if (!buckets.TryGetValue(code, out var bucket))
            {
                bucket = new TrialBalanceRow { HesapKodu = code };
                buckets[code] = bucket;
            }
            bucket.Borc += row.Borc ?? 0m;
            bucket.Alacak += row.Alacak ?? 0m;
        }

        // Hesap adları: önce mükellefin kanonik planı (ChartAccount), yoksa bilinen tekdüzen adlar.
        var chartNames = await _db.ChartAccounts.AsNoTracking()
            .Where(a => a.ClientId == clientId)
            .ToDictionaryAsync(a => a.Code, a => a.Name, StringComparer.Ordinal, ct);

        var rows = buckets.Values
            .OrderBy(r => r.HesapKodu == UncodedBucket ? 1 : 0) // kodsuz kova en sonda
            .ThenBy(r => r.HesapKodu, StringComparer.Ordinal)
            .Select(r =>
            {
                r.Borc = Math.Round(r.Borc, 2);
                r.Alacak = Math.Round(r.Alacak, 2);
                var net = r.Borc - r.Alacak;
                r.BorcBakiye = net > 0 ? net : 0m;
                r.AlacakBakiye = net < 0 ? -net : 0m;
                r.HesapAdi = ResolveAccountName(r.HesapKodu, chartNames);
                return r;
            })
            .ToList();

        var toplamBorc = Math.Round(rows.Sum(r => r.Borc), 2);
        var toplamAlacak = Math.Round(rows.Sum(r => r.Alacak), 2);
        var fark = Math.Round(toplamBorc - toplamAlacak, 2);

        var result = new TrialBalanceResult
        {
            ClientId = clientId,
            Period = period,
            Rows = rows,
            ToplamBorc = toplamBorc,
            ToplamAlacak = toplamAlacak,
            Denk = Math.Abs(fark) <= BalanceTolerance,
            Fark = fark,
            FaturaSayisi = invoices.Count,
            FisSayisi = receipts.Count,
            Uyarilar = build.Errors
        };

        await AddComplianceChecksAsync(result, clientId, period, ct);
        return result;
    }

    /// <summary>
    /// Uyum kontrolleri (E8):
    ///  1) Mizan 191* borç toplamı ≈ KDV1 taslağındaki indirilecek KDV (±0,05 TL).
    ///  2) Mizan KKEG hesabı (parametre "kkeg.hesap", varsayılan 689) toplamı = KDV1 özetindeki KKEG
    ///     matrah + indirilemeyen KDV toplamı (mizan KKEG satırı iki bileşeni birlikte taşır).
    /// KDV1 taslağı hazırlanmadıysa karşılaştırma yapılamaz → Uyari (bilgi).
    /// </summary>
    private async Task AddComplianceChecksAsync(
        TrialBalanceResult result, Guid clientId, string period, CancellationToken ct)
    {
        var summaryJson = await _db.Declarations.AsNoTracking()
            .Where(d => d.ClientId == clientId && d.Type == DeclarationType.KDV1 && d.Period == period)
            .Select(d => d.SummaryJson)
            .FirstOrDefaultAsync(ct);

        var summary = DeclarationService.ParseSummary(summaryJson);

        // 191* toplamı (191, 191.01, 191.02 ... hepsi indirilecek KDV ailesidir).
        var mizan191 = Math.Round(result.Rows
            .Where(r => r.HesapKodu == "191" || r.HesapKodu.StartsWith("191.", StringComparison.Ordinal))
            .Sum(r => r.Borc), 2);

        // KKEG hesabı parametre merkezinden (LucaExcelExporter ile aynı kaynak; varsayılan 689).
        await _parameters.EnsureLoadedAsync(ct);
        var kkegAccount = _parameters.Get("kkeg.hesap", "689");
        var mizanKkeg = Math.Round(result.Rows
            .Where(r => r.HesapKodu == kkegAccount ||
                        r.HesapKodu.StartsWith(kkegAccount + ".", StringComparison.Ordinal))
            .Sum(r => r.Borc), 2);

        if (summary is null)
        {
            result.UyumKontrolleri.Add(new TrialBalanceCheck
            {
                Ad = "KDV1 taslağı karşılaştırması",
                Durum = PeriodCheckStatus.Uyari,
                Detay = "Bu dönem için KDV1 taslağı hazırlanmadığından mizan-beyanname karşılaştırması yapılamadı. " +
                        "Beyannameler sayfasından KDV1 taslağını hazırlayın."
            });
            return;
        }

        // 1) 191 ↔ indirilecek KDV.
        var kdvFark = Math.Round(mizan191 - summary.IndirilecekKdvToplam, 2);
        result.UyumKontrolleri.Add(new TrialBalanceCheck
        {
            Ad = "Mizan 191 = KDV1 indirilecek KDV",
            Durum = Math.Abs(kdvFark) <= Tolerance ? PeriodCheckStatus.Ok : PeriodCheckStatus.Uyari,
            Detay = Math.Abs(kdvFark) <= Tolerance
                ? $"Mizan 191 toplamı ({Money(mizan191)} TL) KDV1 taslağındaki indirilecek KDV ile uyumlu."
                : $"Mizan 191 toplamı {Money(mizan191)} TL, KDV1 taslağındaki indirilecek KDV {Money(summary.IndirilecekKdvToplam)} TL — " +
                  $"fark {Money(Math.Abs(kdvFark))} TL. Taslağı yeniden hesaplayın veya onay bekleyen belgeleri kontrol edin."
        });

        // 2) KKEG hesabı ↔ KKEG toplamı. Mizan KKEG satırı = matrah payı + indirilemeyen KDV
        //    (LucaExcelExporter KKEG bölme kuralı); özetteki iki alanın toplamıyla karşılaştırılır.
        var beklenenKkeg = Math.Round(summary.KkegToplam + summary.KkegIndirilemeyenKdv, 2);
        var kkegFark = Math.Round(mizanKkeg - beklenenKkeg, 2);
        result.UyumKontrolleri.Add(new TrialBalanceCheck
        {
            Ad = $"Mizan {kkegAccount} = KKEG toplamı",
            Durum = Math.Abs(kkegFark) <= Tolerance ? PeriodCheckStatus.Ok : PeriodCheckStatus.Uyari,
            Detay = Math.Abs(kkegFark) <= Tolerance
                ? beklenenKkeg == 0m && mizanKkeg == 0m
                    ? "Dönemde KKEG kaydı yok — kontrol temiz."
                    : $"Mizan {kkegAccount} toplamı ({Money(mizanKkeg)} TL) dönem KKEG toplamıyla (matrah + indirilemeyen KDV) uyumlu."
                : $"Mizan {kkegAccount} toplamı {Money(mizanKkeg)} TL, KDV1 özetindeki KKEG toplamı (matrah + indirilemeyen KDV) {Money(beklenenKkeg)} TL — " +
                  $"fark {Money(Math.Abs(kkegFark))} TL. KKEG işaretli belgeleri ve taslağı kontrol edin."
        });
    }

    /// <summary>Mizanı MinimalXlsxWriter ile .xlsx dosyasına yazar (TOPLAM satırı dahil).</summary>
    public static byte[] ToXlsx(TrialBalanceResult result)
    {
        var headers = new[] { "Hesap Kodu", "Hesap Adı", "Borç", "Alacak", "Borç Bakiye", "Alacak Bakiye" };

        var rows = result.Rows
            .Select(r => (IReadOnlyList<MinimalXlsxWriter.Cell>)new[]
            {
                MinimalXlsxWriter.Cell.Text(r.HesapKodu),
                MinimalXlsxWriter.Cell.Text(r.HesapAdi),
                MinimalXlsxWriter.Cell.Number(r.Borc),
                MinimalXlsxWriter.Cell.Number(r.Alacak),
                MinimalXlsxWriter.Cell.Number(r.BorcBakiye),
                MinimalXlsxWriter.Cell.Number(r.AlacakBakiye),
            })
            .ToList();

        rows.Add(new[]
        {
            MinimalXlsxWriter.Cell.Text("TOPLAM"),
            MinimalXlsxWriter.Cell.Text(result.Denk ? "Denk" : $"Dengesiz (fark {result.Fark:0.00})"),
            MinimalXlsxWriter.Cell.Number(result.ToplamBorc),
            MinimalXlsxWriter.Cell.Number(result.ToplamAlacak),
            MinimalXlsxWriter.Cell.Number(Math.Max(result.Fark, 0m)),
            MinimalXlsxWriter.Cell.Number(Math.Max(-result.Fark, 0m)),
        });

        return MinimalXlsxWriter.Build("Mizan", headers, rows);
    }

    private static string ResolveAccountName(string code, Dictionary<string, string> chartNames)
    {
        if (code == UncodedBucket) return "Hesap kodu atanmamış satırlar";
        if (chartNames.TryGetValue(code, out var exact)) return exact;

        // Alt kırılım (ör. 191.01) için ana hesap adı + kırılım etiketi.
        var mainCode = code.Split('.')[0];
        if (chartNames.TryGetValue(mainCode, out var chartMain))
            return code == mainCode ? chartMain : $"{chartMain} ({code})";
        if (WellKnownAccountNames.TryGetValue(mainCode, out var known))
            return code == mainCode ? known : $"{known} ({code})";
        return string.Empty;
    }

    private static string Money(decimal value) =>
        value.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));
}
