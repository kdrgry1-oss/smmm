using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 11 — E6: Gönderim kuyruğu uçları. İşleri OutboundJobWorker arka planda çalıştırır;
/// buradan liste/iptal/"şimdi dene" yönetilir.
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize]
public class JobsController : ControllerBase
{
    private readonly OutboundJobService _jobs;

    public JobsController(OutboundJobService jobs)
    {
        _jobs = jobs;
    }

    /// <summary>Kuyruk listesi. status isteğe bağlı filtredir (Bekliyor/Calisiyor/Basarili/Basarisiz/Iptal).</summary>
    [HttpGet]
    public async Task<ActionResult<OutboundJobListResponse>> List(
        [FromQuery] string? status = null, CancellationToken ct = default)
    {
        OutboundJobStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<OutboundJobStatus>(status.Trim(), ignoreCase: true, out var parsed))
                return BadRequest(new { message = "Geçersiz durum filtresi. Bekliyor, Calisiyor, Basarili, Basarisiz veya Iptal seçin." });
            statusFilter = parsed;
        }

        var (items, pendingCount) = await _jobs.ListAsync(statusFilter, ct);
        return Ok(new OutboundJobListResponse
        {
            PendingCount = pendingCount,
            Items = items.Select(OutboundJobResponse.FromEntity).ToList()
        });
    }

    /// <summary>Bekleyen/başarısız işi iptal eder.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<OutboundJobResponse>> Cancel(Guid id, CancellationToken ct)
    {
        var result = await _jobs.CancelAsync(id, ct);
        if (!result.Success || result.Job is null)
        {
            var body = new { message = result.Message ?? "İşlem gerçekleştirilemedi." };
            return result.NotFound ? NotFound(body) : BadRequest(body);
        }
        return Ok(OutboundJobResponse.FromEntity(result.Job));
    }

    /// <summary>İşi hemen sıraya alır (başarısız işte deneme serisi yeniden başlar).</summary>
    [HttpPost("{id:guid}/retry-now")]
    public async Task<ActionResult<OutboundJobResponse>> RetryNow(Guid id, CancellationToken ct)
    {
        var result = await _jobs.RetryNowAsync(id, ct);
        if (!result.Success || result.Job is null)
        {
            var body = new { message = result.Message ?? "İşlem gerçekleştirilemedi." };
            return result.NotFound ? NotFound(body) : BadRequest(body);
        }
        return Ok(OutboundJobResponse.FromEntity(result.Job));
    }
}
