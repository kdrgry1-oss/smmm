using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 11 — E8: Mizan &amp; uyum kontrolü ucu. ?format=xlsx ile mizan Excel dosyası iner
/// (MinimalXlsxWriter — harici paket YOK); varsayılan JSON'dur.
/// </summary>
[ApiController]
[Route("api/trial-balance")]
[Authorize]
public class TrialBalanceController : ControllerBase
{
    private static readonly System.Text.RegularExpressions.Regex PeriodRegex =
        new(@"^\d{4}-(0[1-9]|1[0-2])$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly TrialBalanceService _trialBalance;

    public TrialBalanceController(AppDbContext db, TrialBalanceService trialBalance)
    {
        _db = db;
        _trialBalance = trialBalance;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? period = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Mizan için bir mükellef seçin." });
        if (string.IsNullOrWhiteSpace(period) || !PeriodRegex.IsMatch(period.Trim()))
            return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });

        var normalizedFormat = (format ?? "json").Trim().ToLowerInvariant();
        if (normalizedFormat != "json" && normalizedFormat != "xlsx")
            return BadRequest(new { message = "Geçersiz dosya biçimi. 'json' veya 'xlsx' seçin." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        TrialBalanceResult result;
        try
        {
            result = await _trialBalance.BuildAsync(clientId.Value, period.Trim(), ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        if (normalizedFormat == "json")
            return Ok(result);

        if (result.Rows.Count == 0)
            return BadRequest(new { message = "Seçilen dönemde onaylanmış belge yok — indirilecek mizan boş." });

        var slug = LucaExcelExporter.Slugify(client.Title);
        return File(TrialBalanceService.ToXlsx(result),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"mizan-{slug}-{result.Period}.xlsx");
    }
}
