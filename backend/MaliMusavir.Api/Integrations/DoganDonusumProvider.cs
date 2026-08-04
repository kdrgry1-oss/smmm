using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// Doğan e-Dönüşüm (Doğan Dönüşüm) e-Fatura SOAP adaptörü — Cybersoft "connector" ailesi
/// (QNB eFinans ile aynı yazılım tabanı; Doğan white-label kurulumudur).
///
/// Protokol, çalışan bir Python/zeep istemcisinden (dogan_client.py) çıkarılmıştır:
/// - Canlı taban: https://connector.doganedonusum.com — servisler: /AuthenticationWS, /EFaturaOIB.
///   Test taban: https://efaturatest.doganedonusum.com (aynı yollar). Kimlik JSON'undaki opsiyonel
///   "endpoint" alanı taban URL'i EZER (varsayılan canlı taban; test ortamı işaretliyse test taban).
/// - Kimlik: WS-Security YOKTUR. Önce AuthenticationWS'e Login çağrılır:
///   REQUEST_HEADER{SESSION_ID:"", APPLICATION_NAME:"MALIMUSAVIR"} + USER_NAME + PASSWORD →
///   LoginResponse{SESSION_ID, ERROR_TYPE{ERROR_CODE, ERROR_SHORT_DES, ERROR_LONG_DES}}.
///   Sonraki çağrılarda REQUEST_HEADER{SESSION_ID:&lt;oturum&gt;, APPLICATION_NAME, COMPRESSED:"N"}.
///   İş bitince Logout çağrılır (hatası yutulur).
/// - Gelen faturalar: EFaturaOIB'e GetInvoice — INVOICE_SEARCH_KEY{DIRECTION=IN,
///   START_DATE=yyyy-MM-dd, END_DATE=yyyy-MM-dd, READ_INCLUDED=true} + HEADER_ONLY=Y.
///   Yanıtta INVOICE elemanları döner; UUID/ID genelde INVOICE üzerinde ÖZNİTELİKTİR, başlık
///   alanları HEADER çocuğunda elemandır — ayrıştırıcı iki biçimi de tolere eder.
/// - XML ad alanı CANLIDA DOĞRULANDI (WSDL: targetNamespace=http://schemas.i2i.com/ei/wsdl —
///   platform i2i tabanlı). Parametre çocuklarının nitelikliliği kuruluma göre değişebildiği
///   için Login iki biçimi otomatik dener ve başaran biçim oturum boyunca kullanılır. YANIT
///   ayrıştırma yine tamamen AD ALANINDAN BAĞIMSIZDIR (LocalName) ve beklenmeyen yanıt/SOAP
///   Fault durumlarında ham gövdenin ilk ~2000 karakteri hata mesajına eklenir.
/// - Sayfalama: Cybersoft GetInvoice'ta klasik sayfa parametresi yoktur; tarih aralığı sorgusu
///   tek seferde döner. Mükerrer UUID'ler HashSet ile sağlayıcı içinde ayıklanır.
/// - HEADER_ONLY=Y yanıtında KDV kırılımı yoktur → TAX_EXCLUSIVE_AMOUNT matrahlı,
///   (TAX_INCLUSIVE_AMOUNT − TAX_EXCLUSIVE_AMOUNT) KDV'li TEK özet satır üretilir; KDV oranı
///   orandan geri hesaplanıp 1/8/10/18/20'den en yakınına yuvarlanır.
/// </summary>
public class DoganDonusumProvider : IEInvoiceProvider
{
    public const string HttpClientName = "dogandonusum";

    /// <summary>Varsayılan CANLI taban (Doğan müşteri yöneticisinin verdiği resmi adres).</summary>
    public const string DefaultBaseUrl = "https://connector.doganedonusum.com";
    private const string TestBaseUrl = "https://efaturatest.doganedonusum.com";

    private const string AuthPath = "/AuthenticationWS";
    private const string InvoicePath = "/EFaturaOIB";

    /// <summary>REQUEST_HEADER.APPLICATION_NAME değeri (istemci kimliği; serbest metindir).</summary>
    private const string ApplicationName = "MALIMUSAVIR";

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// CANLIDA DOĞRULANDI (31.07.2026, connector.doganedonusum.com WSDL çıktısı):
    /// hem AuthenticationWS hem EFaturaOIB targetNamespace = http://schemas.i2i.com/ei/wsdl
    /// (platform i2i tabanlıdır; ilk tahmin edilen cybersoft ad alanı yanlıştı ve sunucu
    /// "Cannot find dispatch method" fault'u dönmüştü).
    /// </summary>
    private const string ServiceNs = "http://schemas.i2i.com/ei/wsdl";
    private static readonly XNamespace SvcNs = ServiceNs;

