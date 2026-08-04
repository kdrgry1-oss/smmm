using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 7 — E2: Vergi takvimi uçları. Dönem üretimi (standart set), pano listesi
/// (özet sayaçlarla), tamamla/yeniden aç ve manuel yükümlülük ekleme.
/// </summary>
[ApiController]
[Route("api/tax-calendar")]
[Authorize]
public class TaxCalendarController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TaxCalendarService _calendarService;
    private readonly ICurrentUser _currentUser;

    public TaxCalendarController(AppDbContext db, TaxCalendarService calendarService, ICurrentUser currentUser)
    {
        _db = db;
        _calendarService = calendarService;
        _currentUser = currentUser;
    }

    /// <summary>Seçilen ay için tüm aktif mükelleflere standart yükümlülük setini üretir (mükerrer üretmez).</summary>
    [HttpPost("generate")]
    public async Task<ActionResult<GenerateCalendarResultResponse>> Generate(
        [FromBody] GenerateCalendarRequest request,
        CancellationToken ct = default)
    {
        var outcome = await _calendarService.GeneratePeriodAsync(request.Period, ct);
        if (!outcome.Success)
            return BadRequest(new { message = outcome.ErrorMessage ?? "Dönem oluşturulamadı." });

        var message = $"{outcome.Added} yükümlülük oluşturuldu ({outcome.ClientCount} mükellef).";
        if (outcome.SkippedDuplicates > 0)
            message += $" {outcome.SkippedDuplicates} mevcut kayıt atlandı.";
        if (outcome.AutoCompleted > 0)
            message += $" {outcome.AutoCompleted} KDV yükümlülüğü onaylı beyannameden otomatik tamamlandı.";

        return Ok(new GenerateCalendarResultResponse
        {
            Message = message,
            Added = outcome.Added,
            SkippedDuplicates = outcome.SkippedDuplicates,
            AutoCompleted = outcome.AutoCompleted
        });
    }

    /// <summary>
    /// Yükümlülük panosu. Özet sayaçlar onlyPending filtresinden bağımsızdır (tebligat deseni).
    /// Dönem verildiyse önce beyanname senkronu çalışır (onaylı KDV1 → otomatik Tamamlandi).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<TaxCalendarListResponse>> List(
        [FromQuery] string? period = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool onlyPending = false,
        [FromQuery] bool onlyOverdue = false,
        CancellationToken ct = default)
    {
        string? normalizedPeriod = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            if (!TaxCalendarService.TryParsePeriod(period, out var periodStart))
                return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });
            normalizedPeriod = periodStart.ToString("yyyy-MM");

            // İdempotent beyanname senkronu: pano açılırken onaylı KDV1'ler otomatik kapatılır.
            await _calendarService.SyncWithDeclarationsAsync(normalizedPeriod, ct);
        }

        var baseQuery = _db.TaxObligations.AsNoTracking().AsQueryable();

        if (normalizedPeriod is not null)
            baseQuery = baseQuery.Where(o => o.Period == normalizedPeriod);

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            baseQuery = baseQuery.Where(o => o.ClientId == clientId.Value);

        // Özet sayaçlar (filtreden bağımsız — üst kartlar filtre değişince kaybolmasın).
        var today = DateTime.UtcNow.Date;
        var totalCount = await baseQuery.CountAsync(ct);
        var pendingCount = await baseQuery.CountAsync(o => o.Status == ObligationStatus.Bekliyor, ct);
        var overdueCount = await baseQuery.CountAsync(
            o => o.Status == ObligationStatus.Bekliyor && o.DueDate < today, ct);

        var listQuery = baseQuery;
        if (onlyPending)
            listQuery = listQuery.Where(o => o.Status == ObligationStatus.Bekliyor);
        if (onlyOverdue)
            listQuery = listQuery.Where(o => o.Status == ObligationStatus.Bekliyor && o.DueDate < today);

        var obligations = await listQuery
            .OrderBy(o => o.DueDate)
            .ThenBy(o => o.Type)
            .ToListAsync(ct);

        // Mükellef etiketleri tek sorguda.
        var clientIds = obligations.Select(o => o.ClientId).Distinct().ToList();
        var titles = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        return Ok(new TaxCalendarListResponse
        {
            TotalCount = totalCount,
            PendingCount = pendingCount,
            OverdueCount = overdueCount,
            Items = obligations
                .Select(o => TaxObligationResponse.FromEntity(o, titles.TryGetValue(o.ClientId, out var t) ? t : null))
                .ToList()
        });
    }

    /// <summary>Manuel yükümlülük ekler (Source = Manuel; ör. "Geçici Vergi", "Yıllık Gelir V.").</summary>
    [HttpPost]
    public async Task<ActionResult<TaxObligationResponse>> CreateManual(
        [FromBody] ManualObligationCreateRequest request,
        CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ClientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        if (!TaxCalendarService.TryParsePeriod(request.Period, out var periodStart))
            return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });

        var obligation = new TaxObligation
        {
            TenantId = client.TenantId,
            ClientId = client.Id,
            Type = request.Type.Trim(),
            Period = periodStart.ToString("yyyy-MM"),
            DueDate = DateTime.SpecifyKind(request.DueDate.Date, DateTimeKind.Utc),
            Status = ObligationStatus.Bekliyor,
            Source = ObligationSource.Manuel
        };

        _db.TaxObligations.Add(obligation);
        await _db.SaveChangesAsync(ct);

        return Ok(TaxObligationResponse.FromEntity(obligation, client.Title));
    }

    /// <summary>Yükümlülüğü tamamlandı işaretler.</summary>
    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<TaxObligationResponse>> Complete(Guid id, CancellationToken ct = default)
    {
        var obligation = await _db.TaxObligations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (obligation is null)
            return NotFound(new { message = "Yükümlülük bulunamadı." });

        if (obligation.Status == ObligationStatus.Tamamlandi)
            return BadRequest(new { message = "Yükümlülük zaten tamamlanmış." });

        obligation.Status = ObligationStatus.Tamamlandi;
        obligation.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(TaxObligationResponse.FromEntity(obligation, null));
    }

    /// <summary>Tamamlanan yükümlülüğü yeniden açar (yanlış tıklama geri alma).</summary>
    [HttpPost("{id:guid}/reopen")]
    public async Task<ActionResult<TaxObligationResponse>> Reopen(Guid id, CancellationToken ct = default)
    {
        var obligation = await _db.TaxObligations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (obligation is null)
            return NotFound(new { message = "Yükümlülük bulunamadı." });

        if (obligation.Status == ObligationStatus.Bekliyor)
            return BadRequest(new { message = "Yükümlülük zaten bekliyor durumunda." });

        obligation.Status = ObligationStatus.Bekliyor;
        obligation.CompletedAt = null;
        await _db.SaveChangesAsync(ct);

        return Ok(TaxObligationResponse.FromEntity(obligation, null));
    }
}
