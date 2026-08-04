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
/// EDM Bilişim e-Fatura SOAP adaptörü — Cybersoft türevi protokol (Doğan Dönüşüm'e ÇOK yakın).
///
/// - Canlı uç: https://efatura.edmbilisim.com.tr/EFaturaEDM/EFaturaEDM.svc
///   Test uç:  https://test.edmbilisim.com.tr/EFaturaEDM21ea/EFaturaEDM.svc
///   Kimlik JSON'undaki opsiyonel "endpoint" alanı ucu EZER (Uyumsoft/Doğan ile aynı kalıp).
/// - SOAP 1.1. Oturumlu akış: Login → GetDocument → Logout.
///   Login: REQUEST_HEADER{SESSION_ID:"-1", APPLICATION_NAME:"MALIMUSAVIR", CHANNEL_NAME:"",
///   COMPRESSED:"N"} + USER_NAME + PASSWORD → yanıtta SESSION_ID döner. Logout benzer
///   (REQUEST_HEADER'da açık oturumun SESSION_ID'si; hatası yutulur).
/// - TODO-VERIFY: Gelen faturaların tarih aralıklı sorgusu CANLIDA DOĞRULANMADI. Birincil
///   deneme GetDocument + SEARCH_KEY{TYPE:"INVOICE", DIRECTION:"IN", START_DATE, END_DATE,
///   READ_INCLUDED:true, DOCUMENT_CONTENT_TYPE:"XML"}. Sunucu operasyonu/parametreyi
///   tanımazsa fault mesajı + ham gövde hataya eklenir; canlı teşhis sonrası gerekirse
///   DocumentQuery / GetDocumentList operasyon adayları buraya eklenecek.
/// - TODO-VERIFY: SOAPAction — WCF .svc olduğundan birincil aday
///   "http://tempuri.org/IEFaturaEDM/{Op}"; ilk istek Action/Contract uyuşmazlığı fault'u
///   dönerse aynı istek BİR KEZ "http://tempuri.org/{Op}" ikinci adayıyla tekrarlanır.
///   Başarılı aday oturum boyunca kullanılır.
/// - TODO-VERIFY: Gövde ad alanı belirsizdir (muhtemel http://tempuri.org/). Bu yüzden
///   YANIT ayrıştırma tamamen ad alanından bağımsız (LocalName) ve öznitelik+eleman
///   toleranslı yazıldı; SOAP Fault/boş-tanınmayan yanıtta ham gövdenin ilk ~2000 karakteri
///   hata mesajına eklenir (Uyumsoft'ta bu metodolojiyle canlıda 47 fatura çekildi).
/// - KDV kırılımı dönmediğinden başlık toplamlarından TEK özet satır üretilir
///   (matrah + KDV; oran 1/8/10/18/20'den en yakınına yuvarlanır).
/// - Mükerrer UUID'ler HashSet ile sağlayıcı içinde ayıklanır.
/// </summary>
public class EdmProvider : IEInvoiceProvider
{
    public const string HttpClientName = "edm";

    /// <summary>Varsayılan CANLI uç (EDM entegrasyon dokümanındaki resmi adres).</summary>
    public const string DefaultEndpoint = "https://efatura.edmbilisim.com.tr/EFaturaEDM/EFaturaEDM.svc";
    private const string TestEndpoint = "https://test.edmbilisim.com.tr/EFaturaEDM21ea/EFaturaEDM.svc";

    /// <summary>REQUEST_HEADER.APPLICATION_NAME değeri (istemci kimliği; serbest metindir).</summary>
    private const string ApplicationName = "MALIMUSAVIR";

