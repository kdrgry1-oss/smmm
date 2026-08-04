using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaliMusavir.Api.Ocr;

/// <summary>
/// Vision-LLM yanıt metnini (OcrPrompt şemasındaki JSON) LlmReceiptExtraction'a çeviren
/// ORTAK yardımcı — LlmVisionOcrProvider (Anthropic) ve OpenAiVisionOcrProvider (OpenAI)
/// aynı çözümleme davranışını paylaşır: kod bloğu işaretleri (```json ... ```) temizlenir,
/// büyük/küçük harf duyarsız alan adları ve string içinde gelen sayılar kabul edilir.
/// Çözümleme sonrası doğrulama OcrValidator.Normalize ile yapılır (burada yapılmaz).
/// </summary>
public static class OcrJsonParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Yanıt metnini LlmReceiptExtraction'a çözer. Metin JSON değilse JsonException fırlatır
    /// (çağıran sağlayıcı yakalayıp kendi Türkçe hata mesajını üretir); boş/null dönebilir.
    /// </summary>
    public static LlmReceiptExtraction? Deserialize(string responseText)
        => JsonSerializer.Deserialize<LlmReceiptExtraction>(StripCodeFences(responseText), JsonOptions);

    /// <summary>Model prompt'a rağmen ```json ... ``` bloğu döndürürse işaretleri temizler.</summary>
    public static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;
        trimmed = trimmed[(firstNewline + 1)..];

        var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? trimmed[..closing].Trim() : trimmed.Trim();
    }

    /// <summary>Log satırlarında gövdeyi kısaltmak için (hassas/uzun içerik loglanmasın).</summary>
    public static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
