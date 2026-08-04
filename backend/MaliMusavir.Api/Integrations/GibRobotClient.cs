using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// gib-robot iç servisinin HTTP istemcisi (Faz 15 — GİB Portal Robotu).
///
/// Robot, entegratör kullanmayan mükelleflerin gelen belgelerini ve e-Tebligatlarını
/// GİB Dijital Vergi Dairesi portalından mükellefin KENDİ kimliğiyle çeken ayrı bir
/// konteynerdir (Node 20 + Playwright). Yalnız iç ağdan erişilir
/// (http://gib-robot:8080 — compose'da port publish EDİLMEZ) ve paylaşılan sırla
/// korunur: her istekte "X-Robot-Token" başlığı gönderilir.
///
/// GÜVENLİK: Mükellef şifresi yalnız istek gövdesinde iç ağ üzerinden gider;
/// HİÇBİR log satırına yazılmaz (bu sınıfta da yazılmaz).
/// </summary>
public class GibRobotClient
{
    public const string HttpClientName = "gibrobot";

    /// <summary>Robot taban adresi env değişkeni (compose: http://gib-robot:8080).</summary>
    public const string BaseUrlEnvKey = "GIB_ROBOT_URL";

    /// <summary>Paylaşılan sır env değişkeni (api ve robot AYNI değeri alır).</summary>
    public const string TokenEnvKey = "ROBOT_TOKEN";

    private const string DefaultBaseUrl = "http://gib-robot:8080";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GibRobotClient> _logger;

    public GibRobotClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GibRobotClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Robot taban adresi (env yoksa compose içi varsayılan).</summary>
    public string BaseUrl =>
        (_configuration[BaseUrlEnvKey] ?? DefaultBaseUrl).TrimEnd('/');

    private string? Token => _configuration[TokenEnvKey];

    // ---------- Robot yanıt sözleşmesi ----------

    /// <summary>Robotun her uçtan döndürdüğü ortak zarf.</summary>
    public class RobotResponse<T>
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>true → captcha/SMS gibi İNSAN gerektiren adım çıktı; sistem çözmeye ÇALIŞMAZ.</summary>
        [JsonPropertyName("requiresManual")]
        public bool RequiresManual { get; set; }

        /// <summary>
        /// true → kayıtlı GİB oturumu yok/geçersiz; kullanıcı "GİB Oturumu Aç" akışıyla
        /// (BeginLoginAsync + CompleteLoginAsync) doğrulama kodunu girmeli. GİB giriş
        /// ekranı her girişte captcha istediği için robot şifreyle kendi başına giriş yapmaz.
        /// </summary>
        [JsonPropertyName("needsCaptchaLogin")]
        public bool NeedsCaptchaLogin { get; set; }

        /// <summary>Son ekranın kısa özeti (başlık/URL/metin/ekran görüntüsü yolu) — teşhis içindir.</summary>
        [JsonPropertyName("diagnostic")]
        public string? Diagnostic { get; set; }

        [JsonPropertyName("items")]
        public List<T> Items { get; set; } = new();