    /// <summary>Yanıttaki hata mesajlarına eklenen ham gövde üst sınırı (curl-driven debugging).</summary>
    private const int RawBodySnippetLength = 2000;

    /// <summary>KDV oranı geri hesabında yuvarlanan geçerli oranlar.</summary>
    private static readonly decimal[] KnownVatRates = { 1m, 8m, 10m, 18m, 20m };

    private static readonly JsonSerializerOptions CredentialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DoganDonusumProvider> _logger;

    /// <summary>
    /// Login'in başardığı zarf biçimi (parametre çocukları nitelikli mi?) — GetInvoice/Logout
    /// aynı biçimi kullanır. Provider scoped kaydedilir; tek senkron ömründe geçerlidir.
    /// </summary>
    private bool _qualifiedChildren;

    public DoganDonusumProvider(IHttpClientFactory httpClientFactory, ILogger<DoganDonusumProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.DoganDonusum;

    // ---------- Kimlik paketleme (IntegrationsController da kullanır) ----------

    private sealed class DoganCredentials
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        /// <summary>Opsiyonel taban adres; boşsa varsayılan (canlı/test) taban kullanılır.</summary>
        [JsonPropertyName("endpoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Endpoint { get; set; }
    }

    /// <summary>
    /// Kullanıcı adı + şifre + opsiyonel taban adresi ApiKeyEncrypted alanında saklanacak
    /// JSON'a paketler: {"username":"...","password":"...","endpoint":"..."?}. Boş endpoint yazılmaz.
    /// (Uyumsoft ile aynı kalıp — sağlayıcıya özgü ayrı kolon açılmaz.)
    /// </summary>
    public static string PackCredentials(string username, string password, string? endpoint = null) =>
        JsonSerializer.Serialize(new DoganCredentials
        {
            Username = username,
            Password = password,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim()
        });

    /// <summary>
    /// Saklanan JSON'dan kullanıcı adı + şifre + opsiyonel taban adresi çözer.
    /// Geçersizse false döner (endpoint yoksa null kalır).
    /// </summary>
    public static bool TryParseCredentials(
        string? json, out string username, out string password, out string? endpoint)
    {
        username = string.Empty;
        password = string.Empty;
        endpoint = null;

        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var creds = JsonSerializer.Deserialize<DoganCredentials>(json, CredentialJsonOptions);
            if (creds is null ||
                string.IsNullOrWhiteSpace(creds.Username) ||
                string.IsNullOrWhiteSpace(creds.Password))
            {
                return false;
            }

            username = creds.Username.Trim();
            password = creds.Password;
            endpoint = string.IsNullOrWhiteSpace(creds.Endpoint) ? null : creds.Endpoint.Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------- IEInvoiceProvider ----------

    public async Task<ProviderResult<List<ProviderInvoice>>> GetIncomingInvoicesAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, CancellationToken ct)
    {
        // apiKey burada JSON kimlik paketidir: {"username","password","endpoint"?}.
        if (!TryParseCredentials(apiKey, out var username, out var password, out var customEndpoint))
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Doğan Dönüşüm kimlik bilgileri tanımlı değil. Lütfen entegrasyonu kullanıcı adı ve şifreyle yeniden kaydedin.");
        }

        // Taban adres çözümü: kimlikteki özel adres > test tabanı > varsayılan canlı taban.
        var baseUrl = ResolveBaseUrl(customEndpoint, testEnv);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        string? sessionId = null;
        try
        {
            // 1) Login: her senkronda yeni oturum açılır (i2i oturumları kısa ömürlüdür).
            var login = await LoginAsync(client, baseUrl, username, password, ct);
            if (login.Error is not null) return login.Error;
            sessionId = login.SessionId;
            _qualifiedChildren = login.QualifiedChildren;

            // 2) GetInvoice: DIRECTION=IN + tarih aralığı + HEADER_ONLY=Y (tek istekte döner).
            var envelope = BuildGetInvoiceEnvelope(sessionId!, from, to, _qualifiedChildren);
            var (status, body) = await PostEnvelopeAsync(client, baseUrl + InvoicePath, envelope, ct);

            var fault = TryParseSoapFault(body);
            if (fault is not null)
            {
                _logger.LogWarning("Doğan Dönüşüm SOAP Fault (GetInvoice): {Fault}", fault);
                // Ad alanı/operasyon uyuşmazlığı dahil her fault'ta ham gövde mesaja eklenir
                // (canlıda kesin teşhis için — bkz. sınıf yorumu, şema belirsizliği).
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    $"Doğan Dönüşüm servisi hata bildirdi: {fault} — Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
            }

            var httpError = MapErrorStatus(status, body);
            if (httpError is not null)
            {
                _logger.LogWarning("Doğan Dönüşüm GetInvoice HTTP hatası: {Status}", status);
                return httpError;
            }

            return ParseGetInvoiceResponse(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient zaman aşımı (30 sn) TaskCanceledException olarak gelir.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Doğan Dönüşüm servisi yanıt vermedi (zaman aşımı). Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Doğan Dönüşüm servisine bağlanılamadı. Taban: {BaseUrl}", baseUrl);
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Doğan Dönüşüm servisine bağlanılamadı. Servis adresini ve internet bağlantınızı kontrol edip tekrar deneyin.",
                retryable: true);
        }
        catch (XmlException ex)
        {
            _logger.LogError(ex, "Doğan Dönüşüm yanıtı XML olarak çözümlenemedi.");
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Doğan Dönüşüm'den beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        finally
        {
            // 3) Logout: oturum her durumda kapatılmaya çalışılır; hatası senkron sonucunu bozmaz.
            if (sessionId is not null)
                await TryLogoutAsync(client, baseUrl, sessionId);
        }
    }

    // ---------- Taban adres + HTTP gönderim ----------

    private static string ResolveBaseUrl(string? customEndpoint, bool testEnv)
    {
        // Kimlikte kayıtlı özel adres her zaman önceliklidir (test/farklı kurulum bu alanla seçilir).
        if (!string.IsNullOrWhiteSpace(customEndpoint)) return customEndpoint.Trim().TrimEnd('/');
        return testEnv ? TestBaseUrl : DefaultBaseUrl;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostEnvelopeAsync(
        HttpClient client, string url, string envelope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        // Cybersoft connector SOAP 1.1: SOAPAction boş gönderilir (zeep de böyle yapar).
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, body);
    }

    // ---------- Login / Logout ----------

    private async Task<(string? SessionId, bool QualifiedChildren, ProviderResult<List<ProviderInvoice>>? Error)>
        LoginAsync(HttpClient client, string baseUrl, string username, string password, CancellationToken ct)
    {
        // i2i şemasında parametre çocuklarının nitelikli olup olmadığı kuruluma göre değişebilir.
        // Önce niteliksiz (zeep'in yaygın çıktısı) denenir; oturum dönmez ve hata kesin bir
        // kimlik hatası değilse aynı istek NİTELİKLİ çocuklarla bir kez daha denenir.
        ProviderResult<List<ProviderInvoice>>? firstError = null;

        foreach (var qualified in new[] { false, true })
        {
            // i2i/İZİBİZ kalıbı: operasyon elemanları "Request" soneklidir (LoginRequest,
            // GetInvoiceRequest...). "Login" adıyla gönderim "Cannot find dispatch method"
            // fault'u üretmişti (canlı teşhis, 31.07.2026). SESSION_ID login'de "-1" gönderilir.
            var operation = new XElement(SvcNs + "LoginRequest",
                new XElement("REQUEST_HEADER",
                    new XElement("SESSION_ID", "-1"),
                    new XElement("APPLICATION_NAME", ApplicationName)),
                new XElement("USER_NAME", username),
                new XElement("PASSWORD", password));
            if (qualified) operation = QualifyChildren(operation);

            var (status, body) = await PostEnvelopeAsync(client, baseUrl + AuthPath, BuildEnvelope(operation), ct);

            var fault = TryParseSoapFault(body);
            if (fault is not null)
            {
                _logger.LogWarning("Doğan Dönüşüm SOAP Fault (Login, qualified={Qualified}): {Fault}", qualified, fault);
                firstError ??= ProviderResult<List<ProviderInvoice>>.Fail(
                    $"Doğan Dönüşüm oturumu açılamadı: {fault} — Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
                continue; // diğer biçimi dene
            }

            var httpError = MapErrorStatus(status, body);
            if (httpError is not null)
            {
                _logger.LogWarning("Doğan Dönüşüm Login HTTP hatası: {Status}", status);
                return (null, false, httpError);
            }

            var doc = XDocument.Parse(body);
            var sessionId = FindLeafValue(doc.Root, "SESSION_ID");
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                // Bazı kurulumlar başarıda da ERROR_TYPE{ERROR_CODE:0} döndürür;
                // SESSION_ID dolu ise oturum geçerli sayılır (Python istemciyle aynı davranış).
                _logger.LogInformation("Doğan Dönüşüm oturumu açıldı ({BaseUrl}, qualified={Qualified}).",
                    baseUrl, qualified);
                return (sessionId, qualified, null);
            }

            // Oturum yok → ERROR_TYPE'tan Türkçe mesaj (ERROR_SHORT_DES öncelikli) çıkarılır.
            var shortDes = FindLeafValue(doc.Root, "ERROR_SHORT_DES");
            var longDes = FindLeafValue(doc.Root, "ERROR_LONG_DES");
            var code = FindLeafValue(doc.Root, "ERROR_CODE");
            var message = shortDes ?? longDes;

            _logger.LogWarning("Doğan Dönüşüm login başarısız (qualified={Qualified}). Kod: {Code}, Mesaj: {Message}",
                qualified, code, message);

            if (message is not null && LooksLikeAuthError(message))
            {
                // Kimlik hatası kesin: diğer zarf biçimini denemek anlamsız.
                return (null, false, ProviderResult<List<ProviderInvoice>>.Fail(
                    "Doğan Dönüşüm kullanıcı adı veya şifresi hatalı."));
            }

            firstError ??= ProviderResult<List<ProviderInvoice>>.Fail(message is not null
                ? $"Doğan Dönüşüm oturumu açılamadı: {message}"
                : $"Doğan Dönüşüm oturumu açılamadı (SESSION_ID dönmedi). Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
        }

        return (null, false, firstError ?? ProviderResult<List<ProviderInvoice>>.Fail(
            "Doğan Dönüşüm oturumu açılamadı."));
    }

    /// <summary>
    /// Operasyonun TÜM niteliksiz alt elemanlarını servis ad alanına taşıyan kopyasını üretir
    /// (elementFormDefault="qualified" kurulumlar için ikinci deneme biçimi).
    /// </summary>
    private static XElement QualifyChildren(XElement operation)
    {
        var copy = new XElement(operation);
        foreach (var el in copy.Descendants())
        {
            if (el.Name.Namespace == XNamespace.None)
                el.Name = SvcNs + el.Name.LocalName;
        }
        return copy;
    }

    /// <summary>Oturumu kapatır; her tür hata yutulur (yalnız loglanır).</summary>
    private async Task TryLogoutAsync(HttpClient client, string baseUrl, string sessionId)
    {
        try
        {
            var operation = new XElement(SvcNs + "LogoutRequest", MakeSessionHeader(sessionId));
            if (_qualifiedChildren) operation = QualifyChildren(operation);
            // ct verilmez: senkron iptal edilse bile oturum kapatma denenir (kısa istek).
            await PostEnvelopeAsync(client, baseUrl + AuthPath, BuildEnvelope(operation), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Doğan Dönüşüm logout hatası (yutuldu).");
        }
    }

    // ---------- SOAP zarfı üretimi ----------

    /// <summary>Login sonrası tüm çağrıların REQUEST_HEADER'ı (COMPRESSED=N: sıkıştırmasız yanıt).</summary>
    private static XElement MakeSessionHeader(string sessionId) =>
        new("REQUEST_HEADER",
            new XElement("SESSION_ID", sessionId),
            new XElement("APPLICATION_NAME", ApplicationName),
            new XElement("COMPRESSED", "N"));

    private static string BuildGetInvoiceEnvelope(string sessionId, DateTime from, DateTime to, bool qualifiedChildren)
    {
        // Alan/operasyon adları Python istemcideki gibi BÜYÜK HARF SNAKE — birebir korunur.
        var operation = new XElement(SvcNs + "GetInvoiceRequest",
            MakeSessionHeader(sessionId),
            new XElement("INVOICE_SEARCH_KEY",
                // Şema CANLI fault'larla doğrulandı (31.07.2026): INVOICE_SEARCH_KEY yalnız
                // LIMIT → DIRECTION → SENDER → RECEIVER → DRAFT_FLAG kabul eder;
                // START_DATE/END_DATE/READ_INCLUDED bu kurulumda YOKTUR ("Element not allowed").
                // Tarih filtresi bu yüzden yanıt tarafında (ISSUE_DATE zaten ayrıştırılıyor)
                // İSTEMCİDE uygulanmaz — dönen tüm kayıtlar upsert edilir (UUID mükerrer korumalı);
                // LIMIT üst sınırı 100 — dolarsa aralık daraltma uyarısı loglanır.
                new XElement("LIMIT", "100"),
                new XElement("DIRECTION", "IN"),
                new XElement("SENDER", string.Empty),
                new XElement("RECEIVER", string.Empty),
                new XElement("DRAFT_FLAG", "N")),
            new XElement("HEADER_ONLY", "Y"));
        if (qualifiedChildren) operation = QualifyChildren(operation);

        return BuildEnvelope(operation);
    }

    /// <summary>
    /// SOAP 1.1 zarfı. Yalnız operasyon elemanı servis ad alanındadır (parametre çocukları
    /// niteliksiz) — zeep'in ürettiği zarfla aynı yerleşim. Ad alanı yanlışsa sunucu Fault
    /// döner ve ham gövde hata mesajına eklenir (bkz. sınıf yorumu).
    /// </summary>
    private static string BuildEnvelope(XElement operation)
    {
        var envelope = new XElement(SoapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapNs),
            new XAttribute(XNamespace.Xmlns + "ser", SvcNs),
            new XElement(SoapNs + "Body", operation));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), envelope)
            .ToString(SaveOptions.DisableFormatting);
    }

    // ---------- Fault ayrıştırma / hata eşleme ----------

    private static string? TryParseSoapFault(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.Contains("Fault", StringComparison.Ordinal))
            return null;

        try
        {
            var doc = XDocument.Parse(body);
            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is null) return null;

            // SOAP 1.1 fault: faultcode/faultstring isimsiz elemanlardır (1.2: Reason).
            var faultString = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                              ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Reason")?.Value;

            return string.IsNullOrWhiteSpace(faultString) ? "Bilinmeyen servis hatası" : faultString.Trim();
        }
        catch (XmlException)
        {
            return null; // XML değilse HTTP durum eşlemesine düşülür
        }
    }

    private ProviderResult<List<ProviderInvoice>>? MapErrorStatus(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.OK) return null;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Doğan Dönüşüm kullanıcı adı veya şifresi hatalı.");
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Doğan Dönüşüm servisi şu anda meşgul. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }

        if ((int)status >= 500)
        {
            // 500'lerde gövde çoğu zaman fault taşır; taşımıyorsa da teşhis için ham gövde loglanır.
            _logger.LogWarning("Doğan Dönüşüm HTTP {Status}. Ham yanıt (ilk {Len}): {Body}",
                (int)status, RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Doğan Dönüşüm tarafında geçici bir sorun oluştu. Lütfen daha sonra tekrar deneyin.",
                retryable: true);
        }

        return ProviderResult<List<ProviderInvoice>>.Fail(
            $"Doğan Dönüşüm isteği başarısız oldu (HTTP {(int)status}). Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
    }

    private static bool LooksLikeAuthError(string message) =>
        message.Contains("kullanıcı", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("şifre", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("parola", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("username", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("authentication", StringComparison.OrdinalIgnoreCase);

    // ---------- Yanıt ayrıştırma ----------

    private ProviderResult<List<ProviderInvoice>> ParseGetInvoiceResponse(string body)
    {
        var doc = XDocument.Parse(body);

        // Fatura kayıtları: ad alanından bağımsız INVOICE elemanları.
        var invoiceElements = doc.Descendants().Where(e => e.Name.LocalName == "INVOICE").ToList();

        if (invoiceElements.Count == 0)
        {
            // Fatura yoksa iki olasılık: gerçekten boş aralık YA DA servis ERROR_TYPE ile hata
            // bildirdi (ör. oturum düştü). ERROR_CODE 0/boş değilse hata kabul edilir.
            var code = FindLeafValue(doc.Root, "ERROR_CODE");
            var message = FindLeafValue(doc.Root, "ERROR_SHORT_DES") ?? FindLeafValue(doc.Root, "ERROR_LONG_DES");
            if (!string.IsNullOrWhiteSpace(message) &&
                !string.IsNullOrWhiteSpace(code) && code.Trim() != "0")
            {
                _logger.LogWarning("Doğan Dönüşüm GetInvoice hata döndü. Kod: {Code}, Mesaj: {Message}", code, message);
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    $"Doğan Dönüşüm servisi hata bildirdi: {message}");
            }

            // Yanıt hiç tanınmıyorsa (ne INVOICE ne RESPONSE benzeri kök) ham gövdeyle uyarılır.
            var looksLikeResponse = doc.Descendants().Any(e =>
                e.Name.LocalName.Contains("Response", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName == "ERROR_TYPE");
            if (!looksLikeResponse)
            {
                _logger.LogError("Doğan Dönüşüm yanıtı tanınamadı. Ham yanıt (ilk {Len}): {Body}",
                    RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    $"Doğan Dönüşüm'den beklenmeyen bir yanıt alındı. Ham yanıt: {Truncate(body, RawBodySnippetLength)}",
                    retryable: true);
            }

            return ProviderResult<List<ProviderInvoice>>.Ok(new List<ProviderInvoice>());
        }

        var invoices = new List<ProviderInvoice>();
        // TEŞHİS: kurulumun gerçek başlık alan adlarını görmek için ilk kaydın ham XML'i loglanır
        // (canlı alan eşlemesi ayarı; docker logs ile okunur — kalıcı zararsız, tek satır/senkron).
        _logger.LogInformation("Doğan Dönüşüm ilk INVOICE örneği: {Sample}",
            Truncate(invoiceElements[0].ToString(SaveOptions.DisableFormatting), RawBodySnippetLength));
        // Mükerrer koruması: aynı UUID yanıtta birden fazla kez dönerse ilki alınır
        // (DB unique index'ine ve EInvoiceSyncService kontrolüne ek, sağlayıcı içi ilk savunma hattı).
        var seenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateCount = 0;

        foreach (var element in invoiceElements)
        {
            var invoice = TryMapInvoice(element);
            if (invoice is null) continue;

            if (seenUuids.Add(invoice.ExternalUuid)) invoices.Add(invoice);
            else duplicateCount++;
        }

        if (duplicateCount > 0)
        {
            _logger.LogInformation(
                "Doğan Dönüşüm senkronunda {Duplicates} mükerrer kayıt sağlayıcı içinde atlandı.", duplicateCount);
        }

        if (invoices.Count >= 100)
        {
            _logger.LogWarning(
                "Doğan Dönüşüm yanıtı LIMIT'e (100) dayandı — tarih aralığını daraltıp yeniden senkron önerilir.");
        }

        _logger.LogInformation("Doğan Dönüşüm gelen fatura senkronu tamamlandı. Fatura: {Count}", invoices.Count);
        return ProviderResult<List<ProviderInvoice>>.Ok(invoices);
    }

    /// <summary>
    /// Tek INVOICE kaydını toleranslı eşler: UUID/ID önce ÖZNİTELİKTE, yoksa aynı adlı (alt)
    /// elemanda aranır; başlık alanları HEADER çocuğundan (herhangi bir derinlikte) okunur.
    /// Tutarlar invariant culture ile, tarih yyyy-MM-dd biçiminde çözülür.
    /// </summary>
    private static ProviderInvoice? TryMapInvoice(XElement invoice)
    {
        var uuid = GetField(invoice, "UUID");
        if (string.IsNullOrWhiteSpace(uuid)) return null; // UUID'siz fatura yazılmaz

        var taxExclusive = ParseDecimalValue(GetField(invoice,
            "TAX_EXCLUSIVE_AMOUNT", "TAXABLE_AMOUNT", "LINE_EXTENSION_AMOUNT"));
        var taxInclusive = ParseDecimalValue(GetField(invoice, "TAX_INCLUSIVE_AMOUNT"));
        var payable = ParseDecimalValue(GetField(invoice, "PAYABLE_AMOUNT"));

        // Matrah başlıkta yoksa KDV tutarından türetilir: TAX_AMOUNT/VAT_AMOUNT alanı varsa
        // matrah = toplam − KDV (uydurma oran varsayımı YAPILMAZ; alan da yoksa 0 kalır ve
        // teşhis loglarındaki ham örnekle alan adı eklenir).
        if (taxExclusive == 0m)
        {
            var headerVat = ParseDecimalValue(GetField(invoice, "TAX_AMOUNT", "VAT_AMOUNT", "TOTAL_TAX_AMOUNT"));
            var basis = taxInclusive != 0m ? taxInclusive : payable;
            if (headerVat > 0m && basis > headerVat)
                taxExclusive = Math.Round(basis - headerVat, 2);
        }

        // Eksik toplamlar birbirinden tamamlanır (HEADER_ONLY yanıtında üçü de genelde vardır).
        if (payable == 0m && taxInclusive != 0m) payable = taxInclusive;
        if (taxInclusive == 0m && payable != 0m) taxInclusive = payable;

        var taxTotal = Math.Round(taxInclusive - taxExclusive, 2);
        if (taxTotal < 0m) taxTotal = 0m;

        var issueDate = ParseDateValue(GetField(invoice, "ISSUE_DATE")) ?? DateTime.UtcNow.Date;

        // Gelen faturada karşı taraf (satıcı). CANLI GÖZLEM (31.07.2026): bu kurulumda bazı
        // alanlar "urn:mail:..." posta kutusu etiketi taşıyor — VKN yalnız 10-11 haneli düz sayı
        // veren adaydan, unvan ise URN/sayı OLMAYAN ilk adaydan seçilir.
        var senderTaxId = PickTaxId(
            GetField(invoice, "SENDER"),
            GetField(invoice, "SUPPLIER"),
            GetNestedField(invoice, new[] { "SUPPLIER", "SENDER" }, new[] { "VKN", "IDENTIFIER", "TCKN" }));
        var senderTitle = PickTitle(
                              GetField(invoice, "SUPPLIER_TITLE"),
                              GetField(invoice, "SENDER_TITLE"),
                              GetField(invoice, "SUPPLIER"),
                              GetField(invoice, "SUPPLIER_NAME"),
                              GetField(invoice, "SENDER_NAME"),
                              GetField(invoice, "TITLE"),
                              GetNestedField(invoice, new[] { "SUPPLIER", "SENDER" }, new[] { "TITLE", "NAME", "ALIAS" }),
                              GetField(invoice, "FROM"))
                          ?? string.Empty;

        var currency = GetField(invoice, "CURRENCY_CODE", "DOCUMENT_CURRENCY_CODE", "CURRENCY");

        return new ProviderInvoice
        {
            // DB kolon sınırları aşılmasın diye alanlar sessizce kırpılır (ClampLength).
            ExternalUuid = ClampLength(uuid, 64),
            // Cybersoft'ta ID = GİB fatura numarası (ör. ABC2024000000123), UUID = ETTN.
            InvoiceNumber = ClampLength(GetField(invoice, "ID", "INVOICE_ID") ?? string.Empty, 30),
            IssueDate = issueDate,
            SenderTaxId = senderTaxId,
            SenderTitle = ClampLength(senderTitle, 300),
            CurrencyCode = string.IsNullOrWhiteSpace(currency) ? "TRY" : ClampLength(currency, 3),
            TaxExclusiveAmount = taxExclusive,
            TaxTotal = taxTotal,
            PayableAmount = payable,
            Lines = BuildSummaryLines(taxExclusive, taxTotal)
        };
    }

    /// <summary>
    /// HEADER_ONLY=Y yanıtında KDV kırılımı yoktur → başlık toplamlarından TEK özet satır
    /// üretilir; KDV oranı orandan geri hesaplanıp 1/8/10/18/20'den EN YAKININA yuvarlanır.
    /// TODO-VERIFY: gerçek kalemler HEADER_ONLY=N yanıtının UBL içeriğinden çıkarılabilir.
    /// </summary>
    private static List<ProviderInvoiceLine> BuildSummaryLines(decimal taxExclusive, decimal taxTotal)
    {
        var vatRate = 0m;
        if (taxExclusive > 0m && taxTotal > 0m)
        {
            var rawRate = taxTotal / taxExclusive * 100m;
            vatRate = KnownVatRates.OrderBy(r => Math.Abs(r - rawRate)).First();
        }

        return new List<ProviderInvoiceLine>
        {
            new()
            {
                LineNumber = 1,
                ItemName = "Fatura toplamı (kalem detayı UBL XML'den alınacak)",
                Quantity = 1,
                UnitCode = "C62",
                UnitPrice = taxExclusive,
                LineAmount = taxExclusive,
                VatRate = vatRate,
                VatAmount = taxTotal
            }
        };
    }

    // ---------- Alan okuma yardımcıları (ad alanından bağımsız, öznitelik + eleman toleranslı) ----------

    /// <summary>
    /// Alan değerini toleranslı okur: verilen adlar sırasıyla önce elemanın ÖZNİTELİKLERİNDE,
    /// sonra ALT AĞAÇTAKİ YAPRAK elemanlarda aranır (LocalName eşleşmesi, büyük/küçük harf
    /// duyarsız). Çocuk eleman taşıyan kapsayıcılar atlanır; boş değerler geçilir.
    /// </summary>
    private static string? GetField(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var attr = element.Attributes().FirstOrDefault(a =>
                string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (attr is not null && !string.IsNullOrWhiteSpace(attr.Value))
                return attr.Value.Trim();

            var leaf = element.Descendants().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase) &&
                !e.HasElements &&
                !string.IsNullOrWhiteSpace(e.Value));
            if (leaf is not null) return leaf.Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Kapsayıcı eleman (ör. SENDER) içindeki alt alanı okur: containerNames'ten bulunan İLK
    /// elemanın alt ağacında fieldNames sırasıyla (öznitelik + yaprak eleman) aranır.
    /// </summary>
    private static string? GetNestedField(XElement element, string[] containerNames, string[] fieldNames)
    {
        foreach (var containerName in containerNames)
        {
            var container = element.Descendants().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, containerName, StringComparison.OrdinalIgnoreCase) &&
                e.HasElements);
            if (container is null) continue;

            var value = GetField(container, fieldNames);
            if (value is not null) return value;
        }

        return null;
    }

    private static string? FindLeafValue(XElement? root, string localName)
    {
        if (root is null) return null;
        var value = root.Descendants()
            .FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase) &&
                !e.HasElements &&
                !string.IsNullOrWhiteSpace(e.Value))
            ?.Value;
        return value?.Trim();
    }

    /// <summary>VKN/TCKN sütunu 11 karakterle sınırlıdır → yalnız rakamlar, en fazla 11 hane alınır.</summary>
    private static string SanitizeTaxId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length <= 11 ? digits : digits[..11];
    }

    /// <summary>Adaylardan geçerli VKN/TCKN (10-11 haneli DÜZ sayı) verenin ilkini seçer.</summary>
    private static string PickTaxId(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var value = candidate?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            // "urn:mail:..." gibi etiketlerin içindeki rastgele rakamlar VKN sanılmasın:
            // adayın KENDİSİ düz 10-11 haneli sayı olmalı.
            if (value.Length is 10 or 11 && value.All(char.IsDigit)) return value;
        }

        return string.Empty;
    }

    /// <summary>Adaylardan unvana benzeyen (URN/e-posta/düz sayı OLMAYAN) ilk değeri seçer;
    /// hiçbiri uymazsa boş olmayan ilk aday döner (hiç yoktan iyidir).</summary>
    private static string? PickTitle(params string?[] candidates)
    {
        string? fallback = null;
        foreach (var candidate in candidates)
        {
            var value = candidate?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            fallback ??= value;

            var looksLikeLabel = value.StartsWith("urn:", StringComparison.OrdinalIgnoreCase) ||
                                 value.Contains('@') ||
                                 value.All(char.IsDigit);
            if (!looksLikeLabel) return value;
        }

        return fallback;
    }

    /// <summary>Eksik/bozuk sayısal alanlar 0 kabul edilir (invariant culture).</summary>
    private static decimal ParseDecimalValue(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value, 2)
            : 0m;

    private static DateTime? ParseDateValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Beklenen biçim yyyy-MM-dd; xsd:dateTime benzeri değerler de tolere edilir.
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
        {
            return exact.Date;
        }

        try
        {
            return XmlConvert.ToDateTime(raw, XmlDateTimeSerializationMode.Utc).Date;
        }
        catch (FormatException)
        {
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fallback)
                ? fallback.Date
                : null;
        }
    }

    /// <summary>Hata mesajı/log kısaltması (üç noktalı) — DB alanları için ClampLength kullanılır.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>DB kolon sınırı için sessiz kırpma (ek karakter eklemez).</summary>
    private static string ClampLength(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
