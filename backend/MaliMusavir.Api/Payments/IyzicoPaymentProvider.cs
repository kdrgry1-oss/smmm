using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Payments;

/// <summary>
/// iyzico ödeme adaptörü İSKELETİ (blueprint M7). Gerçek API anahtarları kullanıcı tarafından
/// iletilince etkinleştirilecek; anahtar yokken her çağrı Türkçe "yapılandırılmamış" hatası döner
/// (uygulama Mock sağlayıcıyla uçtan uca çalışmaya devam eder — Payments:Provider = "Mock").
///
/// Yapılandırma (appsettings → Payments:Iyzico):
///   ApiKey, SecretKey, BaseUrl (sandbox: https://sandbox-api.iyzipay.com · canlı: https://api.iyzipay.com)
///
/// TODO-VERIFY (canlı anahtar alınınca resmi dokümandan doğrulanacak):
/// - Checkout Form başlatma ucu: POST {BaseUrl}/payment/iyzipos/checkoutform/initialize/auth/ecom
/// - İade ucu: POST {BaseUrl}/payment/refund
/// - Kimlik başlığı: "Authorization: IYZWSv2 ..." (HMAC-SHA256 imza şeması) — aşağıdaki imza
///   üretimi dokümandaki örneklerle birebir karşılaştırılmalı (randomKey + uriPath + requestBody).
/// - Yanıt alanları: status ("success"/"failure"), errorMessage (Türkçe döner), paymentPageUrl, token.
/// </summary>
public class IyzicoPaymentProvider : IPaymentProvider
{
    public const string HttpClientName = "iyzico";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IyzicoPaymentProvider> _logger;

    public IyzicoPaymentProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<IyzicoPaymentProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public string Name => "Iyzico";

    private (string ApiKey, string SecretKey, string BaseUrl)? ReadConfig()
    {
        var apiKey = _configuration["Payments:Iyzico:ApiKey"];
        var secretKey = _configuration["Payments:Iyzico:SecretKey"];
        var baseUrl = _configuration["Payments:Iyzico:BaseUrl"];

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(baseUrl))
            return null;

        return (apiKey.Trim(), secretKey.Trim(), baseUrl.TrimEnd('/'));
    }

    public async Task<ProviderResult<CheckoutResult>> CreateCheckoutAsync(
        Tenant tenant, Plan plan, decimal amount, string? cardNumber, CancellationToken ct)
    {
        var config = ReadConfig();
        if (config is null)
        {
            return ProviderResult<CheckoutResult>.Fail(
                "Ödeme sağlayıcısı yapılandırılmamış. iyzico API anahtarlarını sistem yöneticinize iletin " +
                "(Payments:Iyzico:ApiKey / SecretKey / BaseUrl).");
        }

        var (apiKey, secretKey, baseUrl) = config.Value;

        // iyzico'da kart bilgisi platform üzerinden İLETİLMEZ; kullanıcı iyzico'nun barındırdığı
        // Checkout Form sayfasına yönlendirilir (kart saklamasız, PCI-DSS yükü iyzico'da).
        const string uriPath = "/payment/iyzipos/checkoutform/initialize/auth/ecom"; // TODO-VERIFY
        var conversationId = $"MM-{tenant.Id:N}";

        var requestBody = JsonSerializer.Serialize(new
        {
            locale = "tr",
            conversationId,
            price = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            paidPrice = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            currency = "TRY",
            basketId = plan.Code,
            paymentGroup = "SUBSCRIPTION", // TODO-VERIFY: abonelik tahsilatında beklenen değer
            callbackUrl = _configuration["Payments:Iyzico:CallbackUrl"] ?? "http://localhost:3000/panel/ayarlar",
            buyer = new
            {
                id = tenant.Id.ToString("N"),
                name = tenant.Name,
                surname = "-",
                email = tenant.Email,
                identityNumber = tenant.TaxNumber ?? "11111111111",
                registrationAddress = "-",
                city = "-",
                country = "Türkiye"
            },
            basketItems = new[]
            {
                new
                {
                    id = plan.Code,
                    name = $"{plan.Name} paketi aylık abonelik",
                    category1 = "SaaS Abonelik",
                    itemType = "VIRTUAL",
                    price = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        });

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + uriPath)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };

            // IYZWSv2 imza şeması. TODO-VERIFY: resmi dokümandaki imza örnekleriyle birebir doğrulanacak.
            var randomKey = DateTime.UtcNow.Ticks.ToString();
            var signature = ComputeSignature(secretKey, randomKey + uriPath + requestBody);
            var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"apiKey:{apiKey}&randomKey:{randomKey}&signature:{signature}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"IYZWSv2 {authorization}");

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("iyzico checkout HTTP hatası: {Status}", response.StatusCode);
                return ProviderResult<CheckoutResult>.Fail(
                    "Ödeme sağlayıcısına ulaşılamadı. Lütfen daha sonra tekrar deneyin.", retryable: true);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var errorMessage = root.TryGetProperty("errorMessage", out var em) ? em.GetString() : null;
                return ProviderResult<CheckoutResult>.Ok(CheckoutResult.Decline(
                    string.IsNullOrWhiteSpace(errorMessage)
                        ? "Ödeme sağlayıcısı işlemi reddetti. Kart bilgilerinizi kontrol edip tekrar deneyin."
                        : errorMessage));
            }

            var paymentPageUrl = root.TryGetProperty("paymentPageUrl", out var url) ? url.GetString() : null;
            var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;

            if (string.IsNullOrWhiteSpace(paymentPageUrl))
            {
                return ProviderResult<CheckoutResult>.Fail(
                    "Ödeme sağlayıcısından beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.",
                    retryable: true);
            }

            return ProviderResult<CheckoutResult>.Ok(
                CheckoutResult.Redirect(paymentPageUrl, token ?? conversationId));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProviderResult<CheckoutResult>.Fail(
                "Ödeme sağlayıcısı yanıt vermedi (zaman aşımı). Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "iyzico'ya bağlanılamadı.");
            return ProviderResult<CheckoutResult>.Fail(
                "Ödeme sağlayıcısına bağlanılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.", retryable: true);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "iyzico yanıtı çözümlenemedi.");
            return ProviderResult<CheckoutResult>.Fail(
                "Ödeme sağlayıcısından beklenmeyen bir yanıt alındı. Lütfen daha sonra tekrar deneyin.", retryable: true);
        }
    }

    public Task<ProviderResult<bool>> RefundAsync(PaymentTransaction transaction, CancellationToken ct)
    {
        var config = ReadConfig();
        if (config is null)
        {
            return Task.FromResult(ProviderResult<bool>.Fail(
                "Ödeme sağlayıcısı yapılandırılmamış. iyzico API anahtarlarını sistem yöneticinize iletin."));
        }

        // TODO-VERIFY: POST {BaseUrl}/payment/refund — paymentTransactionId + price + ip alanları.
        // Canlı anahtar alınınca uygulanacak; iskelet aşamasında güvenli tarafta kalınır.
        return Task.FromResult(ProviderResult<bool>.Fail(
            "iyzico iade akışı henüz etkin değil (canlı API anahtarı bekleniyor)."));
    }

    private static string ComputeSignature(string secretKey, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
