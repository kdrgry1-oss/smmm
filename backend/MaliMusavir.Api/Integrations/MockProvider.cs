using System.Security.Cryptography;
using System.Text;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// Demo/test sağlayıcısı: API anahtarı olmadan uçtan uca akışı çalıştırmak için
/// gerçekçi sahte gelen faturalar üretir. Üretim DETERMİNİSTİKTİR: aynı tarih aralığı
/// her zaman aynı UUID'li faturaları döndürür — böylece ikinci senkronda mükerrer
/// atlama akışı da test edilebilir.
/// </summary>
public class MockProvider : IEInvoiceProvider
{
    public IntegrationProvider Provider => IntegrationProvider.Mock;

    /// <summary>Gerçekçi satıcı profilleri: (unvan, VKN, kategori kalemleri, KDV oranı, birim).</summary>
    private static readonly MockVendor[] Vendors =
    {
        new("Petrol Ofisi Akaryakıt A.Ş.", "7290030004",
            new[] { ("Motorin Eurodiesel", "LTR", 47.85m), ("Kurşunsuz Benzin 95", "LTR", 49.20m) }, 20m),
        new("Ofisline Kırtasiye Ltd. Şti.", "6340098765",
            new[] { ("A4 Fotokopi Kağıdı (5'li koli)", "C62", 745.00m), ("Toner Kartuş HP 26A", "C62", 2350.00m), ("Klasör Geniş (10'lu)", "C62", 380.00m) }, 20m),
        new("Aktif Gümrük Müşavirliği A.Ş.", "0350044821",
            new[] { ("Gümrük müşavirliği hizmet bedeli", "C62", 8500.00m), ("Ordino ve ardiye masrafı", "C62", 3200.00m) }, 20m),
        new("CK Boğaziçi Elektrik Perakende Satış A.Ş.", "2110599999",
            new[] { ("Elektrik enerji bedeli (aktif)", "KWH", 2.85m), ("Dağıtım bedeli", "KWH", 1.12m) }, 20m),
        new("Yılmaz Gayrimenkul Yönetim Ltd. Şti.", "9670012345",
            new[] { ("İşyeri kira bedeli (aylık)", "MON", 45000.00m) }, 20m),
        new("Lezzet Toplu Yemek Hizmetleri A.Ş.", "6080076543",
            new[] { ("Personel tabldot yemek hizmeti", "C62", 185.00m), ("İkram/çay-kahve servisi", "C62", 95.00m) }, 10m),
        new("Anadolu Un ve Gıda San. Tic. A.Ş.", "0680034567",
            new[] { ("Ekmeklik buğday unu 50 kg", "KGM", 18.50m), ("Pirinç baldo 25 kg çuval", "KGM", 62.00m) }, 1m),
        new("Marmara Ambalaj San. Ltd. Şti.", "6120087654",
            new[] { ("Koli bandı 100'lü koli", "C62", 1250.00m), ("Streç film 23 mikron", "KGM", 145.00m), ("Karton koli 40x40x40", "C62", 28.50m) }, 20m),
        new("Net İletişim Telekomünikasyon A.Ş.", "6310011223",
            new[] { ("Kurumsal internet erişimi (aylık)", "MON", 1450.00m), ("Sabit hat görüşme bedeli", "C62", 420.00m) }, 20m),
        new("Beyaz Temizlik Hizmetleri Ltd. Şti.", "1660055443",
            new[] { ("Ofis temizlik hizmeti (aylık)", "MON", 12500.00m) }, 20m)
    };

