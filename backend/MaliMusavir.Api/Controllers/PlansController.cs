using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

[ApiController]
[Route("api/plans")]
public class PlansController : ControllerBase
{
    private readonly AppDbContext _db;

    public PlansController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Aktif abonelik planları (herkese açık).</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<List<PlanResponse>>> List()
    {
        var plans = await _db.Plans.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.MonthlyPrice)
            .ToListAsync();

        return Ok(plans.Select(PlanResponse.FromEntity).ToList());
    }
}
