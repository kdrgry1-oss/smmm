using System.Text;
using System.Text.RegularExpressions;

namespace MaliMusavir.Api.Domain;

/// <summary>
/// Faz 13 — Mükellefin kayıtlı aracı (plaka → binek/ticari ayrımı).
/// Akaryakıt fişinden okunan plaka bu kayıtlarla eşleştirilir:
/// eşleşen araç BİNEKSE fişe %30 KKEG (binek.gider.kisit.oran parametresi) otomatik uygulanır;
/// TİCARİYSE KKEG uygulanmaz (tam gider). Eşleşmeyen plaka SMMM'ye uyarı olarak gösterilir.
/// Plaka NORMALİZE saklanır: büyük harf, boşluksuz/tiresiz (ör. "34FPC630").
/// </summary>
public class ClientVehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Normalize plaka: büyük harf, boşluksuz, en fazla 12 karakter (ör. "34FPC630").</summary>
    public string Plate { get; set; } = string.Empty;
    /// <summary>Binek otomobil mi? KKEG %30 kısıtı YALNIZ binek araçta uygulanır (GVK 40/1-5).</summary>
    public bool IsPassengerCar { get; set; }
    /// <summary>Serbest açıklama (ör. "Beyaz Passat"). Opsiyonel.</summary>
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Türk araç plakası normalizasyonu ve doğrulaması — TEK kaynak.
/// Kullanıcı girişi (ClientsController araç kaydı), OCR çıktısı (OcrValidator) ve
/// fiş eşleştirmesi (ReceiptService) aynı normalizasyonu kullanır → tutarlı karşılaştırma.
/// </summary>
public static class PlateNormalizer
{
    /// <summary>Normalize plaka biçimi: 2 hane il kodu + 1-3 harf + 2-5 rakam (ör. 34FPC630).</summary>
    public static readonly Regex PlateRegex = new(@"^[0-9]{2}[A-Z]{1,3}[0-9]{2,5}$", RegexOptions.Compiled);

    /// <summary>Normalize sonrası izin verilen en büyük uzunluk (DB kolonu da 12).</summary>
    public const int MaxLength = 12;

    /// <summary>
    /// Plakayı normalize eder: boşluk/tire/nokta silinir, Türkçe İ/ı → I dahil büyük harfe
    /// çevrilir. Boş girişte boş dize döner; GEÇERLİLİK kontrolü yapmaz (IsValid ile kontrol edin).
    /// </summary>
    public static string Normalize(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return string.Empty;

        var sb = new StringBuilder(plate.Length);
        foreach (var raw in plate.Trim())
        {
            var ch = raw switch
            {
                ' ' or '-' or '.' => '\0', // ayraçlar atılır
                'i' or 'ı' or 'İ' => 'I',  // TR harf tuzağı: invariant upper 'İ'yi korur, elle eşlenir
                _ => char.ToUpperInvariant(raw)
            };
            if (ch != '\0') sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Normalize edilmiş değerin geçerli Türk plakası olup olmadığı.</summary>
    public static bool IsValid(string normalizedPlate)
        => normalizedPlate.Length is > 0 and <= MaxLength && PlateRegex.IsMatch(normalizedPlate);
}