    public Task<ProviderResult<List<ProviderInvoice>>> GetIncomingInvoicesAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, CancellationToken ct)
    {
        // Mock sağlayıcı anahtar gerektirmez; tarih aralığı geçersizse boş liste yerine hata döndürmeyiz,
        // üst katman (controller) tarih doğrulamasını zaten yapar.
        if (to < from)
            return Task.FromResult(ProviderResult<List<ProviderInvoice>>.Ok(new List<ProviderInvoice>()));

        var rangeDays = Math.Max(1, (int)(to.Date - from.Date).TotalDays + 1);

        // Deterministik tohum: aralığın başlangıç/bitiş günü aynıysa üretim aynıdır.
        // (HashCode.Combine süreçler arası deterministik olmadığı için kullanılmaz.)
        var seed = unchecked(from.Date.DayNumber() * 397 ^ to.Date.DayNumber());
        var random = new Random(seed);

        // 8-10 fatura üret (aralık 1-2 gün gibi çok kısaysa yine de en az 8 üretilir; tarihler aralığa yayılır).
        var count = 8 + random.Next(3); // 8, 9 veya 10
        var invoices = new List<ProviderInvoice>(count);

        for (var i = 0; i < count; i++)
        {
            var vendor = Vendors[(Math.Abs(seed % 97) + i * 3) % Vendors.Length];

            var dayOffset = (int)Math.Round(i * (rangeDays - 1) / (double)Math.Max(1, count - 1));
            var issueDate = from.Date.AddDays(dayOffset);

            var lineCount = 1 + random.Next(Math.Min(3, vendor.Items.Length)); // 1-3 satır
            var lines = new List<ProviderInvoiceLine>(lineCount);
            decimal taxExclusive = 0m, taxTotal = 0m;

            for (var l = 0; l < lineCount; l++)
            {
                var (itemName, unitCode, unitPrice) = vendor.Items[l % vendor.Items.Length];
                var quantity = unitCode switch
                {
                    "LTR" => 20m + random.Next(60),           // 20-79 litre
                    "KWH" => 500m + random.Next(1500),        // 500-1999 kWh
                    "KGM" => 5m + random.Next(45),            // 5-49 kg
                    "MON" => 1m,                              // aylık hizmet
                    _ => 1m + random.Next(9)                  // 1-9 adet
                };

                var lineAmount = Math.Round(unitPrice * quantity, 2);
                var vatAmount = Math.Round(lineAmount * vendor.VatRate / 100m, 2);

                lines.Add(new ProviderInvoiceLine
                {
                    LineNumber = l + 1,
                    ItemName = itemName,
                    Quantity = quantity,
                    UnitCode = unitCode,
                    UnitPrice = unitPrice,
                    LineAmount = lineAmount,
                    VatRate = vendor.VatRate,
                    VatAmount = vatAmount
                });

                taxExclusive += lineAmount;
                taxTotal += vatAmount;
            }

            invoices.Add(new ProviderInvoice
            {
                // UUID deterministik üretilir → aynı aralık tekrar senkronlanınca mükerrer atlanır.
                ExternalUuid = DeterministicGuid($"mock|{issueDate:yyyy-MM-dd}|{vendor.TaxId}|{i}").ToString(),
                InvoiceNumber = $"MCK{issueDate:yyyy}{(Math.Abs(seed) % 900000 + 100000 + i):D9}",
                IssueDate = issueDate,
                SenderTaxId = vendor.TaxId,
                SenderTitle = vendor.Title,
                CurrencyCode = "TRY",
                TaxExclusiveAmount = Math.Round(taxExclusive, 2),
                TaxTotal = Math.Round(taxTotal, 2),
                PayableAmount = Math.Round(taxExclusive + taxTotal, 2),
                Lines = lines
            });
        }

        return Task.FromResult(ProviderResult<List<ProviderInvoice>>.Ok(invoices));
    }

    /// <summary>Verilen metinden her çalıştırmada aynı GUID'i üretir (MD5 tabanlı).</summary>
    private static Guid DeterministicGuid(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }

    private sealed record MockVendor(
        string Title,
        string TaxId,
        (string Name, string Unit, decimal UnitPrice)[] Items,
        decimal VatRate);
}

internal static class DateOnlyExtensions
{
    /// <summary>DateTime.Date için gün numarası (deterministik tohum üretiminde kullanılır).</summary>
    public static int DayNumber(this DateTime date) => (int)(date.Date - DateTime.UnixEpoch.Date).TotalDays;
}
