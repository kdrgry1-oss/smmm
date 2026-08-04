using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// e-Beyanname uçları (blueprint M8/B9): taslak hazırla → GİB'e gönder → durum/tahakkuk sorgula.
/// Mock sağlayıcıda entegrasyon ayarı gerekmez; gerçek GİB, kimlik+token iletilince adapter'a eklenir.
/// </summary>
[ApiController]
[Route("api/declarations")]
[Authorize]
public class DeclarationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly DeclarationService _declarationService;
    private readonly OutboundJobService _jobs;

    public DeclarationsController(
        AppDbContext db,
        ICurrentUser currentUser,
        DeclarationService declarationService,
        OutboundJobService jobs)
    {
        _db = db;
        _currentUser = currentUser;
        _declarationService = declarationService;
        _jobs = jobs;
    }

    /// <summary>
    /// KDV1 taslağı hazırlar; aynı (mükellef, tür, dönem) kaydı varsa mevcut taslağı yeniden hesaplar.
    /// Diğer beyanname türleri bu fazda 400 "yakında" döner.
    /// </summary>
    [HttpPost("prepare")]
    public async Task<ActionResult<DeclarationActionResponse>> Prepare(
        [FromBody] PrepareDeclarationRequest request, CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var result = await _declarationService.PrepareAsync(
            _currentUser.TenantId.Value, request.ClientId, request.Type, request.Period.Trim(), ct);

        return ToActionResult(result, await ResolveClientTitleAsync(result, ct));
    }

    /// <summary>Beyanname listesi (durum panosu). clientId ve period (yyyy-MM) isteğe bağlı filtrelerdir.</summary>
    [HttpGet]
    public async Task<ActionResult<List<DeclarationResponse>>> List(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? period = null,
        CancellationToken ct = default)
    {
        var query = _db.Declarations.AsNoTracking().Include(d => d.Client).AsQueryable();

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            query = query.Where(d => d.ClientId == clientId.Value);

        if (!string.IsNullOrWhiteSpace(period))
        {
            if (!DeclarationService.TryParsePeriod(period.Trim(), out _))
                return BadRequest(new { message = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." });
            query = query.Where(d => d.Period == period.Trim());
        }

        var declarations = await query
            .OrderByDescending(d => d.Period)
            .ThenBy(d => d.Client!.Title)
            .ToListAsync(ct);

        // Liste görünümünde özet taşınmaz (hafiflik); detay ucunda kırılımlı özet döner.
        return Ok(declarations
            .Select(d => DeclarationResponse.FromEntity(d, d.Client?.Title, includeSummary: false))
            .ToList());
    }

    /// <summary>Beyanname detayı — KDV kırılımlı taslak özeti dahil.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeclarationResponse>> Get(Guid id, CancellationToken ct)
    {
        var declaration = await _db.Declarations.AsNoTracking()
            .Include(d => d.Client)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (declaration is null)
            return NotFound(new { message = "Beyanname bulunamadı." });

        return Ok(DeclarationResponse.FromEntity(declaration, declaration.Client?.Title, includeSummary: true));
    }

    /// <summary>
    /// Beyannameyi GİB'e gönderir (Draft/Ready/Error → Sent). Error durumunda yeniden deneme budur.
    /// Faz 11 (E6): ?queue=true ile anında göndermek yerine gönderim kuyruğuna atar; anında
    /// gönderim GEÇİCİ hatayla (GİB/ağ çökmesi) başarısız olursa iş OTOMATİK kuyruğa düşer
    /// (mevcut anında-hata yanıtı korunur, mesaja kuyruk bilgisi eklenir).
    /// </summary>
    [HttpPost("{id:guid}/send")]
    public async Task<ActionResult<DeclarationActionResponse>> Send(
        Guid id, [FromQuery] bool queue = false, CancellationToken ct = default)
    {
        // İsteğe bağlı kuyruk modu: gönderim hiç denenmez, iş sıraya alınır.
        if (queue)
        {
            var declaration = await _db.Declarations.AsNoTracking()
                .Include(d => d.Client)
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (declaration is null)
                return NotFound(new { message = "Beyanname bulunamadı." });
            if (declaration.Status is Domain.DeclarationStatus.Sent or Domain.DeclarationStatus.Approved)
                return BadRequest(new { message = "Beyanname zaten gönderilmiş; kuyruğa alınmadı." });
            if (string.IsNullOrWhiteSpace(declaration.SummaryJson))
                return BadRequest(new { message = "Beyanname taslağı boş. Önce \"Taslağı Hazırla\" ile hesaplama yapın." });

            await _jobs.EnqueueAsync(declaration.TenantId, OutboundJobService.TypeBeyannameGonder,
                new OutboundJobService.BeyannamePayload { DeclarationId = declaration.Id }, ct);

            return Ok(new DeclarationActionResponse
            {
                Message = "Gönderim kuyruğa alındı — sistem müsait olduğunda otomatik gönderilecek. " +
                          "Durumu Gönderim Kuyruğu sayfasından izleyebilirsiniz.",
                Declaration = DeclarationResponse.FromEntity(declaration, declaration.Client?.Title, includeSummary: true)
            });
        }

        var result = await _declarationService.SendAsync(id, ct);

        // Faz 11 (E6): geçici (retryable) gönderim hatasında iş otomatik kuyruğa düşer;
        // hata yanıtı davranışı KORUNUR, kullanıcı kuyruk bilgisiyle bilgilendirilir.
        if (!result.Success && result.Retryable && !result.NotFound)
        {
            var declaration = await _db.Declarations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (declaration is not null)
            {
                await _jobs.EnqueueAsync(declaration.TenantId, OutboundJobService.TypeBeyannameGonder,
                    new OutboundJobService.BeyannamePayload { DeclarationId = declaration.Id }, ct);

                return BadRequest(new
                {
                    message = (result.ErrorMessage ?? "Beyanname gönderilemedi.") +
                              " Gönderim kuyruğa alındı — sistem müsait olduğunda otomatik denenecek."
                });
            }
        }

        return ToActionResult(result, await ResolveClientTitleAsync(result, ct));
    }

    /// <summary>Sağlayıcıdan durum/tahakkuk çeker (Sent → Approved/Error; işleme sürüyorsa Sent kalır).</summary>
    [HttpPost("{id:guid}/refresh-status")]
    public async Task<ActionResult<DeclarationActionResponse>> RefreshStatus(Guid id, CancellationToken ct)
    {
        var result = await _declarationService.RefreshStatusAsync(id, ct);
        return ToActionResult(result, await ResolveClientTitleAsync(result, ct));
    }

    private async Task<string?> ResolveClientTitleAsync(DeclarationResult result, CancellationToken ct)
    {
        if (result.Declaration is null) return null;
        return await _db.Clients.AsNoTracking()
            .Where(c => c.Id == result.Declaration.ClientId)
            .Select(c => c.Title)
            .FirstOrDefaultAsync(ct);
    }

    private ActionResult<DeclarationActionResponse> ToActionResult(DeclarationResult result, string? clientTitle)
    {
        if (!result.Success || result.Declaration is null)
        {
            var body = new { message = result.ErrorMessage ?? "İşlem gerçekleştirilemedi. Lütfen tekrar deneyin." };
            return result.NotFound ? NotFound(body) : BadRequest(body);
        }

        return Ok(new DeclarationActionResponse
        {
            Message = result.Message ?? string.Empty,
            Declaration = DeclarationResponse.FromEntity(result.Declaration, clientTitle, includeSummary: true)
        });
    }
}
