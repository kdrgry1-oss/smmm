using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Ocr;

/// <summary>
/// Vision-LLM tabanlı fiş OCR sağlayıcısı (Görev 2 — en doğru okuma).
/// Anthropic Messages API'ye (https://api.anthropic.com/v1/messages) fiş görüntüsünü base64
/// gönderir; Türkçe fiş uzmanı prompt'u ile SADECE geçerli JSON ister, yanıtı OcrValidator
/// çapraz kontrollerinden geçirip OcrReceiptResult'a normalize eder.
/// Seçim: appsettings "Ocr:Provider" = "LlmVision" (varsayılan Mock — anahtar gerekmez).
/// Yapılandırma: Ocr:Anthropic:ApiKey / Model (varsayılan "claude-sonnet-4-5") / MaxTokens.
/// EnvVar override .NET config ile hazırdır: Ocr__Anthropic__ApiKey.
///
/// KADEMELİ OKUMA (saha bulgusu): WhatsApp'ın sıkıştırdığı fişlerde ucuz model
/// (claude-haiku-4-5) "okuyamadım" dönebiliyorken sonnet sınıfı model aynı görseli okur.
/// Bu yüzden birincil model başarısız/zayıfsa AYNI görsel Ocr:Anthropic:FallbackModel
/// (varsayılan "claude-sonnet-4-5") ile bir kez daha okunur ve iki sonuçtan iyisi seçilir.
/// Tetikleyiciler: ana alanlar boş (satıcı boş VE toplam 0), genel güven &lt;
/// Ocr:Anthropic:FallbackThreshold (varsayılan 0.5) veya "okunamadı / düşük kalite"
/// uyarı sinyali. FallbackModel boşsa ya da birincil modelle aynıysa ikinci çağrı
/// YAPILMAZ (çift maliyet koruması).
/// </summary>
public class LlmVisionOcrProvider : IReceiptOcrProvider
{
    public const string HttpClientName = "anthropic-ocr";

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const string DefaultModel = "claude-sonnet-4-5";
    private const string DefaultFallbackModel = "claude-sonnet-4-5";
    private const double DefaultFallbackThreshold = 0.5;
    private const int DefaultMaxTokens = 2000;

    /// <summary>Fallback tetiklendiğinde sonuca eklenen kullanıcı görünür not.</summary>
    private const string FallbackWarningNote = "Güçlü modelle yeniden okundu.";

    /// <summary>
    /// Uyarı metinlerinde "model okuyamadı" sinyali sayılan parçalar (küçük harf karşılaştırma).
    /// Hem OcrValidator/model uyarılarını hem de sağlayıcının kendi "Fiş okunamadı: ..."
    /// hata mesajlarını yakalar ("okunamad" → okunamadı/okunamadi biçimlerinin ortak öneki).
    /// </summary>
    private static readonly string[] UnreadableSignals =
        { "okunamad", "okuyamad", "düşük kalite", "dusuk kalite", "bulanık", "bulanik" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly OcrValidator _validator;
    private readonly ILogger<LlmVisionOcrProvider> _logger;

    public LlmVisionOcrProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        OcrValidator validator,
        ILogger<LlmVisionOcrProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _validator = validator;
        _logger = logger;
    }

    public string Name => "LlmVision";

    public async Task<ProviderResult<OcrReceiptResult>> ExtractAsync(
        string filePath, string originalFileName, long fileSize, OcrDocumentKind kind, CancellationToken ct)
    {
        var apiKey = _configuration["Ocr:Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "Vision OCR için Anthropic API anahtarı yapılandırılmamış. appsettings.json içinde " +
                "Ocr:Anthropic:ApiKey değerini girin veya Ocr__Anthropic__ApiKey ortam değişkenini tanımlayın; " +
                "anahtar olmadan çalışmak için Ocr:Provider değerini \"Mock\" yapın.");
        }

        var mediaType = ResolveMediaType(originalFileName);
        if (mediaType is null)
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "Bu dosya türü vision OCR tarafından desteklenmiyor. Lütfen JPG, PNG veya PDF yükleyin " +
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

        var model = _configuration["Ocr:Anthropic:Model"];
        if (string.IsNullOrWhiteSpace(model)) model = DefaultModel;
        var maxTokens = _configuration.GetValue<int?>("Ocr:Anthropic:MaxTokens") ?? DefaultMaxTokens;

