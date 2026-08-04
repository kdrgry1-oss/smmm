using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>"Dönemi oluştur" sonucu (E7).</summary>
public class FeeGenerateResult
{
    public int Added { get; set; }
    public int SkippedDuplicates { get; set; }
    /// <summary>Vadesi geçtiği için Gecikti'ye çekilen tahakkuk sayısı (tüm dönemler).</summary>
    public int OverdueMarked { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Dönem tahsilat özeti (E7 — özet kartlar).</summary>
public class FeeSummary
{
    /// <summary>Beklenen toplam (dönemdeki tüm tahakkuklar).</summary>
    public decimal ExpectedTotal { get; set; }
    /// <summary>Tahsil edilen toplam (Odendi — PaidAmount yoksa Amount).</summary>
    public decimal PaidTotal { get; set; }
    /// <summary>Geciken toplam (Gecikti).</summary>
    public decimal OverdueTotal { get; set; }
    public int TotalCount { get; set; }
    public int PaidCount { get; set; }
    public int OverdueCount { get; set; }
    public int PendingCount { get; set; }
}

/// <summary>
/// Faz 11 — E7: SMMM ücret/tahsilat takibi. Meslek mensubunun KENDİ hizmet ücretini
/// mükelleflerinden tahsil etmesini takip eder: mükellef başına aylık ücret tanımı (ClientFee),
/// dönem tahakkuku üretimi (FeeInvoice — mükerrersiz), vade geçince otomatik Gecikti işareti,
/// elle "Ödendi" işaretleme. DÜRÜST KAPSAM: e-Arşiv hizmet faturası kesimi ve otomatik
/// hatırlatma (SMS/e-posta) bu fazda yoktur.
/// </summary>
public class FeeService
{
    private readonly AppDbContext _db;
    private readonly ILogger<FeeService> _logger;

    public FeeService(AppDbContext db, ILogger<FeeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Seçilen ay için ücret tanımı olan TÜM aktif mükelleflere tahakkuk üretir (mükerrersiz;
    /// vade = ayın son günü). Ek olarak tenant'ın TÜM dönemlerindeki vadesi geçmiş Bekliyor
    /// kayıtlarını Gecikti'ye çeker.
    /// </summary>
    public async Task<FeeGenerateResult> GenerateAsync(Guid tenantId, string period, CancellationToken ct)
    {
        if (!DeclarationService.TryParsePeriod(period, out var periodStart))
            throw new ArgumentException("Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).", nameof(period));

        // Vade: ayın son günü (UTC gün başı).
        var dueDate = periodStart.AddMonths(1).AddDays(-1);
        var periodEnd = periodStart.AddMonths(1);

        // Ücret tanımı olan aktif mükellefler; StartDate bu dönemden sonra başlıyorsa üretilmez.
        var fees = await _db.ClientFees
            .Include(f => f.Client)
            .Where(f => f.Client != null && !f.Client.IsArchived && f.StartDate < periodEnd)
            .ToListAsync(ct);

        var existingClientIds = await _db.FeeInvoices
            .Where(i => i.Period == period)
            .Select(i => i.ClientId)
            .ToListAsync(ct);
        var existingSet = existingClientIds.ToHashSet();

        var added = 0;
        foreach (var fee in fees)
        {
            if (existingSet.Contains(fee.ClientId)) continue;
            if (fee.MonthlyFee <= 0) continue; // güvenlik: 0/negatif ücret tahakkuk üretmez

            _db.FeeInvoices.Add(new FeeInvoice
            {
                TenantId = tenantId,
                ClientId = fee.ClientId,
                Period = period,
                Amount = fee.MonthlyFee,
                Status = FeeInvoiceStatus.Bekliyor,
                DueDate = dueDate
            });
            added++;
        }

        var overdueMarked = await MarkOverdueAsync(ct);
        await _db.SaveChangesAsync(ct);

        var skipped = existingSet.Count(id => fees.Any(f => f.ClientId == id));
        var message = fees.Count == 0
            ? "Ücret tanımı yapılmış mükellef yok. Önce mükelleflere aylık ücret tanımlayın."
            : $"{added} tahakkuk oluşturuldu" +
              (skipped > 0 ? $", {skipped} mevcut kayıt atlandı" : string.Empty) +
              (overdueMarked > 0 ? $", {overdueMarked} tahakkuk gecikti olarak işaretlendi" : string.Empty) + ".";

        _logger.LogInformation(
            "Ücret tahakkuku üretildi. Tenant: {TenantId}, Dönem: {Period}, Eklenen: {Added}",
            tenantId, period, added);

        return new FeeGenerateResult
        {
            Added = added,
            SkippedDuplicates = skipped,
            OverdueMarked = overdueMarked,
            Message = message
        };
    }

    /// <summary>
    /// Tahakkuk listesi + özet. Liste her açılışında vadesi geçmiş Bekliyor kayıtları da
    /// Gecikti'ye çekilir (durum ekranda daima günceldir). Özet, status filtresinden
    /// BAĞIMSIZ dönem toplamlarını verir (kartlar filtreyle kaybolmaz).
    /// </summary>
    public async Task<(List<FeeInvoice> Items, FeeSummary Summary)> ListAsync(
        string? period, FeeInvoiceStatus? status, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(period) && !DeclarationService.TryParsePeriod(period, out _))
            throw new ArgumentException("Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).", nameof(period));

        var overdueMarked = await MarkOverdueAsync(ct);
        if (overdueMarked > 0)
            await _db.SaveChangesAsync(ct);

        var baseQuery = _db.FeeInvoices.AsNoTracking().Include(i => i.Client).AsQueryable();
        if (!string.IsNullOrWhiteSpace(period))
            baseQuery = baseQuery.Where(i => i.Period == period);

        // Özet filtreden bağımsız (yalnızca dönem daraltmasıyla).
        var all = await baseQuery.ToListAsync(ct);
        var summary = new FeeSummary
        {
            ExpectedTotal = Math.Round(all.Sum(i => i.Amount), 2),
            PaidTotal = Math.Round(all.Where(i => i.Status == FeeInvoiceStatus.Odendi)
                .Sum(i => i.PaidAmount ?? i.Amount), 2),
            OverdueTotal = Math.Round(all.Where(i => i.Status == FeeInvoiceStatus.Gecikti)
                .Sum(i => i.Amount), 2),
            TotalCount = all.Count,
            PaidCount = all.Count(i => i.Status == FeeInvoiceStatus.Odendi),
            OverdueCount = all.Count(i => i.Status == FeeInvoiceStatus.Gecikti),
            PendingCount = all.Count(i => i.Status == FeeInvoiceStatus.Bekliyor)
        };

        var items = all
            .Where(i => !status.HasValue || i.Status == status.Value)
            .OrderByDescending(i => i.Period)
            .ThenBy(i => i.Client?.Title, StringComparer.Create(
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true))
            .ToList();

        return (items, summary);
    }

    /// <summary>Tahakkuku ödendi işaretler (paidAmount boşsa tahakkuk tutarı kabul edilir).</summary>
    public async Task<(FeeInvoice? Invoice, string? Error)> MarkPaidAsync(
        Guid id, decimal? paidAmount, CancellationToken ct)
    {
        var invoice = await _db.FeeInvoices.Include(i => i.Client).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return (null, "Tahakkuk bulunamadı.");
        if (invoice.Status == FeeInvoiceStatus.Odendi)
            return (null, "Bu tahakkuk zaten ödendi olarak işaretli.");
        if (paidAmount is <= 0)
            return (null, "Tahsil edilen tutar 0'dan büyük olmalıdır.");

        invoice.Status = FeeInvoiceStatus.Odendi;
        invoice.PaidAt = DateTime.UtcNow;
        invoice.PaidAmount = paidAmount ?? invoice.Amount;
        await _db.SaveChangesAsync(ct);
        return (invoice, null);
    }

    /// <summary>
    /// Ödendi işaretini geri alır (yanlış işaretleme düzeltmesi). Durum, vadeye göre
    /// Bekliyor veya Gecikti olarak geri hesaplanır.
    /// </summary>
    public async Task<(FeeInvoice? Invoice, string? Error)> ReopenAsync(Guid id, CancellationToken ct)
    {
        var invoice = await _db.FeeInvoices.Include(i => i.Client).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return (null, "Tahakkuk bulunamadı.");
        if (invoice.Status != FeeInvoiceStatus.Odendi)
            return (null, "Yalnızca ödendi işaretli tahakkuklar geri açılabilir.");

        invoice.Status = invoice.DueDate.Date < DateTime.UtcNow.Date
            ? FeeInvoiceStatus.Gecikti
            : FeeInvoiceStatus.Bekliyor;
        invoice.PaidAt = null;
        invoice.PaidAmount = null;
        await _db.SaveChangesAsync(ct);
        return (invoice, null);
    }

    /// <summary>Vadesi geçmiş Bekliyor kayıtlarını Gecikti'ye çeker (SaveChanges ÇAĞIRMAZ).</summary>
    private async Task<int> MarkOverdueAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var overdue = await _db.FeeInvoices
            .Where(i => i.Status == FeeInvoiceStatus.Bekliyor && i.DueDate < today)
            .ToListAsync(ct);

        foreach (var invoice in overdue)
            invoice.Status = FeeInvoiceStatus.Gecikti;

        return overdue.Count;
    }
}
