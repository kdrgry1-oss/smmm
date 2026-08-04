using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// Paraşüt REST adaptörü — resmi halka açık JSON:API v4 (https://api.parasut.com).
///
/// - Kimlik: OAuth2 password grant. Her senkronda POST /oauth/token
///   {client_id, client_secret, username, password, grant_type:"password",
///   redirect_uri:"urn:ietf:wg:oauth:2.0:oob"} → access_token (refresh_token SAKLANMAZ;
///   token her senkronda yeniden alınır — kısa ömürlü senkronlar için yeterlidir).
/// - Kimlik JSON'u (ApiKeyEncrypted): {"clientId","clientSecret","username","password",
///   "companyId","endpoint"?}. Opsiyonel "endpoint" taban adresi EZER (varsayılan api.parasut.com).
/// - Gelen faturalar = ALIŞ faturaları: GET /v4/{company_id}/purchase_bills.
///   TODO-VERIFY: filter[issue_date] desteği sürüme göre değişebildiğinden GÜVENLİ yol seçildi:
///   ?sort=-issue_date&page[size]=25&page[number]=N ile sayfalanır, tarih aralığı CLIENT-SIDE
///   filtrelenir; sayfadaki en eski issue_date aralık başlangıcından eskiyse döngü kesilir.
///   include=supplier ile satıcı adı/VKN "included" dizisinden id eşleştirilerek okunur.
/// - TODO-VERIFY: attributes alan adları (issue_date, invoice_no, description, net_total,
///   gross_total, total_vat, currency) resmi dokümana göredir ama canlıda doğrulanmadı —
///   ayrıştırma JsonElement üzerinden eksik alanlara TOLERANSLI yazıldı (sayı ya da string
///   değer ikisi de kabul edilir, invariant culture).
/// - Paraşüt UUID döndürmez → ExternalUuid := "parasut-"+id; InvoiceNumber := invoice_no ?? id.
/// - KDV kırılımı yok → TEK özet satır: matrah=net_total, KDV=total_vat; oran geri hesaplanıp
///   1/8/10/18/20'den en yakınına yuvarlanır.
/// - 401 → "Paraşüt kimlik bilgileri doğrulanamadı" + ham gövde parçası. Test ortamı yoktur;
///   UseTestEnvironment yok sayılır.
/// - Mükerrer id'ler HashSet ile sağlayıcı içinde ayıklanır.
/// </summary>
public class ParasutProvider : IEInvoiceProvider
{
    public const string HttpClientName = "parasut";

    /// <summary>Varsayılan taban adres (resmi halka açık API).</summary>
    public const string DefaultBaseUrl = "https://api.parasut.com";

    private const int PageSize = 25;
    private const int MaxPages = 40; // güvenlik sınırı: tek senkronda en fazla 1.000 alış faturası

    /// <summary>Yanıttaki hata mesajlarına eklenen ham gövde üst sınırı (teşhis için).</summary>
    private const int RawBodySnippetLength = 2000;

    /// <summary>KDV oranı geri hesabında yuvarlanan geçerli oranlar.</summary>
    private static readonly decimal[] KnownVatRates = { 1m, 8m, 10m, 18m, 20m };

    private static readonly JsonSerializerOptions CredentialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ParasutProvider> _logger;

