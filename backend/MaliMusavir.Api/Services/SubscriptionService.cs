using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Payments;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Abonelik işlemi sonucu (ETebligatSyncResult ile aynı desen — Türkçe mesajlı).</summary>
public class SubscribeResult
{
    public bool Success { get; init; }
    /// <summary>Başarıda kullanıcıya gösterilecek Türkçe özet.</summary>
    public string? Message { get; init; }
    /// <summary>Başarısızlıkta kullanıcıya gösterilecek Türkçe mesaj ("Ödeme alınamadı: ..." dahil).</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Yönlendirmeli sağlayıcıda (iyzico checkout form) ödeme sayfası adresi.</summary>
    public string? RedirectUrl { get; init; }
    public Subscription? Subscription { get; init; }
    public BillingInvoice? Invoice { get; init; }
}

/// <summary>
/// Abonelik yaşam döngüsü servisi (blueprint M7). Kurallar:
/// - Kayıtta 14 gün deneme otomatik başlar (StartTrial — AuthController.Register çağırır).
/// - Plan değişikliği ANINDA geçerlidir; kalan gün oranlaması yapılmaz, yeni dönem bugünden başlar
///   (bilinçli sadelik — blueprint Faz 5 kararı).
/// - İptal, dönem sonuna kadar aktif bırakır (CanceledAt işaretlenir).
/// - Yenileme kontrolü arka plan job'ı DEĞİLDİR: CheckAndUpdateStatusAsync login ve panel
///   özet (auth/me, billing/summary) çağrılarında tetiklenir; süresi geçen abonelikler PastDue olur.
/// - Subscription tablosu tek gerçek kaynaktır; Tenant.SubscriptionStatus her geçişte senkronlanır.
/// </summary>
public class SubscriptionService
{
    public const int TrialDays = 14;
    /// <summary>Abonelik faturalarında uygulanan KDV oranı (%20).</summary>
    public const decimal VatRate = 0.20m;

    private readonly AppDbContext _db;
    private readonly IPaymentProvider _paymentProvider;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(AppDbContext db, IPaymentProvider paymentProvider, ILogger<SubscriptionService> logger)
    {
        _db = db;
        _paymentProvider = paymentProvider;
        _logger = logger;
    }

    /// <summary>
    /// Yeni tenant için 14 günlük deneme aboneliği oluşturur (context'e ekler; SaveChanges çağırana aittir).
    /// Plan seed edilmemişse abonelik oluşturulmaz — tenant yine Trial durumuyla açılır (uyarı loglanır).
    /// </summary>
    public Subscription? StartTrial(Tenant tenant, Plan? trialPlan)
    {
        if (trialPlan is null)
        {
            _logger.LogWarning("Deneme planı bulunamadı; {Tenant} için abonelik kaydı oluşturulmadı.", tenant.Name);
            return null;
        }

        var now = DateTime.UtcNow;
        var subscription = new Subscription
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            PlanId = trialPlan.Id,
            Status = SubscriptionStatus.Trial,
            PeriodStart = now,
            PeriodEnd = now.AddDays(TrialDays),
            TrialEndsAt = now.AddDays(TrialDays)
        };

