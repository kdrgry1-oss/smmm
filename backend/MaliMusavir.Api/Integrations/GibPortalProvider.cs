using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// GİB Portal adaptörü (Faz 15) — ENTEGRATÖR KULLANMAYAN mükellefler için.
///
/// Mali müşavir, mükellefin Dijital Vergi Dairesi giriş bilgilerini sisteme BİR KEZ girer;
/// sonraki tüm çekimler sunucuda otomatik olur (gece 03:00 DailyPortalSyncWorker + elle
/// tetikleme). Bu adaptör gerçek tarayıcı işini yapmaz; iç ağdaki gib-robot servisinin
/// /documents ucunu çağırır ve yanıtı ProviderInvoice listesine eşler.
///
/// KİMLİK: ŞEMA DEĞİŞİKLİĞİ YOKTUR — kimlik, diğer kimlik-tabanlı sağlayıcılarla aynı
/// kolonda (ClientIntegration.ApiKeyEncrypted) AES ile şifreli JSON olarak durur:
/// {"username":"...","password":"...","manualWarning":"..."?,"manualWarningAt":"..."?}.
/// Şifre hiçbir yerde düz metin saklanmaz ve LOGA ASLA yazılmaz.
///
/// GÜVENLİK KURALI: Robot captcha / SMS-OTP / e-Devlet gibi insan gerektiren bir adım
/// görürse ÇÖZMEYE ÇALIŞMAZ; requiresManual=true döner. Bu durumda iş "Elle doğrulama
/// gerekli" olarak işaretlenir (retryable:false — tekrar denenmez) ve entegrasyon kaydına
/// uyarı yazılır (bkz. OutboundJobService.GibPortalSync).
///
/// KAPSAM NOTU: GİB portalının liste ekranı kalem detayı vermez; her belge için TEK ÖZET
/// SATIR üretilir (matrah + KDV; oran 1/8/10/18/20'ye yuvarlanır) — Nilvera/Uyumsoft
/// fallback'iyle aynı yaklaşım.
/// </summary>
public class GibPortalProvider : IEInvoiceProvider
{
    /// <summary>
    /// "Elle doğrulama gerekli" hatalarının sabit öneki. Kuyruk (OutboundJobService) ve UI
    /// bu önekten durumu tanır — DEĞİŞTİRİLİRSE her iki taraf da güncellenmelidir.
    /// </summary>
    public const string ManualVerificationPrefix = "Elle doğrulama gerekli.";

    /// <summary>GİB portalında yaygın KDV oranları (yuvarlama hedefleri).</summary>
    private static readonly decimal[] KnownVatRates = { 1m, 8m, 10m, 18m, 20m };

    private static readonly JsonSerializerOptions CredentialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly GibRobotClient _robot;
    private readonly ILogger<GibPortalProvider> _logger;

