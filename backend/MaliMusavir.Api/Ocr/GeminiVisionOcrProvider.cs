using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Ocr;

/// <summary>
/// Google Gemini (2.5 Flash) vision tabanlı fiş OCR sağlayıcısı — LlmVisionOcrProvider'ın
/// (Anthropic) Gemini generateContent REST API karşılığı. Aynı Türkçe fiş uzmanı prompt'u
/// (OcrPrompt.ReceiptExtraction), aynı JSON şeması (LlmReceiptExtraction → OcrJsonParser)
/// ve aynı doğrulama katmanı (OcrValidator) kullanılır; yalnız taşıma katmanı farklıdır.
/// generationConfig.response_mime_type = "application/json" ile model geçerli JSON
/// döndürmeye zorlanır. Kimlik "x-goog-api-key" HEADER'ı ile gönderilir — URL'de
/// ?key= KULLANILMAZ (query string log/proxy kayıtlarına sızar).
/// PDF: Gemini inline_data "application/pdf" DESTEKLER → PDF de aynı uçtan gönderilir
/// (OpenAI sağlayıcısındaki PDF reddi burada yoktur).
/// Seçim: appsettings "Ocr:Provider" = "Gemini".
/// Yapılandırma: Ocr:Gemini:ApiKey / Model (varsayılan "gemini-2.5-flash") /
/// FallbackModel / FallbackThreshold / MaxTokens.
/// EnvVar override .NET config ile hazırdır: Ocr__Gemini__ApiKey.
///
/// KADEMELİ OKUMA (LlmVisionOcrProvider ile aynı desen): birincil model (flash)
/// başarısız/zayıfsa AYNI görsel Ocr:Gemini:FallbackModel (varsayılan "gemini-2.5-pro")
/// ile bir kez daha okunur ve iki sonuçtan iyisi seçilir. Tetikleyiciler: ana alanlar
/// boş (satıcı boş VE toplam 0), genel güven &lt; Ocr:Gemini:FallbackThreshold
/// (varsayılan 0.5) veya "okunamadı / düşük kalite" uyarı sinyali. FallbackModel boşsa
/// ya da birincil modelle aynıysa ikinci çağrı YAPILMAZ (çift maliyet koruması).
/// </summary>
public class GeminiVisionOcrProvider : IReceiptOcrProvider
{
    public const string HttpClientName = "gemini-ocr";

    private const string ApiUrlBase = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string DefaultModel = "gemini-2.5-flash";
    private const string DefaultFallbackModel = "gemini-2.5-pro";
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
    private readonly ILogger<GeminiVisionOcrProvider> _logger;

