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
/// Uyumsoft e-Fatura SOAP adaptörü (özel entegratör) — BasicIntegration protokolü.
///
/// Protokol, canlıda fatura kesmiş ÇALIŞAN bir istemciden (kullanıcının eski projesi)
/// 30.07.2026'da birebir doğrulanmıştır:
/// - Uç: http://edonusumapi.uyum.com.tr/Services/BasicIntegration — DİKKAT: bu ortam HTTP'dir
///   ve hesaplar genellikle bu ortamda tanımlıdır. efatura.uyumsoft.com.tr FARKLI bir ortamdır
///   (bazı hesaplar oradadır) → kimlik JSON'undaki opsiyonel "endpoint" alanıyla seçilir.
/// - SOAPAction: "http://tempuri.org/IBasicIntegration/{Operasyon}" (SendInvoice, GetAlias,
///   GetOutboxInvoiceView, GetOutboxInvoicePdf, TestConnection, CancelEArchiveInvoice bu
///   kalıpla çalışıyor).
/// - Kimlik: WS-Security YOKTUR; gövdede İLK eleman olarak
///   &lt;userInfo Username="..." Password="..."/&gt; (tempuri ad alanı) gönderilir.
/// - Zarf: SOAP 1.1; Body → &lt;{Operasyon} xmlns="http://tempuri.org/"&gt;&lt;userInfo/&gt;
///   {parametreler}&lt;/{Operasyon}&gt;. Parametreler basit camelCase elemanlardır
///   (ör. &lt;startDate&gt;2024-01-01T00:00:00&lt;/startDate&gt;&lt;pageIndex&gt;1&lt;/pageIndex&gt;).
/// - Yanıt (CANLI GetInboxInvoiceList yanıtından doğrulandı, 31.07.2026):
///   {Operasyon}Result üzerinde IsSucceded/Message ÖZNİTELİKLERİ. Value elemanı
///   PageIndex/PageSize/TotalCount/TotalPages ÖZNİTELİKLERİNİ taşır; fatura kayıtları
///   Value altındaki Items elemanlarıdır ve alanlar ÖZNİTELİK DEĞİL ÇOCUK ELEMANDIR
///   (tempuri default ns). DİKKAT — adlandırma TERSTİR: InvoiceId = GİB FATURA NUMARASI
///   (ör. COS2024000000984), DocumentId = UUID/GUID (ETTN). KDV kırılımı
///   Vat1/Vat8/Vat10/Vat18/Vat20 + VatXTaxableAmount elemanlarıyla döner.
///   View fallback'inde InvoiceInfo kayıtları alanları öznitelik olarak taşıyabilir
///   (ETTN="..." InvoiceNumber="...") — ayrıştırıcı iki biçimi de tolere eder.
/// - GetInboxInvoiceList sunucuda tanınmazsa (ActionNotSupported / "was not recognized" /
///   ContractFilter) aynı uca GetInboxInvoiceView ile düşülür (basit
///   startDate/endDate/pageIndex/pageSize parametreleri).
/// - https ilk denemede kurulamazsa (SSL/bağlantı hatası) AYNI istek bir kez http:// ile
///   denenir (edonusumapi ortamı yalnızca HTTP sunabilir); başarılı şema loglanır.
///
/// e-SMM (Serbest Meslek Makbuzu) — kimlikteki "esmm":true bayrağıyla açılır:
/// - Serbest meslek mensubu mükelleflerin makbuzları BasicIntegration'da DEĞİL, aynı
///   sunucudaki AYRI VoucherIntegration servisindedir (uç: .../Services/VoucherIntegration;
///   SOAPAction sözleşmesi IVoucherIntegration). Kimlik yöntemi ve zarf kalıbı birebir aynıdır.
/// - TODO-VERIFY: VoucherIntegration operasyon adları canlıda HENÜZ doğrulanmadı — aday
///   zinciri (GetOutboxVoucherList → GetVoucherList → GetOutboxVoucherView →
///   GetInboxVoucherList) sırayla denenir; op-tanınmadı fault'unda sıradakine geçilir,
///   tümü düşerse ham yanıt Türkçe hata mesajına eklenir (curl ile doğrulama için).
/// - e-SMM makbuzu mükellefin DÜZENLEDİĞİ gelir belgesidir → giden kutusu (Outbox) birincil
///   adaydır; kayıtlar SenderTitle " (e-SMM)" eki ve satır adı "e-SMM Makbuzu — " önekiyle
///   işaretlenir (LucaExcelExporter gelir fişi kalıbını bu önekten tanır).
/// </summary>
public class UyumsoftProvider : IEInvoiceProvider
{
    public const string HttpClientName = "uyumsoft";

    /// <summary>
    /// Varsayılan canlı uç (edonusumapi ortamı). Hesap farklı bir ortamdaysa
    /// (ör. efatura.uyumsoft.com.tr) kimlikteki opsiyonel "endpoint" alanı önceliklidir.
    /// </summary>
    // CANLI TESTLE DOĞRULANDI (31.07.2026): edonusumapi ortamı HTTPS'te servis sunmuyor
    // (https 404 döner — bağlantı hatası olmadığından otomatik http denemesi tetiklenmez).
    // Çalışan istemcinin kullandığı gibi varsayılan HTTP'dir; farklı ortamdaki hesaplar
    // entegrasyon ayarındaki "Servis Adresi" alanıyla kendi ucunu (https dahil) girebilir.
    public const string DefaultEndpoint = "http://edonusumapi.uyum.com.tr/Services/BasicIntegration";
    private const string TestEndpoint = "https://efatura-test.uyumsoft.com.tr/services/BasicIntegration";

    private const string ListOperation = "GetInboxInvoiceList";
    private const string ViewOperation = "GetInboxInvoiceView";

    // ---------- e-SMM (VoucherIntegration) ----------
    // SOAPAction sözleşme adları (BasicIntegration için canlıda doğrulandı; VoucherIntegration
    // aynı adlandırma kuralını izler).
    private const string BasicContract = "IBasicIntegration";
    private const string VoucherContract = "IVoucherIntegration";

    /// <summary>Uç "BasicIntegration" içermiyorsa kullanılacak varsayılan e-SMM servisi ucu.</summary>
    private const string DefaultVoucherEndpoint = "http://edonusumapi.uyum.com.tr/Services/VoucherIntegration";