        tenant.SubscriptionStatus = SubscriptionStatus.Trial;
        _db.Subscriptions.Add(subscription);
        return subscription;
    }

    /// <summary>
    /// Yenileme/süre kontrolü: deneme süresi veya dönem sonu geçmişse durumu günceller ve
    /// Tenant.SubscriptionStatus'u senkronlar. Login ve panel özet çağrılarında tetiklenir.
    /// </summary>
    public async Task CheckAndUpdateStatusAsync(Guid tenantId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions
            .IgnoreQueryFilters()
            .Include(s => s.Tenant)
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        if (subscription is null) return; // eski kayıtlar (Faz 5 öncesi) — sessizce atlanır

        var now = DateTime.UtcNow;
        var previous = subscription.Status;

        switch (subscription.Status)
        {
            case SubscriptionStatus.Trial when subscription.TrialEndsAt is not null && subscription.TrialEndsAt <= now:
                // Deneme doldu → ödeme bekleniyor. İptal talebi varsa doğrudan Canceled.
                subscription.Status = subscription.CanceledAt is null
                    ? SubscriptionStatus.PastDue
                    : SubscriptionStatus.Canceled;
                break;

            case SubscriptionStatus.Active when subscription.PeriodEnd <= now:
                // Dönem sonu geçti: iptal talebi varsa Canceled, yoksa PastDue (yenileme bekleniyor).
                subscription.Status = subscription.CanceledAt is null
                    ? SubscriptionStatus.PastDue
                    : SubscriptionStatus.Canceled;
                break;
        }

        // Tenant.SubscriptionStatus'u her durumda senkron tut (tek gerçek kaynak: Subscription).
        var tenantChanged = subscription.Tenant is not null &&
                            subscription.Tenant.SubscriptionStatus != subscription.Status;

        if (subscription.Status != previous || tenantChanged)
        {
            if (subscription.Tenant is not null)
                subscription.Tenant.SubscriptionStatus = subscription.Status;

            await _db.SaveChangesAsync(ct);

            if (subscription.Status != previous)
            {
                _logger.LogInformation(
                    "Abonelik durumu güncellendi. Tenant: {TenantId}, {Old} → {New}",
                    tenantId, previous, subscription.Status);
            }
        }
    }

    /// <summary>
    /// Plan satın alma / yükseltme / düşürme: ödeme alınır, abonelik ANINDA yeni plana geçer
    /// (yeni dönem bugünden başlar, oranlaması yok), başarılı ödemede fatura kaydı üretilir.
    /// </summary>
    public async Task<SubscribeResult> SubscribeAsync(Guid tenantId, Guid planId, string? cardNumber, CancellationToken ct)
    {
        var tenant = await _db.Tenants.Include(t => t.Plan).FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return new SubscribeResult { Success = false, ErrorMessage = "Firma bilgisi bulunamadı." };

        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, ct);
        if (plan is null)
            return new SubscribeResult { Success = false, ErrorMessage = "Seçilen paket bulunamadı veya artık satışta değil." };

        // Düşürme koruması: mevcut kullanım yeni plan sınırlarını aşıyorsa geçiş engellenir
        // (MaxClients + MaxUsers kontrolü — plan limitleri tek noktadan burada doğrulanır).
        var activeClients = await _db.Clients.CountAsync(c => !c.IsArchived, ct);
        if (activeClients > plan.MaxClients)
        {
            return new SubscribeResult
            {
                Success = false,
                ErrorMessage = $"Bu pakete geçilemez: {activeClients} aktif mükellefiniz var, " +
                               $"{plan.Name} paketi en fazla {plan.MaxClients} mükellef destekler. " +
                               "Önce mükellef arşivleyin veya daha kapsamlı bir paket seçin."
            };
        }

        var userCount = await _db.Users.CountAsync(ct);
        if (userCount > plan.MaxUsers)
        {
            return new SubscribeResult
            {
                Success = false,
                ErrorMessage = $"Bu pakete geçilemez: {userCount} kullanıcınız var, " +
                               $"{plan.Name} paketi en fazla {plan.MaxUsers} kullanıcı destekler."
            };
        }

        var amount = plan.MonthlyPrice;                    // KDV hariç
        var vatAmount = Math.Round(amount * VatRate, 2);   // %20
        var totalAmount = amount + vatAmount;              // tahsil edilen

        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (subscription is null)
        {
            // Faz 5 öncesi tenant'lar için ilk abonelik kaydı burada açılır.
            subscription = new Subscription { TenantId = tenantId, PlanId = plan.Id };
            _db.Subscriptions.Add(subscription);
        }

        // Denetim izi: deneme dahil her tahsilat girişimi Pending olarak yazılır.
        var transaction = new PaymentTransaction
        {
            TenantId = tenantId,
            SubscriptionId = subscription.Id,
            Amount = totalAmount,
            Currency = "TRY",
            Provider = _paymentProvider.Name,
            Status = PaymentStatus.Pending
        };
        _db.PaymentTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);

        var checkout = await _paymentProvider.CreateCheckoutAsync(tenant, plan, totalAmount, cardNumber, ct);

        if (!checkout.Success || checkout.Data is null)
        {
            transaction.Status = PaymentStatus.Failed;
            transaction.FailureReason = checkout.ErrorMessage;
            await _db.SaveChangesAsync(ct);
            return new SubscribeResult
            {
                Success = false,
                ErrorMessage = "Ödeme alınamadı: " + (checkout.ErrorMessage ?? "bilinmeyen sağlayıcı hatası.")
            };
        }

        var result = checkout.Data;
        transaction.ProviderRef = result.ProviderRef;

        if (result.Declined)
        {
            transaction.Status = PaymentStatus.Failed;
            transaction.FailureReason = result.FailureReason;
            await _db.SaveChangesAsync(ct);
            return new SubscribeResult
            {
                Success = false,
                ErrorMessage = "Ödeme alınamadı: " + (result.FailureReason ?? "kartınız reddedildi.")
            };
        }

        if (!result.DirectSuccess && !string.IsNullOrWhiteSpace(result.RedirectUrl))
        {
            // Yönlendirmeli akış (iyzico checkout form): işlem Pending kalır; tahsilat onayı
            // sağlayıcı callback'i ile tamamlanacak (canlı anahtar sonrası eklenecek).
            await _db.SaveChangesAsync(ct);
            return new SubscribeResult
            {
                Success = true,
                RedirectUrl = result.RedirectUrl,
                Message = "Ödeme sayfasına yönlendiriliyorsunuz."
            };
        }

        // Doğrudan başarı (mock akışı): abonelik anında yeni plana geçer.
        var now = DateTime.UtcNow;
        transaction.Status = PaymentStatus.Succeeded;
        subscription.PlanId = plan.Id;
        subscription.Status = SubscriptionStatus.Active;
        subscription.PeriodStart = now;
        subscription.PeriodEnd = now.AddMonths(1);
        subscription.CanceledAt = null;

        tenant.PlanId = plan.Id;
        tenant.SubscriptionStatus = SubscriptionStatus.Active;

        var invoice = await CreateInvoiceAsync(tenantId, amount, vatAmount, totalAmount, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Abonelik ödemesi alındı. Tenant: {TenantId}, Plan: {Plan}, Tutar: {Total}, Fatura: {Number}",
            tenantId, plan.Code, totalAmount, invoice.Number);

        return new SubscribeResult
        {
            Success = true,
            Subscription = subscription,
            Invoice = invoice,
            Message = $"{plan.Name} paketine geçişiniz tamamlandı. {invoice.Number} numaralı faturanız oluşturuldu."
        };
    }

    /// <summary>İptal talebi: abonelik dönem sonuna kadar aktif kalır, sonra Canceled olur.</summary>
    public async Task<SubscribeResult> CancelAsync(Guid tenantId, CancellationToken ct)
    {
        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (subscription is null)
            return new SubscribeResult { Success = false, ErrorMessage = "Aktif bir aboneliğiniz bulunamadı." };

        if (subscription.Status == SubscriptionStatus.Canceled)
            return new SubscribeResult { Success = false, ErrorMessage = "Aboneliğiniz zaten iptal edilmiş." };

        if (subscription.CanceledAt is not null)
            return new SubscribeResult { Success = false, ErrorMessage = "İptal talebiniz zaten alınmış; abonelik dönem sonunda kapanacak." };

        subscription.CanceledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new SubscribeResult
        {
            Success = true,
            Subscription = subscription,
            Message = $"İptal talebiniz alındı. Aboneliğiniz {subscription.PeriodEnd:dd.MM.yyyy} tarihine kadar aktif kalacak."
        };
    }

    /// <summary>
    /// Yıl bazlı, tenant-bağımsız global sıradan fatura numarası üretir ("2026-000001") ve
    /// fatura kaydını context'e ekler (SaveChanges çağırana aittir). Eşzamanlılık notu:
    /// oku-artır-yaz deseni; Number üzerindeki unique index çakışmayı yakalar, yüksek hacimde
    /// PostgreSQL SEQUENCE'e geçilmelidir (BillingEntities.cs).
    /// </summary>
    private async Task<BillingInvoice> CreateInvoiceAsync(
        Guid tenantId, decimal amount, decimal vatAmount, decimal totalAmount, CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var sequence = await _db.InvoiceNumberSequences.FirstOrDefaultAsync(s => s.Year == year, ct);
        if (sequence is null)
        {
            sequence = new InvoiceNumberSequence { Year = year, LastNumber = 0 };
            _db.InvoiceNumberSequences.Add(sequence);
        }

        sequence.LastNumber++;

        var invoice = new BillingInvoice
        {
            TenantId = tenantId,
            Number = $"{year}-{sequence.LastNumber:D6}",
            Amount = amount,
            VatAmount = vatAmount,
            TotalAmount = totalAmount,
            IssuedAt = DateTime.UtcNow
        };

        _db.BillingInvoices.Add(invoice);
        return invoice;
    }
}
