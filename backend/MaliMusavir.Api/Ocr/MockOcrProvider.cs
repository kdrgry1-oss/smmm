using System.Security.Cryptography;
using System.Text;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Ocr;

/// <summary>
/// Demo/test OCR sağlayıcısı: dosya içeriğine bakmadan, dosya adı + boyutundan
/// DETERMİNİSTİK gerçekçi Türk fişi üretir (aynı dosya her zaman aynı sonucu verir).
/// Market / akaryakıt / restoran / kırtasiye / tekel çeşitliliği içerir; güven skoru
/// 0.55-0.98 arasında değişir — düşük güvenli örnekler onay akışının test edilmesini sağlar,
/// tekel (alkol/tütün) profili gider gösterilebilirlik reddini tetikler.
/// </summary>
public class MockOcrProvider : IReceiptOcrProvider
{
    public string Name => "Mock";

    /// <summary>
    /// Gerçekçi fiş satıcı profilleri:
    /// (unvan, VKN, kalem özeti [kural/uygunluk metni], KDV oranı %, min-maks KDV dahil toplam).
    /// "Nokta Tekel Bayii" profili alkol/tütün anahtar kelimeleriyle uygunluk reddini test eder.
    /// </summary>
    private static readonly MockReceiptVendor[] Vendors =
    {
        new("Migros Ticaret A.Ş.", "6200278131", "market gıda temizlik alışverişi", 10m, 150m, 2400m),
        new("A101 Yeni Mağazacılık A.Ş.", "0110099887", "market gıda içecek alışverişi", 10m, 80m, 1200m),
        new("Shell & Turcas Petrol A.Ş.", "8090012353", "akaryakıt motorin benzin yakıt", 20m, 500m, 3000m, IsFuel: true),
        new("Opet Petrolcülük A.Ş.", "6450321789", "akaryakıt kurşunsuz benzin yakıt", 20m, 400m, 2500m, IsFuel: true),
        new("Divan Lokantası Ltd. Şti.", "2960045678", "restoran yemek servis hizmeti", 10m, 250m, 1800m),
        new("Simit Sarayı İşletmeleri A.Ş.", "7700891234", "restoran kafe yiyecek içecek ikram", 10m, 60m, 450m),
        new("Kırtasiye Dünyası Ltd. Şti.", "5560012398", "kırtasiye kalem defter toner ofis malzemesi", 20m, 120m, 1600m),
        new("Nokta Tekel Bayii", "4420067890", "alkol bira şarap tütün sigara", 20m, 200m, 900m)
    };

    /// <summary>
    /// Faz 13: akaryakıt profillerinde deterministik üretilen plakalar (Türk plaka desenine uygun).
    /// SMMM aracı bu plakayla kaydederse eşleşme, kaydetmezse "eşleşmeyen plaka" uyarısı test edilir.
    /// </summary>
    private static readonly string[] MockPlates =
    {
        "34FPC630", "06ABC123", "35KLM456", "16DEF78", "01TST250", "41KJH880"
    };

    public Task<ProviderResult<OcrReceiptResult>> ExtractAsync(
        string filePath, string originalFileName, long fileSize, OcrDocumentKind kind, CancellationToken ct)
    {
        // Deterministik tohum: dosya adı (küçük harf) + boyut. String.GetHashCode
        // süreçler arası deterministik olmadığı için MD5 kullanılır (MockProvider ile aynı desen).
        var seed = DeterministicSeed($"{originalFileName.ToLowerInvariant()}|{fileSize}");

        // Z raporu okuması: çok oranlı KDV kırılımı + nakit/kart dağılımıyla deterministik rapor.
        if (kind == OcrDocumentKind.ZRaporu)
            return Task.FromResult(ProviderResult<OcrReceiptResult>.Ok(BuildZReport(seed)));

        var vendor = Vendors[seed % Vendors.Length];

        // Belge tarihi: 0-44 gün önce → bazı fişler 30 günden eski çıkar (eski belge uyarısı test edilir).
        var daysAgo = (seed / 7) % 45;
        var receiptDate = DateTime.UtcNow.Date.AddDays(-daysAgo);

        // KDV dahil toplam: profil aralığında deterministik; matrah/KDV geriye doğru hesaplanır.
        var span = vendor.MaxTotal - vendor.MinTotal;
        var total = Math.Round(vendor.MinTotal + span * ((seed / 11) % 1000) / 1000m, 2);
        var net = Math.Round(total / (1 + vendor.VatRate / 100m), 2);
        var vat = Math.Round(total - net, 2);

        // Güven skoru 0.55-0.98: düşük güvenli örnekler de üretilir ki NeedsReview akışı test edilebilsin.
        var confidence = Math.Round(0.55 + ((seed / 13) % 44) / 100.0, 2);

        var result = new OcrReceiptResult
        {
            VendorName = vendor.Title,
            VendorTaxId = vendor.TaxId,
            // Fiş numarası (Z no benzeri) — deterministik; Luca Evrak No kolonunu test eder.
            DocumentNumber = $"Z{(seed / 17) % 9000 + 1000}",
            ReceiptDate = receiptDate,
            TotalAmount = total,
            VatRate = vendor.VatRate,
            VatAmount = vat,
            NetAmount = net,
            // Faz 13: akaryakıt fişinde deterministik plaka (aynı dosya → hep aynı plaka);
            // diğer profillerde null (plakasız fiş akışı birebir eski davranıştır).
            PlateNumber = vendor.IsFuel ? MockPlates[(seed / 19) % MockPlates.Length] : null,
            // Tek oranlı fişte tek satırlık kırılım (gerçek sağlayıcının validator türetmesiyle aynı).
            VatBreakdown = new List<OcrVatBreakdownRow>
            {
                new() { Rate = vendor.VatRate, Net = net, Vat = vat }
            },
            Confidence = confidence,
            ItemsSummary = vendor.ItemsSummary
        };

        return Task.FromResult(ProviderResult<OcrReceiptResult>.Ok(result));
    }