        // Kademeli okuma yapılandırması. Anahtar hiç yoksa varsayılan sonnet kademesi devrededir;
        // bilinçli kapatmak için FallbackModel boş string ("") ya da birincil modelle aynı yapılır.
        var fallbackModel = _configuration["Ocr:Anthropic:FallbackModel"] ?? DefaultFallbackModel;
        var fallbackThreshold = _configuration.GetValue<double?>("Ocr:Anthropic:FallbackThreshold")
                                ?? DefaultFallbackThreshold;
        var fallbackEnabled = !string.IsNullOrWhiteSpace(fallbackModel) &&
                              !string.Equals(fallbackModel.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase);

        // ---------- 1. deneme: birincil (ucuz) model ----------
        var primary = await AttemptAsync(apiKey, model, maxTokens, mediaType, fileBytes, originalFileName, kind, ct);

        if (!fallbackEnabled)
        {
            if (primary.Success)
                _logger.LogInformation(
                    "OCR birincil model {Model}: güven %{Confidence} (fallback kapalı) — {File}",
                    model, Percent(primary.Data!.Confidence), originalFileName);
            return primary;
        }

        if (!ShouldFallback(primary, fallbackThreshold))
        {
            _logger.LogInformation(
                "OCR birincil model {Model}: güven %{Confidence} — fallback gerekmedi ({File})",
                model, primary.Success ? Percent(primary.Data!.Confidence) : "-", originalFileName);
            return primary;
        }

        // ---------- 2. deneme: güçlü model, AYNI görsel ----------
        _logger.LogInformation(
            "OCR birincil model {Model}: {PrimaryOutcome} → fallback {FallbackModel} tetiklendi ({File})",
            model,
            primary.Success ? $"güven %{Percent(primary.Data!.Confidence)}" : "okuma başarısız",
            fallbackModel, originalFileName);

        var fallback = await AttemptAsync(apiKey, fallbackModel, maxTokens, mediaType, fileBytes, originalFileName, kind, ct);

        if (!fallback.Success)
        {
            _logger.LogInformation(
                "OCR fallback {FallbackModel} de başarısız ({Error}) — birincil sonuç korunuyor ({File})",
                fallbackModel, fallback.ErrorMessage, originalFileName);
            return primary.Success ? primary : fallback;
        }

        if (!primary.Success || IsBetter(fallback.Data!, primary.Data!))
        {
            _logger.LogInformation(
                "OCR birincil model {Model}: güven %{PrimaryConfidence} → fallback {FallbackModel} tetiklendi: güven %{FallbackConfidence} — fallback sonucu kullanıldı ({File})",
                model, primary.Success ? Percent(primary.Data!.Confidence) : "-",
                fallbackModel, Percent(fallback.Data!.Confidence), originalFileName);
            fallback.Data!.Warnings.Add(FallbackWarningNote);
            return fallback;
        }

