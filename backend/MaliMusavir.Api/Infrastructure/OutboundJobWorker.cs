using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Faz 11 — E6: Kuyruk işçisi (.NET yerleşik BackgroundService — harici paket YOK).
/// Zamanı gelen (Bekliyor + NextAttemptAt ≤ şimdi) işleri tarar ve her işi, işin tenant'ı
/// adına açılmış AYRI bir scope'ta (WorkerCurrentUser) çalıştırır; böylece tenant izolasyon
/// filtreleri worker'da da aynen geçerli kalır.
///
/// Faz 16 (A2 — OCR hızı) revizyonları:
///  - UYANDIRMA SİNYALİ: OutboundJobSignal (singleton) yeni iş kuyruğa alınınca Notify eder;
///    worker 15 sn'lik tarama beklemesini keser ve işi HEMEN alır (yükleme → OCR gecikmesi ~0).
///  - SÜREKLİ DÖNGÜ: bir turda iş işlendiyse bekleme YAPILMADAN yeni tur atılır (kuyrukta iş
///    varken 15 sn beklenmez); kuyruk boşalınca sinyal/15 sn beklemesine dönülür.
///  - PARALEL OCR: "ReceiptOcr" işleri aynı anda en fazla OCR_CONCURRENCY (env, varsayılan 4)
///    paralel işlenir — her iş kendi scope'u + kendi DbContext'iyle çalıştığı için yarış yoktur;
///    işler ProcessAsync girişinde Calisiyor'a çekildiğinden aynı iş iki kez alınmaz (tek tur
///    içinde de her iş kimliği bir kez dağıtılır). DİĞER iş tipleri mevcut SIRALI davranışını korur.
///
/// TEK INSTANCE VARSAYIMI: İş seçimi satır kilidi (SELECT ... FOR UPDATE SKIP LOCKED)
/// kullanmaz; API'nin tek kopya çalıştığı varsayılır. Yatay ölçeklemede bu sınıf
/// kilitli seçim veya dağıtık kuyruk (ör. Postgres advisory lock) ile güncellenmelidir.
/// </summary>
public class OutboundJobWorker : BackgroundService
{
    /// <summary>Kuyruk tarama aralığı (sinyal gelmezse en geç bu sürede bir tarama yapılır).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Bir turda işlenecek azami iş (tek tur uzun sürmesin; kalan işler hemen sonraki turda alınır).</summary>
    private const int BatchSize = 10;

    /// <summary>OCR paralelliği varsayılanı (env OCR_CONCURRENCY ile değiştirilir).</summary>
    private const int DefaultOcrConcurrency = 4;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboundJobSignal _signal;
    private readonly int _ocrConcurrency;
    private readonly ILogger<OutboundJobWorker> _logger;

