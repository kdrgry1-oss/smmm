using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "SuperAdmin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Tüm tenant'lar; plan, kullanıcı ve mükellef sayılarıyla.</summary>
    [HttpGet("tenants")]
    public async Task<ActionResult<List<TenantAdminResponse>>> Tenants()
    {
        var tenants = await _db.Tenants.AsNoTracking()
            .Select(t => new TenantAdminResponse
            {
                Id = t.Id,
                Name = t.Name,
                Email = t.Email,
                PlanCode = t.Plan != null ? t.Plan.Code : null,
                PlanName = t.Plan != null ? t.Plan.Name : null,
                SubscriptionStatus = t.SubscriptionStatus.ToString(),
                UserCount = t.Users.Count,
                ClientCount = t.Clients.Count(c => !c.IsArchived),
                CreatedAt = t.CreatedAt
            })
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

        return Ok(tenants);
    }

    /// <summary>Genel istatistikler: toplam tenant/kullanıcı/mükellef ve plana göre dağılım.</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<AdminStatsResponse>> Stats()
    {
        var totalTenants = await _db.Tenants.CountAsync();
        var totalUsers = await _db.Users.CountAsync();
        var totalClients = await _db.Clients.CountAsync(c => !c.IsArchived);

        var planDistribution = await _db.Tenants
            .Where(t => t.Plan != null)
            .GroupBy(t => new { t.Plan!.Code, t.Plan.Name })
            .Select(g => new PlanDistributionItem
            {
                PlanCode = g.Key.Code,
                PlanName = g.Key.Name,
                TenantCount = g.Count()
            })
            .OrderByDescending(x => x.TenantCount)
            .ToListAsync();

        return Ok(new AdminStatsResponse
        {
            TotalTenants = totalTenants,
            TotalUsers = totalUsers,
            TotalClients = totalClients,
            PlanDistribution = planDistribution
        });
    }

    /// <summary>
    /// Gelir özeti (blueprint M7 — süper admin gelir/tahsilat panosu): bu ayın ve tüm zamanların
    /// başarılı tahsilat toplamı + abonelik durumu dağılımı. Durum sayıları Subscription tablosundan
    /// okunur (tek gerçek kaynak); abonelik kaydı olmayan eski tenant'lar Tenant durumundan tamamlanır.
    /// </summary>
    [HttpGet("billing/overview")]
    public async Task<ActionResult<AdminBillingOverviewResponse>> BillingOverview(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var monthlyRevenue = await _db.PaymentTransactions
            .Where(t => t.Status == PaymentStatus.Succeeded && t.CreatedAt >= monthStart)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;

        var totalRevenue = await _db.PaymentTransactions
            .Where(t => t.Status == PaymentStatus.Succeeded)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;

        var monthlyInvoiceCount = await _db.BillingInvoices.CountAsync(i => i.IssuedAt >= monthStart, ct);

        // Abonelik kaydı olan tenant'lar Subscription'dan, olmayanlar (Faz 5 öncesi) Tenant'tan sayılır.
        var subscribedStatuses = await _db.Subscriptions
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var legacyStatuses = await _db.Tenants
            .Where(t => !_db.Subscriptions.Any(s => s.TenantId == t.Id))
            .GroupBy(t => t.SubscriptionStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountOf(SubscriptionStatus status) =>
            subscribedStatuses.Where(x => x.Status == status).Sum(x => x.Count) +
            legacyStatuses.Where(x => x.Status == status).Sum(x => x.Count);

        return Ok(new AdminBillingOverviewResponse
        {
            MonthlyRevenue = monthlyRevenue,
            TotalRevenue = totalRevenue,
            MonthlyInvoiceCount = monthlyInvoiceCount,
            ActiveCount = CountOf(SubscriptionStatus.Active),
            TrialCount = CountOf(SubscriptionStatus.Trial),
            PastDueCount = CountOf(SubscriptionStatus.PastDue),
            CanceledCount = CountOf(SubscriptionStatus.Canceled)
        });
    }

    /// <summary>Tenant abonelik durumunu değiştirir (Trial/Active/PastDue/Canceled).</summary>
    [HttpPost("tenants/{id:guid}/status")]
    public async Task<ActionResult<TenantAdminResponse>> UpdateStatus(Guid id, [FromBody] TenantStatusUpdateRequest request)
    {
        var tenant = await _db.Tenants
            .Include(t => t.Plan)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return NotFound(new { message = "Firma (tenant) bulunamadı." });

        tenant.SubscriptionStatus = request.Status;
        await _db.SaveChangesAsync();

        return Ok(new TenantAdminResponse
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Email = tenant.Email,
            PlanCode = tenant.Plan?.Code,
            PlanName = tenant.Plan?.Name,
            SubscriptionStatus = tenant.SubscriptionStatus.ToString(),
            UserCount = await _db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == tenant.Id),
            ClientCount = await _db.Clients.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenant.Id && !c.IsArchived),
            CreatedAt = tenant.CreatedAt
        });
    }
}