    public ParasutProvider(IHttpClientFactory httpClientFactory, ILogger<ParasutProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.Parasut;

    // ---------- Kimlik paketleme (IntegrationsController da kullanır) ----------

    private sealed class ParasutCredentials
    {
        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

        [JsonPropertyName("clientSecret")]
        public string? ClientSecret { get; set; }

        /// <summary>Paraşüt hesabının e-posta adresi (OAuth password grant kullanıcı adı).</summary>
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        /// <summary>Paraşüt firma numarası (adres çubuğundaki /v4/{companyId} değeri).</summary>
        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        /// <summary>Opsiyonel taban adres; boşsa https://api.parasut.com kullanılır.</summary>
        [JsonPropertyName("endpoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Endpoint { get; set; }
    }

    /// <summary>Çözülmüş Paraşüt kimliği (TryParseCredentials çıktısı).</summary>
    public sealed record ParasutCredentialInfo(
        string ClientId, string ClientSecret, string Username, string Password, string CompanyId, string? Endpoint);

    /// <summary>
    /// Paraşüt kimliğini ApiKeyEncrypted alanında saklanacak JSON'a paketler:
    /// {"clientId","clientSecret","username","password","companyId","endpoint"?}.
    /// (Diğer sağlayıcılarla aynı kalıp — sağlayıcıya özgü ayrı kolon açılmaz.)
    /// </summary>
    public static string PackCredentials(
        string clientId, string clientSecret, string username, string password, string companyId,
        string? endpoint = null) =>
        JsonSerializer.Serialize(new ParasutCredentials
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            Username = username,
            Password = password,
            CompanyId = companyId,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim()
        });

    /// <summary>Saklanan JSON'dan Paraşüt kimliğini çözer. Zorunlu alanlardan biri boşsa false döner.</summary>
    public static bool TryParseCredentials(string? json, out ParasutCredentialInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var creds = JsonSerializer.Deserialize<ParasutCredentials>(json, CredentialJsonOptions);
            if (creds is null ||
                string.IsNullOrWhiteSpace(creds.ClientId) ||
                string.IsNullOrWhiteSpace(creds.ClientSecret) ||
                string.IsNullOrWhiteSpace(creds.Username) ||
                string.IsNullOrWhiteSpace(creds.Password) ||
                string.IsNullOrWhiteSpace(creds.CompanyId))
            {
                return false;
            }

            info = new ParasutCredentialInfo(
                creds.ClientId.Trim(),
                creds.ClientSecret.Trim(),
                creds.Username.Trim(),
                creds.Password,
                creds.CompanyId.Trim(),
                string.IsNullOrWhiteSpace(creds.Endpoint) ? null : creds.Endpoint.Trim());
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
        // apiKey burada JSON kimlik paketidir; testEnv yok sayılır (Paraşüt'te test ortamı yoktur).
        if (!TryParseCredentials(apiKey, out var creds) || creds is null)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Paraşüt kimlik bilgileri tanımlı değil. Lütfen entegrasyonu Client ID, Client Secret, " +
                "e-posta, şifre ve Firma ID ile yeniden kaydedin.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(creds.Endpoint)
            ? DefaultBaseUrl
            : creds.Endpoint.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            // 1) OAuth2 password grant ile access_token alınır (her senkronda yeniden;
            //    refresh_token bilinçli olarak SAKLANMAZ).
            var (token, tokenError) = await GetAccessTokenAsync(client, baseUrl, creds, ct);
            if (tokenError is not null) return tokenError;

            // 2) Alış faturaları -issue_date sıralı sayfalanır; tarih aralığı client-side uygulanır.
            return await FetchPurchaseBillsAsync(client, baseUrl, creds.CompanyId, token!, from, to, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient zaman aşımı (30 sn) TaskCanceledException olarak gelir.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Paraşüt servisi yanıt vermedi (zaman aşımı). Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Paraşüt servisine bağlanılamadı. Taban: {BaseUrl}", baseUrl);
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Paraşüt servisine bağlanılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.",
                retryable: true);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Paraşüt yanıtı JSON olarak çözümlenemedi.");
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Paraşüt'ten beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
    }

    // ---------- OAuth2 token ----------

    private async Task<(string? Token, ProviderResult<List<ProviderInvoice>>? Error)> GetAccessTokenAsync(
        HttpClient client, string baseUrl, ParasutCredentialInfo creds, CancellationToken ct)
    {
        // Resmi doküman kalıbı: form-urlencoded gövde; redirect_uri "out of band" sabitidir.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["username"] = creds.Username,
            ["password"] = creds.Password,
            ["grant_type"] = "password",
            ["redirect_uri"] = "urn:ietf:wg:oauth:2.0:oob"
        });

        using var response = await client.PostAsync($"{baseUrl}/oauth/token", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Paraşüt geçersiz kimlikte 401 (bazı sürümlerde 400 invalid_grant) döner.
            _logger.LogWarning("Paraşüt token isteği reddedildi ({Status}).", response.StatusCode);
            return (null, ProviderResult<List<ProviderInvoice>>.Fail(
                $"Paraşüt kimlik bilgileri doğrulanamadı. Client ID/Secret, e-posta ve şifrenizi kontrol edin. " +
                $"— Ham yanıt: {Truncate(body, RawBodySnippetLength)}"));
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Paraşüt token isteği başarısız (HTTP {Status}). Ham yanıt (ilk {Len}): {Body}",
                (int)response.StatusCode, RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
            return (null, ProviderResult<List<ProviderInvoice>>.Fail(
                $"Paraşüt oturumu açılamadı (HTTP {(int)response.StatusCode}). Ham yanıt: {Truncate(body, RawBodySnippetLength)}",
                retryable: (int)response.StatusCode >= 500));
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("access_token", out var tokenEl) &&
            tokenEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(tokenEl.GetString()))
        {
            return (tokenEl.GetString(), null);
        }

        return (null, ProviderResult<List<ProviderInvoice>>.Fail(
            $"Paraşüt oturumu açılamadı (access_token dönmedi). Ham yanıt: {Truncate(body, RawBodySnippetLength)}"));
    }

