using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>Mükellef ↔ entegratör bağlantısı yönetimi. Anahtar şifreli saklanır, maskeli döner.</summary>
[ApiController]
[Route("api/clients/{clientId:guid}/integration")]
[Authorize]
public class IntegrationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly GibRobotClient _gibRobot;

    public IntegrationsController(
        AppDbContext db, ISecretProtector secretProtector, GibRobotClient gibRobot)
    {
        _db = db;
        _secretProtector = secretProtector;
        _gibRobot = gibRobot;
    }

    /// <summary>Mükellefin entegrasyonunu getirir (anahtar maskeli).</summary>
    [HttpGet]
    public async Task<ActionResult<IntegrationResponse>> Get(Guid clientId)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var integration = await _db.ClientIntegrations
            .AsNoTracking()
            .FirstOrDefaultAsync(ci => ci.ClientId == clientId);

        if (integration is null)
            return NotFound(new { message = "Bu mükellef için entegrasyon tanımlı değil." });

        return Ok(ToResponse(integration));
    }

    /// <summary>
    /// Faz 15 — Tenant'ın TÜM mükellef entegrasyonlarını tek istekte döndürür (anahtarlar maskeli).
    /// Mükellef listesinde sağlayıcı rozetini ve GİB Portalı "Elle doğrulama gerekli" uyarısını
    /// göstermek için kullanılır (mükellef başına ayrı istek atılmaz).
    /// </summary>
    [HttpGet("/api/client-integrations")]
    public async Task<ActionResult<List<IntegrationResponse>>> ListAll(CancellationToken ct = default)
    {
        var integrations = await _db.ClientIntegrations.AsNoTracking().ToListAsync(ct);
        return Ok(integrations.Select(ToResponse).ToList());
    }

    /// <summary>Entegrasyon oluşturur veya günceller (mükellef başına tek kayıt).</summary>
    [HttpPost]
    public async Task<ActionResult<IntegrationResponse>> Save(Guid clientId, [FromBody] IntegrationSaveRequest request)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var apiKey = request.ApiKey?.Trim();
        var username = request.Username?.Trim();
        var password = request.Password; // şifre bilinçli olarak trim'lenmez
        var endpoint = request.Endpoint?.Trim();
        var integration = await _db.ClientIntegrations.FirstOrDefaultAsync(ci => ci.ClientId == clientId);
        var isNew = integration is null;

        // Mock için anahtar opsiyonel; Nilvera için zorunlu (yeni anahtar girilmediyse
        // mevcut kayıtlı anahtar korunur — ör. yalnızca test ortamı değişikliği).
        // Kayıtlı sır yalnızca AYNI sağlayıcıya aitse korunur (Nilvera anahtarı ile
        // Uyumsoft JSON kimliği aynı kolonda saklandığından sağlayıcı değişiminde yeniden istenir).
        if (request.Provider == IntegrationProvider.Nilvera)
        {
            var hasStoredKey = integration is not null &&
                               integration.Provider == IntegrationProvider.Nilvera &&
                               !string.IsNullOrEmpty(integration.ApiKeyEncrypted);

            if (string.IsNullOrWhiteSpace(apiKey) && !hasStoredKey)
                return BadRequest(new { message = "Nilvera için API anahtarı zorunludur." });

            if (!string.IsNullOrWhiteSpace(apiKey) && apiKey.Length < 10)
                return BadRequest(new { message = "API anahtarı geçersiz görünüyor (en az 10 karakter olmalı)." });
        }

        // Paraşüt (REST) — kimlik alanları diğerlerinden farklıdır: Client ID + Client Secret +
        // e-posta (Username alanı) + şifre + Firma ID. Beşi birlikte girilir; yeni kimlik
        // girilmediyse mevcut kayıtlı kimlik korunur (ör. yalnızca test ortamı değişikliği).
        var parasutClientId = request.ParasutClientId?.Trim();
        var parasutClientSecret = request.ParasutClientSecret?.Trim();
        var parasutCompanyId = request.ParasutCompanyId?.Trim();
        var parasutAllProvided =
            !string.IsNullOrWhiteSpace(parasutClientId) &&
            !string.IsNullOrWhiteSpace(parasutClientSecret) &&
            !string.IsNullOrWhiteSpace(username) &&
            !string.IsNullOrWhiteSpace(password) &&
            !string.IsNullOrWhiteSpace(parasutCompanyId);

        if (request.Provider == IntegrationProvider.Parasut)
        {
            var hasStoredParasutCredentials = integration is not null &&
                                              integration.Provider == IntegrationProvider.Parasut &&
                                              !string.IsNullOrEmpty(integration.ApiKeyEncrypted);

            var anyProvided = !string.IsNullOrWhiteSpace(parasutClientId) ||
                              !string.IsNullOrWhiteSpace(parasutClientSecret) ||
                              !string.IsNullOrWhiteSpace(username) ||
                              !string.IsNullOrWhiteSpace(password) ||
                              !string.IsNullOrWhiteSpace(parasutCompanyId);

            if (!anyProvided && !hasStoredParasutCredentials)
            {
                return BadRequest(new
                {
                    message = "Paraşüt için Client ID, Client Secret, e-posta, şifre ve Firma ID zorunludur."
                });
            }

            // Kimlik değiştirilecekse beş alan birlikte girilmelidir (yarım güncelleme olmaz).
            if (anyProvided && !parasutAllProvided)
            {
                return BadRequest(new
                {
                    message = "Paraşüt kimliğini kaydetmek için Client ID, Client Secret, e-posta, şifre ve Firma ID alanlarını birlikte girin."
                });
            }
        }
        // Uyumsoft / Doğan Dönüşüm / eLogo / EDM için kullanıcı adı + şifre zorunlu (yeni kimlik
        // girilmediyse mevcut kayıtlı kimlik korunur — ör. yalnızca test ortamı değişikliği).
        else if (IsCredentialProvider(request.Provider))
        {
            var providerLabel = ProviderLabel(request.Provider);
            var hasStoredCredentials = integration is not null &&
                                       integration.Provider == request.Provider &&
                                       !string.IsNullOrEmpty(integration.ApiKeyEncrypted);

            var hasUsername = !string.IsNullOrWhiteSpace(username);
            var hasPassword = !string.IsNullOrWhiteSpace(password);

            if (!hasUsername && !hasPassword && !hasStoredCredentials)
                return BadRequest(new { message = $"{providerLabel} için kullanıcı adı ve şifre zorunludur." });

            // Kimlik değiştirilecekse iki alan birlikte girilmelidir (yarım güncelleme olmaz).
            if (hasUsername != hasPassword)
                return BadRequest(new { message = $"{providerLabel} için kullanıcı adı ve şifre birlikte girilmelidir." });

            // Opsiyonel servis adresi: boşsa varsayılan uç kullanılır; girildiyse
            // geçerli bir http/https URL olmalıdır.
            if (!string.IsNullOrEmpty(endpoint) &&
                (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
                 (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps)))
            {
                return BadRequest(new { message = "Geçerli bir servis adresi girin (https://... )." });
            }

            // Uyumsoft belge türü bayrakları: en az biri seçili olmalıdır (null = kayıtlı
            // değer korunur, o yüzden yalnız İKİSİ DE açıkça false gönderildiğinde reddedilir).
            if (request.Provider == IntegrationProvider.Uyumsoft &&
                request.UyumsoftEsmm == false && request.UyumsoftEinvoice == false)
            {
                return BadRequest(new
                {
                    message = "Uyumsoft için en az bir belge türü seçin (e-Fatura / e-Arşiv veya e-SMM)."
                });
            }
        }

        if (integration is null)
        {
            integration = new ClientIntegration { ClientId = clientId };
            _db.ClientIntegrations.Add(integration);
        }

        var previousProvider = integration.Provider;
        integration.Provider = request.Provider;
        integration.UseTestEnvironment = request.UseTestEnvironment;

        if (request.Provider == IntegrationProvider.Parasut)
        {
            if (parasutAllProvided)
            {
                // Paraşüt kimliği JSON'a paketlenip şifrelenir:
                // {"clientId","clientSecret","username","password","companyId"}.
                integration.ApiKeyEncrypted = _secretProtector.Protect(
                    ParasutProvider.PackCredentials(
                        parasutClientId!, parasutClientSecret!, username!, password!, parasutCompanyId!));
            }
            // Kimlik yeniden girilmediyse kayıtlı JSON olduğu gibi korunur
            // (Paraşüt'te servis adresi alanı yoktur → endpoint yeniden paketleme gerekmez).
        }
        else if (IsCredentialProvider(request.Provider))
        {
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                if (request.Provider == IntegrationProvider.Uyumsoft)
                {
                    // Uyumsoft: e-SMM / e-Fatura bayrakları pakete eklenir. İstekte null gelen
                    // bayrak AYNI sağlayıcının kayıtlı paketinden korunur (geriye uyum: eski
                    // kayıtlarda bayrak yok = yalnız e-Fatura).
                    var (esmm, einvoice) = ResolveUyumsoftFlags(
                        request, previousProvider, integration.ApiKeyEncrypted);
                    integration.ApiKeyEncrypted = _secretProtector.Protect(
                        UyumsoftProvider.PackCredentials(username!, password!, endpoint, esmm, einvoice));
                }
                else
                {
                    // Kimlik JSON'a paketlenip şifrelenir: {"username","password","endpoint"?}.
                    // (Düz metin hiçbir yerde saklanmaz/loglanmaz.)
                    integration.ApiKeyEncrypted = _secretProtector.Protect(
                        PackCredentials(request.Provider, username!, password!, endpoint));
                }
            }
            else if (!string.IsNullOrEmpty(integration.ApiKeyEncrypted))
            {
                // Kimlik yeniden girilmedi ama servis adresi (Uyumsoft'ta ayrıca e-SMM
                // bayrakları) değişmiş olabilir → kayıtlı kimlik çözülür, istekle gelen
                // değerlerle yeniden paketlenir (boş endpoint = varsayılan uca dönüş).
                try
                {
                    var stored = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
                    if (request.Provider == IntegrationProvider.Uyumsoft)
                    {
                        if (UyumsoftProvider.TryParseCredentials(
                                stored, out var uyUser, out var uyPass, out var uyEndpoint,
                                out var storedEsmm, out var storedEinvoice))
                        {
                            var newEndpoint = string.IsNullOrEmpty(endpoint) ? null : endpoint;
                            var newEsmm = request.UyumsoftEsmm ?? storedEsmm;
                            var newEinvoice = request.UyumsoftEinvoice ?? storedEinvoice;
                            if (!newEsmm && !newEinvoice) newEinvoice = true; // en az bir belge türü

                            if (!string.Equals(uyEndpoint, newEndpoint, StringComparison.Ordinal) ||
                                newEsmm != storedEsmm || newEinvoice != storedEinvoice)
                            {
                                integration.ApiKeyEncrypted = _secretProtector.Protect(
                                    UyumsoftProvider.PackCredentials(
                                        uyUser, uyPass, newEndpoint, newEsmm, newEinvoice));
                            }
                        }
                    }
                    else if (TryParseCredentials(
                            request.Provider, stored, out var storedUser, out var storedPass, out var storedEndpoint))
                    {
                        var newEndpoint = string.IsNullOrEmpty(endpoint) ? null : endpoint;
                        if (!string.Equals(storedEndpoint, newEndpoint, StringComparison.Ordinal))
                        {
                            integration.ApiKeyEncrypted = _secretProtector.Protect(
                                PackCredentials(request.Provider, storedUser, storedPass, newEndpoint));
                        }
                    }
                }
                catch
                {
                    // Çözülemeyen sır olduğu gibi bırakılır; kullanıcı kimliği yeniden kaydedebilir.
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Yeni anahtar girildi → şifrele. (Düz metin hiçbir yerde saklanmaz/loglanmaz.)
            integration.ApiKeyEncrypted = _secretProtector.Protect(apiKey);
        }
        else if (request.Provider == IntegrationProvider.Mock ||
                 previousProvider != request.Provider)
        {
            // Mock'a geçişte ya da sağlayıcı değişiminde eski sır tutulmaz.
            integration.ApiKeyEncrypted = null;
        }

        await _db.SaveChangesAsync();

        var response = ToResponse(integration);
        return isNew ? CreatedAtAction(nameof(Get), new { clientId }, response) : Ok(response);
    }

    /// <summary>
    /// Faz 15 — Bağlantı testi. Şu an yalnızca GİB Portalı sağlayıcısında desteklenir:
    /// gib-robot servisinin /login-test ucu çağrılır (yalnız giriş denenir, belge çekilmez).
    ///
    /// Kimlik gövdede gönderilebilir (henüz kaydedilmemiş bilgilerle test); gönderilmezse
    /// kayıtlı kimlik kullanılır. Şifre yanıtta ve loglarda ASLA yer almaz.
    ///
    /// GÜVENLİK: GİB captcha/SMS gibi ek doğrulama isterse robot çözmeye ÇALIŞMAZ;
    /// yanıt requiresManual=true döner.
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult<IntegrationTestResponse>> Test(
        Guid clientId,
        [FromBody] IntegrationTestRequest? request,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var integration = await _db.ClientIntegrations
            .AsNoTracking()
            .FirstOrDefaultAsync(ci => ci.ClientId == clientId, ct);

        // Test edilecek sağlayıcı: istekte belirtilen > kayıtlı.
        var provider = request?.Provider ?? integration?.Provider;
        if (provider is null)
            return BadRequest(new { message = "Önce bir sağlayıcı seçip kaydedin." });

        if (provider != IntegrationProvider.GibPortal)
        {
            return BadRequest(new
            {
                message = $"{ProviderLabel(provider.Value)} için bağlantı testi henüz desteklenmiyor. " +
                          "Bağlantıyı \"Senkronize Et\" ile deneyebilirsiniz."
            });
        }

        // Kimlik: gövdede geldiyse onunla, gelmediyse kayıtlı kimlikle test edilir.
        var username = request?.Username?.Trim();
        var password = request?.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            if (integration is null ||
                integration.Provider != IntegrationProvider.GibPortal ||
                string.IsNullOrEmpty(integration.ApiKeyEncrypted))
            {
                return BadRequest(new
                {
                    message = "Test için GİB kullanıcı kodu ve şifresini girin (veya önce entegrasyonu kaydedin)."
                });
            }

            string stored;
            try
            {
                stored = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
            }
            catch
            {
                return BadRequest(new
                {
                    message = "Kayıtlı GİB kimliği çözülemedi. Lütfen kullanıcı kodu ve şifreyi yeniden kaydedin."
                });
            }

            if (!GibPortalProvider.TryParseCredentials(stored, out var storedUser, out var storedPass))
            {
                return BadRequest(new
                {
                    message = "Kayıtlı GİB kimliği eksik. Lütfen kullanıcı kodu ve şifreyi yeniden kaydedin."
                });
            }

            username = storedUser;
            password = storedPass;
        }

        var result = await _gibRobot.LoginTestAsync(username!, password!, ct);

        return Ok(new IntegrationTestResponse
        {
            Success = result.Ok,
            RequiresManual = result.RequiresManual,
            NeedsCaptchaLogin = result.NeedsCaptchaLogin,
            Message = result.Ok
                ? (result.Message ?? "GİB portalına bağlantı başarılı.")
                : (result.NeedsCaptchaLogin
                    ? (result.Message ?? "GİB oturumu yenilenmeli — doğrulama kodu girilmeli.")
                    : result.RequiresManual
                        ? GibPortalProvider.BuildManualVerificationMessage(result.Message)
                        : result.Message ?? "GİB portalına bağlanılamadı. Lütfen bilgileri kontrol edin.")
        });
    }

    /// <summary>
    /// Faz 17 — GİB insan destekli giriş, 1. aşama. Robot GİB giriş ekranını açar, KAYITLI
    /// (şifreli) kimliği doldurur ve "Doğrulama Kodu" (captcha) görselini döndürür; kullanıcı
    /// kodu okuyup complete ucuyla girer.
    ///
    /// ŞİFRE GÜVENLİĞİ: şifre yalnız şifreli kayıttan çözülüp robota İÇ AĞDA bir kez gider;
    /// bu ucun yanıtında ve loglarda ASLA yer almaz.
    /// </summary>
    [HttpPost("gib-login/auto-start")]
    public async Task<IActionResult> StartAutomaticGibLogin(Guid clientId,
        [FromServices] MaliMusavir.Api.Services.OutboundJobService jobs, CancellationToken ct)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return NotFound(new { message = "Mükellef bulunamadı." });
        var credentials = await ResolveGibCredentialsAsync(clientId, ct);
        if (credentials.Error is not null) return BadRequest(new { message = credentials.Error });
        var job = await jobs.EnqueueAsync(client.TenantId,
            MaliMusavir.Api.Services.OutboundJobService.TypeTebligatSync,
            new MaliMusavir.Api.Services.OutboundJobService.TebligatPayload { ClientId = clientId }, ct);
        return Ok(new { jobId = job.Id });
    }

    [HttpGet("gib-login/auto-status/{jobId:guid}")]
    public async Task<IActionResult> AutomaticGibLoginStatus(Guid clientId, Guid jobId, CancellationToken ct)
    {
        var job = await _db.OutboundJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Type != MaliMusavir.Api.Services.OutboundJobService.TypeTebligatSync)
            return NotFound(new { message = "Giriş işi bulunamadı." });
        var payload = System.Text.Json.JsonSerializer.Deserialize<MaliMusavir.Api.Services.OutboundJobService.TebligatPayload>(
            job.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        if (payload?.ClientId != clientId) return NotFound(new { message = "Giriş işi bulunamadı." });
        return Ok(new { status = job.Status.ToString(), message = job.LastError });
    }

    [HttpPost("gib-login/begin")]
    public async Task<ActionResult<GibLoginBeginResponse>> GibLoginBegin(
        Guid clientId, [FromQuery] bool manual = false, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var credentials = await ResolveGibCredentialsAsync(clientId, ct);
        if (credentials.Error is not null)
            return BadRequest(new { message = credentials.Error });

        // Existing automatic solver remains the default; explicit manual fallback skips it.
        if (!manual)
        try
        {
            var autoResult = await _gibRobot.AutoLoginAsync(credentials.Username!, credentials.Password!, ct);
            if (autoResult.Ok)
            {
                await ClearGibManualWarningAsync(clientId, ct);
                return Ok(new GibLoginBeginResponse
                {
                    Success = true,
                    AlreadyLoggedIn = true,
                    Message = "GİB oturumu otomatik olarak açıldı (OCR ile çözüldü). Çekimler devam edebilir."
                });
            }
        }
        catch
        {
            // Otomatik giriş hatasında manuel akışa düşülür
        }

        // 2. Otomatik OCR çözemezse manuel fallback:
        var result = await _gibRobot.BeginLoginAsync(credentials.Username!, credentials.Password!, ct);

        // Kayıtlı oturum hâlâ geçerliyse koda gerek yok → varsa eski uyarı temizlenir.
        if (result.Ok && result.AlreadyLoggedIn)
            await ClearGibManualWarningAsync(clientId, ct);

        return Ok(new GibLoginBeginResponse
        {
            Success = result.Ok,
            Message = result.Message ?? (result.Ok
                ? "Doğrulama kodu bekleniyor. Görseldeki kodu okuyup girin."
                : "GİB giriş ekranı açılamadı. Lütfen daha sonra tekrar deneyin."),
            PendingId = result.PendingId,
            CaptchaImage = result.CaptchaImageBase64,
            ExpiresInSeconds = result.ExpiresInSeconds,
            AlreadyLoggedIn = result.AlreadyLoggedIn
        });
    }

    /// <summary>
    /// Faz 17 — GİB insan destekli giriş, 2. aşama: kullanıcının okuduğu doğrulama kodu
    /// robota iletilir. Başarıda robot oturumu 12 saat saklar ve entegrasyondaki
    /// "Elle doğrulama gerekli" uyarısı temizlenir. Hatalı kodda AYNI pendingId ile
    /// YENİ captcha görseli döner (en fazla 3 deneme).
    /// </summary>
    [HttpPost("gib-login/complete")]
    public async Task<ActionResult<GibLoginCompleteResponse>> GibLoginComplete(
        Guid clientId, [FromBody] GibLoginCompleteRequest request, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var result = await _gibRobot.CompleteLoginAsync(request.PendingId.Trim(), request.Code.Trim(), ct);

        // Başarılı giriş → entegrasyondaki "Elle doğrulama gerekli" uyarısı temizlenir.
        if (result.Ok)
            await ClearGibManualWarningAsync(clientId, ct);

        return Ok(new GibLoginCompleteResponse
        {
            Success = result.Ok,
            Message = result.Message ?? (result.Ok
                ? "Giriş başarılı, oturum kaydedildi."
                : "Giriş tamamlanamadı. Lütfen tekrar deneyin."),
            CaptchaImage = result.CaptchaImageBase64,
            AttemptsLeft = result.AttemptsLeft,
            PendingExpired = result.PendingExpired
        });
    }

    /// <summary>
    /// Kayıtlı GİB Portalı kimliğini şifreli kolondan çözer. Hata varsa Türkçe mesaj döner
    /// (şifre hiçbir çağırana loglanmaz/yanıtlanmaz; yalnız robota gider).
    /// </summary>
    private async Task<(string? Username, string? Password, string? Error)> ResolveGibCredentialsAsync(
        Guid clientId, CancellationToken ct)
    {
        var integration = await _db.ClientIntegrations
            .AsNoTracking()
            .FirstOrDefaultAsync(ci => ci.ClientId == clientId, ct);

        if (integration is null ||
            integration.Provider != IntegrationProvider.GibPortal ||
            string.IsNullOrEmpty(integration.ApiKeyEncrypted))
        {
            return (null, null,
                "Bu mükellef için kayıtlı GİB Portalı kimliği yok. " +
                "Önce entegrasyon ekranından \"GİB Portalı (entegratör yok)\" sağlayıcısını seçip " +
                "kullanıcı kodu ve şifreyi kaydedin.");
        }

        string plain;
        try
        {
            plain = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
        }
        catch
        {
            return (null, null, "Kayıtlı GİB kimliği çözülemedi. Lütfen kullanıcı kodu ve şifreyi yeniden kaydedin.");
        }

        if (!GibPortalProvider.TryParseCredentials(plain, out var username, out var password))
            return (null, null, "Kayıtlı GİB kimliği eksik. Kullanıcı kodu ve şifreyi yeniden kaydedin.");

        return (username, password, null);
    }

    /// <summary>
    /// Entegrasyon kaydındaki "Elle doğrulama gerekli / GİB oturumu yenilenmeli" uyarısını
    /// temizler (başarılı doğrulama kodu girişi sonrası). Şema değişikliği yoktur: uyarı
    /// kimlik JSON'unun içinde taşınır (OutboundJobService.SetGibPortalWarningAsync kalıbı).
    /// </summary>
    private async Task ClearGibManualWarningAsync(Guid clientId, CancellationToken ct)
    {
        var integration = await _db.ClientIntegrations
            .FirstOrDefaultAsync(ci => ci.ClientId == clientId, ct);

        if (integration is null ||
            integration.Provider != IntegrationProvider.GibPortal ||
            string.IsNullOrEmpty(integration.ApiKeyEncrypted))
        {
            return;
        }

        string plain;
        try
        {
            plain = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
        }
        catch
        {
            return; // çözülemeyen sırra dokunulmaz; kullanıcı kimliği yeniden kaydedebilir
        }

        if (!GibPortalProvider.TryParseCredentials(
                plain, out var username, out var password, out var existingWarning, out _))
        {
            return;
        }

        if (existingWarning is null) return; // temizlenecek uyarı yok

        integration.ApiKeyEncrypted = _secretProtector.Protect(
            GibPortalProvider.PackCredentials(username, password, null, null));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Entegrasyonu kaldırır. Senkronlanmış faturalar silinmez.</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete(Guid clientId)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var integration = await _db.ClientIntegrations.FirstOrDefaultAsync(ci => ci.ClientId == clientId);
        if (integration is null)
            return NotFound(new { message = "Bu mükellef için entegrasyon tanımlı değil." });

        _db.ClientIntegrations.Remove(integration);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    private IntegrationResponse ToResponse(ClientIntegration integration)
    {
        // Maskeleme için sır bellek içinde kısa süreliğine çözülür; yanıtta yalnızca
        // maskeli hali yer alır (Nilvera: son 4 hane; Uyumsoft: kullanıcı adının ilk 2 + son 2 karakteri).
        string? plainKey = null;
        if (!string.IsNullOrEmpty(integration.ApiKeyEncrypted))
        {
            try
            {
                plainKey = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
            }
            catch
            {
                // Çözülemeyen sır maskede "????" olarak gösterilir; kullanıcı yeniden kaydedebilir.
                plainKey = "????";
            }
        }

        if (IsCredentialProvider(integration.Provider))
        {
            // Uyumsoft/Doğan Dönüşüm'de saklanan değer JSON kimlik paketidir; maskede yalnızca
            // kullanıcı adı kullanılır. Servis adresi gizli değildir, maskesiz döner.
            // Uyumsoft'ta e-SMM / e-Fatura bayrakları da (gizli değildir) yanıtta döner.
            string? username;
            string? endpoint = null;
            bool? uyumsoftEsmm = null;
            bool? uyumsoftEinvoice = null;
            string? manualWarning = null;
            DateTime? manualWarningAt = null;

            // GİB Portalı: kimlik JSON'unda taşınan "elle doğrulama gerekli" uyarısı da
            // döner (gizli değildir; UI'da sarı rozet olarak gösterilir).
            if (integration.Provider == IntegrationProvider.GibPortal)
            {
                if (GibPortalProvider.TryParseCredentials(
                        plainKey, out var gibUsername, out _, out var gibWarning, out var gibWarningAt))
                {
                    username = gibUsername;
                    manualWarning = gibWarning;
                    manualWarningAt = gibWarningAt;
                }
                else
                {
                    username = plainKey is null ? null : "????";
                }

                return IntegrationResponse.FromEntity(
                    integration, plainApiKey: null, plainUsername: username, endpoint: null,
                    manualWarning: manualWarning, manualWarningAt: manualWarningAt);
            }

            if (integration.Provider == IntegrationProvider.Uyumsoft &&
                UyumsoftProvider.TryParseCredentials(
                    plainKey, out var uyUsername, out _, out var uyEndpoint,
                    out var parsedEsmm, out var parsedEinvoice))
            {
                username = uyUsername;
                endpoint = uyEndpoint;
                uyumsoftEsmm = parsedEsmm;
                uyumsoftEinvoice = parsedEinvoice;
            }
            else if (integration.Provider != IntegrationProvider.Uyumsoft &&
                     TryParseCredentials(integration.Provider, plainKey, out var parsedUsername, out _, out var parsedEndpoint))
            {
                username = parsedUsername;
                endpoint = parsedEndpoint;
            }
            else
            {
                username = plainKey is null ? null : "????";
            }
            return IntegrationResponse.FromEntity(
                integration, plainApiKey: null, plainUsername: username, endpoint: endpoint,
                uyumsoftEsmm: uyumsoftEsmm, uyumsoftEinvoice: uyumsoftEinvoice);
        }

        return IntegrationResponse.FromEntity(integration, plainKey);
    }

    /// <summary>
    /// Uyumsoft e-SMM / e-Fatura bayraklarını çözer: istekteki değer öncelikli; null ise AYNI
    /// sağlayıcının kayıtlı paketindeki bayrak, o da yoksa varsayılan (esmm=false,
    /// einvoice=true — eski kayıtlar e-fatura mükellefidir). İki bayrak birden false olamaz.
    /// </summary>
    private (bool Esmm, bool Einvoice) ResolveUyumsoftFlags(
        IntegrationSaveRequest request, IntegrationProvider previousProvider, string? storedEncrypted)
    {
        var esmm = request.UyumsoftEsmm ?? false;
        var einvoice = request.UyumsoftEinvoice ?? true;

        if ((request.UyumsoftEsmm is null || request.UyumsoftEinvoice is null) &&
            previousProvider == IntegrationProvider.Uyumsoft &&
            !string.IsNullOrEmpty(storedEncrypted))
        {
            try
            {
                var stored = _secretProtector.Unprotect(storedEncrypted);
                if (UyumsoftProvider.TryParseCredentials(
                        stored, out _, out _, out _, out var storedEsmm, out var storedEinvoice))
                {
                    esmm = request.UyumsoftEsmm ?? storedEsmm;
                    einvoice = request.UyumsoftEinvoice ?? storedEinvoice;
                }
            }
            catch
            {
                // Çözülemeyen eski paket → varsayılanlar kullanılır.
            }
        }

        if (!esmm && !einvoice) einvoice = true;
        return (esmm, einvoice);
    }

    // ---------- Kimlik-tabanlı sağlayıcı yardımcıları (Uyumsoft/Doğan/eLogo/EDM/Paraşüt) ----------

    /// <summary>
    /// Kimliği API anahtarı DEĞİL, alan bazlı JSON kimlik paketi olan sağlayıcılar.
    /// Uyumsoft/Doğan/eLogo/EDM: kullanıcı adı + şifre (+opsiyonel servis adresi).
    /// Paraşüt: Client ID + Client Secret + e-posta + şifre + Firma ID (Save'de ayrı ele alınır).
    /// </summary>
    private static bool IsCredentialProvider(IntegrationProvider provider) =>
        provider is IntegrationProvider.Uyumsoft or IntegrationProvider.DoganDonusum
                 or IntegrationProvider.Elogo or IntegrationProvider.Edm
                 or IntegrationProvider.Parasut or IntegrationProvider.GibPortal
                 or IntegrationProvider.EArsivPortal;

    /// <summary>Hata mesajlarında kullanılan Türkçe sağlayıcı adı.</summary>
    private static string ProviderLabel(IntegrationProvider provider) => provider switch
    {
        IntegrationProvider.DoganDonusum => "Doğan Dönüşüm",
        IntegrationProvider.Elogo => "eLogo",
        IntegrationProvider.Edm => "EDM Bilişim",
        IntegrationProvider.Parasut => "Paraşüt",
        IntegrationProvider.GibPortal => "GİB Portalı",
        IntegrationProvider.EArsivPortal => "GİB e-Arşiv Portalı",
        _ => provider.ToString()
    };

    /// <summary>
    /// Kullanıcı adı + şifre kimliğini ilgili sağlayıcının paketleyicisiyle JSON'a çevirir
    /// (aynı şema). Paraşüt bu yoldan GEÇMEZ — Save'de ParasutProvider.PackCredentials
    /// beş alanla doğrudan çağrılır.
    /// </summary>
    private static string PackCredentials(
        IntegrationProvider provider, string username, string password, string? endpoint) =>
        provider switch
        {
            IntegrationProvider.DoganDonusum => DoganDonusumProvider.PackCredentials(username, password, endpoint),
            IntegrationProvider.Elogo => ElogoProvider.PackCredentials(username, password, endpoint),
            IntegrationProvider.Edm => EdmProvider.PackCredentials(username, password, endpoint),
            // GİB Portalı'nda servis adresi yoktur (adres sabittir) — yalnız kullanıcı kodu + şifre.
            IntegrationProvider.GibPortal => GibPortalProvider.PackCredentials(username, password),
            // e-Arşiv Portalı'nda da adres sabittir (canlı/test seçimi UseTestEnvironment ile yapılır).
            IntegrationProvider.EArsivPortal => EArsivPortalProvider.PackCredentials(username, password),
            _ => UyumsoftProvider.PackCredentials(username, password, endpoint)
        };

    /// <summary>
    /// Saklanan kimlik JSON'unu ilgili sağlayıcının çözücüsüyle açar. Paraşüt'te kullanıcı adı
    /// = e-posta, endpoint = opsiyonel taban adres (diğer alanlar maskelemede kullanılmaz).
    /// </summary>
    private static bool TryParseCredentials(
        IntegrationProvider provider, string? json,
        out string username, out string password, out string? endpoint)
    {
        switch (provider)
        {
            case IntegrationProvider.Parasut:
                username = string.Empty;
                password = string.Empty;
                endpoint = null;
                if (!ParasutProvider.TryParseCredentials(json, out var info) || info is null)
                    return false;
                username = info.Username;
                password = info.Password;
                endpoint = info.Endpoint;
                return true;
            case IntegrationProvider.DoganDonusum:
                return DoganDonusumProvider.TryParseCredentials(json, out username, out password, out endpoint);
            case IntegrationProvider.Elogo:
                return ElogoProvider.TryParseCredentials(json, out username, out password, out endpoint);
            case IntegrationProvider.Edm:
                return EdmProvider.TryParseCredentials(json, out username, out password, out endpoint);
            case IntegrationProvider.GibPortal:
                endpoint = null; // GİB portal adresi sabittir, kullanıcıdan alınmaz
                return GibPortalProvider.TryParseCredentials(json, out username, out password);
            case IntegrationProvider.EArsivPortal:
                endpoint = null; // e-Arşiv portal adresi sabittir (canlı/test UseTestEnvironment ile)
                return EArsivPortalProvider.TryParseCredentials(json, out username, out password);
            default:
                return UyumsoftProvider.TryParseCredentials(json, out username, out password, out endpoint);
        }
    }
}