    public GibPortalProvider(GibRobotClient robot, ILogger<GibPortalProvider> logger)
    {
        _robot = robot;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.GibPortal;

    // ---------- Kimlik paketleme (IntegrationsController + kuyruk da kullanır) ----------

    private sealed class GibPortalCredentials
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        /// <summary>
        /// Son çekimde "Elle doğrulama gerekli" durumu oluştuysa kullanıcıya gösterilecek
        /// Türkçe uyarı. Şema değişikliği gerektirmemesi için kimlik JSON'unda taşınır
        /// (gizli bilgi DEĞİLDİR; UI'da rozet olarak gösterilir).
        /// </summary>
        [JsonPropertyName("manualWarning")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ManualWarning { get; set; }

        /// <summary>Uyarının oluştuğu an (ISO-8601, UTC).</summary>
        [JsonPropertyName("manualWarningAt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ManualWarningAt { get; set; }
    }

    /// <summary>Kullanıcı adı + şifreyi ApiKeyEncrypted alanında saklanacak JSON'a paketler.</summary>
    public static string PackCredentials(string username, string password) =>
        PackCredentials(username, password, null, null);

    /// <summary>
    /// Kimliği (varsa) elle doğrulama uyarısıyla birlikte paketler. Uyarı null ise JSON'a
    /// hiç yazılmaz → uyarısız kayıtla birebir aynı biçim (geriye uyum).
    /// </summary>
    public static string PackCredentials(
        string username, string password, string? manualWarning, DateTime? manualWarningAt) =>
        JsonSerializer.Serialize(new GibPortalCredentials
        {
            Username = username,
            Password = password,
            ManualWarning = string.IsNullOrWhiteSpace(manualWarning) ? null : manualWarning.Trim(),
            ManualWarningAt = manualWarningAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });

    /// <summary>Saklanan JSON'dan kullanıcı adı + şifreyi çözer. Geçersizse false.</summary>
    public static bool TryParseCredentials(string? json, out string username, out string password) =>
        TryParseCredentials(json, out username, out password, out _, out _);

    /// <summary>
    /// Saklanan JSON'u elle doğrulama uyarısıyla birlikte çözer. Uyarı alanı yoksa null döner.
    /// </summary>
    public static bool TryParseCredentials(
        string? json, out string username, out string password,
        out string? manualWarning, out DateTime? manualWarningAt)
    {
        username = string.Empty;
        password = string.Empty;
        manualWarning = null;
        manualWarningAt = null;

        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var creds = JsonSerializer.Deserialize<GibPortalCredentials>(json, CredentialJsonOptions);
            if (creds is null ||
                string.IsNullOrWhiteSpace(creds.Username) ||
                string.IsNullOrWhiteSpace(creds.Password))
            {
                return false;
            }

            username = creds.Username.Trim();
            password = creds.Password;
            manualWarning = string.IsNullOrWhiteSpace(creds.ManualWarning) ? null : creds.ManualWarning;
            manualWarningAt = DateTime.TryParse(
                creds.ManualWarningAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------- Faz 18: Müşavir (SMMM) tek giriş paketi ----------

    private sealed class AdvisorCredentials
    {
        [JsonPropertyName("advisorTenantId")]
        public Guid? AdvisorTenantId { get; set; }

        [JsonPropertyName("targetTaxId")]
        public string? TargetTaxId { get; set; }
    }

    /// <summary>
    /// Faz 18 — Müşavir modu kimlik paketi: mükellefin KENDİ GİB kimliği yoksa ama tenant'ta
    /// müşavir kimliği varsa, sync servisleri sağlayıcıya bu paketi geçirir. Robot çağrısı
    /// sessionKey=advisor:&lt;tenantId&gt; + targetTaxId ile yapılır — ŞİFRE TAŞINMAZ.
    /// </summary>
    public static string PackAdvisorCredentials(Guid tenantId, string targetTaxId) =>
        JsonSerializer.Serialize(new AdvisorCredentials
        {
            AdvisorTenantId = tenantId,
            TargetTaxId = targetTaxId
        });

    /// <summary>Müşavir modu paketini çözer; paket bu türden değilse false.</summary>
    public static bool TryParseAdvisorCredentials(
        string? json, out Guid tenantId, out string targetTaxId)
    {
        tenantId = Guid.Empty;
        targetTaxId = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var creds = JsonSerializer.Deserialize<AdvisorCredentials>(json, CredentialJsonOptions);
            if (creds?.AdvisorTenantId is null || creds.AdvisorTenantId == Guid.Empty ||
                string.IsNullOrWhiteSpace(creds.TargetTaxId))
            {
                return false;
            }

            tenantId = creds.AdvisorTenantId.Value;
            targetTaxId = creds.TargetTaxId.Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Hata mesajı "elle doğrulama gerekli" durumunu mu bildiriyor?</summary>
    public static bool IsManualVerificationMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.StartsWith(ManualVerificationPrefix, StringComparison.Ordinal);

    /// <summary>Robotun requiresManual yanıtını standart Türkçe mesaja çevirir.</summary>
    public static string BuildManualVerificationMessage(string? robotMessage) =>
        $"{ManualVerificationPrefix} " +
        (string.IsNullOrWhiteSpace(robotMessage)
            ? "GİB ek doğrulama istedi (captcha / SMS kodu / e-Devlet). Bu mükellefin belgelerini " +
              "bir kez elle GİB portalından kontrol edin; otomatik çekim güvenlik gereği durduruldu."
            : robotMessage.Trim());

    // ---------- IEInvoiceProvider ----------

    public async Task<ProviderResult<List<ProviderInvoice>>> GetIncomingInvoicesAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, CancellationToken ct)
    {
        // apiKey burada JSON kimlik paketidir: {"username","password", ...} (mükellefin kendi
        // kimliği) VEYA Faz 18 müşavir paketi {"advisorTenantId","targetTaxId"}.
        GibRobotClient.RobotResponse<GibRobotClient.RobotDocument> response;

        if (TryParseAdvisorCredentials(apiKey, out var advisorTenantId, out var targetTaxId))
        {
            // MÜŞAVİR MODU: tek SMMM oturumu + "mükellef değiştir/seç" akışı. Şifre gitmez.
            response = await _robot.GetDocumentsAsAdvisorAsync(
                GibRobotClient.AdvisorSessionKey(advisorTenantId), targetTaxId,
                from.Date, to.Date, ct);
        }
        else if (TryParseCredentials(apiKey, out var username, out var password))
        {
            // GİB portalında test ortamı yoktur; testEnv bilinçli olarak yok sayılır.
            response = await _robot.GetDocumentsAsync(username, password, from.Date, to.Date, ct);
        }
        else
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "GİB Portalı kimlik bilgileri tanımlı değil. Mükellefin GİB kullanıcı kodu ve şifresini " +
                "entegrasyon ekranından kaydedin veya \"GİB Bağlantısı\" sayfasından müşavir girişini tanımlayın.");
        }

        if (response.RequiresManual || response.NeedsCaptchaLogin)
        {
            // GÜVENLİK KURALI: çözmeye çalışılmaz, tekrar denenmez. needsCaptchaLogin
            // (Faz 17): kayıtlı GİB oturumu yok/geçersiz → kullanıcı "GİB Oturumu Aç"
            // düğmesiyle doğrulama kodunu girmeli; mesaj robotun yönlendirmesini taşır.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                BuildManualVerificationMessage(response.Message), retryable: false);
        }

        if (!response.Ok)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                response.Message ?? "GİB portalından belgeler alınamadı. Lütfen daha sonra tekrar deneyin.",
                retryable: response.Retryable);
        }

        var invoices = new List<ProviderInvoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var item in response.Items)
        {
            var uuid = item.Uuid?.Trim();
            if (string.IsNullOrWhiteSpace(uuid)) { skipped++; continue; }
            if (!seen.Add(uuid)) { skipped++; continue; } // sayfa içi mükerrer koruması

            invoices.Add(MapInvoice(item, uuid));
        }

        _logger.LogInformation(
            "GİB portal senkronu tamamlandı. Belge: {Count}, atlanan: {Skipped}, aralık: {From:d}-{To:d}",
            invoices.Count, skipped, from, to);

        // Robot sütun tanıyamadıysa mesajında "Uyarı:" ile bildirir → kullanıcıya taşınır.
        var warning = response.Message is not null && response.Message.Contains("Uyarı:", StringComparison.Ordinal)
            ? response.Message
            : null;

        return ProviderResult<List<ProviderInvoice>>.Ok(invoices, warning);
    }

