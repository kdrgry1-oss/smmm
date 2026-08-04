using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// GİB e-Arşiv Portalı (earsivportal.efatura.gov.tr) düşük seviye HTTP istemcisi.
///
/// KAYNAK: mlevent/fatura (MIT) PHP kütüphanesinin uç nokta/komut haritası C#'a taşınmıştır.
/// https://github.com/mlevent/fatura
///
/// NEDEN AYRI BİR YOL? Mevcut <see cref="GibPortalProvider"/> Dijital Vergi Dairesi ekranını
/// Playwright ile KAZIR (gib-robot) ve o ekranın girişinde doğrulama kodu (captcha) vardır.
/// e-Arşiv Portalı ise AYRI bir sistemdir: girişi düz form POST'udur (assoscmd/userid/sifre),
/// captcha İÇERMEZ ve listeleme/oluşturma işlemleri JSON döner. Bu yüzden burada aşılan bir
/// erişim kontrolü YOKTUR — mükellefin kendi kimliğiyle, portalın kendi arayüzünün kullandığı
/// uçlar çağrılır.
///
/// GÜVENLİK KURALI KORUNUR: SMS ile imzalama adımı (belgeyi resmîleştiren adım) İNSAN
/// ONAYLIDIR — kod mükellefin telefonuna gider, kullanıcı panele girer. Sistem bu kodu
/// üretmez/tahmin etmez. Aynı şekilde captcha da hiçbir yerde çözülmez.
///
/// OTURUM: login → token; token her dispatch çağrısında form alanı olarak gider.
/// Token bellekte tutulur, DİSKE YAZILMAZ ve loglanmaz.
/// </summary>
public class EArsivPortalClient
{
    public const string HttpClientName = "earsiv";

    private const string LiveBaseUrl = "https://earsivportal.efatura.gov.tr";
    private const string TestBaseUrl = "https://earsivportaltest.efatura.gov.tr";

    private const string LoginPath = "/earsiv-services/assos-login";
    private const string DispatchPath = "/earsiv-services/dispatch";
    private const string DownloadPath = "/earsiv-services/download";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EArsivPortalClient> _logger;

    public EArsivPortalClient(IHttpClientFactory httpClientFactory, ILogger<EArsivPortalClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static string BaseUrl(bool testEnv) => testEnv ? TestBaseUrl : LiveBaseUrl;

    // ---------- Oturum ----------

    /// <summary>
    /// Portala giriş yapar ve oturum token'ını döner. Test ortamında komut "login",
    /// canlıda "anologin"dir (kütüphanedeki ayrım birebir korunmuştur).
    /// </summary>
    public async Task<ProviderResult<string>> LoginAsync(
        string username, string password, bool testEnv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return ProviderResult<string>.Fail(
                "GİB e-Arşiv kullanıcı adı veya şifresi tanımlı değil. Mükellef kartından entegrasyonu düzenleyin.");

        var form = new Dictionary<string, string>
        {
            ["assoscmd"] = testEnv ? "login" : "anologin",
            ["userid"] = username,
            ["sifre"] = password,
            ["sifre2"] = password,
            ["parola"] = "1",
        };

        var result = await PostFormAsync(BaseUrl(testEnv) + LoginPath, form, ct);
        if (!result.Success || result.Data is null)
            return ProviderResult<string>.Fail(result.ErrorMessage!, result.Retryable);

        if (!result.Data.Value.TryGetProperty("token", out var tokenEl) ||
            tokenEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenEl.GetString()))
        {
            // Şifre/kullanıcı adı hatasında portal token vermez; mesajı kullanıcıya taşıyalım.
            var portalMessage = ExtractPortalMessage(result.Data.Value);
            return ProviderResult<string>.Fail(
                string.IsNullOrWhiteSpace(portalMessage)
                    ? "GİB e-Arşiv Portalına giriş yapılamadı. Kullanıcı adı/şifreyi kontrol edin."
                    : $"GİB e-Arşiv Portalına giriş yapılamadı: {portalMessage}");
        }

        return ProviderResult<string>.Ok(tokenEl.GetString()!);
    }

