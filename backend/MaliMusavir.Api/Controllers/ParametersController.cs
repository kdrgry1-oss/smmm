using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 7 — E4: Parametre merkezi uçları. GET tüm oturum açmış kullanıcılara açıktır
/// (hesaplamaların hangi değerlerle çalıştığı şeffaftır); yazma uçları YALNIZCA SuperAdmin.
/// Not: AdminController sınıf genelinde SuperAdmin zorunlu kıldığından, "GET herkese açık"
/// gereksinimi için parametre CRUD'u bu ayrı controller'da toplanmıştır.
/// Her yazma işlemi ParameterService önbelleğini geçersiz kılar.
/// </summary>
[ApiController]
[Route("api/parameters")]
[Authorize]
public class ParametersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<ParametersController> _logger;

    public ParametersController(AppDbContext db, ILogger<ParametersController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Tüm parametreler (anahtar sırasıyla). Global tablo — tenant filtresi yoktur.</summary>
    [HttpGet]
    public async Task<ActionResult<List<TaxParameterResponse>>> List(CancellationToken ct = default)
    {
        var parameters = await _db.TaxParameters
            .AsNoTracking()
            .OrderBy(p => p.Key)
            .ToListAsync(ct);

        return Ok(parameters.Select(TaxParameterResponse.FromEntity).ToList());
    }

    /// <summary>Yeni parametre ekler (yalnızca SuperAdmin; anahtar benzersiz olmalıdır).</summary>
    [HttpPost]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<TaxParameterResponse>> Create(
        [FromBody] TaxParameterCreateRequest request,
        CancellationToken ct = default)
    {
        var key = request.Key.Trim();
        var duplicate = await _db.TaxParameters.AnyAsync(p => p.Key == key, ct);
        if (duplicate)
            return Conflict(new { message = "Bu anahtarla kayıtlı parametre zaten var." });

        var parameter = new TaxParameter
        {
            Key = key,
            Value = request.Value.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            EffectiveFrom = DateTime.SpecifyKind((request.EffectiveFrom ?? DateTime.UtcNow).Date, DateTimeKind.Utc),
            UpdatedAt = DateTime.UtcNow
        };

        _db.TaxParameters.Add(parameter);
        await _db.SaveChangesAsync(ct);
        ParameterService.Invalidate(); // önbellek tazelenir — sonraki hesaplama yeni değeri okur

        _logger.LogInformation("Parametre eklendi: {Key} = {Value}", parameter.Key, parameter.Value);
        return Ok(TaxParameterResponse.FromEntity(parameter));
    }

    /// <summary>
    /// Parametre değerini günceller (yalnızca SuperAdmin). DİKKAT: bu değerler
    /// tüm tenant'ların hesaplamalarını (Luca KDV eşlemesi, motor güvenleri...) etkiler.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<TaxParameterResponse>> Update(
        Guid id,
        [FromBody] TaxParameterUpdateRequest request,
        CancellationToken ct = default)
    {
        var parameter = await _db.TaxParameters.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (parameter is null)
            return NotFound(new { message = "Parametre bulunamadı." });

        parameter.Value = request.Value.Trim();
        if (request.Description is not null)
            parameter.Description = request.Description.Trim();
        if (request.EffectiveFrom.HasValue)
            parameter.EffectiveFrom = DateTime.SpecifyKind(request.EffectiveFrom.Value.Date, DateTimeKind.Utc);
        parameter.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        ParameterService.Invalidate();

        _logger.LogInformation("Parametre güncellendi: {Key} = {Value}", parameter.Key, parameter.Value);
        return Ok(TaxParameterResponse.FromEntity(parameter));
    }

    /// <summary>Parametreyi siler (yalnızca SuperAdmin). Okuyanlar varsayılan sabitlerine döner.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var parameter = await _db.TaxParameters.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (parameter is null)
            return NotFound(new { message = "Parametre bulunamadı." });

        _db.TaxParameters.Remove(parameter);
        await _db.SaveChangesAsync(ct);
        ParameterService.Invalidate();

        _logger.LogInformation("Parametre silindi: {Key}", parameter.Key);
        return NoContent();
    }
}
