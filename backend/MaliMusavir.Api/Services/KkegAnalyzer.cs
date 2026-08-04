namespace MaliMusavir.Api.Services;

/// <summary>KKEG analiz bulgusu: gerekçe + KDV hariç KKEG tutarı.</summary>
public class KkegFinding
{
    public string Reason { get; init; } = string.Empty;
    /// <summary>KKEG tutarı (matrahın indirilemeyen kısmı, KDV hariç).</summary>
    public decimal Amount { get; init; }
}

/// <summary>
/// Faz 8 — B3: KKEG / binek oto gider kısıtı analizörü. Kural seti:
///  (a) Binek oto işaretleri ("binek", "otopark", "otoyol"...; akaryakıt YALNIZCA mükellef
///      ayarında HasCompanyVehicle işaretliyse) → matrahın %30'u KKEG
///      (parametre: binek.gider.kisit.oran = indirilebilir %, varsayılan 70).
///  (b) Araç kiralama: aylık kira > binek.kira.aylik.sinir (46.000 TL, 2026) → aşan kısım KKEG.
///      // TODO-VERIFY: kiralamada sınır altı tutara %30 genel gider kısıtının UYGULANMADIĞI
///      // varsayıldı (aylık sınır kuralı kiralamanın kendi kısıtıdır) — güncel mevzuattan doğrula.
///  (c) Ceza / gecikme zammı-faizi → tam KKEG ("Cezalar gider yazılamaz"). Temsil-ağırlama
///      anahtar kelimeleri de tam KKEG işaretlenir.
///      // TODO-VERIFY: temsil-ağırlama giderlerinin bir kısmı işle ilgiliyse indirilebilir;
///      // "tam KKEG" işareti ihtiyatlı bir ön işarettir, SMMM onay ekranında düzeltebilir.
/// Analiz yalnızca İŞARET koyar (satır alanları); kodlama akışının güven/durum mantığına dokunmaz —
/// KKEG işareti olmayan belgelerde davranış birebir eskisiyle aynıdır.
/// Çağıran, ParameterService.EnsureLoadedAsync'i önce çağırmalıdır (CodingEngine zaten çağırır).
/// </summary>
public class KkegAnalyzer
{
    /// <summary>Varsayılan indirilebilir oran (%); kalanı KKEG. Parametre: binek.gider.kisit.oran.</summary>
    private const decimal DefaultDeductiblePercent = 70m;

    /// <summary>Varsayılan aylık binek kira sınırı (TL, 2026). Parametre: binek.kira.aylik.sinir.</summary>
    private const decimal DefaultMonthlyRentLimit = 46000m;

    public const string ReasonPenalty = "Cezalar ve gecikme zamları gider yazılamaz (tam KKEG).";
    public const string ReasonEntertainment = "Temsil-ağırlama gideri KKEG olarak işaretlendi (SMMM kontrolü önerilir).";

    /// <summary>Tam KKEG tetikleyen anahtar kelimeler — ceza/gecikme grubu (normalize aranır).</summary>
    private static readonly string[] PenaltyKeywords =
    {
        "ceza", "gecikme zammı", "gecikme faizi", "usulsüzlük", "idari para",
    };

    /// <summary>Tam KKEG tetikleyen anahtar kelimeler — temsil-ağırlama grubu.</summary>
    private static readonly string[] EntertainmentKeywords =
    {
        "temsil", "ağırlama", "ikram gideri",
    };

    /// <summary>Araç kiralama işaretleri (kural b — aylık sınır kontrolü).</summary>
    private static readonly string[] RentKeywords =
    {
        "araç kira", "oto kira", "binek kira", "rent a car", "araç kiralama", "oto kiralama",
    };

    /// <summary>Binek oto gider işaretleri (kural a — %30 kısıt).</summary>
    private static readonly string[] VehicleKeywords =
    {
        "binek", "otopark", "köprü geçiş", "otoyol", "hgs", "ogs", "oto yıkama", "oto lastik",
    };

    /// <summary>Akaryakıt işaretleri: YALNIZCA HasCompanyVehicle işaretliyken kural a'ya girer.</summary>
    private static readonly string[] FuelKeywords =
    {
        "akaryakıt", "benzin", "motorin", "lpg",
    };

    private readonly ParameterService _parameters;

    public KkegAnalyzer(ParameterService parameters)
    {
        _parameters = parameters;
    }