    /// <summary>Oturumu kapatır. Hata durumu kullanıcıya yansıtılmaz (en iyi çaba).</summary>
    public async Task LogoutAsync(string token, bool testEnv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            var form = new Dictionary<string, string> { ["assoscmd"] = "logout", ["token"] = token };
            await PostFormAsync(BaseUrl(testEnv) + LoginPath, form, ct);
        }
        catch (Exception ex)
        {
            // Oturum kapatma başarısızlığı iş akışını bozmaz; token nasılsa süresi dolunca düşer.
            _logger.LogDebug("e-Arşiv oturum kapatma başarısız (yok sayıldı): {Message}", ex.Message);
        }
    }

    // ---------- Genel dispatch ----------

    /// <summary>
    /// Portalın tüm iş komutlarının geçtiği tek uç. <paramref name="payload"/> "jp" alanında
    /// JSON olarak gider. Yanıtın "data" düğümü döner (yoksa kökün kendisi).
    /// </summary>
    public async Task<ProviderResult<JsonElement>> DispatchAsync(
        string token, string cmd, string pageName, object payload, bool testEnv, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["cmd"] = cmd,
            ["callid"] = Guid.NewGuid().ToString(),
            ["pageName"] = pageName,
            ["token"] = token,
            ["jp"] = JsonSerializer.Serialize(payload, PayloadJsonOptions),
        };

        var result = await PostFormAsync(BaseUrl(testEnv) + DispatchPath, form, ct);
        if (!result.Success || result.Data is null)
            return ProviderResult<JsonElement>.Fail(result.ErrorMessage!, result.Retryable);

        var root = result.Data.Value;

        // Portal hata biçimleri: {"error":"..."} veya {"data":{"hata":"..."}}
        var portalError = ExtractPortalError(root);
        if (!string.IsNullOrWhiteSpace(portalError))
            return ProviderResult<JsonElement>.Fail($"GİB e-Arşiv Portalı hata döndürdü: {portalError}");

        if (root.TryGetProperty("data", out var dataEl))
            return ProviderResult<JsonElement>.Ok(dataEl.Clone());

        return ProviderResult<JsonElement>.Ok(root.Clone());
    }

    /// <summary>Belge indirme bağlantısı (PDF/HTML) — token ve ETTN ile.</summary>
    public string BuildDownloadUrl(string token, string ettn, bool testEnv, bool asHtml = false) =>
        $"{BaseUrl(testEnv)}{DownloadPath}?token={Uri.EscapeDataString(token)}" +
        $"&ettn={Uri.EscapeDataString(ettn)}&belgeTip=FATURA&onayDurumu={Uri.EscapeDataString("Onaylandı")}" +
        (asHtml ? "&cmd=EARSIV_PORTAL_BELGE_GOSTER" : "&cmd=EARSIV_PORTAL_BELGE_INDIR");

    // ---------- HTTP ----------

    private async Task<ProviderResult<JsonElement?>> PostFormAsync(
        string url, Dictionary<string, string> form, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var content = new FormUrlEncodedContent(form);
            content.Headers.ContentType!.CharSet = "UTF-8";

            using var response = await http.PostAsync(url, content, ct);

            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                return ProviderResult<JsonElement?>.Fail(
                    "GİB e-Arşiv Portalı şu anda yanıt vermiyor. İşlem kuyruğa alındı, daha sonra tekrar denenecek.",
                    retryable: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult<JsonElement?>.Fail(
                    $"GİB e-Arşiv Portalı beklenmeyen bir yanıt verdi (HTTP {(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return ProviderResult<JsonElement?>.Fail("GİB e-Arşiv Portalı boş yanıt döndü.", retryable: true);

            try
            {
                using var doc = JsonDocument.Parse(body);
                return ProviderResult<JsonElement?>.Ok(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // Oturum düşünce portal HTML giriş sayfası döndürebilir.
                return ProviderResult<JsonElement?>.Fail(
                    "GİB e-Arşiv Portalı beklenen JSON yerine sayfa döndürdü — oturum düşmüş olabilir. " +
                    "Lütfen tekrar deneyin.",
                    retryable: true);
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProviderResult<JsonElement?>.Fail(
                "GİB e-Arşiv Portalı zaman aşımına uğradı. Daha sonra tekrar denenecek.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("e-Arşiv Portalı bağlantı hatası: {Message}", ex.Message);
            return ProviderResult<JsonElement?>.Fail(
                "GİB e-Arşiv Portalına bağlanılamadı. İnternet/erişim sorunu olabilir, daha sonra tekrar denenecek.",
                retryable: true);
        }
    }

    // ---------- Yardımcılar ----------

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        // Portal Türkçe alan adlarını birebir bekler; camelCase dönüşümü YAPILMAZ.
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string? ExtractPortalError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("error", out var errEl) &&
            errEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(errEl.GetString()))
        {
            return errEl.GetString();
        }

        if (root.TryGetProperty("data", out var dataEl) &&
            dataEl.ValueKind == JsonValueKind.Object &&
            dataEl.TryGetProperty("hata", out var hataEl) &&
            hataEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(hataEl.GetString()))
        {
            return hataEl.GetString();
        }

        return null;
    }

    private static string? ExtractPortalMessage(JsonElement root)
    {
        var error = ExtractPortalError(root);
        if (!string.IsNullOrWhiteSpace(error)) return error;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("mesaj", out var msgEl) &&
            msgEl.ValueKind == JsonValueKind.String)
        {
            return msgEl.GetString();
        }

        return null;
    }

    /// <summary>Portal tarih biçimi: dd/MM/yyyy.</summary>
    public static string FormatDate(DateTime value) =>
        value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Portalın döndürdüğü dd/MM/yyyy tarihini ayrıştırır; başarısızsa null.</summary>
    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(
            value.Trim(), new[] { "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Portalın "1.234,56" biçimli tutarlarını decimal'e çevirir.</summary>
    public static decimal ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;

        var cleaned = value.Trim()
            .Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty);

        // Türkçe biçim: nokta binlik, virgül ondalık.
        cleaned = cleaned.Replace(".", string.Empty).Replace(',', '.');

        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }
}