    private const string LoginOperation = "Login";
    private const string LogoutOperation = "Logout";
    private const string GetDocumentOperation = "GetDocument";

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// TODO-VERIFY: Gövde ad alanı — CANLIDA DOĞRULANMADI. WCF .svc varsayılanı tempuri'dir;
    /// yanlışsa sunucu fault döner ve ham gövdeyle düzeltilecek (yanıt ayrıştırma bu
    /// değerden bağımsızdır; yalnız giden zarfın operasyon elemanında kullanılır).
    /// </summary>
    private const string ServiceNs = "http://tempuri.org/";
    private static readonly XNamespace SvcNs = ServiceNs;

    /// <summary>
    /// TODO-VERIFY: SOAPAction adayları sırayla denenir (ilk fault'ta bir kez ikinciye geçilir).
    /// WCF sözleşme adı muhtemelen IEFaturaEDM'dir; bazı kurulumlar sözleşmesiz action kullanır.
    /// </summary>
    private static readonly string[] SoapActionTemplates =
    {
        "http://tempuri.org/IEFaturaEDM/{0}",
        "http://tempuri.org/{0}"
    };

    /// <summary>Yanıttaki hata mesajlarına eklenen ham gövde üst sınırı (curl-driven debugging).</summary>
    private const int RawBodySnippetLength = 2000;

    /// <summary>KDV oranı geri hesabında yuvarlanan geçerli oranlar.</summary>
    private static readonly decimal[] KnownVatRates = { 1m, 8m, 10m, 18m, 20m };

    private static readonly JsonSerializerOptions CredentialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EdmProvider> _logger;

    public EdmProvider(IHttpClientFactory httpClientFactory, ILogger<EdmProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.Edm;

    // ---------- Kimlik paketleme (IntegrationsController da kullanır) ----------

    private sealed class EdmCredentials
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        /// <summary>Opsiyonel servis adresi; boşsa varsayılan (canlı/test) uç kullanılır.</summary>
        [JsonPropertyName("endpoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Endpoint { get; set; }
    }

    /// <summary>
    /// Kullanıcı adı + şifre + opsiyonel servis adresini ApiKeyEncrypted alanında saklanacak
    /// JSON'a paketler: {"username":"...","password":"...","endpoint":"..."?}. Boş endpoint yazılmaz.
    /// (Uyumsoft/Doğan ile aynı kalıp — sağlayıcıya özgü ayrı kolon açılmaz.)
    /// </summary>
    public static string PackCredentials(string username, string password, string? endpoint = null) =>
        JsonSerializer.Serialize(new EdmCredentials
        {
            Username = username,
            Password = password,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim()
        });

    /// <summary>
    /// Saklanan JSON'dan kullanıcı adı + şifre + opsiyonel servis adresini çözer.
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
            var creds = JsonSerializer.Deserialize<EdmCredentials>(json, CredentialJsonOptions);
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
                "EDM Bilişim kimlik bilgileri tanımlı değil. Lütfen entegrasyonu kullanıcı adı ve şifreyle yeniden kaydedin.");
        }