    /// <summary>
    /// TODO-VERIFY: e-SMM makbuz listeleme operasyon adları canlıda HENÜZ doğrulanmadı.
    /// e-SMM makbuzu mükellefin DÜZENLEDİĞİ belge (gelir) olduğundan giden kutusu (Outbox)
    /// birincil adaydır. Adaylar sırayla denenir (GetInboxInvoiceList→View fallback kalıbıyla
    /// aynı mantık); "View" ile bitenler basit parametreli zarf, diğerleri query zarfı kullanır.
    /// Canlı curl doğrulamasından sonra liste tek ada indirilecek.
    /// </summary>
    private static readonly string[] VoucherOperationCandidates =
    {
        "GetOutboxVoucherList",
        "GetVoucherList",
        "GetOutboxVoucherView",
        "GetInboxVoucherList"
    };

    // Çalışan projede pageIndex 1'den başlar (GetOutboxInvoiceView örneği: pageIndex=1).
    private const int FirstPageIndex = 1;
    private const int PageSize = 100;
    private const int MaxPages = 50; // güvenlik sınırı: tek senkronda en fazla 5.000 fatura

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace TemNs = "http://tempuri.org/";
    // Yalnız YANIT ayrıştırmada kullanılır (xsi:nil'li elemanları atlamak için);
    // giden zarfta xsi bildirimi artık YOKTUR (canlıda gereksiz olduğu kanıtlandı).
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly JsonSerializerOptions CredentialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UyumsoftProvider> _logger;

