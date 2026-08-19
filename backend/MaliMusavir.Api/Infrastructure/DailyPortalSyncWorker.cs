using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Faz 15 — GİB Portal Robotu günlük tetikleyicisi.
///
/// Her gün SUNUCU SAATİYLE 03:00'te, entegrasyonu <see cref="IntegrationProvider.GibPortal"/>
/// olan TÜM mükellefler için son 7 günlük aralıkla "GibPortalSync" işi kuyruğa alır.
/// İşleri OutboundJobWorker üstel geri çekilmeyle yürütür — bu sınıf yalnızca kuyruğa yazar,
/// GİB'e kendisi gitmez (uzun işler API isteğini bloklamaz).
///
/// Mali müşavir mükellefin GİB kimliğini BİR KEZ girer; sonrası bu worker sayesinde
/// tamamen otomatiktir — elle giriş istenmez.
///
/// TEK INSTANCE VARSAYIMI: OutboundJobWorker ile aynı varsayım geçerlidir (API tek kopya).
/// Mükerrer koruması ayrıca OutboundJobService.EnqueueAsync'tedir: aynı tip + aynı payload
/// için bekleyen/çalışan iş varsa yenisi açılmaz.
///
/// Gece 03:00 seçimi bilinçlidir: GİB portalı mesai dışında daha az yüklüdür ve robot
/// "nazik" davranır (istekler arası bekleme, mükellef başına tek oturum).
/// </summary>
public class DailyPortalSyncWorker : BackgroundService
{
    /// <summary>Günlük çalışma saati (Türkiye saati).</summary>
    public static readonly TimeSpan RunAt = TimeSpan.FromHours(3);

    private static readonly TimeZoneInfo TurkeyTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    /// <summary>Her gece taranan geriye dönük aralık (geç düşen belgeler yakalanır).</summary>
    public const int LookbackDays = 7;

    /// <summary>Zamanı kaçırmamak için dakikada bir kontrol edilir (Task.Delay kayması güvenli).</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyPortalSyncWorker> _logger;

    /// <summary>Aynı günde ikinci kez tetiklenmeyi engeller.</summary>
    private DateOnly? _lastRunDate;

    public DailyPortalSyncWorker(IServiceScopeFactory scopeFactory, ILogger<DailyPortalSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GİB portal günlük senkron tetikleyicisi başladı (her gün {Hour:00}:{Minute:00}, son {Days} gün).",
            RunAt.Hours, RunAt.Minutes, LookbackDays);

        // Uygulama 03:00'ten sonra açılmış olabilir. O günün taramasını atlamak yerine
        // ilk turda bir kez kuyruğa al; _lastRunDate aynı süreçte ikinci çalışmayı engeller.

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Docker varsayılanı UTC'dir; iş saati kullanıcıya gösterilen Türkiye
                // saatine göre hesaplanmalıdır.
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TurkeyTimeZone);
                var today = DateOnly.FromDateTime(localNow);

                if (localNow.TimeOfDay >= RunAt && _lastRunDate != today)
                {
                    _lastRunDate = today;
                    await EnqueueAllAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // normal kapanış
            }
            catch (Exception ex)
            {
                // Tur hatası döngüyü ÖLDÜRMEZ; yarın tekrar denenir.
                _logger.LogError(ex, "GİB portal günlük tetikleme turu hata verdi.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("GİB portal günlük senkron tetikleyicisi durdu.");
    }

    /// <summary>
    /// GibPortal entegrasyonu olan tüm (arşivlenmemiş) mükellefler için işi kuyruğa alır.
    /// Her mükellef KENDİ tenant'ının scope'unda kuyruğa yazılır (tenant izolasyonu korunur).
    /// </summary>
    private async Task EnqueueAllAsync(CancellationToken ct)
    {
        List<(Guid ClientId, Guid TenantId)> targets;

        // Sistem taraması: kimliksiz scope'ta tenant filtresi her şeyi gizler → IgnoreQueryFilters.
        using (var scanScope = _scopeFactory.CreateScope())
        {
            var db = scanScope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1) Entegrasyonu GibPortal olan mükellefler (mevcut davranış — mükellefin kendi kimliği).
            var byIntegration = (await db.ClientIntegrations
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(ci => ci.Provider == IntegrationProvider.GibPortal)
                    .Join(
                        db.Clients.IgnoreQueryFilters().AsNoTracking().Where(c => !c.IsArchived),
                        ci => ci.ClientId,
                        c => c.Id,
                        (ci, c) => new { c.Id, c.TenantId })
                    .ToListAsync(ct))
                .Select(x => (x.Id, x.TenantId))
                .ToList();

            // 2) Faz 18 — MÜŞAVİR TEK GİRİŞ: müşavir GİB kimliği tanımlı tenant'larda TÜM
            // (arşivlenmemiş) mükellefler tek müşavir oturumuyla taranır — mükellef başına
            // ClientIntegration şartı YOKTUR.
            var advisorTenantIds = await db.TenantGibCredentials
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Select(c => c.TenantId)
                .ToListAsync(ct);

            var byAdvisor = advisorTenantIds.Count == 0
                ? new List<(Guid Id, Guid TenantId)>()
                : (await db.Clients
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(c => !c.IsArchived && advisorTenantIds.Contains(c.TenantId))
                        .Select(c => new { c.Id, c.TenantId })
                        .ToListAsync(ct))
                    .Select(x => (x.Id, x.TenantId))
                    .ToList();

            targets = byIntegration
                .Concat(byAdvisor)
                .GroupBy(t => t.Item1)
                .Select(g => g.First())
                .ToList();
        }

        if (targets.Count == 0)
        {
            _logger.LogInformation(
                "GİB portal günlük senkronu: entegrasyonu GibPortal olan mükellef veya müşavir kimliği tanımlı tenant yok.");
            return;
        }

        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-LookbackDays);
        var queued = 0;

        foreach (var (clientId, tenantId) in targets)
        {
            ct.ThrowIfCancellationRequested();

            using var jobScope = _scopeFactory.CreateScope();
            // ÖNCE tenant kimliği etkinleştirilir; servisler bundan SONRA çözülür
            // (AppDbContext ICurrentUser'ı kurucusunda okur — sıra kritiktir).
            jobScope.ServiceProvider.GetRequiredService<WorkerCurrentUser>().ActAsTenant(tenantId);

            var jobService = jobScope.ServiceProvider.GetRequiredService<OutboundJobService>();
            try
            {
                await jobService.EnqueueAsync(
                    tenantId,
                    // e-Tebligat kanuni süreli olduğu için belge çekiminden bağımsız işlenir.
                    // Tarih aralığı GİB robotuna doğrudan iletilir; mükerrer koruma DB'dedir.
                    OutboundJobService.TypeTebligatSync,
                    new { clientId, from, to },
                    ct);
                queued++;
            }
            catch (Exception ex)
            {
                // Tek mükellefin hatası diğerlerini durdurmaz.
                _logger.LogError(ex, "GİB portal işi kuyruğa alınamadı. ClientId: {ClientId}", clientId);
            }
        }

        _logger.LogInformation(
            "GİB portal günlük senkronu kuyruğa alındı. Mükellef: {Queued}/{Total}, aralık: {From:d} - {To:d}",
            queued, targets.Count, from, to);
    }
}
