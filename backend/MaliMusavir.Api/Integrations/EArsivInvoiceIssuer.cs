using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaliMusavir.Api.Integrations;

/// <summary>Kesilecek fatura kalemi (kullanıcıdan/panelden gelen ham girdi).</summary>
public class EArsivIssueLine
{
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
    /// <summary>Türkçe birim adı (ör. "Adet", "Saat", "Kg"). Portal Türkçe bekler.</summary>
    public string Unit { get; set; } = "Adet";
    public decimal UnitPrice { get; set; }
    /// <summary>KDV oranı yüzde (ör. 20).</summary>
    public decimal VatRate { get; set; } = 20m;
    /// <summary>İskonto oranı yüzde (0 = iskonto yok).</summary>
    public decimal DiscountRate { get; set; }
}

/// <summary>Kesilecek fatura başlığı.</summary>
public class EArsivIssueRequest
{
    public DateTime IssueDate { get; set; } = DateTime.Now;
    /// <summary>"Satis" | "Iade" | "Tevkifat" | "Istisna" | "OzelMatrah".</summary>
    public string InvoiceType { get; set; } = "Satis";
    public string CurrencyCode { get; set; } = "TRY";
    /// <summary>TRY dışında zorunlu; TRY'de 0 gönderilir.</summary>
    public decimal ExchangeRate { get; set; }

    /// <summary>Alıcı VKN (10 hane) veya TCKN (11 hane).</summary>
    public string BuyerTaxId { get; set; } = string.Empty;
    /// <summary>Tüzel kişide unvan. Doluysa ad/soyad boş bırakılır.</summary>
    public string? BuyerTitle { get; set; }
    public string? BuyerFirstName { get; set; }
    public string? BuyerLastName { get; set; }
    public string? TaxOffice { get; set; }