    public GeminiVisionOcrProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        OcrValidator validator,
        ILogger<GeminiVisionOcrProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _validator = validator;
        _logger = logger;
    }

    public string Name => "Gemini";

    public async Task<ProviderResult<OcrReceiptResult>> ExtractAsync(
        string filePath, string originalFileName, long fileSize, OcrDocumentKind kind, CancellationToken ct)
    {
        var apiKey = _configuration["Ocr:Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "Vision OCR için Gemini API anahtarı yapılandırılmamış. appsettings.json içinde " +
                "Ocr:Gemini:ApiKey değerini girin veya Ocr__Gemini__ApiKey ortam değişkenini tanımlayın; " +
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

        var model = _configuration["Ocr:Gemini:Model"];
        if (string.IsNullOrWhiteSpace(model)) model = DefaultModel;
        var maxTokens = _configuration.GetValue<int?>("Ocr:Gemini:MaxTokens") ?? DefaultMaxTokens;

        // Kademeli okuma yapılandırması. Anahtar hiç yoksa varsayılan pro kademesi devrededir;
        // bilinçli kapatmak için FallbackModel boş string ("") ya da birincil modelle aynı yapılır.
        var fallbackModel = _configuration["Ocr:Gemini:FallbackModel"] ?? DefaultFallbackModel;
        var fallbackThreshold = _configuration.GetValue<double?>("Ocr:Gemini:FallbackThreshold")
                                ?? DefaultFallbackThreshold;
        var fallbackEnabled = !string.IsNullOrWhiteSpace(fallbackModel) &&
                              !string.Equals(fallbackModel.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase);

        // ---------- 1. deneme: birincil (ucuz/hızlı) model ----------
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
        // Görseller de PDF de aynı inline_data bloğuyla gider (Gemini application/pdf destekler).
        var requestBody = new
        {
            contents = new object[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = OcrPrompt.For(kind) },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mediaType,
                                data = Convert.ToBase64String(fileBytes)
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                // JSON modu: model geçerli JSON döndürmeye zorlanır (prompt'taki
                // "SADECE geçerli JSON döndür" kuralıyla uyumlu).
                response_mime_type = "application/json",
                maxOutputTokens = maxTokens
            }
        };

        string responseText;
        string rawBody;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            // Kimlik HEADER ile taşınır; URL'de ?key= kullanılmaz (log/proxy sızıntısı riski).
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ApiUrlBase}{Uri.EscapeDataString(model.Trim())}:generateContent");
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            rawBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini OCR çağrısı başarısız. Durum: {Status}, Gövde: {Body}",
                    (int)response.StatusCode, OcrJsonParser.Truncate(rawBody, 500));

                var status = (int)response.StatusCode;
                // 400 + API_KEY_INVALID: Gemini geçersiz anahtarı 401 yerine 400 gövde
                // detayıyla bildirir; 403 ise yetkisiz/kapalı anahtar demektir.
                if (status == 403 || (status == 400 && rawBody.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)))
                {
                    return ProviderResult<OcrReceiptResult>.Fail(
                        "Gemini API anahtarı geçersiz veya yetkisiz. Ocr:Gemini:ApiKey değerini kontrol edin.");
                }

                return status switch
                {
                    429 => ProviderResult<OcrReceiptResult>.Fail(
                        "Gemini istek sınırı aşıldı (ücretsiz katman kotası dolmuş olabilir). " +
                        "Lütfen kısa bir süre sonra tekrar deneyin.", retryable: true),
                    >= 500 => ProviderResult<OcrReceiptResult>.Fail(
                        "OCR servisi şu anda yanıt vermiyor. Lütfen daha sonra tekrar deneyin.", retryable: true),
                    _ => ProviderResult<OcrReceiptResult>.Fail(
                        "Fiş okuma isteği reddedildi. Dosyanın bozuk olmadığından emin olup tekrar deneyin.")
                };
            }

            responseText = ExtractCandidateText(rawBody, out var blockedBySafety);

            if (blockedBySafety)
            {
                _logger.LogWarning("Gemini OCR yanıtı güvenlik filtresine takıldı. Gövde: {Body}",
                    OcrJsonParser.Truncate(rawBody, 300));
                return ProviderResult<OcrReceiptResult>.Fail(
                    "Fiş okunamadı: OCR servisi görseli güvenlik filtresi nedeniyle işlemedi. " +
                    "Lütfen yalnızca fiş/fatura içeren net bir görsel yükleyin.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProviderResult<OcrReceiptResult>.Fail(
                "OCR isteği zaman aşımına uğradı. Lütfen tekrar deneyin.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Gemini OCR ağ hatası.");
            return ProviderResult<OcrReceiptResult>.Fail(
                "OCR servisine ulaşılamadı. İnternet bağlantısını kontrol edip tekrar deneyin.", retryable: true);
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning("Gemini OCR yanıtında içerik yok. Gövde: {Body}", OcrJsonParser.Truncate(rawBody, 300));
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
            _logger.LogWarning(ex, "Gemini OCR yanıtı JSON olarak çözümlenemedi: {Text}", OcrJsonParser.Truncate(responseText, 300));
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
    /// Uzantıdan Gemini inline_data mime_type; desteklenmeyen türde null (HEIC desteklenmez).
    /// Faz 12: normalize edilen görsellerin adı/uzantısı ".jpg" olduğundan bunlar her zaman
    /// "image/jpeg" olarak gönderilir; PDF Gemini'de inline_data ile desteklenir (OpenAI'daki
    /// PDF reddi burada yok).
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

    /// <summary>
    /// generateContent yanıtındaki candidates[0].content.parts[0].text metnini döndürür.
    /// candidates boşsa boş string; finishReason=SAFETY ise blockedBySafety=true döner
    /// (promptFeedback.blockReason ile hiç candidate dönmemesi de güvenlik engeli sayılır).
    /// </summary>
    private static string ExtractCandidateText(string responseBody, out bool blockedBySafety)
    {
        blockedBySafety = false;

        using var doc = JsonDocument.Parse(responseBody);

        // İstek daha üretime girmeden engellendiyse candidates hiç dönmez.
        if (doc.RootElement.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var blockReason) &&
            blockReason.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(blockReason.GetString()))
        {
            blockedBySafety = true;
            return string.Empty;
        }

        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = candidates[0];

        if (first.TryGetProperty("finishReason", out var finishReason) &&
            finishReason.ValueKind == JsonValueKind.String &&
            string.Equals(finishReason.GetString(), "SAFETY", StringComparison.OrdinalIgnoreCase))
        {
            blockedBySafety = true;
            return string.Empty;
        }

        if (first.TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(text.GetString()))
                {
                    return text.GetString()!;
                }
            }
        }

        return string.Empty;
    }
}