    /// <summary>
    /// Kalem/satıcı metnini analiz eder. KKEG yoksa null döner (belge eskisi gibi akar).
    /// text: satıcı unvanı + kalem açıklaması (ham; içeride normalize edilir).
    /// netAmount: KDV hariç matrah. hasCompanyVehicle: mükellef ayarı (akaryakıt kuralı için).
    /// </summary>
    public KkegFinding? Analyze(string? text, decimal netAmount, bool hasCompanyVehicle)
    {
        var normalized = TextNormalizer.Normalize(text);
        if (normalized.Length == 0 || netAmount <= 0) return null;

        // (c) Tam KKEG: ceza/gecikme — "Cezalar gider yazılamaz."
        if (MatchesAny(normalized, PenaltyKeywords))
            return new KkegFinding { Reason = ReasonPenalty, Amount = Math.Round(netAmount, 2) };

        // (c) Tam KKEG: temsil-ağırlama (ihtiyatlı ön işaret — SMMM düzeltebilir).
        if (MatchesAny(normalized, EntertainmentKeywords))
            return new KkegFinding { Reason = ReasonEntertainment, Amount = Math.Round(netAmount, 2) };

        // (b) Araç kiralama: aylık sınırı aşan kısım KKEG; sınır altı kiralama işaretlenmez.
        if (MatchesAny(normalized, RentKeywords))
        {
            var limit = _parameters.GetDecimal("binek.kira.aylik.sinir", DefaultMonthlyRentLimit);
            if (netAmount > limit)
            {
                return new KkegFinding
                {
                    Reason = $"Binek oto kira gideri aylık sınırı ({limit:N0} TL) aşıyor; aşan kısım KKEG.",
                    Amount = Math.Round(netAmount - limit, 2)
                };
            }
            return null;
        }

        // (a) Binek oto gideri: %30 (100 - indirilebilir oran) KKEG; KDV'sinin aynı oranı indirilemez.
        var isVehicleExpense = MatchesAny(normalized, VehicleKeywords)
                               || (hasCompanyVehicle && MatchesAny(normalized, FuelKeywords));
        if (isVehicleExpense)
        {
            var deductiblePercent = _parameters.GetDecimal("binek.gider.kisit.oran", DefaultDeductiblePercent);
            var kkegPercent = Math.Clamp(100m - deductiblePercent, 0m, 100m);
            if (kkegPercent <= 0) return null;

            return new KkegFinding
            {
                Reason = $"Binek oto gideri: %{kkegPercent:0.##} kısmı KKEG (GVK 40/1-5); KDV'sinin aynı oranı indirilemez.",
                Amount = Math.Round(netAmount * kkegPercent / 100m, 2)
            };
        }

        return null;
    }

    /// <summary>
    /// Faz 13: metin akaryakıt gideri işareti taşıyor mu? (satıcı adı + kalem özeti, normalize aranır).
    /// ReceiptService'in "akaryakıt fişi" tespiti bu kelime setini kullanır — kural (a)'daki
    /// FuelKeywords ile AYNI kaynak, tanım ikilenmez.
    /// </summary>
    public static bool IsFuelExpense(string? text)
        => MatchesAny(TextNormalizer.Normalize(text), FuelKeywords);

    /// <summary>
    /// Faz 13: plaka eşleşmesiyle DOĞRULANMIŞ binek araç akaryakıt gideri için KKEG bulgusu.
    /// Kural (a) ile aynı parametreyi (binek.gider.kisit.oran, varsayılan indirilebilir %70) kullanır
    /// ama HasCompanyVehicle bayrağından BAĞIMSIZDIR — plaka eşleşmesi daha güçlü kanıttır.
    /// Oran %100 indirilebilirse (kkeg %0) veya matrah yoksa null döner (fiş eski akışta kalır).
    /// Çağıran, ParameterService.EnsureLoadedAsync'i önce çağırmalıdır.
    /// </summary>
    public KkegFinding? AnalyzeFuelForPassengerVehicle(string plate, decimal netAmount)
    {
        if (netAmount <= 0) return null;

        var deductiblePercent = _parameters.GetDecimal("binek.gider.kisit.oran", DefaultDeductiblePercent);
        var kkegPercent = Math.Clamp(100m - deductiblePercent, 0m, 100m);
        if (kkegPercent <= 0) return null;

        return new KkegFinding
        {
            Reason = $"Binek araç ({plate}) akaryakıt gideri — %{kkegPercent:0.##} KKEG",
            Amount = Math.Round(netAmount * kkegPercent / 100m, 2)
        };
    }

    /// <summary>
    /// KKEG'e isabet eden (indirilemeyen) KDV: satır KDV'sinin, KKEG tutarının matraha oranı
    /// kadarki kısmı. Tam KKEG'de KDV'nin tamamı; işaret yoksa 0. Tüm tüketiciler
    /// (Luca export, KDV1 taslağı) aynı hesabı kullanır → tutarlılık.
    /// </summary>
    public static decimal DisallowedVat(bool isKkeg, decimal? kkegAmount, decimal netAmount, decimal vatAmount)
    {
        if (!isKkeg || kkegAmount is not > 0 || vatAmount <= 0) return 0m;
        if (netAmount <= 0) return Math.Round(vatAmount, 2); // matrah bilinmiyorsa ihtiyatlı: tamamı
        var portion = Math.Min(kkegAmount.Value, netAmount) / netAmount;
        return Math.Min(vatAmount, Math.Round(vatAmount * portion, 2));
    }

    private static bool MatchesAny(string normalizedText, IEnumerable<string> keywords)
        => keywords
            .Select(k => TextNormalizer.Normalize(k))
            .Any(k => k.Length > 0 && normalizedText.Contains(k, StringComparison.Ordinal));
}
