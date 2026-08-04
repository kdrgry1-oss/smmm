using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 7 — E3: Sertifika süre takibi (e-İmza / mali mühür / KEP) — basit CRUD +
/// "süresi yaklaşanlar" filtresi. ClientId null = SMMM'nin (ofisin) kendi sertifikası.
/// </summary>
[ApiController]
[Route("api/certificates")]
[Authorize]
public class CertificatesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public CertificatesController(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Sertifika listesi (bitişi en yakın önce). expiringInDays: yalnızca N gün içinde bitenler.</summary>
    [HttpGet]
    public async Task<ActionResult<List<CertificateResponse>>> List(
        [FromQuery] int? expiringInDays = null,
        CancellationToken ct = default)
    {
        var query = _db.ClientCertificates.AsNoTracking().AsQueryable();

        if (expiringInDays.HasValue)
        {
            if (expiringInDays.Value < 0)
                return BadRequest(new { message = "Gün filtresi negatif olamaz." });

            var threshold = DateTime.UtcNow.Date.AddDays(expiringInDays.Value);
            query = query.Where(c => c.ExpiresAt <= threshold);
        }

        var certificates = await query
            .OrderBy(c => c.ExpiresAt)
            .ToListAsync(ct);

        // Mükellef etiketleri tek sorguda (null ClientId = ofisin kendi sertifikası).
        var clientIds = certificates.Where(c => c.ClientId.HasValue).Select(c => c.ClientId!.Value).Distinct().ToList();
        var titles = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        return Ok(certificates
            .Select(c => CertificateResponse.FromEntity(
                c, c.ClientId.HasValue && titles.TryGetValue(c.ClientId.Value, out var t) ? t : null))
            .ToList());
    }

    /// <summary>Yeni sertifika kaydı.</summary>
    [HttpPost]
    public async Task<ActionResult<CertificateResponse>> Create(
        [FromBody] CertificateSaveRequest request,
        CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        string? clientTitle = null;
        if (request.ClientId.HasValue)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ClientId.Value, ct);
            if (client is null)
                return NotFound(new { message = "Mükellef bulunamadı." });
            clientTitle = client.Title;
        }

        var certificate = new ClientCertificate
        {
            TenantId = _currentUser.TenantId.Value,
            ClientId = request.ClientId,
            Type = request.Type,
            Owner = request.Owner.Trim(),
            ExpiresAt = DateTime.SpecifyKind(request.ExpiresAt.Date, DateTimeKind.Utc),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        };

        _db.ClientCertificates.Add(certificate);
        await _db.SaveChangesAsync(ct);

        return Ok(CertificateResponse.FromEntity(certificate, clientTitle));
    }

    /// <summary>Sertifika kaydını günceller (ör. yenilenen sertifikanın yeni bitiş tarihi).</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CertificateResponse>> Update(
        Guid id,
        [FromBody] CertificateSaveRequest request,
        CancellationToken ct = default)
    {
        var certificate = await _db.ClientCertificates.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (certificate is null)
            return NotFound(new { message = "Sertifika kaydı bulunamadı." });

        string? clientTitle = null;
        if (request.ClientId.HasValue)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ClientId.Value, ct);
            if (client is null)
                return NotFound(new { message = "Mükellef bulunamadı." });
            clientTitle = client.Title;
        }

        certificate.ClientId = request.ClientId;
        certificate.Type = request.Type;
        certificate.Owner = request.Owner.Trim();
        certificate.ExpiresAt = DateTime.SpecifyKind(request.ExpiresAt.Date, DateTimeKind.Utc);
        certificate.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

        await _db.SaveChangesAsync(ct);

        return Ok(CertificateResponse.FromEntity(certificate, clientTitle));
    }

    /// <summary>Sertifika kaydını siler.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var certificate = await _db.ClientCertificates.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (certificate is null)
            return NotFound(new { message = "Sertifika kaydı bulunamadı." });

        _db.ClientCertificates.Remove(certificate);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
