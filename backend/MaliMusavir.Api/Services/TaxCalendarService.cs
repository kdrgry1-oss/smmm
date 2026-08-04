using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Dönem üretimi sonucu (Türkçe özet mesaj için sayaçlar).</summary>
public class GenerateObligationsOutcome
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int Added { get; init; }
    public int SkippedDuplicates { get; init; }
    public int ClientCount { get; init; }
    /// <summary>Beyanname senkronunda otomatik Tamamlandi işaretlenen yükümlülük sayısı.</summary>
    public int AutoCompleted { get; init; }
}

/// <summary>
/// Vergi takvimi servisi (Faz 7 — E2). "Dönemi oluştur": seçilen ay için tüm aktif
/// mükelleflere standart yükümlülük setini üretir (mükerrer üretmez); beyanname senkronu:
/// aynı dönem KDV1 beyannamesi Approved olan mükellefin KDV yükümlülüğünü otomatik tamamlar.
/// </summary>
public class TaxCalendarService
{
    /// <summary>KDV yükümlülüğünün tür etiketi (beyanname senkronu bu türü arar).</summary>
    public const string KdvType = "KDV Beyannamesi";

    /// <summary>
    /// Standart yükümlülük seti: tür + son günün TAKİP EDEN AYDAKİ sabit günü.
    /// KDV: takip eden ayın 28'i; Muhtasar ve SGK: 26'sı. Mevzuat değişirse bu tablo
    /// tek yerden güncellenir (kolayca değiştirilebilir const tablo — E2 tasarım notu).
    /// </summary>
    public static readonly IReadOnlyList<(string Type, int DayOfFollowingMonth)> StandardSet = new[]
    {
        (KdvType, 28),
        ("Muhtasar", 26),
        ("SGK Bildirge", 26),
    };

    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<TaxCalendarService> _logger;

    public TaxCalendarService(AppDbContext db, ICurrentUser currentUser, ILogger<TaxCalendarService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>"yyyy-MM" dönemini ay başlangıcına çözer (Declaration ile aynı biçim).</summary>
    public static bool TryParsePeriod(string period, out DateTime periodStart)
    {
        periodStart = default;
        if (string.IsNullOrWhiteSpace(period)) return false;

        if (!DateTime.TryParseExact(period.Trim(), "yyyy-MM",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return false;

        periodStart = DateTime.SpecifyKind(new DateTime(parsed.Year, parsed.Month, 1), DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// Seçilen ay için tüm aktif (arşivlenmemiş) mükelleflere standart seti üretir.
    /// Aynı (mükellef, tür, dönem) OTOMATİK kaydı varsa atlanır (mükerrer üretme yok);
    /// üretim sonrası beyanname senkronu çalışır.
    /// </summary>
    public async Task<GenerateObligationsOutcome> GeneratePeriodAsync(string period, CancellationToken ct)
    {
        if (_currentUser.TenantId is null)
            return new GenerateObligationsOutcome { Success = false, ErrorMessage = "Bu işlem için firma hesabı gereklidir." };

        if (!TryParsePeriod(period, out var periodStart))
            return new GenerateObligationsOutcome { Success = false, ErrorMessage = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA)." };

        var normalizedPeriod = periodStart.ToString("yyyy-MM");
        var tenantId = _currentUser.TenantId.Value;

        var clients = await _db.Clients
            .AsNoTracking()
            .Where(c => !c.IsArchived)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (clients.Count == 0)
            return new GenerateObligationsOutcome { Success = false, ErrorMessage = "Dönemi oluşturmak için önce en az bir mükellef ekleyin." };

        // Mevcut kayıtlar tek sorguda: (ClientId, Type) çifti mükerrer kontrolü için.
        var existing = await _db.TaxObligations
            .AsNoTracking()
            .Where(o => o.Period == normalizedPeriod)
            .Select(o => new { o.ClientId, o.Type })
            .ToListAsync(ct);
        var existingKeys = existing.Select(x => (x.ClientId, x.Type)).ToHashSet();

        var added = 0;
        var skipped = 0;
        var followingMonth = periodStart.AddMonths(1);

        foreach (var clientId in clients)
        {
            foreach (var (type, day) in StandardSet)
            {
                if (existingKeys.Contains((clientId, type)))
                {
                    skipped++;
                    continue;
                }

                _db.TaxObligations.Add(new TaxObligation
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    Type = type,
                    Period = normalizedPeriod,
                    // Son gün: dönemi TAKİP EDEN ayın sabit günü (ör. 2026-06 KDV → 28.07.2026).
                    DueDate = DateTime.SpecifyKind(
                        new DateTime(followingMonth.Year, followingMonth.Month, day), DateTimeKind.Utc),
                    Status = ObligationStatus.Bekliyor,
                    Source = ObligationSource.Otomatik
                });
                added++;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Beyanname bağlantısı: onaylanmış KDV1 beyannamesi olan mükelleflerin KDV yükümlülüğü kapatılır.
        var autoCompleted = await SyncWithDeclarationsAsync(normalizedPeriod, ct);

        _logger.LogInformation(
            "Vergi takvimi dönemi oluşturuldu. Dönem: {Period}, Eklendi: {Added}, Atlandı: {Skipped}",
            normalizedPeriod, added, skipped);

        return new GenerateObligationsOutcome
        {
            Success = true,
            Added = added,
            SkippedDuplicates = skipped,
            ClientCount = clients.Count,
            AutoCompleted = autoCompleted
        };
    }

    /// <summary>
    /// Beyanname senkronu (E2 ↔ Faz 5 bağlantısı): aynı dönemde KDV1 beyannamesi Approved
    /// olan mükelleflerin bekleyen "KDV Beyannamesi" yükümlülüklerini Tamamlandi işaretler.
    /// İdempotenttir — liste görüntülenirken de çağrılabilir.
    /// </summary>
    public async Task<int> SyncWithDeclarationsAsync(string period, CancellationToken ct)
    {
        var approvedClientIds = await _db.Declarations
            .AsNoTracking()
            .Where(d => d.Period == period
                        && d.Type == DeclarationType.KDV1
                        && d.Status == DeclarationStatus.Approved)
            .Select(d => d.ClientId)
            .ToListAsync(ct);

        if (approvedClientIds.Count == 0) return 0;

        var pending = await _db.TaxObligations
            .Where(o => o.Period == period
                        && o.Type == KdvType
                        && o.Status == ObligationStatus.Bekliyor
                        && approvedClientIds.Contains(o.ClientId))
            .ToListAsync(ct);

        if (pending.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var obligation in pending)
        {
            obligation.Status = ObligationStatus.Tamamlandi;
            obligation.CompletedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Beyanname senkronu: {Count} KDV yükümlülüğü otomatik tamamlandı. Dönem: {Period}",
            pending.Count, period);

        return pending.Count;
    }
}