        // Uç çözümü: kimlikteki özel adres > test ucu > varsayılan canlı uç.
        var endpoint = ResolveEndpoint(customEndpoint, testEnv);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // Başarılı SOAPAction şablonu oturum boyunca korunur (ilk fault'ta bir kez değişir).
        var actionIndex = 0;
        string? sessionId = null;
        try
        {
            // 1) Login: her senkronda yeni oturum açılır (EDM oturumları kısa ömürlüdür).
            var login = await LoginAsync(client, endpoint, username, password, ct);
            if (login.Error is not null) return login.Error;
            sessionId = login.SessionId;
            actionIndex = login.ActionIndex;

            // 2) GetDocument: TYPE=INVOICE + DIRECTION=IN + tarih aralığı (bkz. sınıf yorumu, TODO-VERIFY).
            var envelope = BuildGetDocumentEnvelope(sessionId!, from, to);
            var (status, body, _) = await PostWithActionFallbackAsync(
                client, endpoint, GetDocumentOperation, envelope, actionIndex, ct);

            var fault = TryParseSoapFault(body);
            if (fault is not null)
            {
                _logger.LogWarning("EDM Bilişim SOAP Fault (GetDocument): {Fault}", fault);
                // Operasyon/parametre uyuşmazlığı dahil her fault'ta ham gövde mesaja eklenir
                // (canlıda kesin teşhis için — bkz. sınıf yorumu, şema belirsizliği).
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    $"EDM Bilişim servisi hata bildirdi: {fault} — Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
            }

            var httpError = MapErrorStatus(status, body);
            if (httpError is not null)
            {
                _logger.LogWarning("EDM Bilişim GetDocument HTTP hatası: {Status}", status);
                return httpError;
            }

            return ParseGetDocumentResponse(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient zaman aşımı (30 sn) TaskCanceledException olarak gelir.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "EDM Bilişim servisi yanıt vermedi (zaman aşımı). Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "EDM Bilişim servisine bağlanılamadı. Uç: {Endpoint}", endpoint);
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "EDM Bilişim servisine bağlanılamadı. Servis adresini ve internet bağlantınızı kontrol edip tekrar deneyin.",
                retryable: true);
        }
        catch (XmlException ex)
        {
            _logger.LogError(ex, "EDM Bilişim yanıtı XML olarak çözümlenemedi.");
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "EDM Bilişim'den beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        finally
        {
            // 3) Logout: oturum her durumda kapatılmaya çalışılır; hatası senkron sonucunu bozmaz.
            if (sessionId is not null)
                await TryLogoutAsync(client, endpoint, sessionId, actionIndex);
        }
    }

    // ---------- Uç çözümü + HTTP gönderim ----------

    private static string ResolveEndpoint(string? customEndpoint, bool testEnv)
    {
        // Kimlikte kayıtlı özel adres her zaman önceliklidir (test/farklı kurulum bu alanla seçilir).
        if (!string.IsNullOrWhiteSpace(customEndpoint)) return customEndpoint.Trim();
        return testEnv ? TestEndpoint : DefaultEndpoint;
    }

