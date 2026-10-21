using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 4 — e-Tebligat tek pano uçları: senkron (tek mükellef veya tümü),
/// filtreli liste + özet sayaçlar ve okundu işaretleme.
/// </summary>
[ApiController]
[Authorize]
public class ETebligatController : ControllerBase
{
    private const int MaxListItems = 500;

    /// <summary>Tek çekimde izin verilen azami tarih aralığı (robotun GİB'e yükünü sınırlar).</summary>
    private const int MaxSyncRangeDays = 366;

    private readonly AppDbContext _db;
    private readonly ETebligatSyncService _syncService;
    private readonly IFileStorage _storage;

    public ETebligatController(AppDbContext db, ETebligatSyncService syncService, IFileStorage storage)
    {
        _db = db;
        _syncService = syncService;
        _storage = storage;
    }

    /// <summary>
    /// Senkron: clientId verilirse tek mükellef, verilmezse tenant'ın tüm aktif mükellefleri taranır.
    ///
    /// Faz 15 — "Tebligatları Çek": opsiyonel from/to aralığı GİB Portalı entegrasyonu olan
    /// mükelleflerde robot'a iletilir (portal ekranındaki tarih filtresi). Diğer sağlayıcılar
    /// aralığı yok sayar (tüm kutuyu döndürürler). Aralık verilmezse GibPortal için son 90 gün.
    /// </summary>
    [HttpPost("api/etebligat/sync")]
    public async Task<ActionResult<ETebligatSyncResponse>> Sync(
        [FromQuery] Guid? clientId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            return BadRequest(new { message = "Başlangıç tarihi bitiş tarihinden sonra olamaz." });

        if (to.HasValue && to.Value.Date > DateTime.UtcNow.Date.AddDays(1))
            return BadRequest(new { message = "Bitiş tarihi gelecekte olamaz." });

        if (from.HasValue && to.HasValue && (to.Value.Date - from.Value.Date).TotalDays > MaxSyncRangeDays)
            return BadRequest(new { message = $"Tarih aralığı en fazla {MaxSyncRangeDays} gün olabilir." });

        var result = await _syncService.SyncAsync(clientId, from?.Date, to?.Date, ct);

        if (!result.Success)
        {
            var message = result.ErrorMessage ?? "Senkronizasyon başarısız oldu. Lütfen tekrar deneyin.";
            return result.Retryable
                ? StatusCode(StatusCodes.Status502BadGateway, new { message })
                : BadRequest(new { message });
        }

        return Ok(new ETebligatSyncResponse
        {
            Added = result.Added,
            SkippedDuplicates = result.SkippedDuplicates,
            ClientCount = result.ClientCount,
            Message = result.Added > 0
                ? $"{result.Added} yeni tebligat alındı ({result.ClientCount} mükellef tarandı)."
                : $"Yeni tebligat yok ({result.ClientCount} mükellef tarandı)."
        });
    }

    /// <summary>
    /// Tek pano listesi. Özet sayaçlar (toplam/okunmamış/acil) yalnızca mükellef seçimine göre
    /// hesaplanır; onlyUnread/onlyUrgent filtreleri sadece satır listesini daraltır.
    /// Acil = kalan süre ≤ 3 gün (süresi geçmişler dahil).
    /// </summary>
    [HttpGet("api/etebligat")]
    public async Task<ActionResult<ETebligatListResponse>> List(
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool onlyUnread = false,
        [FromQuery] bool onlyUrgent = false,
        CancellationToken ct = default)
    {
        var baseQuery = _db.ETebligatMessages.AsNoTracking().AsQueryable();

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            baseQuery = baseQuery.Where(m => m.ClientId == clientId.Value);

        // Acil eşiği: bugünden 3 gün sonrasının sonu (kalan ≤ 3 gün ve süresi geçenler).
        var urgentThreshold = DateTime.UtcNow.Date.AddDays(4).AddTicks(-1);

        var totalCount = await baseQuery.CountAsync(ct);
        var unreadCount = await baseQuery.CountAsync(m => !m.IsRead, ct);
        var urgentCount = await baseQuery.CountAsync(m => m.LegalDeadline != null && m.LegalDeadline <= urgentThreshold, ct);

        var query = baseQuery;
        if (onlyUnread)
            query = query.Where(m => !m.IsRead);
        if (onlyUrgent)
            query = query.Where(m => m.LegalDeadline != null && m.LegalDeadline <= urgentThreshold);

        // En yakın son gün en üstte; süresiz bilgilendirmeler en sonda (tebliğ tarihine göre yeni → eski).
        var messages = await query
            .OrderBy(m => m.LegalDeadline == null)
            .ThenBy(m => m.LegalDeadline)
            .ThenByDescending(m => m.DeliveredAt)
            .Take(MaxListItems)
            .ToListAsync(ct);

        var clientIds = messages.Select(m => m.ClientId).Distinct().ToList();
        var titles = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        return Ok(new ETebligatListResponse
        {
            TotalCount = totalCount,
            UnreadCount = unreadCount,
            UrgentCount = urgentCount,
            Items = messages
                .Select(m => ETebligatItemResponse.FromEntity(m, titles.TryGetValue(m.ClientId, out var t) ? t : null))
                .ToList()
        });
    }

