using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 11 — E7: SMMM ücret / tahsilat takibi uçları. Mükellef ücret tanımı CRUD
/// (/api/clients/{id}/fee) + dönem tahakkuku üretimi ve ödendi/geri al işlemleri (/api/fees).
/// </summary>
[ApiController]
[Authorize]
public class FeesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly FeeService _fees;

    public FeesController(AppDbContext db, ICurrentUser currentUser, FeeService fees)
    {
        _db = db;
        _currentUser = currentUser;
        _fees = fees;
    }

    // ---------- Mükellef ücret tanımı (ClientFee CRUD) ----------

    /// <summary>Mükellefin ücret tanımı (yoksa 404 — UI boş form gösterir).</summary>
    [HttpGet("api/clients/{clientId:guid}/fee")]
    public async Task<ActionResult<ClientFeeResponse>> GetFee(Guid clientId, CancellationToken ct)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var fee = await _db.ClientFees.AsNoTracking().FirstOrDefaultAsync(f => f.ClientId == clientId, ct);
        if (fee is null)
            return NotFound(new { message = "Bu mükellef için ücret tanımı yapılmadı." });

        return Ok(ClientFeeResponse.FromEntity(fee));
    }

    /// <summary>Ücret tanımını oluşturur/günceller (upsert — mükellef başına tek kayıt).</summary>
    [HttpPut("api/clients/{clientId:guid}/fee")]
    public async Task<ActionResult<ClientFeeResponse>> SaveFee(
        Guid clientId, [FromBody] ClientFeeSaveRequest request, CancellationToken ct)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var fee = await _db.ClientFees.FirstOrDefaultAsync(f => f.ClientId == clientId, ct);
        if (fee is null)
        {
            fee = new ClientFee { TenantId = client.TenantId, ClientId = clientId };
            _db.ClientFees.Add(fee);
        }

        fee.MonthlyFee = Math.Round(request.MonthlyFee, 2);
        fee.StartDate = (request.StartDate ?? DateTime.UtcNow).Date;
        fee.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

        await _db.SaveChangesAsync(ct);
        return Ok(ClientFeeResponse.FromEntity(fee));
    }

    /// <summary>Ücret tanımını siler (mevcut tahakkuklar SİLİNMEZ — tahsilat geçmişi korunur).</summary>
    [HttpDelete("api/clients/{clientId:guid}/fee")]
    public async Task<IActionResult> DeleteFee(Guid clientId, CancellationToken ct)
    {
        var fee = await _db.ClientFees.FirstOrDefaultAsync(f => f.ClientId == clientId, ct);
        if (fee is null)
            return NotFound(new { message = "Bu mükellef için ücret tanımı yapılmadı." });

        _db.ClientFees.Remove(fee);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Tahakkuk / tahsilat ----------

    /// <summary>Seçilen ay için tahakkukları üretir (mükerrersiz; vade = ayın son günü).</summary>
    [HttpPost("api/fees/generate")]
    public async Task<ActionResult<FeeGenerateResult>> Generate(
        [FromBody] FeeGenerateRequest request, CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        try
        {
            return Ok(await _fees.GenerateAsync(_currentUser.TenantId.Value, request.Period.Trim(), ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Tahakkuk listesi + özet kartlar. period (yyyy-MM) ve status isteğe bağlıdır.</summary>
    [HttpGet("api/fees")]
    public async Task<ActionResult<FeeListResponse>> List(
        [FromQuery] string? period = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        FeeInvoiceStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<FeeInvoiceStatus>(status.Trim(), ignoreCase: true, out var parsed))
                return BadRequest(new { message = "Geçersiz durum filtresi. Bekliyor, Odendi veya Gecikti seçin." });
            statusFilter = parsed;
        }

        try
        {
            var (items, summary) = await _fees.ListAsync(
                string.IsNullOrWhiteSpace(period) ? null : period.Trim(), statusFilter, ct);

            return Ok(new FeeListResponse
            {
                ExpectedTotal = summary.ExpectedTotal,
                PaidTotal = summary.PaidTotal,
                OverdueTotal = summary.OverdueTotal,
                TotalCount = summary.TotalCount,
                PaidCount = summary.PaidCount,
                OverdueCount = summary.OverdueCount,
                PendingCount = summary.PendingCount,
                Items = items.Select(i => FeeInvoiceResponse.FromEntity(i, i.Client?.Title)).ToList()
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Tahakkuku ödendi işaretler (paidAmount boşsa tahakkuk tutarı).</summary>
    [HttpPost("api/fees/{id:guid}/mark-paid")]
    public async Task<ActionResult<FeeInvoiceResponse>> MarkPaid(
        Guid id, [FromBody] MarkFeePaidRequest? request, CancellationToken ct)
    {
        var (invoice, error) = await _fees.MarkPaidAsync(id, request?.PaidAmount, ct);
        if (invoice is null)
        {
            var body = new { message = error ?? "İşlem gerçekleştirilemedi." };
            return error == "Tahakkuk bulunamadı." ? NotFound(body) : BadRequest(body);
        }
        return Ok(FeeInvoiceResponse.FromEntity(invoice, invoice.Client?.Title));
    }

    /// <summary>Yanlış "Ödendi" işaretini geri alır.</summary>
    [HttpPost("api/fees/{id:guid}/reopen")]
    public async Task<ActionResult<FeeInvoiceResponse>> Reopen(Guid id, CancellationToken ct)
    {
        var (invoice, error) = await _fees.ReopenAsync(id, ct);
        if (invoice is null)
        {
            var body = new { message = error ?? "İşlem gerçekleştirilemedi." };
            return error == "Tahakkuk bulunamadı." ? NotFound(body) : BadRequest(body);
        }
        return Ok(FeeInvoiceResponse.FromEntity(invoice, invoice.Client?.Title));
    }
}
