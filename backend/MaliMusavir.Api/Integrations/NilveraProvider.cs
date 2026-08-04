using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// Nilvera e-Fatura API adaptörü.
///
/// Nilvera geliştirici dokümanından (30.07.2026) doğrulanan bilgiler:
/// - Base URL: canlı https://api.nilvera.com · test https://apitest.nilvera.com
/// - e-Fatura API öneki: /einvoice (OpenAPI servers.url)
/// - Gelen faturalar: GET /einvoice/Purchase?StartDate=&EndDate=&Page=&PageSize=&SortColumn=IssueDate&SortType=DESC
///   Kaynak: https://developer.nilvera.com/api/e-fatura-api/gelen-faturalar/gelen-faturalari-listeler.md
/// - Yanıt zarfı: { Page, PageSize, TotalCount, TotalPages, Content: [...] }
/// - Fatura alanları: UUID, SenderTaxNumber, SenderName, InvoiceNumber, CurrencyCode, IssueDate,
///   PayableAmount, TaxExclusiveAmount, TaxTotalAmount, LineExtensionAmount, StatusCode ...
/// - Kimlik: Authorization: Bearer {API_KEY}
/// </summary>
public class NilveraProvider : IEInvoiceProvider
{
    public const string HttpClientName = "nilvera";

    private const string LiveBaseUrl = "https://api.nilvera.com";
    private const string TestBaseUrl = "https://apitest.nilvera.com";
    private const int PageSize = 100;
    private const int MaxPages = 50; // güvenlik sınırı: tek senkronda en fazla 5.000 fatura

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NilveraProvider> _logger;

    public NilveraProvider(IHttpClientFactory httpClientFactory, ILogger<NilveraProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.Nilvera;

    public async Task<ProviderResult<List<ProviderInvoice>>> GetIncomingInvoicesAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return ProviderResult<List<ProviderInvoice>>.Fail("Entegratör API anahtarı tanımlı değil.");

        var baseUrl = testEnv ? TestBaseUrl : LiveBaseUrl;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var invoices = new List<ProviderInvoice>();

        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                // Tarihler ISO 8601 (dokümandaki örnek: StartDate=2023-03-23T00:00:00.000Z).
                var url = $"{baseUrl}/einvoice/Purchase" +
                          $"?StartDate={Uri.EscapeDataString(from.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))}" +
                          $"&EndDate={Uri.EscapeDataString(to.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))}" +
                          $"&Page={page}&PageSize={PageSize}&SortColumn=IssueDate&SortType=DESC";

                using var response = await client.GetAsync(url, ct);

                var error = MapErrorStatus(response.StatusCode);
                if (error is not null)
                {
                    _logger.LogWarning("Nilvera gelen fatura listesi hatası: {Status}", response.StatusCode);
                    return error;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var pageResult = JsonSerializer.Deserialize<NilveraPurchasePage>(body, JsonOptions);

                if (pageResult?.Content is null || pageResult.Content.Count == 0)
                    break;

                foreach (var item in pageResult.Content)
                {
                    if (string.IsNullOrWhiteSpace(item.UUID)) continue;
                    invoices.Add(MapInvoice(item));
                }

                var totalPages = pageResult.TotalPages
                    ?? (pageResult.TotalCount is int tc ? (int)Math.Ceiling(tc / (double)PageSize) : page);
                if (page >= totalPages) break;
            }

            return ProviderResult<List<ProviderInvoice>>.Ok(invoices);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient zaman aşımı (30 sn) TaskCanceledException olarak gelir.
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Entegratör yanıt vermedi (zaman aşımı). Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Nilvera'ya bağlanılamadı.");
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Entegratöre bağlanılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.", retryable: true);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Nilvera yanıtı çözümlenemedi.");
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Entegratörden beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
    }

    private static ProviderResult<List<ProviderInvoice>>? MapErrorStatus(HttpStatusCode status)
    {
        if (status == HttpStatusCode.OK) return null;

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Entegratör API anahtarı geçersiz veya süresi dolmuş.");
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Entegratör istek sınırı aşıldı, birazdan tekrar denenecek.", retryable: true);
        }

        if ((int)status >= 500)
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "Entegratör tarafında geçici bir sorun oluştu. Lütfen daha sonra tekrar deneyin.",
                retryable: true);
        }

        return ProviderResult<List<ProviderInvoice>>.Fail(
            "Entegratör isteği başarısız oldu. Bilgilerinizi kontrol edip tekrar deneyin.");
    }

    private static ProviderInvoice MapInvoice(NilveraPurchaseInvoice item)
    {
        var taxExclusive = ToDecimal(item.TaxExclusiveAmount);
        var taxTotal = ToDecimal(item.TaxTotalAmount);

        return new ProviderInvoice
        {
            ExternalUuid = item.UUID!.Trim(),
            InvoiceNumber = item.InvoiceNumber?.Trim() ?? string.Empty,
            IssueDate = item.IssueDate,
            SenderTaxId = item.SenderTaxNumber?.Trim() ?? string.Empty,
            SenderTitle = item.SenderName?.Trim() ?? string.Empty,
            CurrencyCode = string.IsNullOrWhiteSpace(item.CurrencyCode) ? "TRY" : item.CurrencyCode.Trim(),
            TaxExclusiveAmount = taxExclusive,
            TaxTotal = taxTotal,
            PayableAmount = ToDecimal(item.PayableAmount),
            // Liste ucunda kalem satırları dönmez; Details ucu da yalnızca başlık düzeyidir.
            // Kalemler UBL XML'inden çıkarılır (GET /einvoice/Purchase/{UUID}/xml).
            // TODO-VERIFY: XML ucunun tam yolu/casing'i ve UBL satır ayrıştırma canlı anahtar alınınca doğrulanacak.
            // Faz 1'de başlık toplamlarından tek özet satır üretilir.
            Lines = new List<ProviderInvoiceLine>
            {
                new()
                {
                    LineNumber = 1,
                    ItemName = "Fatura toplamı (kalem detayı UBL XML'den alınacak)",
                    Quantity = 1,
                    UnitCode = "C62",
                    UnitPrice = taxExclusive,
                    LineAmount = taxExclusive,
                    VatRate = taxExclusive > 0 ? Math.Round(taxTotal / taxExclusive * 100m, 0) : 0,
                    VatAmount = taxTotal
                }
            }
        };
    }

    private static decimal ToDecimal(double value) => Convert.ToDecimal(Math.Round(value, 2));

    // ---- Nilvera yanıt modelleri (PurchaseInvoiceResponsePagination / PurchaseInvoiceResponse) ----

    private sealed class NilveraPurchasePage
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int? TotalCount { get; set; }
        public int? TotalPages { get; set; }
        public List<NilveraPurchaseInvoice>? Content { get; set; }
    }

    private sealed class NilveraPurchaseInvoice
    {
        [JsonPropertyName("UUID")]
        public string? UUID { get; set; }
        public string? SenderTaxNumber { get; set; }
        public string? SenderName { get; set; }
        public string? InvoiceNumber { get; set; }
        public string? CurrencyCode { get; set; }
        public DateTime IssueDate { get; set; }
        public double PayableAmount { get; set; }
        public double TaxExclusiveAmount { get; set; }
        public double TaxTotalAmount { get; set; }
        public double LineExtensionAmount { get; set; }
    }
}
