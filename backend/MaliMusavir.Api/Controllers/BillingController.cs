using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Abonelik/ödeme uçları (blueprint M7). Abonelik durumunun tek gerçek kaynağı Subscription
/// tablosudur; özet çağrısı süre kontrolünü de tetikler (SubscriptionService.CheckAndUpdateStatusAsync).
/// </summary>
[ApiController]
[Route("api/billing")]
[Authorize]
public class BillingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly SubscriptionService _subscriptionService;

    public BillingController(AppDbContext db, ICurrentUser currentUser, SubscriptionService subscriptionService)
    {
        _db = db;
        _currentUser = currentUser;
        _subscriptionService = subscriptionService;
    }

    /// <summary>Mevcut plan, abonelik durumu, dönem ve deneme süresinden kalan gün.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<BillingSummaryResponse>> Summary(CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        // Panel özet çağrısı yenileme kontrolünü tetikler (dönem sonu geçtiyse PastDue'ya düşer).
        await _subscriptionService.CheckAndUpdateStatusAsync(_currentUser.TenantId.Value, ct);

        return Ok(await BuildSummaryAsync(_currentUser.TenantId.Value, ct));
    }

    /// <summary>
    /// Paket satın alma / değiştirme. Mock sağlayıcıda ödeme ~2 sn içinde sonuçlanır:
    /// başarıda abonelik anında yeni plana geçer ve fatura kesilir; başarısızlıkta
    /// "Ödeme alınamadı: ..." Türkçe mesajı döner. Yalnızca ofis sahibi yapabilir.
    /// </summary>
    [HttpPost("subscribe")]
    [Authorize(Roles = "TenantOwner")]
    public async Task<ActionResult<SubscribeResponse>> Subscribe([FromBody] SubscribeRequest request, CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var result = await _subscriptionService.SubscribeAsync(
            _currentUser.TenantId.Value, request.PlanId, request.MockCardNumber, ct);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage ?? "Ödeme alınamadı. Lütfen tekrar deneyin." });

        return Ok(new SubscribeResponse
        {
            Message = result.Message ?? "Ödeme alındı.",
            RedirectUrl = result.RedirectUrl,
            Summary = await BuildSummaryAsync(_currentUser.TenantId.Value, ct)
        });
    }

    /// <summary>Abonelik iptali: dönem sonuna kadar aktif kalır. Yalnızca ofis sahibi yapabilir.</summary>
    [HttpPost("cancel")]
    [Authorize(Roles = "TenantOwner")]
    public async Task<ActionResult<SubscribeResponse>> Cancel(CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var result = await _subscriptionService.CancelAsync(_currentUser.TenantId.Value, ct);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage ?? "İptal işlemi gerçekleştirilemedi." });

        return Ok(new SubscribeResponse
        {
            Message = result.Message ?? "İptal talebiniz alındı.",
            Summary = await BuildSummaryAsync(_currentUser.TenantId.Value, ct)
        });
    }

    /// <summary>Tenant'ın abonelik faturaları (yeniden eskiye).</summary>
    [HttpGet("invoices")]
    public async Task<ActionResult<List<BillingInvoiceResponse>>> Invoices(CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var invoices = await _db.BillingInvoices.AsNoTracking()
            .OrderByDescending(i => i.IssuedAt)
            .ToListAsync(ct);

        return Ok(invoices.Select(BillingInvoiceResponse.FromEntity).ToList());
    }

    /// <summary>Tenant'ın ödeme işlemleri — başarısız denemeler dahil (denetim izi).</summary>
    [HttpGet("transactions")]
    public async Task<ActionResult<List<PaymentTransactionResponse>>> Transactions(CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var transactions = await _db.PaymentTransactions.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        return Ok(transactions.Select(PaymentTransactionResponse.FromEntity).ToList());
    }

    private async Task<BillingSummaryResponse> BuildSummaryAsync(Guid tenantId, CancellationToken ct)
    {
        var subscription = await _db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        if (subscription is null)
        {
            // Faz 5 öncesi tenant'lar: abonelik kaydı yok — Tenant üzerindeki durum gösterilir.
            var tenant = await _db.Tenants.AsNoTracking()
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

            return new BillingSummaryResponse
            {
                HasSubscription = false,
                PlanCode = tenant?.Plan?.Code,
                PlanName = tenant?.Plan?.Name,
                MonthlyPrice = tenant?.Plan?.MonthlyPrice,
                Status = (tenant?.SubscriptionStatus ?? SubscriptionStatus.Trial).ToString()
            };
        }

        int? trialDaysRemaining = null;
        if (subscription.Status == SubscriptionStatus.Trial && subscription.TrialEndsAt is not null)
        {
            var remaining = (int)Math.Ceiling((subscription.TrialEndsAt.Value - DateTime.UtcNow).TotalDays);
            trialDaysRemaining = Math.Max(remaining, 0);
        }

        return new BillingSummaryResponse
        {
            HasSubscription = true,
            PlanCode = subscription.Plan?.Code,
            PlanName = subscription.Plan?.Name,
            MonthlyPrice = subscription.Plan?.MonthlyPrice,
            Status = subscription.Status.ToString(),
            PeriodStart = subscription.PeriodStart,
            PeriodEnd = subscription.PeriodEnd,
            TrialEndsAt = subscription.TrialEndsAt,
            TrialDaysRemaining = trialDaysRemaining,
            CanceledAt = subscription.CanceledAt,
            CancelsAtPeriodEnd = subscription.CanceledAt is not null &&
                                 subscription.Status is SubscriptionStatus.Trial or SubscriptionStatus.Active
        };
    }
}
