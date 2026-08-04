using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 8 — B5: Mükerrer / eksik fatura mutabakatı uçları.
/// Analiz her çağrıda yeniden hesaplanır (bulgular DB'de saklanmaz); "Yoksay" kalıcıdır (DB).
/// </summary>
[ApiController]
[Route("api/reconciliation")]
[Authorize]
public class ReconciliationController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ReconciliationService _service;
    private readonly ICurrentUser _currentUser;

    public ReconciliationController(AppDbContext db, ReconciliationService service, ICurrentUser currentUser)
    {
        _db = db;
        _service = service;
        _currentUser = currentUser;
    }

    /// <summary>Dönem analizi: mükerrer / eksik / tutarsız bulgular (yok sayılanlar hariç).</summary>
    [HttpGet]
    public async Task<ActionResult<ReconciliationReport>> Analyze(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? period = null,
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Analiz için bir mükellef seçin." });

        if (string.IsNullOrWhiteSpace(period) || !DeclarationService.TryParsePeriod(period.Trim(), out _))
            return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var report = await _service.AnalyzeAsync(clientId.Value, period.Trim(), ct);
        return Ok(report);
    }

    /// <summary>"Yoksay": bulgu anahtarını kalıcı olarak gizler (ReconciliationDismissal tablosu).</summary>
    [HttpPost("dismiss")]
    public async Task<IActionResult> Dismiss([FromBody] DismissFindingRequest request, CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        await _service.DismissAsync(_currentUser.TenantId.Value, request.FindingKey, ct);
        return Ok(new { message = "Bulgu yok sayıldı; sonraki analizlerde gösterilmeyecek." });
    }
}