    public UyumsoftProvider(IHttpClientFactory httpClientFactory, ILogger<UyumsoftProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.Uyumsoft;

    // ---------- Kimlik paketleme (IntegrationsController da kullanır) ----------

    private sealed class UyumsoftCredentials
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        /// <summary>Opsiyonel servis adresi; boşsa varsayılan uç kullanılır.</summary>
        [JsonPropertyName("endpoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Endpoint { get; set; }

        /// <summary>
        /// Mükellef e-SMM (serbest meslek makbuzu) mükellefi mi? true ise makbuzlar
        /// VoucherIntegration servisinden de çekilir. Alan yoksa false (geriye uyum).
        /// </summary>
        [JsonPropertyName("esmm")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Esmm { get; set; }

        /// <summary>
        /// e-Fatura / e-Arşiv gelen kutusu da çekilsin mi? Alan yoksa true (geriye uyum:
        /// mevcut kayıtlar e-fatura mükellefi). Yalnız e-SMM mükellefinde false yazılır.
        /// </summary>
        [JsonPropertyName("einvoice")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Einvoice { get; set; }
    }

    /// <summary>Kullanıcı adı + şifreyi ApiKeyEncrypted alanında saklanacak JSON'a paketler.</summary>
    public static string PackCredentials(string username, string password) =>
        PackCredentials(username, password, endpoint: null);

    /// <summary>
    /// Kullanıcı adı + şifre + opsiyonel servis adresini JSON'a paketler:
    /// {"username":"...","password":"...","endpoint":"..."?}. Boş endpoint yazılmaz.
    /// </summary>
    public static string PackCredentials(string username, string password, string? endpoint) =>
        PackCredentials(username, password, endpoint, esmm: false, einvoice: true);

    /// <summary>
    /// Kimliği e-SMM bayraklarıyla paketler:
    /// {"username","password","endpoint"?,"esmm":true?,"einvoice":false?}.
    /// Varsayılan durum (esmm=false, einvoice=true) hiç yazılmaz → eski kayıtlarla aynı JSON.
    /// </summary>
    public static string PackCredentials(
        string username, string password, string? endpoint, bool esmm, bool einvoice) =>
        JsonSerializer.Serialize(new UyumsoftCredentials
        {
            Username = username,
            Password = password,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            Esmm = esmm ? true : null,
            Einvoice = einvoice ? null : false
        });

    /// <summary>
    /// Saklanan JSON'dan kullanıcı adı + şifre + opsiyonel servis adresini çözer.
    /// Geçersizse false döner (endpoint yoksa null kalır).
    /// </summary>
    public static bool TryParseCredentials(
        string? json, out string username, out string password, out string? endpoint) =>
        TryParseCredentials(json, out username, out password, out endpoint, out _, out _);

    /// <summary>
    /// Saklanan JSON'u e-SMM bayraklarıyla birlikte çözer. Geriye uyumlu: "esmm" alanı yoksa
    /// false, "einvoice" alanı yoksa true döner (eski kayıtlar e-fatura mükellefidir).
    /// </summary>
    public static bool TryParseCredentials(
        string? json, out string username, out string password, out string? endpoint,
        out bool esmm, out bool einvoice)
    {
        username = string.Empty;
        password = string.Empty;
        endpoint = null;
        esmm = false;
        einvoice = true;

        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var creds = JsonSerializer.Deserialize<UyumsoftCredentials>(json, CredentialJsonOptions);
            if (creds is null ||
                string.IsNullOrWhiteSpace(creds.Username) ||
                string.IsNullOrWhiteSpace(creds.Password))
            {
                return false;
            }

            username = creds.Username.Trim();
            password = creds.Password;
            endpoint = string.IsNullOrWhiteSpace(creds.Endpoint) ? null : creds.Endpoint.Trim();
            esmm = creds.Esmm ?? false;
            einvoice = creds.Einvoice ?? true;
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
        // apiKey burada JSON kimlik paketidir: {"username","password","endpoint"?,"esmm"?,"einvoice"?}.
        if (!TryParseCredentials(apiKey, out var username, out var password, out var customEndpoint,
                out var esmm, out var einvoice))
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft kimlik bilgileri tanımlı değil. Lütfen entegrasyonu kullanıcı adı ve şifreyle yeniden kaydedin.");
        }

        // Güvenlik: iki bayrak birden kapalıysa (bozuk paket) mevcut davranışa dönülür.
        if (!esmm && !einvoice) einvoice = true;

        // Uç çözümü: kimlikteki özel adres > test ortamı > varsayılan (edonusumapi).
        var endpoint = ResolveEndpoint(customEndpoint, testEnv);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var invoices = new List<ProviderInvoice>();
        // Sayfa içi + sayfalar arası + SERVİSLER ARASI (BasicIntegration/VoucherIntegration)
        // mükerrer koruması: aynı ExternalUuid ikinci kez gelirse atlanır.
        var seenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        try
        {
            // e-Fatura / e-Arşiv gelen kutusu (mevcut CANLI akış — einvoice bayrağı kapalı
            // değilse her durumda çalışır; yalnız-e-SMM mükellefinde atlanır).
            if (einvoice)
            {
                var error = await FetchInboxInvoicesAsync(
                    client, endpoint, username, password, from, to, seenUuids, invoices, warnings, ct);
                if (error is not null) return error;
            }

            // e-SMM makbuzları (VoucherIntegration) — esmm bayrağı işaretliyse çekilir.
            if (esmm)
            {
                var error = await FetchEsmmVouchersAsync(
                    client, ToVoucherEndpoint(endpoint), username, password, from, to,
                    seenUuids, invoices, ct);
                if (error is not null) return error;

                if (!einvoice)
                {
                    // BasicIntegration'a hiç gidilmedi → kullanıcı bilgilendirilir (madde 5).
                    warnings.Add(
                        "Bu mükellef e-SMM mükellefi olarak işaretli olduğundan yalnız e-SMM makbuzları çekildi " +
                        "(e-Fatura gelen kutusu sorgulanmadı).");
                }
            }

            _logger.LogInformation(
                "Uyumsoft senkronu tamamlandı. Uç: {Endpoint}, e-Fatura: {EInvoice}, e-SMM: {Esmm}, Kayıt: {Count}",
                endpoint, einvoice, esmm, invoices.Count);

            return ProviderResult<List<ProviderInvoice>>.Ok(
                invoices, warnings.Count > 0 ? string.Join(" ", warnings) : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient zaman aşımı (30 sn) TaskCanceledException olarak gelir.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft servisi yanıt vermedi (zaman aşımı). Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Uyumsoft servisine bağlanılamadı. Uç: {Endpoint}", endpoint);
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft servisine bağlanılamadı. Servis adresini ve internet bağlantınızı kontrol edip tekrar deneyin.",
                retryable: true);
        }
        catch (XmlException ex)
        {
            _logger.LogError(ex, "Uyumsoft yanıtı XML olarak çözümlenemedi.");
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft'tan beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
    }

    /// <summary>
    /// e-Fatura / e-Arşiv gelen kutusu senkronu (BasicIntegration — CANLIDA ÇALIŞAN akış,
    /// GetIncomingInvoicesAsync'ten değiştirilmeden taşındı). Başarıda null döner; bulunan
    /// faturalar invoices listesine (seenUuids mükerrer korumasıyla) eklenir.
    /// </summary>
    private async Task<ProviderResult<List<ProviderInvoice>>?> FetchInboxInvoicesAsync(
        HttpClient client, string endpoint, string username, string password,
        DateTime from, DateTime to, HashSet<string> seenUuids, List<ProviderInvoice> invoices,
        List<string> warnings, CancellationToken ct)
    {
        var duplicateCount = 0;
        // MaxPages sınırına takılıp sonuç kırpıldıysa kullanıcıya bilgi notu eklenir.
        var truncated = false;
        var added = 0;
        // Önce GetInboxInvoiceList (query modeli) denenir; sunucu operasyonu tanımazsa
        // GetInboxInvoiceView'e (basit parametreler) düşülür.
        var operation = ListOperation;

        for (var page = FirstPageIndex; page < FirstPageIndex + MaxPages; page++)
        {
            var envelope = operation == ViewOperation
                ? BuildViewEnvelope(operation, username, password, from, to, page)
                : BuildListEnvelope(operation, username, password, from, to, page);

            // İlk istekte https kurulamazsa aynı istek bir kez http:// ile denenir;
            // başarılı şema sonraki sayfalarda da kullanılır.
            var (status, body, effectiveEndpoint) = await PostEnvelopeAsync(
                client, endpoint, BasicContract, operation, envelope,
                allowHttpFallback: page == FirstPageIndex, ct);
            endpoint = effectiveEndpoint;

            // SOAP Fault'lar 500 ile dönebilir; önce gövdedeki Fault'a bakılır.
            var fault = TryParseSoapFault(body);
            if (fault is not null)
            {
                // Fault metni her durumda TAM loglanır (gerçek hata metinleri eşleme için gerekli).
                _logger.LogWarning("Uyumsoft SOAP Fault ({Operation}, {Endpoint}): {Fault}",
                    operation, endpoint, fault);

                if (operation == ListOperation && IsOperationNotSupportedFault(fault))
                {
                    _logger.LogWarning(
                        "Uyumsoft {ListOperation} operasyonu bu uçta tanınmadı; {ViewOperation} operasyonuna düşülüyor.",
                        ListOperation, ViewOperation);
                    operation = ViewOperation;
                    page--; // aynı sayfa fallback operasyonuyla tekrar denenir
                    continue;
                }

                return MapSoapFault(fault);
            }

            var httpError = MapErrorStatus(status);
            if (httpError is not null)
            {
                _logger.LogWarning("Uyumsoft gelen fatura isteği HTTP hatası ({Operation}): {Status}",
                    operation, status);
                return httpError;
            }

            var (pageInvoices, totalPages, serviceError) = operation == ViewOperation
                ? ParseViewResponse(body)
                : ParseListResponse(body);
            if (serviceError is not null)
                return serviceError;

            foreach (var invoice in pageInvoices)
            {
                if (seenUuids.Add(invoice.ExternalUuid)) { invoices.Add(invoice); added++; }
                else duplicateCount++;
            }

            // Sayfalama: boş sayfa her durumda döngüyü sonlandırır.
            if (pageInvoices.Count == 0) break;
            if (totalPages is int tp)
            {
                var lastPage = FirstPageIndex + Math.Max(tp, 1) - 1;
                if (page >= lastPage) break;
            }
            else if (pageInvoices.Count < PageSize)
            {
                break; // TotalPages dönmedi; eksik dolu sayfa son sayfadır
            }

            // Buraya gelindiyse sunucuda daha fazla sayfa var; izin verilen son sayfadaysak
            // sonuç kırpılmıştır → kullanıcıya bilgi notu düşülür.
            if (page == FirstPageIndex + MaxPages - 1) truncated = true;
        }

        if (duplicateCount > 0)
        {
            _logger.LogInformation(
                "Uyumsoft senkronunda {Duplicates} mükerrer kayıt sağlayıcı içinde atlandı (sayfalar arası tekrar).",
                duplicateCount);
        }

        if (truncated)
        {
            _logger.LogWarning(
                "Uyumsoft senkronu sayfa sınırına takıldı ({MaxPages} sayfa × {PageSize}); sonuç kırpıldı.",
                MaxPages, PageSize);
            warnings.Add("İlk 5.000 fatura alındı; daha eski kayıtlar için tarih aralığını daraltın.");
        }

        _logger.LogInformation(
            "Uyumsoft gelen fatura senkronu tamamlandı. Operasyon: {Operation}, Uç: {Endpoint}, Fatura: {Count}",
            operation, endpoint, added);

        return null;
    }

    // ---------- e-SMM (VoucherIntegration) makbuz senkronu ----------

    /// <summary>
    /// e-SMM makbuzlarını VoucherIntegration servisinden çeker. Aday operasyonlar sırayla
    /// denenir (TODO-VERIFY: adlar canlıda doğrulanmadı); operasyon-tanınmadı fault'unda
    /// sıradaki adaya geçilir, tüm adaylar düşerse Türkçe hata + ham yanıt döndürülür
    /// (curl ile doğrulama için). Başarıda null döner; makbuzlar invoices listesine eklenir.
    /// </summary>
    private async Task<ProviderResult<List<ProviderInvoice>>?> FetchEsmmVouchersAsync(
        HttpClient client, string endpoint, string username, string password,
        DateTime from, DateTime to, HashSet<string> seenUuids, List<ProviderInvoice> invoices,
        CancellationToken ct)
    {
        string? lastFailure = null;

        foreach (var operation in VoucherOperationCandidates)
        {
            var candidateFailed = false;
            var collected = new List<ProviderInvoice>();

            for (var page = FirstPageIndex; page < FirstPageIndex + MaxPages; page++)
            {
                // İstek gövdesi BasicIntegration kalıbının birebir aynısı: "View" adayları basit
                // startDate/endDate/pageIndex parametreleri, diğerleri PageIndex/PageSize
                // öznitelikli query + ExecutionStartDate/EndDate elemanları kullanır.
                var envelope = operation.EndsWith("View", StringComparison.Ordinal)
                    ? BuildViewEnvelope(operation, username, password, from, to, page)
                    : BuildListEnvelope(operation, username, password, from, to, page);

                var (status, body, effectiveEndpoint) = await PostEnvelopeAsync(
                    client, endpoint, VoucherContract, operation, envelope,
                    allowHttpFallback: page == FirstPageIndex, ct);
                endpoint = effectiveEndpoint;

                var fault = TryParseSoapFault(body);
                if (fault is not null)
                {
                    _logger.LogWarning("Uyumsoft e-SMM SOAP Fault ({Operation}, {Endpoint}): {Fault}",
                        operation, endpoint, fault);

                    if (IsOperationNotSupportedFault(fault))
                    {
                        // Bu aday bu uçta tanınmıyor → sıradaki aday operasyon denenir.
                        lastFailure = fault;
                        candidateFailed = true;
                        break;
                    }

                    return MapSoapFault(fault);
                }

                var httpError = MapErrorStatus(status);
                if (httpError is not null)
                {
                    _logger.LogWarning("Uyumsoft e-SMM isteği HTTP hatası ({Operation}): {Status}",
                        operation, status);
                    return httpError;
                }

                var (pageVouchers, totalPages, serviceError, resultMissing) =
                    ParseVoucherResponse(body, operation);
                if (resultMissing)
                {
                    // {Operasyon}Result yok → yanıt bu adaya ait değil; sıradaki aday denenir.
                    lastFailure = Truncate(body, 500);
                    candidateFailed = true;
                    break;
                }
                if (serviceError is not null) return serviceError;

                collected.AddRange(pageVouchers);

                // Sayfalama: BasicIntegration ile aynı kurallar.
                if (pageVouchers.Count == 0) break;
                if (totalPages is int tp)
                {
                    var lastPage = FirstPageIndex + Math.Max(tp, 1) - 1;
                    if (page >= lastPage) break;
                }
                else if (pageVouchers.Count < PageSize)
                {
                    break;
                }
            }

            if (candidateFailed) continue;

            var added = 0;
            foreach (var voucher in collected)
            {
                if (seenUuids.Add(voucher.ExternalUuid)) { invoices.Add(voucher); added++; }
            }

            _logger.LogInformation(
                "Uyumsoft e-SMM makbuz senkronu tamamlandı. Operasyon: {Operation}, Uç: {Endpoint}, Makbuz: {Count}",
                operation, endpoint, added);
            return null;
        }

        // TODO-VERIFY: tüm aday operasyonlar düştü — ham yanıt hata mesajına eklenir ki canlıda
        // curl ile doğru operasyon adı tespit edilip aday listesi tek ada indirilebilsin.
        _logger.LogError(
            "Uyumsoft e-SMM: VoucherIntegration ucunda hiçbir aday operasyon tanınmadı. Uç: {Endpoint}, Son yanıt: {Failure}",
            endpoint, lastFailure);
        return ProviderResult<List<ProviderInvoice>>.Fail(
            "Uyumsoft e-SMM (VoucherIntegration) servisinde makbuz listeleme operasyonu bulunamadı. " +
            $"Denenen operasyonlar: {string.Join(", ", VoucherOperationCandidates)}. " +
            $"Servis yanıtı (ilk 500 karakter): {lastFailure ?? "-"}");
    }

    /// <summary>
    /// e-SMM servis ucu: BasicIntegration ucundan türetilir ("BasicIntegration" →
    /// "VoucherIntegration"; kimlikteki özel adres de aynı dönüşümden geçer). Uçta
    /// "BasicIntegration" geçmiyorsa varsayılan VoucherIntegration ucu kullanılır.
    /// </summary>
    private static string ToVoucherEndpoint(string endpoint) =>
        endpoint.Contains("BasicIntegration", StringComparison.OrdinalIgnoreCase)
            ? endpoint.Replace("BasicIntegration", "VoucherIntegration", StringComparison.OrdinalIgnoreCase)
            : DefaultVoucherEndpoint;

    // ---------- Uç çözümü + HTTP gönderim ----------

    private static string ResolveEndpoint(string? customEndpoint, bool testEnv)
    {
        // Kimlikte kayıtlı özel adres her zaman önceliklidir (Uyumsoft'un "Web Servis URL"
        // e-postasındaki adres hesabın tanımlı olduğu ortamı gösterir).
        if (!string.IsNullOrWhiteSpace(customEndpoint)) return customEndpoint.Trim();
        return testEnv ? TestEndpoint : DefaultEndpoint;
    }

    /// <summary>
    /// Zarfı POST eder. allowHttpFallback=true iken https bağlantısı HttpRequestException/SSL
    /// hatasıyla kurulamazsa AYNI istek bir kez http:// ile denenir (edonusumapi ortamı HTTP
    /// olabilir); başarılı şema loglanır ve sonraki isteklerde kullanılmak üzere döndürülür.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body, string Endpoint)> PostEnvelopeAsync(
        HttpClient client, string endpoint, string contract, string operation, string envelope,
        bool allowHttpFallback, CancellationToken ct)
    {
        try
        {
            using var response = await SendOnceAsync(client, endpoint, contract, operation, envelope, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return (response.StatusCode, body, endpoint);
        }
        catch (HttpRequestException ex) when (
            allowHttpFallback &&
            endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var httpEndpoint = "http://" + endpoint["https://".Length..];
            _logger.LogWarning(ex,
                "Uyumsoft https bağlantısı kurulamadı ({Endpoint}); aynı istek http ile deneniyor: {HttpEndpoint}",
                endpoint, httpEndpoint);

            using var response = await SendOnceAsync(client, httpEndpoint, contract, operation, envelope, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Uyumsoft bağlantısı http şemasıyla başarılı: {Endpoint}", httpEndpoint);
            return (response.StatusCode, body, httpEndpoint);
        }
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client, string endpoint, string contract, string operation, string envelope,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        // Sözleşme adları: IBasicIntegration (çalışan projeden doğrulandı) /
        // IVoucherIntegration (e-SMM — aynı adlandırma kuralı).
        request.Headers.TryAddWithoutValidation(
            "SOAPAction", $"\"http://tempuri.org/{contract}/{operation}\"");
        return await client.SendAsync(request, ct);
    }

    // ---------- SOAP zarfı üretimi ----------

    /// <summary>Kimlik gövdede İLK eleman olarak gönderilir (WS-Security kullanılmaz).</summary>
    private static XElement UserInfo(string username, string password) =>
        new(TemNs + "userInfo",
            new XAttribute("Username", username),
            new XAttribute("Password", password));

    /// <summary>
    /// GetInboxInvoiceList zarfı — CANLI TESTLE SADELEŞTİRİLDİ (31.07.2026): query yalnız
    /// PageIndex/PageSize (+ OnlyNewestInvoices) öznitelikleri ve ExecutionStartDate/
    /// ExecutionEndDate elemanlarıyla ÇALIŞIR (tarih filtresi işlevsel). xsi:nil'li elemanlar
    /// (CreateStartDate/CreateEndDate/Status/IsArchived) ve SortColumn/SortMode canlıda
    /// gereksiz olduğu kanıtlanarak KALDIRILDI — sunucu sıralaması garanti edilmediğinden
    /// mükerrer koruması ayrıca uygulanır.
    /// </summary>
    private static string BuildListEnvelope(
        string operation, string username, string password, DateTime from, DateTime to, int pageIndex)
    {
        var envelope = new XElement(SoapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tem", TemNs),
            new XElement(SoapNs + "Body",
                new XElement(TemNs + operation,
                    UserInfo(username, password),
                    new XElement(TemNs + "query",
                        new XAttribute("PageIndex", pageIndex),
                        new XAttribute("PageSize", PageSize),
                        new XAttribute("OnlyNewestInvoices", "false"),
                        // ExecutionStartDate/EndDate: fatura tarihine göre aralık (doküman §2.2.25).
                        new XElement(TemNs + "ExecutionStartDate",
                            from.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(TemNs + "ExecutionEndDate",
                            to.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))))));

        return ToXmlString(envelope);
    }

    /// <summary>
    /// GetInboxInvoiceView zarfı (fallback): basit camelCase parametreler — çalışan projedeki
    /// GetOutboxInvoiceView kalıbının gelen kutusu karşılığı.
    /// </summary>
    private static string BuildViewEnvelope(
        string operation, string username, string password, DateTime from, DateTime to, int pageIndex)
    {
        var envelope = new XElement(SoapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapNs),
            new XAttribute(XNamespace.Xmlns + "tem", TemNs),
            new XElement(SoapNs + "Body",
                new XElement(TemNs + operation,
                    UserInfo(username, password),
                    new XElement(TemNs + "startDate",
                        from.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                    new XElement(TemNs + "endDate",
                        to.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                    new XElement(TemNs + "pageIndex", pageIndex),
                    new XElement(TemNs + "pageSize", PageSize))));

        return ToXmlString(envelope);
    }

    private static string ToXmlString(XElement envelope) =>
        new XDocument(new XDeclaration("1.0", "utf-8", null), envelope)
            .ToString(SaveOptions.DisableFormatting);

    // ---------- Fault ayrıştırma / hata eşleme ----------

    private static string? TryParseSoapFault(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.Contains("Fault", StringComparison.Ordinal))
            return null;

        try
        {
            var doc = XDocument.Parse(body);
            var fault = doc.Descendants(SoapNs + "Fault").FirstOrDefault();
            if (fault is null) return null;

            // SOAP 1.1 fault: faultcode/faultstring isimsiz (namespace'siz) elemanlardır.
            var faultString = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                              ?? fault.Elements().FirstOrDefault(e => e.Name.LocalName == "Reason")?.Value;
            var faultCode = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value ?? "";

            return string.IsNullOrWhiteSpace(faultString) ? "Bilinmeyen servis hatası" : $"{faultCode}|{faultString.Trim()}";
        }
        catch (XmlException)
        {
            return null; // XML değilse HTTP durum eşlemesine düşülür
        }
    }

    /// <summary>
    /// Fault, operasyonun bu uçta tanınmadığını mı bildiriyor? (WCF: "ActionNotSupported" /
    /// "The message ... cannot be processed at the receiver, due to a ContractFilter mismatch";
    /// ASMX: "... was not recognized"). Bu durumda GetInboxInvoiceView fallback'i denenir.
    /// </summary>
    private static bool IsOperationNotSupportedFault(string fault) =>
        fault.Contains("ActionNotSupported", StringComparison.OrdinalIgnoreCase) ||
        fault.Contains("was not recognized", StringComparison.OrdinalIgnoreCase) ||
        fault.Contains("ContractFilter", StringComparison.OrdinalIgnoreCase) ||
        fault.Contains("cannot be processed at the receiver", StringComparison.OrdinalIgnoreCase);

    private static ProviderResult<List<ProviderInvoice>> MapSoapFault(string fault)
    {
        var parts = fault.Split('|', 2);
        var code = parts.Length == 2 ? parts[0] : "";
        var message = parts.Length == 2 ? parts[1] : parts[0];

        // "Bu sisteme erişmek için gerekli yetkiniz yok, Kullanıcı: X, Ip: Y" — kimlik doğru
        // ama hesabın web servis yetkisi/IP izni yok. Kullanıcıyı doğru yönlendir.
        if (message.Contains("yetkiniz yok", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("yetkisi yok", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft bu hesaba web servis erişim izni vermedi. Kimlik doğru; ancak hesabınız için " +
                "Uyumsoft'tan web servis yetkisi ve sunucu IP izni tanımlatmanız gerekiyor. " +
                $"(Uyumsoft mesajı: {message})");
        }

        if (LooksLikeAuthError(message))
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft kullanıcı adı veya şifresi hatalı.");
        }

        // Sunucu kaynaklı fault'lar (s:Server / Receiver) geçicidir, tekrar denenebilir.
        var retryable = code.Contains("Server", StringComparison.OrdinalIgnoreCase) ||
                        code.Contains("Receiver", StringComparison.OrdinalIgnoreCase);

        return ProviderResult<List<ProviderInvoice>>.Fail(
            $"Uyumsoft servisi hata bildirdi: {message}", retryable);
    }

    private static ProviderResult<List<ProviderInvoice>>? MapErrorStatus(HttpStatusCode status)
    {
        if (status == HttpStatusCode.OK) return null;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft kullanıcı adı veya şifresi hatalı.");
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft servisi şu anda meşgul. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }

        if ((int)status >= 500)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft tarafında geçici bir sorun oluştu. Lütfen daha sonra tekrar deneyin.",
                retryable: true);
        }

        return ProviderResult<List<ProviderInvoice>>.Fail(
            "Uyumsoft isteği başarısız oldu. Bilgilerinizi kontrol edip tekrar deneyin.");
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
    /// {Operasyon}Result zarfını (IsSucceded/Message öznitelikleri) denetler.
    /// IsSucceded=false ise Message TAM olarak loglanır ve Türkçe hataya eşlenir.
    /// </summary>
    private ProviderResult<List<ProviderInvoice>>? CheckResultEnvelope(XElement result, string operation)
    {
        var isSucceeded = string.Equals(
            (string?)result.Attribute("IsSucceded"), "true", StringComparison.OrdinalIgnoreCase);
        if (isSucceeded) return null;

        var message = ((string?)result.Attribute("Message"))?.Trim();
        // Servis mesajı her durumda TAM loglanır (gerçek hata metinleri eşleme için gerekli).
        _logger.LogWarning("Uyumsoft IsSucceded=false ({Operation}): {Message}", operation, message);

        if (!string.IsNullOrEmpty(message))
        {
            if (message.Contains("yetkiniz yok", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("yetkisi yok", StringComparison.OrdinalIgnoreCase))
            {
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    "Uyumsoft bu hesaba web servis erişim izni vermedi. Kimlik doğru; ancak hesabınız için " +
                    "Uyumsoft'tan web servis yetkisi ve sunucu IP izni tanımlatmanız gerekiyor. " +
                    $"(Uyumsoft mesajı: {message})");
            }

            if (LooksLikeAuthError(message))
            {
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    "Uyumsoft kullanıcı adı veya şifresi hatalı.");
            }
        }

        var text = string.IsNullOrEmpty(message)
            ? "Uyumsoft isteği başarısız oldu. Bilgilerinizi kontrol edip tekrar deneyin."
            : $"Uyumsoft servisi hata bildirdi: {message}";
        return ProviderResult<List<ProviderInvoice>>.Fail(text);
    }

    /// <summary>
    /// GetInboxInvoiceListResponse gövdesini ayrıştırır. CANLI YANIT (31.07.2026): Result →
    /// Value (PageIndex/PageSize/TotalCount/TotalPages öznitelikleri) → Items elemanları;
    /// Items alanları ÇOCUK ELEMANDIR (GetField eleman biçimini de okur).
    /// </summary>
    private (List<ProviderInvoice> Invoices, int? TotalPages, ProviderResult<List<ProviderInvoice>>? Error)
        ParseListResponse(string body)
    {
        var doc = XDocument.Parse(body);

        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "GetInboxInvoiceListResult");
        if (result is null)
        {
            _logger.LogError("Uyumsoft yanıtında GetInboxInvoiceListResult bulunamadı. Yanıt (ilk 500): {Body}",
                Truncate(body, 500));
            return (new List<ProviderInvoice>(), null, ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft'tan beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true));
        }

        var error = CheckResultEnvelope(result, ListOperation);
        if (error is not null) return (new List<ProviderInvoice>(), null, error);

        var value = result.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
        var totalPages = ParseIntAttribute(value, "TotalPages");

        var invoices = new List<ProviderInvoice>();
        if (value is null) return (invoices, totalPages, null);

        foreach (var item in value.Elements().Where(e => e.Name.LocalName == "Items"))
        {
            var invoice = TryMapItem(item);
            if (invoice is not null) invoices.Add(invoice);
        }

        return (invoices, totalPages, null);
    }

    /// <summary>
    /// GetInboxInvoiceViewResponse gövdesini ayrıştırır (fallback). Fatura kayıtları
    /// InvoiceInfo elemanlarıdır; alanlar çoğunlukla ÖZNİTELİK olarak gelir
    /// (ETTN="..." InvoiceNumber="..."), eleman biçimi de tolere edilir.
    /// </summary>
    private (List<ProviderInvoice> Invoices, int? TotalPages, ProviderResult<List<ProviderInvoice>>? Error)
        ParseViewResponse(string body)
    {
        var doc = XDocument.Parse(body);

        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "GetInboxInvoiceViewResult");
        if (result is null)
        {
            _logger.LogError("Uyumsoft yanıtında GetInboxInvoiceViewResult bulunamadı. Yanıt (ilk 500): {Body}",
                Truncate(body, 500));
            return (new List<ProviderInvoice>(), null, ProviderResult<List<ProviderInvoice>>.Fail(
                "Uyumsoft'tan beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true));
        }

        var error = CheckResultEnvelope(result, ViewOperation);
        if (error is not null) return (new List<ProviderInvoice>(), null, error);

        var invoices = new List<ProviderInvoice>();
        foreach (var item in result.Descendants().Where(e => e.Name.LocalName == "InvoiceInfo"))
        {
            var invoice = TryMapItem(item);
            if (invoice is not null) invoices.Add(invoice);
        }

        // TotalPages dönerse kullanılır; yoksa null → eksik dolu sayfa son sayfa sayılır.
        var value = result.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
        var totalPages = ParseIntAttribute(value, "TotalPages") ?? ParseIntAttribute(result, "TotalPages");

        return (invoices, totalPages, null);
    }

    /// <summary>
    /// e-SMM makbuz yanıtını ayrıştırır (ns-bağımsız, öznitelik+eleman toleranslı).
    /// ResultMissing=true → yanıtta {Operasyon}Result yok; çağıran sıradaki aday operasyonu dener.
    /// TODO-VERIFY: kayıt eleman adı canlıda doğrulanmadı — Items (List kalıbı) / VoucherInfo
    /// (View kalıbı) / Voucher / InvoiceInfo adayları toleranslı toplanır.
    /// </summary>
    private (List<ProviderInvoice> Invoices, int? TotalPages, ProviderResult<List<ProviderInvoice>>? Error, bool ResultMissing)
        ParseVoucherResponse(string body, string operation)
    {
        var doc = XDocument.Parse(body);

        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == operation + "Result");
        if (result is null)
        {
            _logger.LogWarning(
                "Uyumsoft e-SMM yanıtında {Operation}Result bulunamadı. Yanıt (ilk 500): {Body}",
                operation, Truncate(body, 500));
            return (new List<ProviderInvoice>(), null, null, true);
        }

        var error = CheckResultEnvelope(result, operation);
        if (error is not null) return (new List<ProviderInvoice>(), null, error, false);

        var value = result.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");

        var items = new List<XElement>();
        if (value is not null)
            items.AddRange(value.Elements().Where(e => e.Name.LocalName == "Items"));
        if (items.Count == 0)
        {
            items.AddRange(result.Descendants().Where(e =>
                e.Name.LocalName is "Items" or "VoucherInfo" or "Voucher" or "InvoiceInfo"));
        }

        var invoices = new List<ProviderInvoice>();
        foreach (var item in items)
        {
            var invoice = TryMapVoucherItem(item);
            if (invoice is not null) invoices.Add(invoice);
        }

        var totalPages = ParseIntAttribute(value, "TotalPages") ?? ParseIntAttribute(result, "TotalPages");
        return (invoices, totalPages, null, false);
    }

    /// <summary>
    /// Tek e-SMM makbuz kaydını toleranslı eşler (öznitelik + alt eleman; ns bağımsız).
    /// TODO-VERIFY: alan adları canlıda doğrulanmadı — Uyumsoft'un TERS adlandırması
    /// hatırlanarak (InvoiceId/VoucherId = BELGE NUMARASI, DocumentId = UUID/ETTN) aday
    /// listeleri geniş tutuldu; bulunamayan sayısal alanlar 0 kabul edilir.
    /// e-SMM makbuzu mükellefin DÜZENLEDİĞİ gelir belgesidir: karşı taraf MÜŞTERİDİR.
    /// Gelir işareti (şema değişikliği olmadan): SenderTitle'a " (e-SMM)" eki ve satır adına
    /// "e-SMM Makbuzu — " öneki — LucaExcelExporter gelir fişi kalıbını bu önekten tanır.
    /// </summary>
    private static ProviderInvoice? TryMapVoucherItem(XElement item)
    {
        // UUID adayları: ETTN → DocumentId (BasicIntegration'daki gibi UUID) → Uuid/Guid.
        var uuid = GetField(item, "ETTN", "DocumentId", "Uuid", "Guid");
        if (string.IsNullOrWhiteSpace(uuid)) return null; // UUID'siz kayıt yazılmaz

        // Makbuz numarası: VoucherNumber/VoucherId/InvoiceId = belge no (ters adlandırma).
        var voucherNo = GetField(item,
            "VoucherNumber", "VoucherId", "InvoiceNumber", "InvoiceId", "Id") ?? string.Empty;

        // Brüt ücret / KDV / stopaj / net — bulunamayan alanlar 0.
        var gross = ParseDecimalValue(GetField(item,
            "GrossFee", "BrutUcret", "GrossAmount", "GrossWage", "TaxExclusiveAmount"));
        var vat = ParseDecimalValue(GetField(item,
            "VatAmount", "Kdv", "KdvTutari", "TaxTotal"));
        var withholding = ParseDecimalValue(GetField(item,
            "WithholdingAmount", "Stopaj", "StopajTutari", "WithholdingTaxAmount", "GvStopaj"));
        var net = ParseDecimalValue(GetField(item,
            "NetAmount", "NetUcret", "CollectedAmount", "PayableAmount"));

        // Tahsil edilen: net alan dönmediyse brüt + KDV − stopaj'dan türetilir
        // (exporter stopajı brüt + KDV − tahsil formülüyle geri türetir).
        var payable = net != 0m ? net : Math.Round(gross + vat - withholding, 2);

        var issueDate = ParseDateValue(GetField(item, "ExecutionDate", "IssueDate", "VoucherDate"))
                        ?? ParseDateValue(GetField(item, "CreateDateUtc"))
                        ?? DateTime.UtcNow.Date;

        // Karşı taraf = müşteri (giden belge olduğundan Target* birincil aday).
        var customerTitle = GetField(item,
            "TargetTitle", "CustomerTitle", "CustomerName", "TargetName", "SenderTitle") ?? string.Empty;
        var customerTaxId = GetField(item,
            "TargetTcknVkn", "CustomerTcknVkn", "CustomerTaxId", "SenderTcknVkn") ?? string.Empty;
        var currency = GetField(item, "DocumentCurrencyCode", "CurrencyCode");

        var vatRate = gross > 0 ? Math.Round(vat / gross * 100m, 0) : 0m;

        return new ProviderInvoice
        {
            ExternalUuid = uuid,
            InvoiceNumber = voucherNo,
            IssueDate = issueDate,
            SenderTaxId = customerTaxId,
            SenderTitle = string.IsNullOrWhiteSpace(customerTitle)
                ? "e-SMM Müşterisi (e-SMM)"
                : $"{customerTitle} (e-SMM)",
            CurrencyCode = string.IsNullOrWhiteSpace(currency) ? "TRY" : currency,
            TaxExclusiveAmount = gross,
            TaxTotal = vat,
            PayableAmount = payable,
            Lines = new List<ProviderInvoiceLine>
            {
                new()
                {
                    LineNumber = 1,
                    // "e-SMM Makbuzu — " öneki AYIRT EDİCİ İŞARETTİR (exporter gelir fişi
                    // kalıbını bundan tanır) — değiştirilirse LucaExcelExporter da güncellenmeli.
                    ItemName = string.IsNullOrWhiteSpace(customerTitle)
                        ? $"e-SMM Makbuzu — {voucherNo}".Trim()
                        : $"e-SMM Makbuzu — {customerTitle}",
                    Quantity = 1,
                    UnitCode = "C62",
                    UnitPrice = gross,
                    LineAmount = gross,
                    VatRate = vatRate,
                    VatAmount = vat
                }
            }
        };
    }

    /// <summary>KDV kırılımında aranan oranlar (canlı yanıt: VatX / VatXTaxableAmount elemanları).</summary>
    private static readonly int[] VatBreakdownRates = { 1, 8, 10, 18, 20 };

    /// <summary>
    /// Tek fatura kaydını toleranslı eşler: alanlar önce öznitelikte, sonra alt elemanda aranır.
    /// CANLI DOĞRULAMA (31.07.2026, GetInboxInvoiceList): adlandırma TERSTİR —
    /// DocumentId = UUID/GUID (ETTN), InvoiceId = GİB fatura numarası (ör. COS2024000000984).
    /// View fallback'i ETTN/InvoiceNumber adlarını kullanır; iki biçim de tolere edilir.
    /// TargetTcknVkn/SenderTcknVkn → VKN; TargetTitle/SenderTitle → unvan (trim'li);
    /// ExecutionDate/CreateDateUtc → tarih; eksik sayısal alanlar 0.
    /// </summary>
    private static ProviderInvoice? TryMapItem(XElement item)
    {
        // UUID: ETTN (View) → DocumentId (List). DİKKAT: InvoiceId UUID DEĞİLDİR (fatura no'dur).
        var uuid = GetField(item, "ETTN", "DocumentId");
        if (string.IsNullOrWhiteSpace(uuid)) return null; // UUID'siz fatura yazılmaz

        var taxExclusive = ParseDecimalValue(GetField(item, "TaxExclusiveAmount"));
        var taxTotal = ParseDecimalValue(GetField(item, "TaxTotal"));
        var payable = ParseDecimalValue(GetField(item, "PayableAmount"));

        // Fatura tarihi: ExecutionDate (fatura tarihi) → yoksa CreateDateUtc → yoksa bugün.
        var issueDate = ParseDateValue(GetField(item, "ExecutionDate"))
                        ?? ParseDateValue(GetField(item, "CreateDateUtc"))
                        ?? DateTime.UtcNow.Date;

        var currency = GetField(item, "DocumentCurrencyCode", "CurrencyCode");

        return new ProviderInvoice
        {
            ExternalUuid = uuid,
            // Fatura numarası: InvoiceNumber (View) → InvoiceId (List; ör. COS2024000000984).
            InvoiceNumber = GetField(item, "InvoiceNumber", "InvoiceId") ?? string.Empty,
            IssueDate = issueDate,
            // Gelen kutusunda karşı taraf (satıcı) Target* alanlarında döner; View yanıtı
            // Sender* adlarını kullanabilir — ikisi de tolere edilir. GetField değerleri
            // trim'ler (canlıda "KOZMOPLEKS ... A.Ş. " gibi sondaki boşluklu unvan görüldü).
            SenderTaxId = GetField(item, "TargetTcknVkn", "SenderTcknVkn") ?? string.Empty,
            SenderTitle = GetField(item, "TargetTitle", "SenderTitle") ?? string.Empty,
            CurrencyCode = string.IsNullOrWhiteSpace(currency) ? "TRY" : currency,
            TaxExclusiveAmount = taxExclusive,
            TaxTotal = taxTotal,
            PayableAmount = payable,
            Lines = BuildLines(item, taxExclusive, taxTotal)
        };
    }

    /// <summary>
    /// Satır üretimi. Liste yanıtı kalem detayı dönmez ama KDV kırılımını VatX /
    /// VatXTaxableAmount elemanlarıyla verir → sıfır olmayan her oran için bir satır üretilir
    /// (kod motoru satır bazlı çalıştığından oran kırılımı ciddi kalite artışıdır).
    /// Kırılım tamamen boşsa (ör. View fallback'i) başlık toplamlarından tek özet satıra düşülür.
    /// TODO-VERIFY: gerçek kalemler GetInboxInvoiceData'nın UBL XML'inden çıkarılacak.
    /// </summary>
    private static List<ProviderInvoiceLine> BuildLines(XElement item, decimal taxExclusive, decimal taxTotal)
    {
        var lines = new List<ProviderInvoiceLine>();

        foreach (var rate in VatBreakdownRates)
        {
            var vatAmount = ParseDecimalValue(GetField(item, $"Vat{rate}"));
            var lineAmount = ParseDecimalValue(GetField(item, $"Vat{rate}TaxableAmount"));
            if (vatAmount == 0m && lineAmount == 0m) continue;

            lines.Add(new ProviderInvoiceLine
            {
                LineNumber = lines.Count + 1,
                ItemName = $"%{rate} KDV'li mal/hizmet toplamı",
                Quantity = 1,
                UnitCode = "C62",
                UnitPrice = lineAmount,
                LineAmount = lineAmount,
                VatRate = rate,
                VatAmount = vatAmount
            });
        }

        if (lines.Count > 0) return lines;

        // KDV kırılımı dönmedi → mevcut tek-özet-satır davranışı (Nilvera ile aynı yaklaşım).
        lines.Add(new ProviderInvoiceLine
        {
            LineNumber = 1,
            ItemName = "Fatura toplamı (kalem detayı UBL XML'den alınacak)",
            Quantity = 1,
            UnitCode = "C62",
            UnitPrice = taxExclusive,
            LineAmount = taxExclusive,
            VatRate = taxExclusive > 0 ? Math.Round(taxTotal / taxExclusive * 100m, 0) : 0,
            VatAmount = taxTotal
        });
        return lines;
    }

    /// <summary>
    /// Alan değerini toleranslı okur: verilen adlar sırasıyla önce ÖZNİTELİKLERDE, sonra alt
    /// ELEMANLARDA aranır (ad alanından bağımsız, büyük/küçük harf duyarsız). xsi:nil ve boş
    /// değerler atlanır; hiçbiri yoksa null döner.
    /// </summary>
    private static string? GetField(XElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var attr = item.Attributes().FirstOrDefault(a =>
                string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (attr is not null && !string.IsNullOrWhiteSpace(attr.Value))
                return attr.Value.Trim();

            var element = item.Elements().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (element is not null &&
                !string.Equals((string?)element.Attribute(XsiNs + "nil"), "true", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(element.Value))
            {
                return element.Value.Trim();
            }
        }

        return null;
    }

    private static int? ParseIntAttribute(XElement? element, string name)
    {
        var raw = (string?)element?.Attribute(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Eksik/bozuk sayısal alanlar 0 kabul edilir.</summary>
    private static decimal ParseDecimalValue(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value, 2)
            : 0m;

    private static DateTime? ParseDateValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            return XmlConvert.ToDateTime(raw, XmlDateTimeSerializationMode.Utc);
        }
        catch (FormatException)
        {
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fallback)
                ? fallback
                : null;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
