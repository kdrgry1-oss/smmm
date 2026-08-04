using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 11 — E5: e-Defter berat ön-kontrol uçları. Gerçek XBRL-GL üretimi/imza YOK (dürüst
/// kapsam — bkz. EDefterPrecheckService); ön-kontrol + elle "berat yüklendi" işaretleme vardır.
/// </summary>
[ApiController]
[Authorize]
public class EDefterController : ControllerBase
{
    private static readonly System.Text.RegularExpressions.Regex PeriodRegex =
        new(@"^\d{4}-(0[1-9]|1[0-2])$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly EDefterPrecheckService _precheck;

    public EDefterController(AppDbContext db, EDefterPrecheckService precheck)
    {
        _db = db;
        _precheck = precheck;
    }

    /// <summary>Kontrol setini çalıştırır (dönem kaydını da upsert eder).</summary>
    [HttpGet("api/edefter/precheck")]
    public async Task<ActionResult<EDefterPrecheckResult>> Precheck(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? period = null,
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Ön-kontrol için bir mükellef seçin." });
        if (string.IsNullOrWhiteSpace(period) || !PeriodRegex.IsMatch(period.Trim()))
            return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });

        try
        {
            return Ok(await _precheck.RunAsync(clientId.Value, period.Trim(), ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// SMMM, beratı GİB e-Defter uygulamasında yükledikten SONRA elle işaretler
    /// (ileride gerçek entegrasyonla otomatikleşecek).
    /// </summary>
    [HttpPost("api/edefter/{clientId:guid}/{period}/mark-uploaded")]
    public async Task<ActionResult<EDefterPeriodResponse>> MarkUploaded(
        Guid clientId,
        string period,
        [FromBody] EDefterMarkUploadedRequest? request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(period) || !PeriodRegex.IsMatch(period.Trim()))
            return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });
        period = period.Trim();

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var record = await _db.EDefterPeriods
            .FirstOrDefaultAsync(p => p.ClientId == clientId && p.Period == period, ct);
        if (record is null)
        {
            // Kontrol hiç çalıştırılmadan da işaretlenebilir (SMMM sorumluluğunda).
            record = new EDefterPeriod { TenantId = client.TenantId, ClientId = clientId, Period = period };
            _db.EDefterPeriods.Add(record);
        }

        if (record.Status == EDefterPeriodStatus.BeratYuklendi)
            return BadRequest(new { message = "Bu dönem zaten \"Berat yüklendi\" olarak işaretli." });

        record.Status = EDefterPeriodStatus.BeratYuklendi;
        record.MarkedUploadedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request?.Note))
            record.Note = request!.Note!.Trim();

        await _db.SaveChangesAsync(ct);
        return Ok(EDefterPeriodResponse.FromEntity(record, client.Title));
    }

    /// <summary>Dönem geçmişi listesi (yeniden eskiye). clientId isteğe bağlı filtredir.</summary>
    [HttpGet("api/edefter")]
    public async Task<ActionResult<List<EDefterPeriodResponse>>> List(
        [FromQuery] Guid? clientId = null,
        CancellationToken ct = default)
    {
        var query = _db.EDefterPeriods.AsNoTracking().Include(p => p.Client).AsQueryable();
        if (clientId.HasValue && clientId.Value != Guid.Empty)
            query = query.Where(p => p.ClientId == clientId.Value);

        var records = await query
            .OrderByDescending(p => p.Period)
            .ThenBy(p => p.Client!.Title)
            .Take(200)
            .ToListAsync(ct);

        return Ok(records.Select(p => EDefterPeriodResponse.FromEntity(p, p.Client?.Title)).ToList());
    }
}