    public string? Address { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string Country { get; set; } = "Türkiye";
    public string? PostalCode { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Note { get; set; }

    public List<EArsivIssueLine> Lines { get; set; } = new();
}

/// <summary>Kesim sonucu — taslak oluşturuldu, HENÜZ İMZALANMADI.</summary>
public class EArsivIssueResult
{
    /// <summary>Portal tarafındaki belge kimliği (ETTN). Onay ve indirme bu kimlikle yapılır.</summary>
    public string Ettn { get; set; } = string.Empty;
    public decimal TaxExclusiveAmount { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal PayableAmount { get; set; }
}

/// <summary>
/// GİB e-Arşiv Portalı üzerinden FATURA KESME (belge oluşturma + SMS ile onaylama).
///
/// AKIŞ — İKİ AŞAMALIDIR VE İKİNCİ AŞAMA İNSAN ONAYLIDIR:
///   1. <see cref="CreateDraftAsync"/> → taslak oluşur, ETTN döner. Taslak resmî belge DEĞİLDİR;
///      silinebilir/düzeltilebilir.
///   2. <see cref="StartSmsVerificationAsync"/> → GİB, MÜKELLEFİN telefonuna kod gönderir.
///      <see cref="CompleteSmsVerificationAsync"/> → kullanıcının panele girdiği kodla imzalanır.
///
/// GÜVENLİK KURALI: SMS kodu sistem tarafından ÜRETİLMEZ, TAHMİN EDİLMEZ, OTOMATİK ÇÖZÜLMEZ.
/// Kod mükellefin telefonuna gider ve insan tarafından girilir — bu, projedeki "insan gerektiren
/// doğrulama adımı asla otomatikleştirilmez" kuralının aynısıdır.
///
/// GERİ ALINAMAZLIK: İmzalanan belge RESMÎ MALİ KAYITTIR; silinemez, değiştirilemez. Yalnızca
/// iptal/itiraz talebi açılabilir. Bu yüzden imza adımı panelde ayrı ve açık onay ister.
/// </summary>
public class EArsivInvoiceIssuer
{
    private readonly EArsivPortalClient _client;
    private readonly ILogger<EArsivInvoiceIssuer> _logger;

    public EArsivInvoiceIssuer(EArsivPortalClient client, ILogger<EArsivInvoiceIssuer> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Kimlikle giriş yapıp token döner (çağıran, işi bitince <see cref="LogoutAsync"/> çağırmalı).</summary>
    public Task<ProviderResult<string>> LoginAsync(
        string apiKey, bool testEnv, CancellationToken ct)
    {
        if (!EArsivPortalProvider.TryParseCredentials(apiKey, out var username, out var password))
        {
            return Task.FromResult(ProviderResult<string>.Fail(
                "GİB e-Arşiv Portalı kimlik bilgileri tanımlı değil. Mükellef kartından " +
                "kullanıcı kodu ve şifresini kaydedin."));
        }

        return _client.LoginAsync(username, password, testEnv, ct);
    }

    public Task LogoutAsync(string token, bool testEnv, CancellationToken ct) =>
        _client.LogoutAsync(token, testEnv, ct);

    // ---------- 1. Aşama: taslak ----------

    /// <summary>
    /// Portalda fatura taslağı oluşturur. Tutarlar SUNUCUDA hesaplanır (istemciden gelen
    /// toplama güvenilmez) ve portalın beklediği Türkçe alan adlarıyla gönderilir.
    /// </summary>
    public async Task<ProviderResult<EArsivIssueResult>> CreateDraftAsync(
        string token, EArsivIssueRequest request, bool testEnv, CancellationToken ct)
    {
        var validation = Validate(request);
        if (validation is not null)
            return ProviderResult<EArsivIssueResult>.Fail(validation);

        var ettn = Guid.NewGuid().ToString();
        var items = new List<Dictionary<string, object?>>();

        decimal totalBase = 0m, totalVat = 0m;

        foreach (var line in request.Lines)
        {
            var gross = Math.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            var discount = Math.Round(gross * line.DiscountRate / 100m, 2, MidpointRounding.AwayFromZero);
            var net = gross - discount;
            var vat = Math.Round(net * line.VatRate / 100m, 2, MidpointRounding.AwayFromZero);

            totalBase += net;
            totalVat += vat;

            items.Add(new Dictionary<string, object?>
            {
                ["malHizmet"] = line.ItemName,
                ["miktar"] = Num(line.Quantity),
                ["birim"] = line.Unit,
                ["birimFiyat"] = Num(line.UnitPrice),
                ["fiyat"] = Num(gross),
                ["iskontoOrani"] = Num(line.DiscountRate),
                ["iskontoTutari"] = Num(discount),
                ["iskontoNedeni"] = string.Empty,
                ["malHizmetTutari"] = Num(net),
                ["kdvOrani"] = Num(line.VatRate),
                ["kdvTutari"] = Num(vat),
                ["vergiler"] = Array.Empty<object>(),
                ["vergilerToplami"] = Num(vat),
            });
        }

        var payable = totalBase + totalVat;

        var payload = new Dictionary<string, object?>
        {
            ["faturaUuid"] = ettn,
            ["belgeNumarasi"] = string.Empty, // portal üretir
            ["faturaTarihi"] = EArsivPortalClient.FormatDate(request.IssueDate),
            ["saat"] = request.IssueDate.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["paraBirimi"] = request.CurrencyCode.ToUpperInvariant(),
            ["dovizKuru"] = Num(request.CurrencyCode.Equals("TRY", StringComparison.OrdinalIgnoreCase)
                ? 0m
                : request.ExchangeRate),
            ["faturaTipi"] = MapInvoiceType(request.InvoiceType),
            ["hangiTip"] = "5000/30000",

            ["vknTckn"] = request.BuyerTaxId.Trim(),
            ["aliciUnvan"] = request.BuyerTitle ?? string.Empty,
            ["aliciAdi"] = request.BuyerFirstName ?? string.Empty,
            ["aliciSoyadi"] = request.BuyerLastName ?? string.Empty,
            ["vergiDairesi"] = request.TaxOffice ?? string.Empty,

            ["adres"] = request.Address ?? string.Empty,
            ["mahalleSemtIlce"] = request.District ?? string.Empty,
            ["sehir"] = request.City ?? string.Empty,
            ["ulke"] = request.Country,
            ["postaKodu"] = request.PostalCode ?? string.Empty,
            ["eposta"] = request.Email ?? string.Empty,
            ["tel"] = request.Phone ?? string.Empty,
            ["not"] = request.Note ?? string.Empty,

            ["malHizmetTable"] = items,
            ["matrah"] = Num(totalBase),
            ["malhizmetToplamTutari"] = Num(totalBase),
            ["hesaplananKdv"] = Num(totalVat),
            ["vergilerToplami"] = Num(totalVat),
            ["vergilerDahilToplamTutar"] = Num(payable),
            ["odenecekTutar"] = Num(payable),
        };

        var result = await _client.DispatchAsync(
            token, "EARSIV_PORTAL_FATURA_OLUSTUR", "RG_BASITFATURA", payload, testEnv, ct);

        if (!result.Success)
            return ProviderResult<EArsivIssueResult>.Fail(result.ErrorMessage!, result.Retryable);

        _logger.LogInformation(
            "e-Arşiv fatura taslağı oluşturuldu. ETTN: {Ettn}, Tutar: {Payable}", ettn, payable);

        return ProviderResult<EArsivIssueResult>.Ok(new EArsivIssueResult
        {
            Ettn = ettn,
            TaxExclusiveAmount = totalBase,
            TaxTotal = totalVat,
            PayableAmount = payable,
        });
    }

    /// <summary>Taslağı siler. İMZALANMIŞ belgede çalışmaz (portal hata döner).</summary>
    public async Task<ProviderResult<bool>> DeleteDraftAsync(
        string token, IEnumerable<string> ettns, bool testEnv, CancellationToken ct)
    {
        var list = ettns.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList();
        if (list.Count == 0)
            return ProviderResult<bool>.Fail("Silinecek belge seçilmedi.");

        var payload = new Dictionary<string, object?>
        {
            ["silinecekler"] = list.Select(e => new Dictionary<string, string>
            {
                ["ettn"] = e,
                ["belgeTuru"] = "FATURA",
                ["onayDurumu"] = "Onaylanmadı",
            }).ToList(),
        };

        var result = await _client.DispatchAsync(
            token, "EARSIV_PORTAL_FATURA_SIL", "RG_TASLAKLAR", payload, testEnv, ct);

        return result.Success
            ? ProviderResult<bool>.Ok(true)
            : ProviderResult<bool>.Fail(result.ErrorMessage!, result.Retryable);
    }

    // ---------- 2. Aşama: SMS ile imzalama (İNSAN ONAYLI) ----------

    /// <summary>
    /// Mükellefin GİB'de kayıtlı telefonuna SMS kodu gönderilmesini tetikler ve işlem
    /// kimliğini (operationId) döner. Kod SİSTEME GELMEZ — mükellefin telefonuna gider.
    /// </summary>
    public async Task<ProviderResult<string>> StartSmsVerificationAsync(
        string token, string phoneNumber, bool testEnv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return ProviderResult<string>.Fail("Telefon numarası bulunamadı. Portal kullanıcı bilgilerini kontrol edin.");

        var payload = new Dictionary<string, object?>
        {
            ["CEPTEL"] = phoneNumber.Trim(),
            ["KCEPTEL"] = false,
            ["TIP"] = string.Empty,
        };

        var result = await _client.DispatchAsync(
            token, "EARSIV_PORTAL_SMSSIFRE_GONDER", "RG_SMSONAY", payload, testEnv, ct);

        if (!result.Success)
            return ProviderResult<string>.Fail(result.ErrorMessage!, result.Retryable);

        if (result.Data.ValueKind == JsonValueKind.Object &&
            result.Data.TryGetProperty("oid", out var oidEl) &&
            oidEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(oidEl.GetString()))
        {
            return ProviderResult<string>.Ok(oidEl.GetString()!);
        }

        return ProviderResult<string>.Fail(
            "SMS doğrulama başlatılamadı — GİB işlem kimliği döndürmedi. Lütfen tekrar deneyin.",
            retryable: true);
    }

    /// <summary>
    /// Kullanıcının panele girdiği SMS koduyla belgeleri imzalar. Bu adımdan sonra belgeler
    /// RESMÎDİR: silinemez, değiştirilemez.
    /// </summary>
    public async Task<ProviderResult<int>> CompleteSmsVerificationAsync(
        string token, string smsCode, string operationId, IEnumerable<string> ettns,
        bool testEnv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(smsCode))
            return ProviderResult<int>.Fail("SMS doğrulama kodunu girin.");
        if (string.IsNullOrWhiteSpace(operationId))
            return ProviderResult<int>.Fail("SMS doğrulama oturumu bulunamadı. Kodu yeniden isteyin.");

        var list = ettns.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList();
        if (list.Count == 0)
            return ProviderResult<int>.Fail("Onaylanacak belge seçilmedi.");

        var payload = new Dictionary<string, object?>
        {
            ["SIFRE"] = smsCode.Trim(),
            ["OID"] = operationId,
            ["OPR"] = 1,
            ["DATA"] = list.Select(e => new Dictionary<string, string>
            {
                ["ettn"] = e,
                ["belgeTuru"] = "FATURA",
            }).ToList(),
        };

        var result = await _client.DispatchAsync(
            token, "0lhozfib5410mp", "RG_SMSONAY", payload, testEnv, ct);

        if (!result.Success)
            return ProviderResult<int>.Fail(result.ErrorMessage!, result.Retryable);

        // Portal başarıda {"sonuc":"1"} döner.
        var ok = result.Data.ValueKind == JsonValueKind.Object &&
                 result.Data.TryGetProperty("sonuc", out var sonucEl) &&
                 sonucEl.ToString() == "1";

        if (!ok)
        {
            return ProviderResult<int>.Fail(
                "SMS doğrulama kodu kabul edilmedi. Kodu kontrol edip tekrar deneyin.");
        }

        _logger.LogInformation("e-Arşiv belgeleri SMS ile imzalandı. Adet: {Count}", list.Count);
        return ProviderResult<int>.Ok(list.Count);
    }

    /// <summary>Portalda kayıtlı cep telefonunu okur (SMS adımı için gerekir).</summary>
    public async Task<ProviderResult<string>> GetPhoneNumberAsync(
        string token, bool testEnv, CancellationToken ct)
    {
        var result = await _client.DispatchAsync(
            token, "EARSIV_PORTAL_TELEFONNO_SORGULA", "RG_BASITTASLAKLAR",
            new Dictionary<string, object?>(), testEnv, ct);

        if (!result.Success)
            return ProviderResult<string>.Fail(result.ErrorMessage!, result.Retryable);

        if (result.Data.ValueKind == JsonValueKind.Object &&
            result.Data.TryGetProperty("telefon", out var telEl) &&
            telEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(telEl.GetString()))
        {
            return ProviderResult<string>.Ok(telEl.GetString()!);
        }

        return ProviderResult<string>.Fail(
            "GİB'de kayıtlı cep telefonu bulunamadı. e-Arşiv Portalı kullanıcı bilgilerinden telefon ekleyin.");
    }

    /// <summary>VKN/TCKN'den alıcı unvan/ad/soyad/vergi dairesi bilgisini sorgular.</summary>
    public async Task<ProviderResult<JsonElement>> GetRecipientAsync(
        string token, string taxId, bool testEnv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taxId))
            return ProviderResult<JsonElement>.Fail("VKN/TCKN girin.");

        return await _client.DispatchAsync(
            token, "SICIL_VEYA_MERNISTEN_BILGILERI_GETIR", "RG_BASITFATURA",
            new Dictionary<string, object?> { ["vknTckn"] = taxId.Trim() }, testEnv, ct);
    }

    /// <summary>İmzalanmış belge için iptal talebi açar.</summary>
    public async Task<ProviderResult<bool>> CancellationRequestAsync(
        string token, string ettn, string explanation, bool testEnv, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ettn"] = ettn,
            ["aciklama"] = string.IsNullOrWhiteSpace(explanation) ? "Hatalı işlem" : explanation.Trim(),
        };

        var result = await _client.DispatchAsync(
            token, "EARSIV_PORTAL_IPTAL_TALEBI_OLUSTUR", "RG_TASLAKLAR", payload, testEnv, ct);

        return result.Success
            ? ProviderResult<bool>.Ok(true)
            : ProviderResult<bool>.Fail(result.ErrorMessage!, result.Retryable);
    }