    // ---------- Alış faturaları ----------

    private async Task<ProviderResult<List<ProviderInvoice>>> FetchPurchaseBillsAsync(
        HttpClient client, string baseUrl, string companyId, string token,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var invoices = new List<ProviderInvoice>();
        // Mükerrer koruması: aynı id birden fazla sayfada dönerse ilki alınır (DB unique
        // index'ine ve EInvoiceSyncService kontrolüne ek, sağlayıcı içi ilk savunma hattı).
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateCount = 0;
        var truncated = false;

        var fromDate = from.Date;
        var toDate = to.Date;

        for (var page = 1; page <= MaxPages; page++)
        {
            // TODO-VERIFY: filter[issue_date] sürüme göre değişebildiğinden kullanılmaz;
            // -issue_date sıralamasıyla sayfalanıp aralık client-side uygulanır.
            var url = $"{baseUrl}/v4/{Uri.EscapeDataString(companyId)}/purchase_bills" +
                      $"?sort=-issue_date&page%5Bsize%5D={PageSize}&page%5Bnumber%5D={page}&include=supplier";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            var httpError = MapErrorStatus(response.StatusCode, body);
            if (httpError is not null)
            {
                _logger.LogWarning("Paraşüt purchase_bills HTTP hatası: {Status}", response.StatusCode);
                return httpError;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                _logger.LogError("Paraşüt yanıtında data dizisi yok. Ham yanıt (ilk {Len}): {Body}",
                    RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
                return ProviderResult<List<ProviderInvoice>>.Fail(
                    $"Paraşüt'ten beklenmeyen bir yanıt alındı. Ham yanıt: {Truncate(body, RawBodySnippetLength)}",
                    retryable: true);
            }

            // included → contacts sözlüğü (satıcı adı/VKN eşleştirmesi için).
            var suppliers = BuildSupplierLookup(root);

            var pageCount = 0;
            var sawOlderThanRange = false;

            foreach (var item in data.EnumerateArray())
            {
                pageCount++;

                var mapped = TryMapBill(item, suppliers);
                if (mapped is null) continue;

                var (invoice, issueDate) = mapped.Value;

                // Client-side tarih aralığı filtresi (sayfalama -issue_date sıralıdır).
                if (issueDate < fromDate)
                {
                    sawOlderThanRange = true;
                    continue;
                }
                if (issueDate > toDate) continue;

                if (seenIds.Add(invoice.ExternalUuid)) invoices.Add(invoice);
                else duplicateCount++;
            }

            // Döngü sonlandırma: boş sayfa ya da aralık başlangıcından eski kayıt görülmesi
            // (sıralama -issue_date olduğundan sonraki sayfalar tümüyle daha eskidir).
            if (pageCount == 0 || sawOlderThanRange) break;
            if (pageCount < PageSize) break; // eksik dolu sayfa son sayfadır

            if (page == MaxPages) truncated = true;
        }

        if (duplicateCount > 0)
        {
            _logger.LogInformation(
                "Paraşüt senkronunda {Duplicates} mükerrer kayıt sağlayıcı içinde atlandı.", duplicateCount);
        }

        string? warning = null;
        if (truncated)
        {
            _logger.LogWarning(
                "Paraşüt senkronu sayfa sınırına takıldı ({MaxPages} sayfa × {PageSize}); sonuç kırpıldı.",
                MaxPages, PageSize);
            warning = "İlk 1.000 alış faturası tarandı; daha eski kayıtlar için tarih aralığını daraltın.";
        }

        _logger.LogInformation("Paraşüt gelen (alış) fatura senkronu tamamlandı. Fatura: {Count}", invoices.Count);
        return ProviderResult<List<ProviderInvoice>>.Ok(invoices, warning);
    }

    private ProviderResult<List<ProviderInvoice>>? MapErrorStatus(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.OK) return null;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                $"Paraşüt kimlik bilgileri doğrulanamadı. — Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
        }