    /// <summary>Robot satırını ProviderInvoice'a çevirir (tek özet satır).</summary>
    private static ProviderInvoice MapInvoice(GibRobotClient.RobotDocument item, string uuid)
    {
        var taxExclusive = Math.Round(item.TaxExclusiveAmount, 2);
        var taxTotal = Math.Round(item.TaxTotal, 2);
        var payable = Math.Round(item.PayableAmount, 2);

        // Portal bazı ekranlarda yalnız genel toplam verir → matrah/KDV geri türetilir.
        if (taxExclusive == 0m && payable > 0m && taxTotal > 0m)
            taxExclusive = Math.Round(payable - taxTotal, 2);
        if (payable == 0m)
            payable = Math.Round(taxExclusive + taxTotal, 2);

        var issueDate = ParseDate(item.IssueDate) ?? DateTime.UtcNow.Date;
        var currency = string.IsNullOrWhiteSpace(item.Currency) ? "TRY" : item.Currency.Trim().ToUpperInvariant();
        var documentType = string.IsNullOrWhiteSpace(item.DocumentType) ? null : item.DocumentType.Trim();

        return new ProviderInvoice
        {
            ExternalUuid = uuid,
            InvoiceNumber = item.DocumentNumber?.Trim() ?? string.Empty,
            IssueDate = issueDate,
            SenderTaxId = item.SenderTaxId?.Trim() ?? string.Empty,
            SenderTitle = item.SenderTitle?.Trim() ?? string.Empty,
            CurrencyCode = currency.Length == 3 ? currency : "TRY",
            TaxExclusiveAmount = taxExclusive,
            TaxTotal = taxTotal,
            PayableAmount = payable,
            Lines = new List<ProviderInvoiceLine>
            {
                new()
                {
                    LineNumber = 1,
                    ItemName = documentType is null
                        ? "Belge toplamı (GİB portalı — kalem detayı yok)"
                        : $"{documentType} toplamı (GİB portalı — kalem detayı yok)",
                    Quantity = 1,
                    UnitCode = "C62",
                    UnitPrice = taxExclusive,
                    LineAmount = taxExclusive,
                    VatRate = RoundVatRate(taxExclusive, taxTotal),
                    VatAmount = taxTotal
                }
            }
        };
    }

    /// <summary>
    /// KDV oranını hesaplayıp bilinen orana (1/8/10/18/20) yuvarlar. Hesaplanan oran hiçbir
    /// bilinen orana ±1,5 puandan yakın değilse (ör. tevkifatlı/istisna belge) ham oran
    /// yuvarlanarak korunur — uydurma oran yazılmaz.
    /// </summary>
    private static decimal RoundVatRate(decimal taxExclusive, decimal taxTotal)
    {
        if (taxExclusive <= 0m || taxTotal <= 0m) return 0m;

        var raw = taxTotal / taxExclusive * 100m;
        var nearest = KnownVatRates.OrderBy(r => Math.Abs(r - raw)).First();
        return Math.Abs(nearest - raw) <= 1.5m ? nearest : Math.Round(raw, 0);
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value.Date
            : null;
    }
}