        /// <summary>
        /// Yalnız API tarafında doldurulur (robot yanıtında YOKTUR): altyapı kaynaklı,
        /// tekrar denenebilir hata mı? (ör. robot ayakta değil, zaman aşımı).
        /// </summary>
        [JsonIgnore]
        public bool Retryable { get; set; }
    }

    /// <summary>/documents ucundan dönen tek belge satırı.</summary>
    public class RobotDocument
    {
        public string? Uuid { get; set; }
        public string? DocumentNumber { get; set; }
        public string? IssueDate { get; set; }
        public string? SenderTaxId { get; set; }
        public string? SenderTitle { get; set; }
        public decimal TaxExclusiveAmount { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal PayableAmount { get; set; }
        public string? Currency { get; set; }
        public string? DocumentType { get; set; }
    }

    /// <summary>/tebligat ucundan dönen tek tebligat satırı.</summary>
    public class RobotTebligat
    {
        public string? ExternalId { get; set; }
        public string? Subject { get; set; }
        public string? SentAt { get; set; }
        public string? ReadAt { get; set; }
        public string? TaxOffice { get; set; }
        public string? DocumentUrlOrText { get; set; }
    }

    /// <summary>Faz 18 — /advisor/clients ucundan dönen tek mükellef satırı.</summary>
    public class RobotAdvisorClient
    {
        public string? TaxId { get; set; }
        public string? Title { get; set; }
    }

    // ---------- İnsan destekli giriş zarfları (Faz 17 — doğrulama kodu akışı) ----------

    /// <summary>Begin/Complete yanıtlarının ortak alanları.</summary>
    public abstract class RobotEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("diagnostic")]
        public string? Diagnostic { get; set; }
    }

    /// <summary>/login-begin yanıtı: captcha görseli + bekleyen oturum kimliği.</summary>
    public class RobotLoginBegin : RobotEnvelope
    {
        [JsonPropertyName("pendingId")]
        public string? PendingId { get; set; }

        /// <summary>Doğrulama kodu görseli (base64 PNG). Görsel yoksa null.</summary>
        [JsonPropertyName("captchaImageBase64")]
        public string? CaptchaImageBase64 { get; set; }

        [JsonPropertyName("expiresInSeconds")]
        public int? ExpiresInSeconds { get; set; }

        /// <summary>true → kayıtlı oturum hâlâ geçerli; doğrulama koduna gerek yok.</summary>
        [JsonPropertyName("alreadyLoggedIn")]
        public bool AlreadyLoggedIn { get; set; }
    }

    /// <summary>/login-complete yanıtı: sonuç + (hatalıysa) yeni captcha görseli.</summary>
    public class RobotLoginComplete : RobotEnvelope
    {
        [JsonPropertyName("pendingId")]
        public string? PendingId { get; set; }

        /// <summary>Hatalı denemede YENİ doğrulama kodu görseli (base64 PNG); yoksa null.</summary>
        [JsonPropertyName("captchaImageBase64")]
        public string? CaptchaImageBase64 { get; set; }

        /// <summary>Kalan deneme hakkı (en fazla 3); bilinmiyorsa null.</summary>
        [JsonPropertyName("attemptsLeft")]
        public int? AttemptsLeft { get; set; }

        /// <summary>true → bekleyen oturum iptal/zaman aşımı; "Yeni Kod" ile baştan başlanmalı.</summary>
        [JsonPropertyName("pendingExpired")]
        public bool PendingExpired { get; set; }
    }

    // ---------- Uçlar ----------

    /// <summary>Bağlantı testi: yalnız giriş denenir, belge çekilmez.</summary>
    public Task<RobotResponse<object>> LoginTestAsync(
        string username, string password, CancellationToken ct) =>
        PostAsync<object>("/login-test", new { username, password }, ct);

    /// <summary>"Adıma Düzenlenen Belgeler" ekranından tarih aralığındaki belgeler.</summary>
    public Task<RobotResponse<RobotDocument>> GetDocumentsAsync(
        string username, string password, DateTime from, DateTime to, CancellationToken ct) =>
        PostAsync<RobotDocument>("/documents", new
        {
            username,
            password,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd")
        }, ct);

    /// <summary>
    /// İnsan destekli giriş — 1. aşama: robot GİB giriş ekranını açar, kimliği doldurur ve
    /// "Doğrulama Kodu" (captcha) görselini base64 PNG olarak döndürür; sayfa robotta
    /// bekletilir (TTL 5 dk). Şifre yalnız iç ağda istek gövdesinde gider, LOGLANMAZ ve
    /// yanıtta ASLA yer almaz.
    /// </summary>
    public Task<RobotLoginBegin> BeginLoginAsync(
        string username, string password, CancellationToken ct) =>
        PostEnvelopeAsync<RobotLoginBegin>("/login-begin", new { username, password }, ct);

    /// <summary>
    /// İnsan destekli giriş — 2. aşama: kullanıcının okuduğu doğrulama kodu bekleyen sayfaya
    /// yazılır ve giriş tamamlanır. Başarıda robot oturumu kaydeder (12 saat); hatalı kodda
    /// AYNI pendingId ile YENİ captcha görseli döner (en fazla 3 deneme).
    /// </summary>
    public Task<RobotLoginComplete> CompleteLoginAsync(
        string pendingId, string code, CancellationToken ct) =>
        PostEnvelopeAsync<RobotLoginComplete>("/login-complete", new { pendingId, code }, ct);

    /// <summary>"e-Tebligat" ekranından tarih aralığındaki tebligatlar.</summary>
    public Task<RobotResponse<RobotTebligat>> GetTebligatAsync(
        string username, string password, DateTime from, DateTime to, CancellationToken ct) =>
        PostAsync<RobotTebligat>("/tebligat", new
        {
            username,
            password,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd")
        }, ct);

    // ---------- Faz 18: Müşavir (SMMM) tek giriş uçları ----------

    /// <summary>
    /// Müşavir oturum anahtarı — robotla PAYLAŞILAN sözleşme: "advisor:&lt;tenantId&gt;".
    /// Robot bu anahtarı SHA-256 ile özetleyip oturum dosyası adı olarak kullanır.
    /// </summary>
    public static string AdvisorSessionKey(Guid tenantId) => $"advisor:{tenantId:D}";

    /// <summary>
    /// Müşavir girişi — 1. aşama (/advisor/login-begin): mevcut BeginLoginAsync akışının
    /// aynısı; oturum sessionKey altına kaydedilir. Şifre yalnız iç ağda gider, LOGLANMAZ.
    /// </summary>
    public Task<RobotLoginBegin> BeginAdvisorLoginAsync(
        string username, string password, string sessionKey, CancellationToken ct) =>
        PostEnvelopeAsync<RobotLoginBegin>("/advisor/login-begin",
            new { username, password, sessionKey }, ct);

    /// <summary>Müşavir girişi — 2. aşama (/advisor/login-complete): CompleteLoginAsync ile aynı.</summary>
    public Task<RobotLoginComplete> CompleteAdvisorLoginAsync(
        string pendingId, string code, CancellationToken ct) =>
        PostEnvelopeAsync<RobotLoginComplete>("/advisor/login-complete", new { pendingId, code }, ct);

    /// <summary>
    /// Müşavire bağlı mükellef listesi (/advisor/clients): müşavir oturumuyla portaldaki
    /// mükellef listesi ekranına gidilir, dönen JSON yakalanır → [{taxId, title}].
    /// </summary>
    public Task<RobotResponse<RobotAdvisorClient>> GetAdvisorClientsAsync(
        string sessionKey, CancellationToken ct) =>
        PostAsync<RobotAdvisorClient>("/advisor/clients", new { sessionKey }, ct);

    /// <summary>
    /// Müşavir oturumuyla belge çekimi: sessionKey oturumu kullanılır, targetTaxId ile önce
    /// "mükellef değiştir/seç" akışı çalışır. Şifre GÖNDERİLMEZ (yalnız kayıtlı oturum).
    /// </summary>
    public Task<RobotResponse<RobotDocument>> GetDocumentsAsAdvisorAsync(
        string sessionKey, string targetTaxId, DateTime from, DateTime to, CancellationToken ct) =>
        PostAsync<RobotDocument>("/documents", new
        {
            sessionKey,
            targetTaxId,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd")
        }, ct);

    /// <summary>Müşavir oturumuyla e-Tebligat çekimi (GetDocumentsAsAdvisorAsync ile aynı model).</summary>
    public Task<RobotResponse<RobotTebligat>> GetTebligatAsAdvisorAsync(
        string sessionKey, string targetTaxId, DateTime from, DateTime to, CancellationToken ct) =>
        PostAsync<RobotTebligat>("/tebligat", new
        {
            sessionKey,
            targetTaxId,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd")
        }, ct);

    /// <summary>/advisor/session-status yanıtı (rozet için; tarayıcı açılmaz).</summary>
    public class RobotSessionStatus : RobotEnvelope
    {
        [JsonPropertyName("exists")]
        public bool Exists { get; set; }

        [JsonPropertyName("expired")]
        public bool Expired { get; set; }

        [JsonPropertyName("lastSeenAt")]
        public string? LastSeenAt { get; set; }
    }

    /// <summary>Müşavir oturum durumu (aktif / yenilenmeli + son görülme).</summary>
    public Task<RobotSessionStatus> GetAdvisorSessionStatusAsync(
        string sessionKey, CancellationToken ct) =>
        PostEnvelopeAsync<RobotSessionStatus>("/advisor/session-status", new { sessionKey }, ct);

    /// <summary>
    /// Ortak POST: token başlığı, zaman aşımı, Türkçe hata eşlemesi.
    /// Robot iş hatalarını 200 + {ok:false} ile döndürür; HTTP hataları burada eşlenir.
    /// </summary>
    private async Task<RobotResponse<T>> PostAsync<T>(string path, object payload, CancellationToken ct)
    {
        var token = Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("ROBOT_TOKEN tanımsız; gib-robot çağrısı yapılmadı. Uç: {Path}", path);
            return Failure<T>(
                "GİB Portal Robotu yapılandırılmamış (ROBOT_TOKEN tanımlı değil). " +
                "Sunucuda deploy/.env dosyasına ROBOT_TOKEN ekleyip servisleri yeniden başlatın.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = BaseUrl + path;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // Şifre yalnız gövdede, iç ağ üzerinden gider — LOGA YAZILMAZ.
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.TryAddWithoutValidation("X-Robot-Token", token);

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogError("gib-robot isteği reddedildi (401). API ve robot ROBOT_TOKEN değerleri farklı olabilir.");
                return Failure<T>(
                    "GİB Portal Robotu isteği reddetti (geçersiz erişim anahtarı). " +
                    "API ve robot servislerinin ROBOT_TOKEN değerlerinin aynı olduğundan emin olun.");
            }

            if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("gib-robot HTTP hatası: {Status} ({Path})", response.StatusCode, path);
                return Failure<T>(
                    "GİB Portal Robotu beklenmeyen bir yanıt döndürdü. Sunucuda \"docker logs gib-robot\" ile inceleyin.",
                    retryable: true);
            }

            RobotResponse<T>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<RobotResponse<T>>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "gib-robot yanıtı çözümlenemedi. Yanıt (ilk 300): {Body}", Truncate(body, 300));
                return Failure<T>(
                    "GİB Portal Robotundan beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.",
                    retryable: true);
            }

            if (parsed is null)
            {
                return Failure<T>(
                    "GİB Portal Robotu boş yanıt döndürdü. Lütfen daha sonra tekrar deneyin.",
                    retryable: true);
            }

            if (!parsed.Ok && !string.IsNullOrWhiteSpace(parsed.Diagnostic))
            {
                // Teşhis metni (ekran başlığı/URL/ekran görüntüsü yolu) yalnız sunucu günlüğüne yazılır.
                _logger.LogWarning("gib-robot teşhis ({Path}): {Diagnostic}", path, parsed.Diagnostic);
            }

            return parsed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("gib-robot zaman aşımı. Uç: {Path}", path);
            return Failure<T>(
                "GİB Portal Robotu zamanında yanıt vermedi (portal yavaş olabilir). Lütfen daha sonra tekrar deneyin.",
                retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "gib-robot servisine bağlanılamadı. Adres: {Url}", url);
            return Failure<T>(
                $"GİB Portal Robotu servisine bağlanılamadı ({BaseUrl}). " +
                "Sunucuda \"docker compose ps\" ile gib-robot servisinin çalıştığını doğrulayın.",
                retryable: true);
        }
    }

    /// <summary>
    /// İnsan destekli giriş uçları için ortak POST (RobotEnvelope türevleri). Hata eşlemesi
    /// PostAsync ile aynı ilkededir: iş hataları 200 + {ok:false} ile gelir; HTTP/ağ hataları
    /// Türkçe mesajlı başarısız zarfa çevrilir. Şifre/kod LOGA ASLA yazılmaz.
    /// </summary>
    private async Task<T> PostEnvelopeAsync<T>(string path, object payload, CancellationToken ct)
        where T : RobotEnvelope, new()
    {
        static T Fail(string message) => new() { Ok = false, Message = message };

        var token = Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("ROBOT_TOKEN tanımsız; gib-robot çağrısı yapılmadı. Uç: {Path}", path);
            return Fail(
                "GİB Portal Robotu yapılandırılmamış (ROBOT_TOKEN tanımlı değil). " +
                "Sunucuda deploy/.env dosyasına ROBOT_TOKEN ekleyip servisleri yeniden başlatın.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = BaseUrl + path;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // Şifre/doğrulama kodu yalnız gövdede, iç ağ üzerinden gider — LOGA YAZILMAZ.
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.TryAddWithoutValidation("X-Robot-Token", token);

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogError("gib-robot isteği reddedildi (401). API ve robot ROBOT_TOKEN değerleri farklı olabilir.");
                return Fail(
                    "GİB Portal Robotu isteği reddetti (geçersiz erişim anahtarı). " +
                    "API ve robot servislerinin ROBOT_TOKEN değerlerinin aynı olduğundan emin olun.");
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("gib-robot boş yanıt döndürdü: {Status} ({Path})", response.StatusCode, path);
                return Fail("GİB Portal Robotu boş yanıt döndürdü. Lütfen daha sonra tekrar deneyin.");
            }

            T? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<T>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "gib-robot yanıtı çözümlenemedi. Uç: {Path}", path);
                return Fail("GİB Portal Robotundan beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.");
            }

            if (parsed is null)
            {
                return Fail("GİB Portal Robotu boş yanıt döndürdü. Lütfen daha sonra tekrar deneyin.");
            }

            if (!parsed.Ok && !string.IsNullOrWhiteSpace(parsed.Diagnostic))
            {
                // Teşhis metni yalnız sunucu günlüğüne yazılır (şifre/kod içermez).
                _logger.LogWarning("gib-robot teşhis ({Path}): {Diagnostic}", path, parsed.Diagnostic);
            }

            return parsed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("gib-robot zaman aşımı. Uç: {Path}", path);
            return Fail(
                "GİB Portal Robotu zamanında yanıt vermedi (portal yavaş olabilir). Lütfen daha sonra tekrar deneyin.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "gib-robot servisine bağlanılamadı. Adres: {Url}", url);
            return Fail(
                $"GİB Portal Robotu servisine bağlanılamadı ({BaseUrl}). " +
                "Sunucuda \"docker compose ps\" ile gib-robot servisinin çalıştığını doğrulayın.");
        }
    }

    /// <summary>Robotu hiç çağıramadığımız durumlar için ortak başarısız zarf.</summary>
    private static RobotResponse<T> Failure<T>(string message, bool retryable = false) => new()
    {
        Ok = false,
        Message = message,
        RequiresManual = false,
        Retryable = retryable
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
