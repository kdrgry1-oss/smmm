namespace MaliMusavir.Api.Integrations;

/// <summary>
/// Bir günün Z raporu verisi (sağlayıcı-bağımsız DTO). OCR yolundaki alan setiyle birebir:
/// ÖKC seri no, Z no, tarih, oran bazlı KDV kırılımı, toplamlar ve nakit/kart dağılımı.
/// </summary>
public class ZReportData
{
    /// <summary>Yazar kasa (ÖKC) sicil/seri numarası.</summary>
    public string OkcSerialNumber { get; set; } = string.Empty;
    /// <summary>Z raporu sayaç numarası.</summary>
    public string ZNumber { get; set; } = string.Empty;
    public DateTime ReportDate { get; set; }
    /// <summary>Oran bazlı KDV kırılımı (oran %, matrah, KDV).</summary>
    public List<ZReportVatLine> VatBreakdown { get; set; } = new();
    /// <summary>Günlük KDV dahil toplam satış.</summary>
    public decimal TotalSales { get; set; }
    /// <summary>Günlük toplam KDV.</summary>
    public decimal TotalVat { get; set; }
    /// <summary>Nakit tahsilat (KDV dahil); sağlayıcı vermiyorsa null.</summary>
    public decimal? CashTotal { get; set; }
    /// <summary>Kart tahsilatı (KDV dahil); sağlayıcı vermiyorsa null.</summary>
    public decimal? CardTotal { get; set; }
}

public class ZReportVatLine
{
    public decimal Rate { get; set; }
    public decimal Net { get; set; }
    public decimal Vat { get; set; }
}

/// <summary>
/// İLERİYE DÖNÜK adaptör arayüzü: Z raporlarını doğrudan TSM merkezi / POS-ÖKC sağlayıcısı
/// API'sinden çekmek için. Bugün BİRİNCİL yol OCR'dır (fotoğraf/PDF yükleme) — çünkü yazar
/// kasa üreticilerinin merchant düzeyinde açık API'si yoktur; veri TSM merkezleri üzerinden
/// GİB'e akar. Ödeal / Paycell / Token gibi merchant API'leri TİCARİ ANLAŞMA gerektirir;
/// anlaşma sağlandığında bu arayüzü uygulayan gerçek adaptör eklenecektir.
/// DI'a MockZReportProvider kaydedilir; UI'da GÖSTERİLMEZ (henüz kullanıcıya açık değil).
/// </summary>
public interface IZReportProvider
{
    /// <summary>Sağlayıcının görünen adı (ör. "Mock", "Odeal", "Paycell", "Token").</summary>
    string Name { get; }

    /// <summary>Mükellefin [from, to] aralığındaki günlük Z raporlarını döndürür.</summary>
    Task<ProviderResult<List<ZReportData>>> GetDailyReportsAsync(
        Guid clientId, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// Deterministik demo sağlayıcısı: gün başına tek Z raporu üretir (üç oranlı kırılım,
/// nakit/kart dağılımı). Gerçek TSM/POS adaptörü gelene kadar arayüz sözleşmesini canlı tutar.
/// </summary>
public class MockZReportProvider : IZReportProvider
{
    public string Name => "Mock";

    public Task<ProviderResult<List<ZReportData>>> GetDailyReportsAsync(
        Guid clientId, DateTime from, DateTime to, CancellationToken ct)
    {
        var reports = new List<ZReportData>();
        var seedBase = clientId.GetHashCode() & 0x7FFFFFFF;

        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var seed = (seedBase + day.DayOfYear * 31) & 0x7FFFFFFF;

            var net1 = Math.Round(150m + seed % 700, 2);
            var net10 = Math.Round(400m + (seed / 7) % 2000, 2);
            var net20 = Math.Round(900m + (seed / 11) % 4000, 2);

            var lines = new List<ZReportVatLine>
            {
                new() { Rate = 20m, Net = net20, Vat = Math.Round(net20 * 0.20m, 2) },
                new() { Rate = 10m, Net = net10, Vat = Math.Round(net10 * 0.10m, 2) },
                new() { Rate = 1m, Net = net1, Vat = Math.Round(net1 * 0.01m, 2) },
            };

            var totalVat = lines.Sum(l => l.Vat);
            var totalSales = Math.Round(lines.Sum(l => l.Net) + totalVat, 2);
            var cash = Math.Round(totalSales * (0.30m + (seed % 41) / 100m), 2);

            reports.Add(new ZReportData
            {
                OkcSerialNumber = $"AB{(seed % 90000000) + 10000000}",
                ZNumber = ((seed / 13) % 9000 + 1000).ToString(),
                ReportDate = day,
                VatBreakdown = lines,
                TotalSales = totalSales,
                TotalVat = Math.Round(totalVat, 2),
                CashTotal = cash,
                CardTotal = Math.Round(totalSales - cash, 2)
            });
        }

        return Task.FromResult(ProviderResult<List<ZReportData>>.Ok(reports));
    }
}
