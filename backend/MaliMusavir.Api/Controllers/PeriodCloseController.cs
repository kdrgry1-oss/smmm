using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 8 — B10: Dönem sonu kontrol listesi ucu. Mükellef + dönem için tüm kontrolleri
/// çalıştırır ve "Beyana hazır" yüzdesiyle döner.
/// </summary>
[ApiController]
[Route("api/period-close")]
[Authorize]
public class PeriodCloseController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PeriodCloseService _service;

    public PeriodCloseController(AppDbContext db, PeriodCloseService service)
    {
        _db = db;
        _service = service;
    }

    [HttpGet("checklist")]
    public async Task<ActionResult<PeriodCloseChecklist>> Checklist(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? period = null,
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Kontrol listesi için bir mükellef seçin." });

        if (string.IsNullOrWhiteSpace(period) || !DeclarationService.TryParsePeriod(period.Trim(), out _))
            return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var checklist = await _service.BuildChecklistAsync(clientId.Value, period.Trim(), ct);
        return Ok(checklist);
    }
}