        if (status == HttpStatusCode.NotFound)
        {
            // Yanlış companyId en sık 404 üretir — kullanıcıyı doğru alana yönlendir.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Paraşüt firması bulunamadı. Firma ID (adres çubuğundaki sayı) değerini kontrol edin. " +
                $"— Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Paraşüt servisi şu anda meşgul. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }

        if ((int)status >= 500)
        {
            _logger.LogWarning("Paraşüt HTTP {Status}. Ham yanıt (ilk {Len}): {Body}",
                (int)status, RawBodySnippetLength, Truncate(body, RawBodySnippetLength));
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Paraşüt tarafında geçici bir sorun oluştu. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }

        return ProviderResult<List<ProviderInvoice>>.Fail(
            $"Paraşüt isteği başarısız oldu (HTTP {(int)status}). Ham yanıt: {Truncate(body, RawBodySnippetLength)}");
    }

    // ---------- JSON:API ayrıştırma (eksik alanlara toleranslı) ----------

    /// <summary>
    /// included dizisindeki contacts kayıtlarını id → (name, tax_number) sözlüğüne çevirir.
    /// TODO-VERIFY: include=supplier desteklenmiyorsa dizi boş kalır; satıcı alanları boş yazılır.
    /// </summary>
    private static Dictionary<string, (string Name, string TaxNumber)> BuildSupplierLookup(JsonElement root)
    {
        var suppliers = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        if (!root.TryGetProperty("included", out var included) || included.ValueKind != JsonValueKind.Array)
            return suppliers;

        foreach (var item in included.EnumerateArray())
        {
            if (GetString(item, "type") != "contacts") continue;
            var id = GetString(item, "id");
            if (string.IsNullOrEmpty(id)) continue;

            if (!item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                continue;

            suppliers[id] = (GetString(attrs, "name") ?? string.Empty,
                             GetString(attrs, "tax_number") ?? string.Empty);
        }

        return suppliers;
    }

    /// <summary>
    /// Tek purchase_bills kaydını toleranslı eşler. TODO-VERIFY: alan adları resmi dokümana
    /// göredir (issue_date, invoice_no, description, net_total, gross_total, total_vat,
    /// currency) — canlıda doğrulanacak; eksik alanlar 0/boş kabul edilir.
    /// Dönen tuple'daki IssueDate client-side tarih filtresi için ayrıca verilir.
    /// </summary>
    private static (ProviderInvoice Invoice, DateTime IssueDate)? TryMapBill(
        JsonElement item, Dictionary<string, (string Name, string TaxNumber)> suppliers)
    {
        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null; // id'siz kayıt yazılmaz

        JsonElement attrs = default;
        var hasAttrs = item.TryGetProperty("attributes", out attrs) && attrs.ValueKind == JsonValueKind.Object;

        var issueDate = hasAttrs ? ParseDateValue(GetString(attrs, "issue_date")) : null;

        var netTotal = hasAttrs ? GetDecimal(attrs, "net_total") : 0m;
        var grossTotal = hasAttrs ? GetDecimal(attrs, "gross_total") : 0m;
        var totalVat = hasAttrs ? GetDecimal(attrs, "total_vat") : 0m;

        // Eksik toplamlar birbirinden tamamlanır (net + KDV = brüt).
        if (grossTotal == 0m && netTotal != 0m) grossTotal = Math.Round(netTotal + totalVat, 2);
        if (netTotal == 0m && grossTotal != 0m) netTotal = Math.Round(grossTotal - totalVat, 2);

        var invoiceNo = hasAttrs ? GetString(attrs, "invoice_no") : null;
        var currency = hasAttrs ? GetString(attrs, "currency") : null;

        // relationships.supplier.data.id → included contacts kaydından ad/VKN.
        var supplierName = string.Empty;
        var supplierTaxNumber = string.Empty;
        if (item.TryGetProperty("relationships", out var rels) && rels.ValueKind == JsonValueKind.Object &&
            rels.TryGetProperty("supplier", out var supplierRel) && supplierRel.ValueKind == JsonValueKind.Object &&
            supplierRel.TryGetProperty("data", out var supplierData) && supplierData.ValueKind == JsonValueKind.Object)
        {
            var supplierId = GetString(supplierData, "id");
            if (!string.IsNullOrEmpty(supplierId) && suppliers.TryGetValue(supplierId, out var supplier))
            {
                supplierName = supplier.Name;
                supplierTaxNumber = supplier.TaxNumber;
            }
        }

        // Satıcı adı yoksa açıklama (description) son çare olarak kullanılır.
        if (string.IsNullOrWhiteSpace(supplierName) && hasAttrs)
            supplierName = GetString(attrs, "description") ?? string.Empty;

        var effectiveDate = issueDate ?? DateTime.UtcNow.Date;

        var invoice = new ProviderInvoice
        {
            // Paraşüt UUID döndürmez; entegratör-içi benzersiz id "parasut-" önekiyle kullanılır.
            ExternalUuid = ClampLength($"parasut-{id}", 64),
            InvoiceNumber = ClampLength(string.IsNullOrWhiteSpace(invoiceNo) ? id : invoiceNo!, 30),
            IssueDate = effectiveDate,
            SenderTaxId = SanitizeTaxId(supplierTaxNumber),
            SenderTitle = ClampLength(supplierName.Trim(), 300),
            CurrencyCode = string.IsNullOrWhiteSpace(currency) ? "TRY" : ClampLength(currency!.Trim().ToUpperInvariant(), 3),
            TaxExclusiveAmount = netTotal,
            TaxTotal = totalVat,
            PayableAmount = grossTotal,
            Lines = BuildSummaryLines(netTotal, totalVat)
        };

        return (invoice, effectiveDate);
    }

    /// <summary>
    /// Paraşüt satır detayı bu uçta çekilmez → TEK özet satır: matrah=net_total, KDV=total_vat;
    /// KDV oranı geri hesaplanıp 1/8/10/18/20'den EN YAKININA yuvarlanır.
    /// TODO-VERIFY: kalem detayı gerekirse include=details ile ayrıca çekilebilir.
    /// </summary>
    private static List<ProviderInvoiceLine> BuildSummaryLines(decimal netTotal, decimal totalVat)
    {
        var vatRate = 0m;
        if (netTotal > 0m && totalVat > 0m)
        {
            var rawRate = totalVat / netTotal * 100m;
            vatRate = KnownVatRates.OrderBy(r => Math.Abs(r - rawRate)).First();
        }

        return new List<ProviderInvoiceLine>
        {
            new()
            {
                LineNumber = 1,
                ItemName = "Fatura toplamı (Paraşüt alış faturası özeti)",
                Quantity = 1,
                UnitCode = "C62",
                UnitPrice = netTotal,
                LineAmount = netTotal,
                VatRate = vatRate,
                VatAmount = totalVat
            }
        };
    }

    // ---------- JSON yardımcıları ----------

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(prop.GetString()) ? null : prop.GetString()!.Trim(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null
        };
    }

    /// <summary>Sayı YA DA string gelen tutar alanlarını invariant culture ile okur; eksik/bozuk = 0.</summary>
    private static decimal GetDecimal(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop))
            return 0m;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var number))
            return Math.Round(number, 2);

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Round(parsed, 2);
        }

        return 0m;
    }

    private static DateTime? ParseDateValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // JSON:API issue_date biçimi yyyy-MM-dd; ISO tarih-saat de tolere edilir.
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
        {
            return exact.Date;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fallback)
            ? fallback.Date
            : null;
    }

    /// <summary>VKN/TCKN sütunu 11 karakterle sınırlıdır → yalnız rakamlar, en fazla 11 hane alınır.</summary>
    private static string SanitizeTaxId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length <= 11 ? digits : digits[..11];
    }

    /// <summary>Hata mesajı/log kısaltması (üç noktalı) — DB alanları için ClampLength kullanılır.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>DB kolon sınırı için sessiz kırpma (ek karakter eklemez).</summary>
    private static string ClampLength(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