    /// <summary>Okundu işaretleme (platform içi takip durumu; tekrar çağrılırsa değişiklik yapmaz).</summary>
    [HttpPost("api/etebligat/{id:guid}/mark-read")]
    public async Task<ActionResult<ETebligatItemResponse>> MarkRead(Guid id, CancellationToken ct = default)
    {
        var message = await _db.ETebligatMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
            return NotFound(new { message = "Tebligat bulunamadı." });

        if (!message.IsRead)
        {
            message.IsRead = true;
            message.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        var clientTitle = await _db.Clients
            .AsNoTracking()
            .Where(c => c.Id == message.ClientId)
            .Select(c => c.Title)
            .FirstOrDefaultAsync(ct);

        return Ok(ETebligatItemResponse.FromEntity(message, clientTitle));
    }

    /// <summary>
    /// e-Tebligat resmi evrakını tarayıcıda görüntüler (PDF varsa stream eder, yoksa resmi formatlı evrak üretir).
    /// Kullanıcının GİB'e bağlanmadan tüm detayları görmesini sağlar.
    /// </summary>
    [HttpGet("api/etebligat/{id:guid}/document")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDocument(Guid id, CancellationToken ct = default)
    {
        var message = await _db.ETebligatMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
            return NotFound(new { message = "Tebligat bulunamadı." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == message.ClientId, ct);

        // 1. Eğer yerel diskte kayıtlı PDF varsa doğrudan sun
        if (!string.IsNullOrWhiteSpace(message.DocumentPath) && _storage.Exists(message.DocumentPath))
        {
            var stream = _storage.OpenRead(message.DocumentPath);
            return File(stream, "application/pdf", enableRangeProcessing: true);
        }

        // 2. Disk dosyası yoksa: T.C. Hazine ve Maliye Bakanlığı GİB formatında
        // resmi, barkodlu, hukuki dayanaklı ve yazdırılabilir resmi tebligat belgesi üret
        var html = ETebligatHtmlRenderer.RenderOfficialDocument(message, client);
        return Content(html, "text/html; charset=utf-8", System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// e-Tebligat resmi belgesini dosya olarak indirir.
    /// </summary>
    [HttpGet("api/etebligat/{id:guid}/download")]
    [AllowAnonymous]
    public async Task<IActionResult> DownloadDocument(Guid id, CancellationToken ct = default)
    {
        var message = await _db.ETebligatMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
            return NotFound(new { message = "Tebligat bulunamadı." });

        if (!string.IsNullOrWhiteSpace(message.DocumentPath) && _storage.Exists(message.DocumentPath))
        {
            var stream = _storage.OpenRead(message.DocumentPath);
            var fileName = message.DocumentFileName ?? $"Tebligat_{message.ExternalId}.pdf";
            return File(stream, "application/pdf", fileName);
        }

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == message.ClientId, ct);
        var html = ETebligatHtmlRenderer.RenderOfficialDocument(message, client);
        var safeFileName = $"eTebligat_{client?.TaxId ?? "Mukellef"}_{message.Id.ToString()[..8]}.html";
        return File(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", safeFileName);
    }
}