    public OutboundJobWorker(
        IServiceScopeFactory scopeFactory,
        OutboundJobSignal signal,
        IConfiguration configuration,
        ILogger<OutboundJobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _logger = logger;

        // OCR paralelliği: env OCR_CONCURRENCY (compose köprüsü var) — geçersiz/eksikse 4.
        var raw = configuration["OCR_CONCURRENCY"];
        _ocrConcurrency = int.TryParse(raw, out var parsed) && parsed >= 1 && parsed <= 32
            ? parsed
            : DefaultOcrConcurrency;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Gönderim kuyruğu işçisi başladı (tarama aralığı: {Interval} sn, OCR paralelliği: {Concurrency}).",
            PollInterval.TotalSeconds, _ocrConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            var processedAny = false;
            try
            {
                processedAny = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // normal kapanış
            }
            catch (Exception ex)
            {
                // Tur hatası döngüyü ÖLDÜRMEZ; bir sonraki turda tekrar denenir.
                _logger.LogError(ex, "Kuyruk tarama turu hata verdi; bir sonraki turda tekrar denenecek.");
            }

            // Faz 16 (A2): iş işlendiyse kuyrukta devamı olabilir → beklemeden yeni tur.
            // Kuyruk boşsa "yeni iş var" sinyali ya da 15 sn'lik aralık beklenir.
            if (processedAny) continue;

            try
            {
                await _signal.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Gönderim kuyruğu işçisi durdu.");
    }

    /// <summary>
    /// Tek tarama turu: zamanı gelen işleri bul; ReceiptOcr işlerini paralel (en fazla
    /// _ocrConcurrency), diğerlerini mevcut sıralı davranışla işle. Dönüş: bu turda en az
    /// bir iş işlendi mi (true → çağıran beklemeden yeni tur atar).
    /// </summary>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        List<(Guid JobId, Guid TenantId, string Type)> due;

        // Sistem scope'u: kimliksiz olduğundan tenant filtresi her şeyi gizler → IgnoreQueryFilters
        // ile TÜM tenant'ların zamanı gelen işleri taranır (yalnızca kimlik alanları okunur).
        using (var scanScope = _scopeFactory.CreateScope())
        {
            var db = scanScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            due = (await db.OutboundJobs
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(j => j.Status == OutboundJobStatus.Bekliyor && j.NextAttemptAt <= now)
                    .OrderBy(j => j.NextAttemptAt)
                    .Take(BatchSize)
                    .Select(j => new { j.Id, j.TenantId, j.Type })
                    .ToListAsync(ct))
                .Select(j => (j.Id, j.TenantId, j.Type))
                .ToList();
        }

        if (due.Count == 0) return false;

        // ---------- ReceiptOcr: paralel işleme (Faz 16 — A2) ----------
        // Her iş kendi scope + kendi DbContext'iyle çalışır (paylaşılan durum yok); dış API
        // (Gemini vb.) çağrıları eşzamanlı gittiği için toplu yüklemelerde bekleme N kat düşer.
        var ocrJobs = due.Where(j => j.Type == OutboundJobService.TypeReceiptOcr).ToList();
        if (ocrJobs.Count > 0)
        {
            using var throttle = new SemaphoreSlim(_ocrConcurrency);
            var tasks = ocrJobs.Select(async job =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    await ProcessSingleAsync(job.JobId, job.TenantId, ct);
                }
                finally
                {
                    throttle.Release();
                }
            }).ToList();
            await Task.WhenAll(tasks);
        }

        // ---------- Diğer iş tipleri: mevcut SIRALI davranış birebir korunur ----------
        foreach (var (jobId, tenantId, type) in due)
        {
            if (type == OutboundJobService.TypeReceiptOcr) continue; // yukarıda işlendi

            ct.ThrowIfCancellationRequested();
            await ProcessSingleAsync(jobId, tenantId, ct);
        }

        return true;
    }

    /// <summary>Tek işi, işin tenant'ı adına açılmış ayrı bir scope'ta işler (hata diğer işleri durdurmaz).</summary>
    private async Task ProcessSingleAsync(Guid jobId, Guid tenantId, CancellationToken ct)
    {
        using var jobScope = _scopeFactory.CreateScope();

        // ÖNCE tenant kimliği etkinleştirilir; AppDbContext ve servisler bundan SONRA çözülür
        // (AppDbContext, ICurrentUser'ı kurucusunda okur — sıra kritiktir).
        jobScope.ServiceProvider.GetRequiredService<WorkerCurrentUser>().ActAsTenant(tenantId);

        var jobService = jobScope.ServiceProvider.GetRequiredService<OutboundJobService>();
        try
        {
            await jobService.ProcessAsync(jobId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tek işin hatası diğer işleri durdurmaz (ProcessAsync kendi hatasını zaten yazar;
            // burası yalnızca ProcessAsync'in kendisi çökerse devreye girer).
            _logger.LogError(ex, "Kuyruk işi işlenirken hata. JobId: {JobId}", jobId);
        }
    }
}
