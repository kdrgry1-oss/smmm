using System.Globalization;
using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 18 — MÜŞAVİR (SMMM) TEK GİB GİRİŞİ.
///
/// Mali müşavir KENDİ GİB kullanıcı kodu/şifresiyle BİR KEZ giriş yapar; GİB
/// "müşavir/mükellef işlemleri" özelliğiyle aynı oturum üzerinden bağlı mükelleflerin
/// ekranlarına geçilir. Mükellef başına ayrı şifre/captcha GEREKMEZ.
///
/// GÜVENLİK:
///  - Şifre SecretProtector (AES-256) ile TenantGibCredential.PasswordEncrypted kolonunda
///    saklanır; yanıtlarda ASLA dönmez, LOGLANMAZ; robota yalnız iç ağda bir kez gider.
///  - Captcha ASLA otomatik çözülmez (mutlak kural): giriş, mevcut insan destekli
///    begin/complete akışıyla yapılır — kodu kullanıcı girer.
/// </summary>
[ApiController]
[Route("api/gib-advisor")]
[Authorize]
public class GibAdvisorController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly GibRobotClient _gibRobot;
    private readonly ICurrentUser _currentUser;

    public GibAdvisorController(
        AppDbContext db,
        ISecretProtector secretProtector,
        GibRobotClient gibRobot,
        ICurrentUser currentUser)
    {
        _db = db;
        _secretProtector = secretProtector;
        _gibRobot = gibRobot;
        _currentUser = currentUser;
    }

    /// <summary>İşlem yapılan tenant (müşavir bürosu). SuperAdmin'in tenant'ı yoktur.</summary>
    private Guid? TenantId => _currentUser.TenantId;

    /// <summary>Müşavir GİB kimliği durumu (şifre maskeli — hiç dönmez).</summary>
    [HttpGet("credentials")]
    public async Task<ActionResult<GibAdvisorCredentialResponse>> GetCredentials(CancellationToken ct)
    {
        if (TenantId is null)
            return BadRequest(new { message = "Bu işlem için bir büro (tenant) hesabıyla giriş yapın." });

        var credential = await _db.TenantGibCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == TenantId, ct);

        return Ok(new GibAdvisorCredentialResponse
        {
            HasCredential = credential is not null,
            MaskedUsername = credential is null ? null : MaskUsername(credential.Username),
            UpdatedAt = credential?.UpdatedAt
        });
    }

    /// <summary>
    /// Müşavir GİB kimliğini kaydeder/günceller (tenant başına tek kayıt).
    /// Şifre şifrelenir; düz metin hiçbir yerde saklanmaz/loglanmaz.
    /// </summary>
    [HttpPost("credentials")]
    public async Task<ActionResult<GibAdvisorCredentialResponse>> SaveCredentials(
        [FromBody] GibAdvisorCredentialSaveRequest request, CancellationToken ct)
    {
        if (TenantId is null)
            return BadRequest(new { message = "Bu işlem için bir büro (tenant) hesabıyla giriş yapın." });

        var username = request.Username.Trim();
        var password = request.Password; // şifre bilinçli olarak trim'lenmez

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return BadRequest(new { message = "Müşavir GİB kullanıcı kodu ve şifresi zorunludur." });

        var credential = await _db.TenantGibCredentials
            .FirstOrDefaultAsync(c => c.TenantId == TenantId, ct);

        if (credential is null)
        {
            credential = new TenantGibCredential { TenantId = TenantId.Value };
            _db.TenantGibCredentials.Add(credential);
        }

        credential.Username = username;
        credential.PasswordEncrypted = _secretProtector.Protect(password);
        credential.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new GibAdvisorCredentialResponse
        {
            HasCredential = true,
            MaskedUsername = MaskUsername(credential.Username),
            UpdatedAt = credential.UpdatedAt
        });
    }

    /// <summary>
    /// Müşavir girişi — 1. aşama: robot GİB giriş ekranını açar, KAYITLI müşavir kimliğini
    /// doldurur, doğrulama kodu görselini döndürür. Oturum advisor:&lt;tenantId&gt; anahtarıyla
    /// saklanır — tüm mükellef çekimleri bu TEK oturumu kullanır.
    /// </summary>
    [HttpPost("login/begin")]
    public async Task<ActionResult<GibLoginBeginResponse>> LoginBegin(
        [FromQuery] bool manual = false, CancellationToken ct = default)
    {
        if (TenantId is null)
            return BadRequest(new { message = "Bu işlem için bir büro (tenant) hesabıyla giriş yapın." });

        var credential = await _db.TenantGibCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == TenantId, ct);

        if (credential is null)
        {
            return BadRequest(new
            {
                message = "Önce müşavir GİB kullanıcı kodu ve şifresini kaydedin (GİB Bağlantısı sayfası)."
            });
        }

        string password;
        try
        {
            password = _secretProtector.Unprotect(credential.PasswordEncrypted);
        }
        catch
        {
            return BadRequest(new { message = "Kayıtlı müşavir GİB kimliği çözülemedi. Lütfen yeniden kaydedin." });
        }

        var sessionKey = GibRobotClient.AdvisorSessionKey(TenantId.Value);

        // 1. ÖNCE OTOMATİK GİRİŞ DENE (Yerel OCR ile Captcha Çözümü):
        if (!manual)
        try
        {
            var autoResult = await _gibRobot.AutoAdvisorLoginAsync(credential.Username, password, sessionKey, ct);
            if (autoResult.Ok)
            {
                return Ok(new GibLoginBeginResponse
                {
                    Success = true,
                    AlreadyLoggedIn = true,
                    Message = "Müşavir GİB oturumu otomatik olarak açıldı (OCR ile çözüldü)."
                });
            }
        }
        catch
        {
            // fallback
        }

        // 2. Otomatik OCR çözemezse manuel fallback:
        var result = await _gibRobot.BeginAdvisorLoginAsync(credential.Username, password, sessionKey, ct);

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
    /// Müşavir girişi — 2. aşama: kullanıcının okuduğu doğrulama kodu robota iletilir.
    /// Başarıda müşavir oturumu kaydedilir; TÜM mükellef çekimleri kod istemeden sürer.
    /// </summary>
    [HttpPost("login/complete")]
    public async Task<ActionResult<GibLoginCompleteResponse>> LoginComplete(
        [FromBody] GibLoginCompleteRequest request, CancellationToken ct)
    {
        if (TenantId is null)
            return BadRequest(new { message = "Bu işlem için bir büro (tenant) hesabıyla giriş yapın." });

        var result = await _gibRobot.CompleteAdvisorLoginAsync(
            request.PendingId.Trim(), request.Code.Trim(), ct);

        return Ok(new GibLoginCompleteResponse
        {
            Success = result.Ok,
            Message = result.Message ?? (result.Ok
                ? "Giriş başarılı, müşavir oturumu kaydedildi."
                : "Giriş tamamlanamadı. Lütfen tekrar deneyin."),
            CaptchaImage = result.CaptchaImageBase64,
            AttemptsLeft = result.AttemptsLeft,
            PendingExpired = result.PendingExpired
        });
    }

    /// <summary>
    /// Müşavir oturum durumu rozeti: robot diskteki oturum dosyasına bakar (tarayıcı AÇILMAZ).
    /// aktif / yenilenmeli / hiç girilmemiş + son görülme zamanı.
    /// </summary>
    [HttpGet("session")]
    public async Task<ActionResult<GibAdvisorSessionStatusResponse>> SessionStatus(CancellationToken ct)
    {
        if (TenantId is null)
            return BadRequest(new { message = "Bu işlem için bir büro (tenant) hesabıyla giriş yapın." });

        var sessionKey = GibRobotClient.AdvisorSessionKey(TenantId.Value);
        var result = await _gibRobot.GetAdvisorSessionStatusAsync(sessionKey, ct);

        return Ok(new GibAdvisorSessionStatusResponse
        {
            Exists = result.Exists,
            Expired = result.Expired,
            Active = result.Ok && result.Exists && !result.Expired,
            LastSeenAt = result.LastSeenAt,
            Message = result.Ok ? null : result.Message
        });
    }

    /// <summary>
    /// "Mükelleflerimi GİB'den Getir": müşavir oturumuyla portaldaki mükellef listesi çekilir
    /// (robot JSON yanıt yakalar), VKN'ler sistemdeki mükelleflerle eşleştirilir. Eşleşmeyenler
    /// "sisteme eklenebilir" olarak döner.
    /// </summary>
    [HttpPost("clients")]
    public async Task<ActionResult<GibAdvisorClientsResponse>> FetchClients(CancellationToken ct)
    {
        if (TenantId is null)
            return BadRequest(new { message = "Bu işlem için bir büro (tenant) hesabıyla giriş yapın." });

        var hasCredential = await _db.TenantGibCredentials
            .AsNoTracking()
            .AnyAsync(c => c.TenantId == TenantId, ct);
        if (!hasCredential)
        {
            return BadRequest(new
            {
                message = "Önce müşavir GİB kullanıcı kodu ve şifresini kaydedin (GİB Bağlantısı sayfası)."
            });
        }

        var sessionKey = GibRobotClient.AdvisorSessionKey(TenantId.Value);
        var result = await _gibRobot.GetAdvisorClientsAsync(sessionKey, ct);

        if (!result.Ok)
        {
            return Ok(new GibAdvisorClientsResponse
            {
                Success = false,
                Message = result.Message ?? "GİB'den mükellef listesi alınamadı. Lütfen daha sonra tekrar deneyin.",
                NeedsCaptchaLogin = result.NeedsCaptchaLogin || result.RequiresManual
            });
        }

        // VKN eşleştirmesi: tenant'ın kayıtlı mükellefleri (arşivliler dahil — mükerrer eklenmesin).
        var existingClients = await _db.Clients
            .AsNoTracking()
            .Select(c => new { c.Id, c.TaxId, c.Title })
            .ToListAsync(ct);
        var byTaxId = existingClients
            .GroupBy(c => c.TaxId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var items = new List<GibAdvisorClientItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var robotClient in result.Items)
        {
            var taxId = new string((robotClient.TaxId ?? string.Empty).Where(char.IsDigit).ToArray());
            var title = (robotClient.Title ?? string.Empty).Trim();
            if (taxId.Length is not (10 or 11) || title.Length == 0) continue;
            if (!seen.Add(taxId)) continue;

            var matched = byTaxId.TryGetValue(taxId, out var existing);
            items.Add(new GibAdvisorClientItem
            {
                TaxId = taxId,
                Title = title.Length > 300 ? title[..300] : title,
                Matched = matched,
                ClientId = matched ? existing!.Id : null,
                ClientTitle = matched ? existing!.Title : null
            });
        }

        var matchedCount = items.Count(i => i.Matched);
        return Ok(new GibAdvisorClientsResponse
        {
            Success = true,
            Items = items,
            Message = string.Format(
                CultureInfo.InvariantCulture,
                "GİB'den {0} mükellef alındı; {1} tanesi sistemdeki mükelleflerle eşleşti, {2} tanesi sisteme eklenebilir.",
                items.Count, matchedCount, items.Count - matchedCount)
        });
    }

    /// <summary>Kullanıcı adı maskesi: ilk 2 + son 2 karakter (mevcut maskeleme kalıbı).</summary>
    private static string MaskUsername(string username)
    {
        if (string.IsNullOrEmpty(username)) return "••••";
        if (username.Length <= 4) return $"{username[0]}•••";
        return $"{username[..2]}••••{username[^2..]}";
    }
}