    /// <summary>
    /// Zarfı POST eder; SOAPAction adayları sırayla denenir. actionIndex'ten başlanır; yanıt
    /// Action/Contract uyuşmazlığı fault'u ise SONRAKİ adayla BİR kez tekrarlanır ve başarılı
    /// adayın indeksi döndürülür (oturum boyunca sonraki çağrılarda kullanılır).
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body, int ActionIndex)> PostWithActionFallbackAsync(
        HttpClient client, string endpoint, string operation, string envelope, int actionIndex, CancellationToken ct)
    {
        var (status, body) = await SendOnceAsync(client, endpoint, operation, envelope, actionIndex, ct);

        var fault = TryParseSoapFault(body);
        if (fault is not null && IsActionMismatchFault(fault) && actionIndex + 1 < SoapActionTemplates.Length)
        {
            var nextIndex = actionIndex + 1;
            _logger.LogWarning(
                "EDM Bilişim SOAPAction adayı tanınmadı ({Operation}, aday {Index}); sonraki adayla tekrarlanıyor: {Action}",
                operation, actionIndex, string.Format(CultureInfo.InvariantCulture, SoapActionTemplates[nextIndex], operation));
            (status, body) = await SendOnceAsync(client, endpoint, operation, envelope, nextIndex, ct);
            return (status, body, nextIndex);
        }

        return (status, body, actionIndex);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendOnceAsync(
        HttpClient client, string endpoint, string operation, string envelope, int actionIndex, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        var action = string.Format(CultureInfo.InvariantCulture, SoapActionTemplates[actionIndex], operation);
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, body);
    }

    /// <summary>
    /// Fault, gönderilen SOAPAction/sözleşmenin bu uçta tanınmadığını mı bildiriyor?
    /// (WCF: "ActionNotSupported" / "ContractFilter mismatch"; ASMX: "... was not recognized").
    /// Bu durumda ikinci SOAPAction adayı denenir.
    /// </summary>
    private static bool IsActionMismatchFault(string fault) =>
        fault.Contains("ActionNotSupported", StringComparison.OrdinalIgnoreCase) ||
        fault.Contains("ContractFilter", StringComparison.OrdinalIgnoreCase) ||
        fault.Contains("was not recognized", StringComparison.OrdinalIgnoreCase) ||
        fault.Contains("cannot be processed at the receiver", StringComparison.OrdinalIgnoreCase);

    // ---------- Login / Logout ----------

    private async Task<(string? SessionId, int ActionIndex, ProviderResult<List<ProviderInvoice>>? Error)> LoginAsync(
        HttpClient client, string endpoint, string username, string password, CancellationToken ct)
    {
        // Login öncesi oturum yokken SESSION_ID "-1" gönderilir (EDM dokümantasyon kalıbı).
        var operation = new XElement(SvcNs + LoginOperation,
            new XElement("REQUEST_HEADER",
                new XElement("SESSION_ID", "-1"),
                new XElement("APPLICATION_NAME", ApplicationName),
                new XElement("CHANNEL_NAME", string.Empty),
                new XElement("COMPRESSED", "N")),
            new XElement("USER_NAME", username),
            new XElement("PASSWORD", password));

        var (status, body, actionIndex) = await PostWithActionFallbackAsync(
            client, endpoint, LoginOperation, BuildEnvelope(operation), actionIndex: 0, ct);

        var fault = TryParseSoapFault(body);
        if (fault is not null)
        {
            _logger.LogWarning("EDM Bilişim SOAP Fault (Login): {Fault}", fault);
            return (null, actionIndex, ProviderResult<List<ProviderInvoice>>.Fail(
                $"EDM Bilişim oturumu açılamadı: {fault} — Ham yanıt: {Truncate(body, RawBodySnippetLength)}"));
        }

        var httpError = MapErrorStatus(status, body);
        if (httpError is not null)
        {
            _logger.LogWarning("EDM Bilişim Login HTTP hatası: {Status}", status);
            return (null, actionIndex, httpError);
        }

        var doc = XDocument.Parse(body);
        var sessionId = FindLeafValue(doc.Root, "SESSION_ID");
        if (!string.IsNullOrWhiteSpace(sessionId) && sessionId.Trim() != "-1")
        {
            // Bazı Cybersoft türevleri başarıda da ERROR_TYPE{ERROR_CODE:0} döndürür;
            // geçerli (-1 dışı) SESSION_ID dolu ise oturum açık sayılır.
            _logger.LogInformation("EDM Bilişim oturumu açıldı ({Endpoint}).", endpoint);
            return (sessionId.Trim(), actionIndex, null);
        }

        // Oturum yok → ERROR_TYPE'tan Türkçe mesaj (ERROR_SHORT_DES öncelikli) çıkarılır.
        var shortDes = FindLeafValue(doc.Root, "ERROR_SHORT_DES");
        var longDes = FindLeafValue(doc.Root, "ERROR_LONG_DES");
        var code = FindLeafValue(doc.Root, "ERROR_CODE");
        var message = shortDes ?? longDes;

        _logger.LogWarning("EDM Bilişim login başarısız. Kod: {Code}, Mesaj: {Message}", code, message);

        if (message is not null && LooksLikeAuthError(message))
        {
            return (null, actionIndex, ProviderResult<List<ProviderInvoice>>.Fail(
                "EDM Bilişim kullanıcı adı veya şifresi hatalı."));
        }

        var text = message is not null
            ? $"EDM Bilişim oturumu açılamadı: {message}"
            : $"EDM Bilişim oturumu açılamadı (SESSION_ID dönmedi). Ham yanıt: {Truncate(body, RawBodySnippetLength)}";
        return (null, actionIndex, ProviderResult<List<ProviderInvoice>>.Fail(text));
    }

    /// <summary>Oturumu kapatır; her tür hata yutulur (yalnız loglanır).</summary>
    private async Task TryLogoutAsync(HttpClient client, string endpoint, string sessionId, int actionIndex)
    {
        try
        {
            var operation = new XElement(SvcNs + LogoutOperation, MakeSessionHeader(sessionId));
            // ct verilmez: senkron iptal edilse bile oturum kapatma denenir (kısa istek).
            await SendOnceAsync(client, endpoint, LogoutOperation, BuildEnvelope(operation),
                actionIndex, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EDM Bilişim logout hatası (yutuldu).");
        }
    }

    // ---------- SOAP zarfı üretimi ----------

    /// <summary>Login sonrası tüm çağrıların REQUEST_HEADER'ı (COMPRESSED=N: sıkıştırmasız yanıt).</summary>
    private static XElement MakeSessionHeader(string sessionId) =>
        new("REQUEST_HEADER",
            new XElement("SESSION_ID", sessionId),
            new XElement("APPLICATION_NAME", ApplicationName),
            new XElement("CHANNEL_NAME", string.Empty),
            new XElement("COMPRESSED", "N"));

    /// <summary>
    /// TODO-VERIFY: GetDocument parametre şeması CANLIDA DOĞRULANMADI. Birincil deneme:
    /// SEARCH_KEY{TYPE:"INVOICE", DIRECTION:"IN", START_DATE, END_DATE, READ_INCLUDED:true,
    /// DOCUMENT_CONTENT_TYPE:"XML"}. Sunucu SEARCH_KEY/alan adlarını tanımazsa fault + ham
    /// gövde döner; canlı teşhiste DocumentQuery / GetDocumentList adayları değerlendirilecek.
    /// </summary>
    private static string BuildGetDocumentEnvelope(string sessionId, DateTime from, DateTime to)
    {
        var operation = new XElement(SvcNs + GetDocumentOperation,
            MakeSessionHeader(sessionId),
            new XElement("SEARCH_KEY",
                new XElement("TYPE", "INVOICE"),
                // Gelen kutusu: EDM türevlerinde DIRECTION=IN ya da RECEIVED ayrımı görülür;
                // birincil deneme IN'dir (Doğan/Cybersoft ile aynı değer).
                new XElement("DIRECTION", "IN"),
                new XElement("START_DATE", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement("END_DATE", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement("READ_INCLUDED", "true"),
                new XElement("DOCUMENT_CONTENT_TYPE", "XML")));

        return BuildEnvelope(operation);
    }

    /// <summary>
    /// SOAP 1.1 zarfı. Yalnız operasyon elemanı servis ad alanındadır (parametre çocukları
    /// niteliksiz) — Doğan adaptörüyle aynı yerleşim. Ad alanı yanlışsa sunucu Fault döner
    /// ve ham gövde hata mesajına eklenir (bkz. sınıf yorumu).
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
                "EDM Bilişim kullanıcı adı veya şifresi hatalı.");
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "EDM Bilişim servisi şu anda meşgul. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }

        if ((int)status >= 500)
        {
            // 500'lerde gövde çoğu zaman fault taşır; taşımıyorsa da teşhis için ham gövde loglanır.
            _logger.LogWarning("EDM Bilişim HTTP {Status}. Ham yanıt (ilk {Len}): {Body}",
                (int)status, RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "EDM Bilişim tarafında geçici bir sorun oluştu. Lütfen daha sonra tekrar deneyin.",
                retryable: true);
        }

        return ProviderResult<List<ProviderInvoice>>.Fail(
            $"EDM Bilişim isteği başarısız oldu (HTTP {(int)status}). Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
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

    /// <summary>
    /// GetDocument yanıtını ayrıştırır. TODO-VERIFY: yanıt şeması canlıda doğrulanmadı —
    /// belge kayıtları ad alanından bağımsız DOCUMENT / INVOICE elemanlarında aranır;
    /// hiçbiri yoksa ERROR_TYPE denetlenir, o da yoksa ham gövdeyle hata dönülür.
    /// </summary>
    private ProviderResult<List<ProviderInvoice>> ParseGetDocumentResponse(string body)
    {
        var doc = XDocument.Parse(body);

        // Belge kayıtları: EDM türevlerinde DOCUMENT, Cybersoft kökeninde INVOICE görülür.
        var documentElements = doc.Descendants()
            .Where(e => e.Name.LocalName is "DOCUMENT" or "INVOICE")
            .ToList();

        if (documentElements.Count == 0)
        {
            // Belge yoksa iki olasılık: gerçekten boş aralık YA DA servis ERROR_TYPE ile hata
            // bildirdi (ör. oturum düştü). ERROR_CODE 0/boş değilse hata kabul edilir.
            var code = FindLeafValue(doc.Root, "ERROR_CODE");
            var message = FindLeafValue(doc.Root, "ERROR_SHORT_DES") ?? FindLeafValue(doc.Root, "ERROR_LONG_DES");
            if (!string.IsNullOrWhiteSpace(message) &&
                !string.IsNullOrWhiteSpace(code) && code.Trim() != "0")
            {
                _logger.LogWarning("EDM Bilişim GetDocument hata döndü. Kod: {Code}, Mesaj: {Message}", code, message);
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    $"EDM Bilişim servisi hata bildirdi: {message}");
            }

            // Yanıt hiç tanınmıyorsa (ne DOCUMENT/INVOICE ne Response/ERROR_TYPE benzeri kök)
            // ham gövdeyle uyarılır — canlı teşhis turu için (bkz. sınıf yorumu).
            var looksLikeResponse = doc.Descendants().Any(e =>
                e.Name.LocalName.Contains("Response", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Contains("Result", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName == "ERROR_TYPE");
            if (!looksLikeResponse)
            {
                _logger.LogError("EDM Bilişim yanıtı tanınamadı. Ham yanıt (ilk {Len}): {Body}",
                    RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    $"EDM Bilişim'den beklenmeyen bir yanıt alındı. Ham yanıt: {Truncate(body, RawBodySnippetLength)}",
                    retryable: true);
            }

            return ProviderResult<List<ProviderInvoice>>.Ok(new List<ProviderInvoice>());
        }

        var invoices = new List<ProviderInvoice>();
        // Mükerrer koruması: aynı UUID yanıtta birden fazla kez dönerse ilki alınır
        // (DB unique index'ine ve EInvoiceSyncService kontrolüne ek, sağlayıcı içi ilk savunma hattı).
        var seenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateCount = 0;

        foreach (var element in documentElements)
        {
            var invoice = TryMapDocument(element);
            if (invoice is null) continue;

            if (seenUuids.Add(invoice.ExternalUuid)) invoices.Add(invoice);
            else duplicateCount++;
        }

        if (invoices.Count == 0)
        {
            // Belge elemanları var ama hiçbirinden UUID çıkarılamadı → şema beklenenden farklı;
            // ham gövdeyle hata dönülür (canlı teşhis turu için).
            _logger.LogError("EDM Bilişim belge kayıtlarından UUID çıkarılamadı. Ham yanıt (ilk {Len}): {Body}",
                RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
            return ProviderResult<List<ProviderInvoice>>.Fail(
                $"EDM Bilişim yanıtındaki belgeler tanınamadı (UUID bulunamadı). Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
        }

        if (duplicateCount > 0)
        {
            _logger.LogInformation(
                "EDM Bilişim senkronunda {Duplicates} mükerrer kayıt sağlayıcı içinde atlandı.", duplicateCount);
        }

        _logger.LogInformation("EDM Bilişim gelen fatura senkronu tamamlandı. Fatura: {Count}", invoices.Count);
        return ProviderResult<List<ProviderInvoice>>.Ok(invoices);
    }

    /// <summary>
    /// Tek belge kaydını toleranslı eşler: UUID/ID önce ÖZNİTELİKTE, yoksa aynı adlı (alt)
    /// elemanda aranır; başlık alanları HEADER çocuğundan (herhangi bir derinlikte) okunur.
    /// Tutarlar invariant culture ile, tarih yyyy-MM-dd biçiminde çözülür.
    /// TODO-VERIFY: alan adları canlı yanıtla doğrulanacak (Cybersoft türevi varsayımı).
    /// </summary>
    private static ProviderInvoice? TryMapDocument(XElement document)
    {
        var uuid = GetField(document, "UUID", "DOCUMENT_UUID", "ETTN");
        if (string.IsNullOrWhiteSpace(uuid)) return null; // UUID'siz belge yazılmaz

        var taxExclusive = ParseDecimalValue(GetField(document, "TAX_EXCLUSIVE_AMOUNT", "TAXEXCLUSIVEAMOUNT"));
        var taxInclusive = ParseDecimalValue(GetField(document, "TAX_INCLUSIVE_AMOUNT", "TAXINCLUSIVEAMOUNT"));
        var payable = ParseDecimalValue(GetField(document, "PAYABLE_AMOUNT", "PAYABLEAMOUNT"));

        // Eksik toplamlar birbirinden tamamlanır (başlık yanıtında üçü de genelde vardır).
        if (payable == 0m && taxInclusive != 0m) payable = taxInclusive;
        if (taxInclusive == 0m && payable != 0m) taxInclusive = payable;

        var taxTotal = ParseDecimalValue(GetField(document, "TAX_TOTAL", "TAXTOTAL"));
        if (taxTotal == 0m)
        {
            taxTotal = Math.Round(taxInclusive - taxExclusive, 2);
            if (taxTotal < 0m) taxTotal = 0m;
        }

        var issueDate = ParseDateValue(GetField(document, "ISSUE_DATE", "ISSUEDATE", "DOCUMENT_DATE"))
                        ?? DateTime.UtcNow.Date;

        // Gelen faturada karşı taraf (satıcı): SENDER/SUPPLIER — düz VKN değeri ya da içinde
        // VKN/IDENTIFIER çocuğu taşıyan kapsayıcı olabilir; iki biçim de tolere edilir.
        var senderTaxId = SanitizeTaxId(
            GetField(document, "SENDER_IDENTIFIER", "SUPPLIER_VKN", "SENDER_VKN")
            ?? GetNestedField(document, new[] { "SUPPLIER", "SENDER", "FROM" }, new[] { "VKN", "IDENTIFIER", "TCKN", "PARTY_IDENTIFICATION" }));
        var senderTitle = GetField(document, "SUPPLIER_TITLE", "SENDER_TITLE", "SENDER_NAME", "SUPPLIER_NAME", "TITLE")
                          ?? GetNestedField(document, new[] { "SUPPLIER", "SENDER", "FROM" }, new[] { "TITLE", "NAME", "ALIAS" })
                          ?? string.Empty;

        var currency = GetField(document, "CURRENCY_CODE", "DOCUMENT_CURRENCY_CODE", "CURRENCY");

        return new ProviderInvoice
        {
            // DB kolon sınırları aşılmasın diye alanlar sessizce kırpılır (ClampLength).
            ExternalUuid = ClampLength(uuid, 64),
            // ID = GİB fatura numarası (ör. ABC2024000000123), UUID = ETTN (Cybersoft kalıbı).
            InvoiceNumber = ClampLength(GetField(document, "ID", "DOCUMENT_ID", "INVOICE_ID", "DOCUMENT_NO") ?? string.Empty, 30),
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
    /// Başlık yanıtında KDV kırılımı yoktur → başlık toplamlarından TEK özet satır üretilir;
    /// KDV oranı orandan geri hesaplanıp 1/8/10/18/20'den EN YAKININA yuvarlanır.
    /// TODO-VERIFY: gerçek kalemler belge içeriğinin (UBL XML) ayrıştırılmasıyla çıkarılabilir.
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
