using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Ocr;

/// <summary>
/// OpenAI (GPT-4o mini) vision tabanlı fiş OCR sağlayıcısı — LlmVisionOcrProvider'ın
/// (Anthropic) OpenAI Chat Completions API karşılığı. Aynı Türkçe fiş uzmanı prompt'u
/// (OcrPrompt.ReceiptExtraction), aynı JSON şeması (LlmReceiptExtraction → OcrJsonParser)
/// ve aynı doğrulama katmanı (OcrValidator) kullanılır; yalnız taşıma katmanı farklıdır.
/// response_format=json_object ile model geçerli JSON döndürmeye zorlanır.
/// NOT: OpenAI chat görüntü ucu PDF kabul ETMEZ — PDF için LlmVision kullanılmalıdır.
/// Seçim: appsettings "Ocr:Provider" = "OpenAi".
/// Yapılandırma: Ocr:OpenAi:ApiKey / Model (varsayılan "gpt-4o-mini") / MaxTokens.
/// EnvVar override .NET config ile hazırdır: Ocr__OpenAi__ApiKey.
/// </summary>
public class OpenAiVisionOcrProvider : IReceiptOcrProvider
{
    public const string HttpClientName = "openai-ocr";

    private const string ApiUrl = "https://api.openai.com/v1/chat/completions";
    private const string DefaultModel = "gpt-4o-mini";
    private const int DefaultMaxTokens = 2000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly OcrValidator _validator;
    private readonly ILogger<OpenAiVisionOcrProvider> _logger;

    public OpenAiVisionOcrProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        OcrValidator validator,
        ILogger<OpenAiVisionOcrProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _validator = validator;
        _logger = logger;
    }

    public string Name => "OpenAi";

    public async Task<ProviderResult<OcrReceiptResult>> ExtractAsync(
        string filePath, string originalFileName, long fileSize, OcrDocumentKind kind, CancellationToken ct)
    {
        var apiKey = _configuration["Ocr:OpenAi:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "Vision OCR için OpenAI API anahtarı yapılandırılmamış. appsettings.json içinde " +
                "Ocr:OpenAi:ApiKey değerini girin veya Ocr__OpenAi__ApiKey ortam değişkenini tanımlayın; " +
                "anahtar olmadan çalışmak için Ocr:Provider değerini \"Mock\" yapın.");
        }

        // OpenAI chat görüntü ucu PDF kabul etmez — kullanıcıyı doğru sağlayıcıya yönlendir.
        if (Path.GetExtension(originalFileName).ToLowerInvariant() == ".pdf")
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "PDF dosyaları için Anthropic (LlmVision) sağlayıcısını kullanın veya görüntü yükleyin.");
        }

        var mediaType = ResolveMediaType(originalFileName);
        if (mediaType is null)
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "Bu dosya türü vision OCR tarafından desteklenmiyor. Lütfen JPG veya PNG yükleyin " +
                "(HEIC fotoğrafları yüklemeden önce JPG'ye dönüştürün).");
        }

        byte[] fileBytes;
        try
        {
            fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR için fiş dosyası okunamadı: {Path}", filePath);
            return ProviderResult<OcrReceiptResult>.Fail("Fiş dosyası okunamadı. Lütfen tekrar yükleyin.");
        }

        var model = _configuration["Ocr:OpenAi:Model"];
        if (string.IsNullOrWhiteSpace(model)) model = DefaultModel;
        var maxTokens = _configuration.GetValue<int?>("Ocr:OpenAi:MaxTokens") ?? DefaultMaxTokens;

        var requestBody = new
        {
            model,
            max_tokens = maxTokens,
            // json_object modu: model geçerli JSON döndürmeye zorlanır (prompt'taki
            // "SADECE geçerli JSON döndür" kuralıyla uyumlu — modun şartı prompt'ta "JSON" geçmesidir).
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = OcrPrompt.For(kind) },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{mediaType};base64,{Convert.ToBase64String(fileBytes)}"
                            }
                        }
                    }
                }
            }
        };

        string responseText;
        string rawBody;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            rawBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI OCR çağrısı başarısız. Durum: {Status}, Gövde: {Body}",
                    (int)response.StatusCode, OcrJsonParser.Truncate(rawBody, 500));

                return (int)response.StatusCode switch
                {
                    401 or 403 => ProviderResult<OcrReceiptResult>.Fail(
                        "OpenAI API anahtarı geçersiz veya yetkisiz. Ocr:OpenAi:ApiKey değerini kontrol edin."),
                    429 => ProviderResult<OcrReceiptResult>.Fail(
                        "OpenAI istek sınırı aşıldı. Lütfen kısa bir süre sonra tekrar deneyin.", retryable: true),
                    >= 500 => ProviderResult<OcrReceiptResult>.Fail(
                        "OCR servisi şu anda yanıt vermiyor. Lütfen daha sonra tekrar deneyin.", retryable: true),
                    _ => ProviderResult<OcrReceiptResult>.Fail(
                        "Fiş okuma isteği reddedildi. Dosyanın bozuk olmadığından emin olup tekrar deneyin.")
                };
            }

            responseText = ExtractMessageContent(rawBody);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "OCR isteği zaman aşımına uğradı. Lütfen tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OpenAI OCR ağ hatası.");
            return ProviderResult<OcrReceiptResult>.Fail(
                "OCR servisine ulaşılamadı. İnternet bağlantısını kontrol edip tekrar deneyin.", retryable: true);
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning("OpenAI OCR yanıtında içerik yok. Gövde: {Body}", OcrJsonParser.Truncate(rawBody, 300));
            return ProviderResult<OcrReceiptResult>.Fail(
                "Fiş okunamadı: OCR servisi boş yanıt döndürdü. Daha net bir görselle tekrar deneyin.");
        }

        LlmReceiptExtraction? extraction;
        try
        {
            extraction = OcrJsonParser.Deserialize(responseText);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OpenAI OCR yanıtı JSON olarak çözümlenemedi: {Text}", OcrJsonParser.Truncate(responseText, 300));
            return ProviderResult<OcrReceiptResult>.Fail(
                "Fiş okunamadı: OCR yanıtı çözümlenemedi. Daha net bir görselle tekrar deneyin.");
        }

        if (extraction is null)
            return ProviderResult<OcrReceiptResult>.Fail(
                "Fiş okunamadı: OCR yanıtı boş. Daha net bir görselle tekrar deneyin.");

        // Doğrulama katmanı: çapraz kontroller + alan güvenlerinin minimumu → genel güven.
        var result = _validator.Normalize(extraction, kind);

        if (result.Warnings.Count > 0)
        {
            _logger.LogInformation("OCR doğrulama uyarıları ({File}): {Warnings}",
                originalFileName, string.Join(" | ", result.Warnings));
        }

        return ProviderResult<OcrReceiptResult>.Ok(result);
    }

    /// <summary>
    /// Uzantıdan data URL media type; desteklenmeyen türde null (HEIC desteklenmez,
    /// PDF bu metoda gelmeden ayrı Türkçe hatayla reddedilir).
    /// Faz 12: normalize edilen görsellerin adı/uzantısı ".jpg" olduğundan bunlar her zaman
    /// "image/jpeg" olarak gönderilir.
    /// </summary>
    private static string? ResolveMediaType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null
        };

    /// <summary>Chat Completions yanıtındaki choices[0].message.content metnini döndürür.</summary>
    private static string ExtractMessageContent(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = choices[0];
        if (first.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