    // ---------- Yardımcılar ----------

    /// <summary>Portal alanlarını "1234.56" biçiminde (nokta ondalık) metin bekler.</summary>
    private static string Num(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    private static string MapInvoiceType(string type) => type?.Trim().ToLowerInvariant() switch
    {
        "iade" => "İADE",
        "tevkifat" => "TEVKİFAT",
        "istisna" => "İSTİSNA",
        "ozelmatrah" or "özelmatrah" => "ÖZEL MATRAH",
        _ => "SATIŞ",
    };

    /// <summary>Kesim öncesi zorunlu alan denetimi. Hata varsa Türkçe mesaj, yoksa null.</summary>
    private static string? Validate(EArsivIssueRequest request)
    {
        if (request.Lines.Count == 0)
            return "Faturada en az bir kalem olmalıdır.";

        var taxId = request.BuyerTaxId?.Trim() ?? string.Empty;
        if (taxId.Length is not (10 or 11) || !taxId.All(char.IsDigit))
            return "Alıcı VKN 10 hane, TCKN 11 hane olmalı ve yalnız rakam içermelidir.";

        var hasTitle = !string.IsNullOrWhiteSpace(request.BuyerTitle);
        var hasName = !string.IsNullOrWhiteSpace(request.BuyerFirstName) ||
                      !string.IsNullOrWhiteSpace(request.BuyerLastName);
        if (!hasTitle && !hasName)
            return "Alıcı unvanı veya ad/soyad bilgisi zorunludur.";

        foreach (var line in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ItemName))
                return "Her kalemde mal/hizmet adı zorunludur.";
            if (line.Quantity <= 0)
                return $"\"{line.ItemName}\" kaleminde miktar sıfırdan büyük olmalıdır.";
            if (line.UnitPrice < 0)
                return $"\"{line.ItemName}\" kaleminde birim fiyat negatif olamaz.";
            if (line.VatRate is < 0 or > 100)
                return $"\"{line.ItemName}\" kaleminde KDV oranı 0-100 aralığında olmalıdır.";
            if (line.DiscountRate is < 0 or > 100)
                return $"\"{line.ItemName}\" kaleminde iskonto oranı 0-100 aralığında olmalıdır.";
        }

        if (!request.CurrencyCode.Equals("TRY", StringComparison.OrdinalIgnoreCase) &&
            request.ExchangeRate <= 0)
        {
            return "Yabancı para birimi seçildiğinde döviz kuru zorunludur.";
        }

        return null;
    }
}
