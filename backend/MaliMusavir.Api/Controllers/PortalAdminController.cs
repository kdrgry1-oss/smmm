using System.Security.Cryptography;
using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 7 — E1: Belge portalının SMMM tarafı. Erişim üretme/yenileme (PIN yalnızca üretim
/// anında BİR KEZ döner; DB'de hash), deaktive etme, belge talebi CRUD ve gelen belgeler listesi.
/// Normal JWT ister ([Authorize]); portal token'ları varsayılan policy gereği buraya giremez.
/// </summary>
[ApiController]
[Authorize]
public class PortalAdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly ILogger<PortalAdminController> _logger;

    public PortalAdminController(AppDbContext db, IFileStorage storage, ILogger<PortalAdminController> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    // ---------- Portal erişimi ----------

    /// <summary>
    /// Portal erişimi üretir veya yeniler. İlk üretimde yeni link (token) + PIN oluşur;
    /// yenilemede link SABİT kalır (mükellefe yeniden gönderme gerekmez), yalnızca PIN değişir
    /// ve erişim yeniden aktifleştirilir. PIN bu yanıttan sonra bir daha GÖSTERİLEMEZ.
    /// </summary>
    [HttpPost("api/clients/{id:guid}/portal")]
    public async Task<ActionResult<PortalAccessCreatedResponse>> CreateOrRenew(Guid id, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var pin = GeneratePin();
        var access = await _db.ClientPortalAccesses.FirstOrDefaultAsync(a => a.ClientId == id, ct);

        string message;
        if (access is null)
        {
            access = new ClientPortalAccess
            {
                ClientId = id,
                Token = Guid.NewGuid().ToString("N"), // URL-güvenli, tahmin edilemez
                PinHash = BCrypt.Net.BCrypt.HashPassword(pin),
                IsActive = true
            };
            _db.ClientPortalAccesses.Add(access);
            message = "Portal erişimi oluşturuldu. PIN'i yalnızca şimdi görebilirsiniz — mükellefinize güvenli bir kanaldan iletin.";
        }
        else
        {
            access.PinHash = BCrypt.Net.BCrypt.HashPassword(pin);
            access.IsActive = true;
            message = "PIN yenilendi (link aynı kaldı). Yeni PIN'i yalnızca şimdi görebilirsiniz — mükellefinize iletin.";
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Portal erişimi üretildi/yenilendi. ClientId: {ClientId}", id);

        return Ok(new PortalAccessCreatedResponse
        {
            Token = access.Token,
            PortalPath = $"/portal/{access.Token}",
            Pin = pin,
            Message = message
        });
    }

    /// <summary>Portal erişim durumu (PIN dönmez).</summary>
    [HttpGet("api/clients/{id:guid}/portal")]
    public async Task<ActionResult<PortalAccessResponse>> Get(Guid id, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var access = await _db.ClientPortalAccesses.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == id, ct);
        if (access is null)
            return Ok(new PortalAccessResponse { Exists = false });

        return Ok(new PortalAccessResponse
        {
            Exists = true,
            IsActive = access.IsActive,
            Token = access.Token,
            PortalPath = $"/portal/{access.Token}",
            CreatedAt = access.CreatedAt,
            LastAccessAt = access.LastAccessAt
        });
    }

    /// <summary>Portal erişimini kapatır (link ve PIN çalışmaz olur; kayıt silinmez, yeniden açılabilir).</summary>
    [HttpDelete("api/clients/{id:guid}/portal")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct = default)
    {
        var access = await _db.ClientPortalAccesses.FirstOrDefaultAsync(a => a.ClientId == id, ct);
        if (access is null)
            return NotFound(new { message = "Bu mükellef için portal erişimi bulunamadı." });

        access.IsActive = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Portal erişimi kapatıldı. ClientId: {ClientId}", id);
        return NoContent();
    }

    // ---------- Belge talepleri (eksik belge takibi) ----------

    /// <summary>Belge talebi açar (ör. "Haziran banka ekstresi"); mükellef portalda kırmızı rozetle görür.</summary>
    [HttpPost("api/clients/{id:guid}/document-requests")]
    public async Task<ActionResult<DocumentRequestResponse>> CreateRequest(
        Guid id,
        [FromBody] DocumentRequestCreateRequest request,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var docRequest = new DocumentRequest
        {
            TenantId = client.TenantId,
            ClientId = id,
            Title = request.Title.Trim(),
            DueDate = request.DueDate.HasValue
                ? DateTime.SpecifyKind(request.DueDate.Value.Date, DateTimeKind.Utc)
                : null
        };

        _db.DocumentRequests.Add(docRequest);
        await _db.SaveChangesAsync(ct);

        return Ok(DocumentRequestResponse.FromEntity(docRequest));
    }

    /// <summary>Mükellefin tüm belge talepleri (durum rozetli liste).</summary>
    [HttpGet("api/clients/{id:guid}/document-requests")]
    public async Task<ActionResult<List<DocumentRequestResponse>>> ListRequests(Guid id, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var requests = await _db.DocumentRequests
            .AsNoTracking()
            .Where(r => r.ClientId == id)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        return Ok(requests.Select(DocumentRequestResponse.FromEntity).ToList());
    }

    /// <summary>Talebi iptal eder (Bekliyor → Iptal). Karşılanmış talep iptal edilemez (denetim izi).</summary>
    [HttpDelete("api/clients/{id:guid}/document-requests/{requestId:guid}")]
    public async Task<ActionResult<DocumentRequestResponse>> CancelRequest(
        Guid id, Guid requestId, CancellationToken ct = default)
    {
        var docRequest = await _db.DocumentRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ClientId == id, ct);
        if (docRequest is null)
            return NotFound(new { message = "Belge talebi bulunamadı." });

        if (docRequest.Status == DocumentRequestStatus.Yuklendi)
            return BadRequest(new { message = "Karşılanmış (yüklenmiş) talep iptal edilemez." });
        if (docRequest.Status == DocumentRequestStatus.Iptal)
            return BadRequest(new { message = "Talep zaten iptal edilmiş." });

        docRequest.Status = DocumentRequestStatus.Iptal;
        await _db.SaveChangesAsync(ct);

        return Ok(DocumentRequestResponse.FromEntity(docRequest));
    }

    // ---------- Gelen belgeler ----------

    /// <summary>
    /// Portaldan gelen belgeler (mükellef + kategori filtreli, en yeni önce).
    /// Fis kategorisindekiler ProcessedReceiptId ile fiş doğrulama ekranına bağlanır.
    /// </summary>
    [HttpGet("api/portal-uploads")]
    public async Task<ActionResult<List<PortalUploadResponse>>> ListUploads(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? category = null,
        CancellationToken ct = default)
    {
        var query = _db.PortalUploads.AsNoTracking().AsQueryable();

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            query = query.Where(u => u.ClientId == clientId.Value);

        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!Enum.TryParse<PortalUploadCategory>(category.Trim(), ignoreCase: true, out var parsedCategory))
                return BadRequest(new { message = "Geçersiz kategori filtresi." });

            query = query.Where(u => u.Category == parsedCategory);
        }

        var uploads = await query
            .OrderByDescending(u => u.UploadedAt)
            .Take(200) // liste ekranı sınırı; daha eskiler filtreyle daraltılarak bulunur
            .ToListAsync(ct);

        // Mükellef etiketleri tek sorguda (fiş listesindeki desenle aynı).
        var clientIds = uploads.Select(u => u.ClientId).Distinct().ToList();
        var titles = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        return Ok(uploads
            .Select(u => PortalUploadResponse.FromEntity(u, titles.TryGetValue(u.ClientId, out var t) ? t : null))
            .ToList());
    }

    /// <summary>Gelen belge dosyasını akış olarak döner (önizleme/indirme).</summary>
    [HttpGet("api/portal-uploads/{id:guid}/file")]
    public async Task<IActionResult> GetUploadFile(Guid id, CancellationToken ct = default)
    {
        var upload = await _db.PortalUploads.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (upload is null)
            return NotFound(new { message = "Belge bulunamadı." });

        if (!_storage.Exists(upload.StoredPath))
            return NotFound(new { message = "Belge dosyası bulunamadı. Dosya silinmiş olabilir." });

        var stream = _storage.OpenRead(upload.StoredPath);
        return File(stream, _storage.GetContentType(upload.FileName), upload.FileName, enableRangeProcessing: true);
    }

    // ---------- Yardımcılar ----------

    /// <summary>Kriptografik rastgele 6 haneli PIN (baştaki sıfırlar korunur, ör. "042519").</summary>
    private static string GeneratePin()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