        _logger.LogInformation(
            "OCR birincil model {Model}: güven %{PrimaryConfidence} → fallback {FallbackModel} tetiklendi: güven %{FallbackConfidence} — birincil sonuç daha iyi, o kullanıldı ({File})",
            model, Percent(primary.Data!.Confidence),
            fallbackModel, Percent(fallback.Data!.Confidence), originalFileName);
        primary.Data!.Warnings.Add(FallbackWarningNote);
        return primary;
    }

    /// <summary>
    /// Fallback tetiklenmeli mi? Başarılı sonuçta: (a) ana alanlar boş (satıcı boş VE toplam 0),
    /// (b) genel güven &lt; eşik, (c) uyarılarda "okunamadı / düşük kalite / bulanık" sinyali.
    /// Başarısız denemede: yanıt-kalitesi hataları ("Fiş okunamadı: ..." — boş/çözümlenemeyen
    /// yanıt) fallback'i tetikler; retryable hatalar (429/5xx/ağ/zaman aşımı) ve yapılandırma
    /// hataları tetiklemez — güçlü model bunları çözmez, çift maliyet olur.
    /// </summary>
    private static bool ShouldFallback(ProviderResult<OcrReceiptResult> attempt, double threshold)
    {
        if (!attempt.Success)
            return !attempt.Retryable && ContainsUnreadableSignal(attempt.ErrorMessage);

        var r = attempt.Data!;
        if (string.IsNullOrWhiteSpace(r.VendorName) && r.TotalAmount == 0m) return true;
        if (r.Confidence < threshold) return true;
        return r.Warnings.Any(ContainsUnreadableSignal);
    }

    /// <summary>Fallback sonucu birincilden daha mı iyi? Ana alanları dolduruyorsa veya güveni yüksekse evet.</summary>
    private static bool IsBetter(OcrReceiptResult fallback, OcrReceiptResult primary)
    {
        var primaryEmpty = string.IsNullOrWhiteSpace(primary.VendorName) && primary.TotalAmount == 0m;
        var fallbackEmpty = string.IsNullOrWhiteSpace(fallback.VendorName) && fallback.TotalAmount == 0m;
        if (primaryEmpty && !fallbackEmpty) return true;
        if (!primaryEmpty && fallbackEmpty) return false;
        return fallback.Confidence > primary.Confidence;
    }

    private static bool ContainsUnreadableSignal(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lowered = text.ToLowerInvariant();
        return UnreadableSignals.Any(lowered.Contains);
    }

    private static string Percent(double confidence) => Math.Round(confidence * 100).ToString("0");

    /// <summary>
    /// Tek model denemesi: istek gövdesi + HTTP çağrısı + JSON çözümleme + doğrulama.
    /// Kademeli okuma bu metodu birincil ve fallback model için ayrı ayrı çağırır.
    /// </summary>
    private async Task<ProviderResult<OcrReceiptResult>> AttemptAsync(
        string apiKey, string model, int maxTokens, string mediaType, byte[] fileBytes,
        string originalFileName, OcrDocumentKind kind, CancellationToken ct)
    {
        // PDF "document", görseller "image" içerik bloğu olarak gönderilir (Messages API).
        var sourceBlockType = mediaType == "application/pdf" ? "document" : "image";
        var requestBody = new
        {
            model,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = sourceBlockType,
                            source = new
                            {
                                type = "base64",
                                media_type = mediaType,
                                data = Convert.ToBase64String(fileBytes)
                            }
                        },
                        new { type = "text", text = OcrPrompt.For(kind) }
                    }
                }
            }
        };

        string responseText;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic OCR çağrısı başarısız. Durum: {Status}, Gövde: {Body}",
                    (int)response.StatusCode, OcrJsonParser.Truncate(body, 500));

                return (int)response.StatusCode switch
                {
                    401 or 403 => ProviderResult<OcrReceiptResult>.Fail(
                        "Anthropic API anahtarı geçersiz veya yetkisiz. Ocr:Anthropic:ApiKey değerini kontrol edin."),
                    429 => ProviderResult<OcrReceiptResult>.Fail(
                        "OCR servisi istek sınırına ulaştı. Lütfen kısa bir süre sonra tekrar deneyin.", retryable: true),
                    >= 500 => ProviderResult<OcrReceiptResult>.Fail(
                        "OCR servisi şu anda yanıt vermiyor. Lütfen daha sonra tekrar deneyin.", retryable: true),
                    _ => ProviderResult<OcrReceiptResult>.Fail(
                        "Fiş okuma isteği reddedildi. Dosyanın bozuk olmadığından emin olup tekrar deneyin.")
                };
            }

            responseText = ExtractTextContent(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "OCR isteği zaman aşımına uğradı. Lütfen tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Anthropic OCR ağ hatası.");
            return ProviderResult<OcrReceiptResult>.Fail(
                "OCR servisine ulaşılamadı. İnternet bağlantısını kontrol edip tekrar deneyin.", retryable: true);
        }

        if (string.IsNullOrWhiteSpace(responseText))
            return ProviderResult<OcrReceiptResult>.Fail(
                "Fiş okunamadı: OCR servisi boş yanıt döndürdü. Daha net bir görselle tekrar deneyin.");

        LlmReceiptExtraction? extraction;
        try
        {
            extraction = OcrJsonParser.Deserialize(responseText);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Anthropic OCR yanıtı JSON olarak çözümlenemedi: {Text}", OcrJsonParser.Truncate(responseText, 500));
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
    /// Uzantıdan Anthropic media_type; desteklenmeyen türde null (HEIC desteklenmez).
    /// Faz 12 revizyonu ("önce OCR, sonra sıkıştırma"): OCR girdisi artık ORİJİNAL dosyadır
    /// (temp/ altındaki dokunulmamış kopya) — uzantı da orijinaldir (jpg/png/pdf...);
    /// PDF'e dokunulmaz, HEIC istemci katmanları JPEG ürettiği için istisnadır.
    /// </summary>
    private static string? ResolveMediaType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            _ => null
        };

    /// <summary>Messages API yanıtındaki ilk text içerik bloğunu döndürür.</summary>
    private static string ExtractTextContent(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                block.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}
