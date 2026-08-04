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
/// eLogo (Logo özel entegratör) e-Fatura SOAP adaptörü — PostBox servisi.
///
/// - Canlı uç: https://pb.elogo.com.tr/PostBoxService.svc
///   Test uç:  https://pbtest.elogo.com.tr/PostBoxService.svc
///   Kimlik JSON'undaki opsiyonel "endpoint" alanı ucu EZER (Uyumsoft/Doğan ile aynı kalıp).
/// - eLogo PostBox'ta OTURUM YOKTUR: kimlik HER çağrıda gövdede gönderilir.
/// - TODO-VERIFY: GetDocumentList parametre şeması CANLIDA DOĞRULANMADI. Tek dosyada iki
///   istek şablonu sırayla denenir:
///   (a) birincil: userName / password / documentType:"EINVOICE" / queryStartDate / queryEndDate
///   (b) fault ya da tanınmayan-boş yanıt dönerse alternatif alan adlarıyla ikinci şablon:
///       AppStr{Username, Password} + docType:1 + startDate / endDate.
///   İkisi de başarısızsa ham gövde hata mesajına eklenir (canlı teşhis turu — Uyumsoft'ta bu
///   metodolojiyle canlıda 47 fatura çekildi).
/// - TODO-VERIFY: SOAPAction adayı "http://tempuri.org/IPostBoxService/{Op}" ile başlanır;
///   ad alanı tempuri varsayımıdır. Yanlışsa fault + ham gövdeyle düzeltilecek.
/// - Yanıtta belge listesi: UUID/ENVELOPEID/DOCID benzeri alanlar LocalName toleranslı okunur.
///   Belge içeriği base64 UBL ise ŞİMDİLİK ayrıştırılmaz — başlık alanları (uuid, no, tarih,
///   gönderen VKN/unvan, tutarlar) yakalanabiliyorsa yakalanır; hiç yakalanamıyorsa senkron
///   hatasında ham yanıt gösterilir (canlı teşhis turu için).
/// - KDV kırılımı dönmediğinden başlık toplamlarından TEK özet satır üretilir
///   (matrah + KDV; oran 1/8/10/18/20'den en yakınına yuvarlanır).
/// - Mükerrer UUID'ler HashSet ile sağlayıcı içinde ayıklanır.
/// </summary>
public class ElogoProvider : IEInvoiceProvider
{
    public const string HttpClientName = "elogo";

    /// <summary>Varsayılan CANLI uç (eLogo PostBox resmi adresi).</summary>
    public const string DefaultEndpoint = "https://pb.elogo.com.tr/PostBoxService.svc";
    private const string TestEndpoint = "https://pbtest.elogo.com.tr/PostBoxService.svc";

    private const string ListOperation = "GetDocumentList";

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// TODO-VERIFY: Gövde ad alanı — CANLIDA DOĞRULANMADI (WCF .svc varsayılanı tempuri).
    /// Yanıt ayrıştırma bu değerden bağımsızdır; yalnız giden zarfta kullanılır.
    /// </summary>
    private const string ServiceNs = "http://tempuri.org/";
    private static readonly XNamespace SvcNs = ServiceNs;

    /// <summary>
    /// TODO-VERIFY: SOAPAction — WCF sözleşme adı varsayımı IPostBoxService.
    /// Yanlışsa sunucu ActionNotSupported fault'u döner; ham gövdeyle düzeltilecek.
    /// </summary>
    private const string SoapActionTemplate = "http://tempuri.org/IPostBoxService/{0}";

    /// <summary>Yanıttaki hata mesajlarına eklenen ham gövde üst sınırı (curl-driven debugging).</summary>
    private const int RawBodySnippetLength = 2000;

    /// <summary>KDV oranı geri hesabında yuvarlanan geçerli oranlar.</summary>
    private static readonly decimal[] KnownVatRates = { 1m, 8m, 10m, 18m, 20m };

    private static readonly JsonSerializerOptions CredentialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ElogoProvider> _logger;

    public ElogoProvider(IHttpClientFactory httpClientFactory, ILogger<ElogoProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.Elogo;

    // ---------- Kimlik paketleme (IntegrationsController da kullanır) ----------

    private sealed class ElogoCredentials
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
        JsonSerializer.Serialize(new ElogoCredentials
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
            var creds = JsonSerializer.Deserialize<ElogoCredentials>(json, CredentialJsonOptions);
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
                "eLogo kimlik bilgileri tanımlı değil. Lütfen entegrasyonu kullanıcı adı ve şifreyle yeniden kaydedin.");
        }

