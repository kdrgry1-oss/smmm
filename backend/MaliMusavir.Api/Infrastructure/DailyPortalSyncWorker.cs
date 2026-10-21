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
    private static readonly TimeZoneInfo TurkeyTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    /// <summary>Her turda taranan geriye dönük aralık (geç düşen belgeler yakalanır).</summary>
    public const int LookbackDays = 7;

    /// <summary>Zamanı kaçırmamak için dakikada bir kontrol edilir.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyPortalSyncWorker> _logger;

    /// <summary>Aynı saat diliminde ikinci kez tetiklenmeyi engeller.</summary>
    private DateTime? _lastRunHour;

    public DailyPortalSyncWorker(IServiceScopeFactory scopeFactory, ILogger<DailyPortalSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GİB portal saatlik senkron tetikleyicisi başladı (her saat başı tüm mükellefler taranır, son {Days} gün).",
            LookbackDays);

        var isFirstRun = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TurkeyTimeZone);
                var currentHour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);

                if (isFirstRun || _lastRunHour != currentHour)
                {
                    isFirstRun = false;
                    _lastRunHour = currentHour;
                    _logger.LogInformation("GİB portal saatlik tarama döngüsü başladı: {Time:yyyy-MM-dd HH:00}", currentHour);
                    await EnqueueAllAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // normal kapanış
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GİB portal saatlik tetikleme turu hata verdi.");
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

        _logger.LogInformation("GİB portal saatlik senkron tetikleyicisi durdu.");
    }

    /// <summary>
    /// GibPortal entegrasyonu olan tüm (arşivlenmemiş) mükellefler için işi kuyruğa alır.
    /// Her mükellef KENDİ tenant'ının scope'unda kuyruğa yazılır (tenant izolasyonu korunur).
    /// </summary>
    private async Task EnqueueAllAsync(CancellationToken ct)
    {
        List<(Guid ClientId, Guid TenantId)> targets;
        // Fatura çekimi AYRI hedef kümesi kullanır: e-Tebligat yalnız GİB'den gelir,
        // ama gelen fatura entegratörün KENDİ kimliğiyle de çekilebilir. Bu yüzden
        // entegratörlü (Nilvera/Uyumsoft/eLogo/EDM/Doğan/Paraşüt/e-Arşiv) mükellefler
        // tebligat listesinde olmasa da fatura listesinde yer alır.
        List<(Guid ClientId, Guid TenantId)> invoiceTargets;

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

            // Fatura hedefleri: entegrasyon kaydı OLAN tüm aktif mükellefler (Mock hariç) + müşavir GİB mükellefleri.
            // EInvoiceSyncService sağlayıcıyı mükellefin kendi kaydından veya müşavir GİB oturumundan seçer.
            var integrationInvoiceTargets = (await db.ClientIntegrations
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(ci => ci.Provider != IntegrationProvider.Mock)
                    .Join(
                        db.Clients.IgnoreQueryFilters().AsNoTracking().Where(c => !c.IsArchived),
                        ci => ci.ClientId,
                        c => c.Id,
                        (ci, c) => new { c.Id, c.TenantId })
                    .ToListAsync(ct))
                .Select(x => (x.Id, x.TenantId));

            invoiceTargets = integrationInvoiceTargets
                .Concat(byAdvisor)
                .GroupBy(t => t.Item1)
                .Select(g => g.First())
                .ToList();
        }

        if (targets.Count == 0 && invoiceTargets.Count == 0)
        {
            _logger.LogInformation(
                "Günlük senkron: ne GİB portal/müşavir kimliği olan mükellef ne de entegratör kaydı var.");
            return;
        }

        if (targets.Count == 0)
        {
            // Tebligat kaynağı yok ama fatura çekilebilir — tur fatura ile devam eder.
            _logger.LogInformation(
                "Günlük senkron: e-Tebligat için GİB kimliği tanımlı mükellef yok. " +
                "Entegratörlü mükelleflerin tebligatı için \"GİB Bağlantısı\" sayfasından " +
                "müşavir girişi tanımlayın. Fatura çekimi etkilenmez.");
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
            "e-Tebligat günlük senkronu kuyruğa alındı. Mükellef: {Queued}/{Total}, aralık: {From:d} - {To:d}",
            queued, targets.Count, from, to);

        // ---------- Gelen fatura turu (tebligattan BAĞIMSIZ) ----------
        // Ayrı iş tipi kullanılır: portal tebligat çağrısı takılsa bile faturalar akar,
        // faturalar hata verse bile kanuni süreli tebligat gecikmez.
        var invoiceQueued = 0;

        foreach (var (clientId, tenantId) in invoiceTargets)
        {
            ct.ThrowIfCancellationRequested();

            using var invoiceScope = _scopeFactory.CreateScope();
            invoiceScope.ServiceProvider.GetRequiredService<WorkerCurrentUser>().ActAsTenant(tenantId);

            var invoiceJobService = invoiceScope.ServiceProvider.GetRequiredService<OutboundJobService>();
            try
            {
                await invoiceJobService.EnqueueAsync(
                    tenantId,
                    OutboundJobService.TypeFaturaSync,
                    new { clientId, from, to },
                    ct);
                invoiceQueued++;
            }
            catch (Exception ex)
            {
                // Tek mükellefin hatası diğerlerini durdurmaz.
                _logger.LogError(ex, "Fatura senkron işi kuyruğa alınamadı. ClientId: {ClientId}", clientId);
            }
        }

        _logger.LogInformation(
            "Gelen fatura günlük senkronu kuyruğa alındı. Mükellef: {Queued}/{Total}, aralık: {From:d} - {To:d}",
            invoiceQueued, invoiceTargets.Count, from, to);
    }
}