    /// <summary>
    /// Deterministik Z raporu: %1/%10/%20 üç oranlı KDV kırılımı, günlük toplam
    /// ve nakit/kart dağılımı (nakit payı %30-%70 arası deterministik).
    /// </summary>
    private static OcrReceiptResult BuildZReport(int seed)
    {
        var daysAgo = (seed / 7) % 10; // Z raporu güncel olur (0-9 gün önce)
        var reportDate = DateTime.UtcNow.Date.AddDays(-daysAgo);

        // Oran bazlı matrahlar (deterministik): %1 gıda, %10, %20.
        var net1 = Math.Round(200m + (seed / 11) % 800, 2);
        var net10 = Math.Round(500m + (seed / 13) % 2500, 2);
        var net20 = Math.Round(1000m + (seed / 17) % 5000, 2);

        var breakdown = new List<OcrVatBreakdownRow>
        {
            new() { Rate = 20m, Net = net20, Vat = Math.Round(net20 * 0.20m, 2) },
            new() { Rate = 10m, Net = net10, Vat = Math.Round(net10 * 0.10m, 2) },
            new() { Rate = 1m, Net = net1, Vat = Math.Round(net1 * 0.01m, 2) },
        };

        var netTotal = breakdown.Sum(b => b.Net);
        var vatTotal = breakdown.Sum(b => b.Vat);
        var total = Math.Round(netTotal + vatTotal, 2);

        // Nakit payı %30-%70 arası; kart kalan tutar (nakit+kart = toplam garanti).
        var cashRatio = 0.30m + ((seed / 19) % 41) / 100m;
        var cash = Math.Round(total * cashRatio, 2);
        var card = Math.Round(total - cash, 2);

        var okcSerial = $"AB{(seed % 90000000) + 10000000}";
        var zNumber = ((seed / 23) % 9000 + 1000).ToString();

        return new OcrReceiptResult
        {
            VendorName = string.Empty, // ReceiptService "Z Raporu {seri} #{Z no}" olarak kurar
            DocumentNumber = zNumber,
            ReceiptDate = reportDate,
            TotalAmount = total,
            VatRate = 20m, // en yüksek tutarlı oran
            VatAmount = Math.Round(vatTotal, 2),
            NetAmount = Math.Round(netTotal, 2),
            VatBreakdown = breakdown,
            OkcSerialNumber = okcSerial,
            ZNumber = zNumber,
            CashTotal = cash,
            CardTotal = card,
            Confidence = Math.Round(0.75 + ((seed / 29) % 24) / 100.0, 2),
            ItemsSummary = string.Empty
        };
    }

    /// <summary>Verilen metinden her çalıştırmada aynı pozitif tohumu üretir (MD5 tabanlı).</summary>
    private static int DeterministicSeed(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var value = BitConverter.ToInt32(hash, 0);
        return value == int.MinValue ? 0 : Math.Abs(value);
    }

    private sealed record MockReceiptVendor(
        string Title,
        string TaxId,
        string ItemsSummary,
        decimal VatRate,
        decimal MinTotal,
        decimal MaxTotal,
        bool IsFuel = false);
}