        // Uç çözümü: kimlikteki özel adres > test ucu > varsayılan canlı uç.
        var endpoint = ResolveEndpoint(customEndpoint, testEnv);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            // (a) Birincil şablon: userName/password + documentType:"EINVOICE" + queryStartDate/queryEndDate.
            var primary = await TryFetchAsync(
                client, endpoint, BuildPrimaryEnvelope(username, password, from, to), "birincil", ct);
            if (primary.Result is not null) return primary.Result;

            // (b) Alternatif şablon: AppStr{Username,Password} + docType:1 + startDate/endDate
            // (TODO-VERIFY — birincil şablon fault/tanınmayan yanıt döndüğünde bir kez denenir).
            _logger.LogWarning(
                "eLogo birincil GetDocumentList şablonu başarısız ({Reason}); alternatif şablon deneniyor.",
                primary.FailReason);
            var fallback = await TryFetchAsync(
                client, endpoint, BuildAlternateEnvelope(username, password, from, to), "alternatif", ct);
            if (fallback.Result is not null) return fallback.Result;

            // İki şablon da başarısız → kimlik hatası öncelikli, değilse ham gövdeyle teşhis hatası.
            if (LooksLikeAuthError(primary.FailReason) || LooksLikeAuthError(fallback.FailReason))
            {
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    "eLogo kullanıcı adı veya şifresi hatalı.");
            }

            return ProviderResult<List<ProviderInvoice>>.Fail(
                $"eLogo servisinden belge listesi alınamadı. Birincil deneme: {primary.FailReason} — " +
                $"Alternatif deneme: {fallback.FailReason} — Ham yanıt: {Truncate(fallback.RawBody, RawBodySnippetLength)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient zaman aşımı (30 sn) TaskCanceledException olarak gelir.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "eLogo servisi yanıt vermedi (zaman aşımı). Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "eLogo servisine bağlanılamadı. Uç: {Endpoint}", endpoint);
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "eLogo servisine bağlanılamadı. Servis adresini ve internet bağlantınızı kontrol edip tekrar deneyin.",
                retryable: true);
        }
        catch (XmlException ex)
        {
            _logger.LogError(ex, "eLogo yanıtı XML olarak çözümlenemedi.");
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "eLogo'dan beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
    }

    // ---------- Şablon deneme akışı ----------

    /// <summary>
    /// Tek istek şablonunu gönderir ve yanıtı değerlendirir. Başarılıysa (belge listesi ya da
    /// geçerli-boş yanıt) Result dolu döner; fault/tanınmayan yanıtta Result null + FailReason
    /// dolu döner ve çağıran diğer şablonu dener. Ağ/zaman aşımı istisnaları çağırana fırlar.
    /// </summary>
    private async Task<(ProviderResult<List<ProviderInvoice>>? Result, string FailReason, string RawBody)>
        TryFetchAsync(HttpClient client, string endpoint, string envelope, string templateName, CancellationToken ct)
    {
        var (status, body) = await PostEnvelopeAsync(client, endpoint, envelope, ct);

        var fault = TryParseSoapFault(body);
        if (fault is not null)
        {
            _logger.LogWarning("eLogo SOAP Fault ({Template} şablon): {Fault}", templateName, fault);
            return (null, $"SOAP Fault: {fault}", body);
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (null, "kimlik doğrulanamadı (HTTP 401/403)", body);

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return (ProviderResult<List<ProviderInvoice>>.Fail(
                "eLogo servisi şu anda meşgul. Lütfen daha sonra tekrar deneyin.", retryable: true), "", body);
        }

        if (status != HttpStatusCode.OK)
        {
            _logger.LogWarning("eLogo HTTP {Status} ({Template} şablon). Ham yanıt (ilk {Len}): {Body}",
                (int)status, templateName, RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
            return (null, $"HTTP {(int)status}", body);
        }

        return ParseListResponse(body, templateName);
    }

    private static string ResolveEndpoint(string? customEndpoint, bool testEnv)
    {
        // Kimlikte kayıtlı özel adres her zaman önceliklidir (test/farklı kurulum bu alanla seçilir).
        if (!string.IsNullOrWhiteSpace(customEndpoint)) return customEndpoint.Trim();
        return testEnv ? TestEndpoint : DefaultEndpoint;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostEnvelopeAsync(
        HttpClient client, string endpoint, string envelope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        var action = string.Format(CultureInfo.InvariantCulture, SoapActionTemplate, ListOperation);
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, body);
    }

    // ---------- SOAP zarfı üretimi ----------

    /// <summary>
    /// (a) Birincil GetDocumentList şablonu — TODO-VERIFY: alan adları canlıda doğrulanmadı.
    /// Oturum yoktur; kimlik her çağrıda gövdededir.
    /// </summary>
    private static string BuildPrimaryEnvelope(string username, string password, DateTime from, DateTime to)
    {
        var operation = new XElement(SvcNs + ListOperation,
            new XElement(SvcNs + "userName", username),
            new XElement(SvcNs + "password", password),
            new XElement(SvcNs + "documentType", "EINVOICE"),
            new XElement(SvcNs + "queryStartDate",
                from.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(SvcNs + "queryEndDate",
                to.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));

        return BuildEnvelope(operation);
    }

    /// <summary>
    /// (b) Alternatif GetDocumentList şablonu — TODO-VERIFY: bazı PostBox kurulumları kimliği
    /// AppStr{Username,Password} kapsayıcısında, belge türünü sayısal docType (1=EINVOICE) ve
    /// tarihleri startDate/endDate adlarıyla bekler.
    /// </summary>
    private static string BuildAlternateEnvelope(string username, string password, DateTime from, DateTime to)
    {
        var operation = new XElement(SvcNs + ListOperation,
            new XElement(SvcNs + "AppStr",
                new XElement(SvcNs + "Username", username),
                new XElement(SvcNs + "Password", password)),
            new XElement(SvcNs + "docType", "1"),
            new XElement(SvcNs + "startDate",
                from.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(SvcNs + "endDate",
                to.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));

        return BuildEnvelope(operation);
    }

    /// <summary>SOAP 1.1 zarfı — operasyon ve parametreler tempuri ad alanındadır (WCF varsayılanı).</summary>
    private static string BuildEnvelope(XElement operation)
    {
        var envelope = new XElement(SoapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tem", SvcNs),
            new XElement(SoapNs + "Body", operation));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), envelope)
            .ToString(SaveOptions.DisableFormatting);
    }

    // ---------- Fault ayrıştırma ----------

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

    private static bool LooksLikeAuthError(string message) =>
        message.Contains("kullanıcı", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("şifre", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("parola", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("username", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase);

    // ---------- Yanıt ayrıştırma ----------

    /// <summary>
    /// GetDocumentList yanıtını ayrıştırır. TODO-VERIFY: yanıt şeması canlıda doğrulanmadı —
    /// belge kayıtları LocalName toleranslı Document/DocumentInfo/Item benzeri elemanlarda
    /// aranır; hiç kayıt/Response izi yoksa şablon başarısız sayılır (çağıran diğer şablonu
    /// dener, ikisi de olmazsa ham gövde hataya eklenir).
    /// </summary>
    private (ProviderResult<List<ProviderInvoice>>? Result, string FailReason, string RawBody)
        ParseListResponse(string body, string templateName)
    {
        var doc = XDocument.Parse(body);

        // Belge kayıt adayları: PostBox türevlerinde Document / DocumentInfo / Documents altındaki
        // Item benzeri elemanlar görülür. UUID/ENVELOPEID/DOCID taşıyan elemanlar kayıt sayılır.
        var candidates = doc.Descendants()
            .Where(e => e.Name.LocalName is "Document" or "DocumentInfo" or "DOCUMENT" or "Item" or "anyType")
            .Where(e => GetField(e, "UUID", "Uuid", "EnvelopeId", "ENVELOPEID", "DocId", "DOCID", "DocumentId", "ETTN") is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            // Kayıt yok: yanıt geçerli bir Response/Result iziyse boş aralık kabul edilir;
            // hiç tanınmıyorsa şablon başarısız sayılır (çağıran alternatifi dener).
            var looksLikeResponse = doc.Descendants().Any(e =>
                e.Name.LocalName.Contains(ListOperation + "Response", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Contains(ListOperation + "Result", StringComparison.OrdinalIgnoreCase));

            if (looksLikeResponse)
            {
                _logger.LogInformation("eLogo gelen fatura senkronu tamamlandı ({Template} şablon). Fatura: 0",
                    templateName);
                return (ProviderResult<List<ProviderInvoice>>.Ok(new List<ProviderInvoice>()), "", body);
            }

            _logger.LogWarning("eLogo yanıtı tanınamadı ({Template} şablon). Ham yanıt (ilk {Len}): {Body}",
                templateName, RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
            return (null, "yanıt tanınamadı (belge listesi/Response izi yok)", body);
        }

        var invoices = new List<ProviderInvoice>();
        // Mükerrer koruması: aynı UUID yanıtta birden fazla kez dönerse ilki alınır
        // (DB unique index'ine ve EInvoiceSyncService kontrolüne ek, sağlayıcı içi ilk savunma hattı).
        var seenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateCount = 0;

        foreach (var element in candidates)
        {
            var invoice = TryMapDocument(element);
            if (invoice is null) continue;

            if (seenUuids.Add(invoice.ExternalUuid)) invoices.Add(invoice);
            else duplicateCount++;
        }

        if (invoices.Count == 0)
        {
            // UUID'li kayıt adayı vardı ama hiçbiri eşlenemedi → ham gövdeyle teşhise düşülür.
            return (null, "belge kayıtları eşlenemedi (başlık alanları çıkarılamadı)", body);
        }

        if (duplicateCount > 0)
        {
            _logger.LogInformation(
                "eLogo senkronunda {Duplicates} mükerrer kayıt sağlayıcı içinde atlandı.", duplicateCount);
        }

        _logger.LogInformation("eLogo gelen fatura senkronu tamamlandı ({Template} şablon). Fatura: {Count}",
            templateName, invoices.Count);
        return (ProviderResult<List<ProviderInvoice>>.Ok(invoices), "", body);
    }

    /// <summary>
    /// Tek belge kaydını toleranslı eşler (öznitelik + yaprak eleman, LocalName bazlı).
    /// TODO-VERIFY: alan adları canlı yanıtla doğrulanacak. Belge içeriği base64 UBL ise
    /// ŞİMDİLİK ayrıştırılmaz — yalnız başlık alanları yakalanır; tutar alanları dönmüyorsa
    /// 0 yazılır (kalem detayı canlı teşhis turundan sonra UBL'den çıkarılacak).
    /// </summary>
    private static ProviderInvoice? TryMapDocument(XElement document)
    {
        var uuid = GetField(document, "UUID", "Uuid", "ETTN", "EnvelopeId", "ENVELOPEID", "DocId", "DOCID", "DocumentId");
        if (string.IsNullOrWhiteSpace(uuid)) return null; // UUID'siz belge yazılmaz

        var taxExclusive = ParseDecimalValue(GetField(document,
            "TaxExclusiveAmount", "TAX_EXCLUSIVE_AMOUNT", "LineExtensionAmount"));
        var taxTotal = ParseDecimalValue(GetField(document, "TaxTotal", "TAX_TOTAL", "TaxAmount"));
        var payable = ParseDecimalValue(GetField(document, "PayableAmount", "PAYABLE_AMOUNT", "TaxInclusiveAmount"));

        // Eksik toplamlar birbirinden tamamlanır.
        if (payable == 0m && taxExclusive != 0m) payable = Math.Round(taxExclusive + taxTotal, 2);
        if (taxExclusive == 0m && payable != 0m && taxTotal != 0m)
            taxExclusive = Math.Round(payable - taxTotal, 2);

        var issueDate = ParseDateValue(GetField(document,
            "IssueDate", "ISSUE_DATE", "DocDate", "DOCDATE", "DocumentDate", "CreateDate")) ?? DateTime.UtcNow.Date;

        var senderTaxId = SanitizeTaxId(
            GetField(document, "SenderVkn", "SENDER_VKN", "SenderTaxId", "SourceId", "SourceTcknVkn", "VknTckn")
            ?? GetNestedField(document, new[] { "Sender", "SENDER", "Source" }, new[] { "Vkn", "Identifier", "Tckn", "TaxNumber" }));
        var senderTitle = GetField(document, "SenderTitle", "SENDER_TITLE", "SenderName", "SourceTitle", "SourceUrn", "Title")
                          ?? GetNestedField(document, new[] { "Sender", "SENDER", "Source" }, new[] { "Title", "Name", "Alias" })
                          ?? string.Empty;

        var currency = GetField(document, "CurrencyCode", "DocumentCurrencyCode", "CURRENCY_CODE", "Currency");

        return new ProviderInvoice
        {
            // DB kolon sınırları aşılmasın diye alanlar sessizce kırpılır (ClampLength).
            ExternalUuid = ClampLength(uuid, 64),
            InvoiceNumber = ClampLength(
                GetField(document, "DocumentNo", "DocNo", "DOCNO", "InvoiceNumber", "InvoiceId", "ID") ?? string.Empty, 30),
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
    /// Liste yanıtında KDV kırılımı yoktur → başlık toplamlarından TEK özet satır üretilir;
    /// KDV oranı orandan geri hesaplanıp 1/8/10/18/20'den EN YAKININA yuvarlanır.
    /// TODO-VERIFY: gerçek kalemler base64 UBL belge içeriğinden çıkarılacak (şimdilik girilmez).
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
    /// Kapsayıcı eleman (ör. Sender) içindeki alt alanı okur: containerNames'ten bulunan İLK
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
