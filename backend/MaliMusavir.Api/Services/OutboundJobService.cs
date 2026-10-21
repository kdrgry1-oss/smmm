using System.Text.Json;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Kuyruk işlem sonucu (Türkçe mesajlı).</summary>
public class OutboundJobActionResult
{
    public bool Success { get; init; }
    public bool NotFound { get; init; }
    public string? Message { get; init; }
    public OutboundJob? Job { get; init; }

    public static OutboundJobActionResult Ok(OutboundJob job, string message) =>
        new() { Success = true, Job = job, Message = message };

    public static OutboundJobActionResult Fail(string message, bool notFound = false) =>
        new() { Success = false, Message = message, NotFound = notFound };
}

/// <summary>
/// Faz 11 — E6: Dışa gönderim kuyruğu servisi. Kuyruğa alma, iptal, "şimdi dene" ve
/// worker'ın tek iş yürütme adımı (ProcessAsync) buradadır. Üstel geri çekilme:
/// 1, 5, 15, 60, 180 dk (5. denemeden sonra Basarisiz).
/// </summary>
public class OutboundJobService
{
    /// <summary>Desteklenen iş tipleri (dispatcher anahtarları).</summary>
    public const string TypeBeyannameGonder = "BeyannameGonder";
    public const string TypeTebligatSync = "TebligatSync";
    public const string TypeFaturaSync = "FaturaSync";
    /// <summary>Faz 14: arka plan fiş OCR okuma işi (payload: {"receiptId":"..."}).</summary>
    public const string TypeReceiptOcr = "ReceiptOcr";

    /// <summary>
    /// Faz 15: GİB Portal Robotu senkronu — TEK işte hem gelen belgeler hem e-Tebligat
    /// çekilir (payload: {"clientId":"...","from":"...","to":"..."}). Entegratör kullanmayan,
    /// entegrasyonu GibPortal olan mükellefler içindir.
    /// </summary>
    public const string TypeGibPortalSync = "GibPortalSync";

    /// <summary>Üstel geri çekilme aralıkları (deneme sırasına göre; sonuncusu tekrarlanır).</summary>
    public static readonly TimeSpan[] BackoffDelays =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(180),
    };

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly OutboundJobSignal _signal;
    private readonly ILogger<OutboundJobService> _logger;

    public OutboundJobService(
        AppDbContext db, IServiceProvider services, OutboundJobSignal signal, ILogger<OutboundJobService> logger)
    {
        _db = db;
        _services = services;
        _signal = signal;
        _logger = logger;
    }

    /// <summary>İş tipinin Türkçe etiketi (UI listesi).</summary>
    public static string TypeLabel(string type) => type switch
    {
        TypeBeyannameGonder => "Beyanname gönderimi",
        TypeTebligatSync => "e-Tebligat senkronu",
        TypeFaturaSync => "e-Fatura senkronu",
        TypeReceiptOcr => "Fiş OCR okuma",
        TypeGibPortalSync => "GİB portal çekimi (belge + tebligat)",
        _ => type
    };

    /// <summary>
    /// İşi kuyruğa alır. Aynı tip + aynı payload için zaten BEKLEYEN/ÇALIŞAN bir iş varsa
    /// yenisini açmaz, mevcut kaydı döner (mükerrer gönderim koruması).
    /// </summary>
    public async Task<OutboundJob> EnqueueAsync(
        Guid tenantId, string type, object payload, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(payload, PayloadJsonOptions);

        var existing = await _db.OutboundJobs.FirstOrDefaultAsync(j =>
            j.TenantId == tenantId && j.Type == type && j.PayloadJson == payloadJson &&
            (j.Status == OutboundJobStatus.Bekliyor || j.Status == OutboundJobStatus.Calisiyor), ct);
        if (existing is not null) return existing;

        var job = new OutboundJob
        {
            TenantId = tenantId,
            Type = type,
            PayloadJson = payloadJson,
            Status = OutboundJobStatus.Bekliyor,
            NextAttemptAt = DateTime.UtcNow
        };
        _db.OutboundJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        // Faz 16 (A2): worker'ı hemen uyandır — fiş yüklemesi 15 sn'lik taramayı beklemez.
        _signal.Notify();

        _logger.LogInformation("İş kuyruğa alındı. Id: {Id}, Tip: {Type}", job.Id, job.Type);
        return job;
    }

    /// <summary>Kuyruk listesi (yeniden eskiye) + bekleyen sayısı (sidebar rozeti).</summary>
    public async Task<(List<OutboundJob> Items, int PendingCount)> ListAsync(
        OutboundJobStatus? status, CancellationToken ct)
    {
        var query = _db.OutboundJobs.AsNoTracking().AsQueryable();
        if (status.HasValue)
            query = query.Where(j => j.Status == status.Value);

        var items = await query.OrderByDescending(j => j.CreatedAt).Take(200).ToListAsync(ct);
        var pending = await _db.OutboundJobs.CountAsync(
            j => j.Status == OutboundJobStatus.Bekliyor || j.Status == OutboundJobStatus.Calisiyor, ct);
        return (items, pending);
    }

    /// <summary>Bekleyen veya başarısız işi iptal eder (çalışan/tamamlanmış iş iptal edilemez).</summary>
    public async Task<OutboundJobActionResult> CancelAsync(Guid id, CancellationToken ct)
    {
        var job = await _db.OutboundJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return OutboundJobActionResult.Fail("Kuyruk işi bulunamadı.", notFound: true);

        if (job.Status is not (OutboundJobStatus.Bekliyor or OutboundJobStatus.Basarisiz))
            return OutboundJobActionResult.Fail(
                job.Status == OutboundJobStatus.Calisiyor
                    ? "İş şu anda çalışıyor; bitmesini bekleyin."
                    : "Yalnızca bekleyen veya başarısız işler iptal edilebilir.");

        job.Status = OutboundJobStatus.Iptal;
        job.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return OutboundJobActionResult.Ok(job, "İş iptal edildi.");
    }

    /// <summary>
    /// "Şimdi dene": bekleyen işi hemen sıraya alır; başarısız işi deneme hakkı sıfırlanarak
    /// yeniden kuyruğa koyar.
    /// </summary>
    public async Task<OutboundJobActionResult> RetryNowAsync(Guid id, CancellationToken ct)
    {
        var job = await _db.OutboundJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return OutboundJobActionResult.Fail("Kuyruk işi bulunamadı.", notFound: true);

        if (job.Status is not (OutboundJobStatus.Bekliyor or OutboundJobStatus.Basarisiz))
            return OutboundJobActionResult.Fail("Yalnızca bekleyen veya başarısız işler yeniden denenebilir.");

        if (job.Status == OutboundJobStatus.Basarisiz)
            job.AttemptCount = 0; // yeni bir deneme serisi başlar (eski hata LastError'da görünür kalır)

        job.Status = OutboundJobStatus.Bekliyor;
        job.NextAttemptAt = DateTime.UtcNow;
        job.CompletedAt = null;
        await _db.SaveChangesAsync(ct);

        // Faz 16 (A2): "şimdi dene" de worker'ı hemen uyandırır.
        _signal.Notify();

        return OutboundJobActionResult.Ok(job, "İş sıraya alındı; en geç birkaç saniye içinde denenecek.");
    }

    // ---------- Worker tarafı (tek iş yürütme adımı) ----------

    /// <summary>
    /// Worker'ın çağırdığı tek iş adımı. Scope'un WorkerCurrentUser'ı işin tenant'ına
    /// AYARLANMIŞ olmalıdır. İşi Calisiyor'a çeker, tipe göre yönlendirir, sonuçta
    /// Basarili / Bekliyor (geri çekilmeli yeniden deneme) / Basarisiz durumuna yazar.
    /// </summary>
    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _db.OutboundJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != OutboundJobStatus.Bekliyor || job.NextAttemptAt > DateTime.UtcNow)
            return; // arada iptal edildi / zamanı ertelendi

        job.Status = OutboundJobStatus.Calisiyor;
        job.AttemptCount++;
        await _db.SaveChangesAsync(ct);

        bool success;
        bool permanent; // kalıcı hata → yeniden deneme anlamsız (ör. kayıt bulunamadı)
        string? error;
        try
        {
            (success, permanent, error) = await DispatchAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor: iş Bekliyor'a geri döner, deneme hakkı yakılmaz.
            job.Status = OutboundJobStatus.Bekliyor;
            job.AttemptCount--;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kuyruk işi beklenmeyen hata verdi. Id: {Id}, Tip: {Type}", job.Id, job.Type);
            success = false;
            permanent = false;
            error = "Beklenmeyen bir hata oluştu: " + ex.Message;
        }

        if (success)
        {
            job.Status = OutboundJobStatus.Basarili;
            job.LastError = null;
            job.CompletedAt = DateTime.UtcNow;
        }
        else if (permanent || job.AttemptCount >= job.MaxAttempts)
        {
            job.Status = OutboundJobStatus.Basarisiz;
            job.LastError = error ?? "İşlem başarısız oldu.";
            job.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            // Üstel geri çekilme: 1, 5, 15, 60, 180 dk.
            var delay = BackoffDelays[Math.Min(job.AttemptCount - 1, BackoffDelays.Length - 1)];
            job.Status = OutboundJobStatus.Bekliyor;
            job.LastError = error ?? "İşlem başarısız oldu; otomatik yeniden denenecek.";
            job.NextAttemptAt = DateTime.UtcNow.Add(delay);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Kuyruk işi işlendi. Id: {Id}, Tip: {Type}, Deneme: {Attempt}, Durum: {Status}",
            job.Id, job.Type, job.AttemptCount, job.Status);
    }

    /// <summary>İş tipine göre ilgili servise yönlendirir. Dönüş: (başarı, kalıcıHata, hataMesajı).</summary>
    private async Task<(bool Success, bool Permanent, string? Error)> DispatchAsync(
        OutboundJob job, CancellationToken ct)
    {
        switch (job.Type)
        {
            case TypeBeyannameGonder:
            {
                var payload = Deserialize<BeyannamePayload>(job.PayloadJson);
                if (payload?.DeclarationId is null || payload.DeclarationId == Guid.Empty)
                    return (false, true, "İş verisi bozuk: beyanname kimliği yok.");

                // Kuyrukta beklerken beyanname elden gönderilmiş olabilir → iş amacına ulaşmıştır.
                var current = await _db.Declarations.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == payload.DeclarationId, ct);
                if (current is null)
                    return (false, true, "Beyanname bulunamadı (silinmiş olabilir).");
                if (current.Status is DeclarationStatus.Sent or DeclarationStatus.Approved)
                    return (true, false, null);

                var declarationService = _services.GetRequiredService<DeclarationService>();
                var result = await declarationService.SendAsync(payload.DeclarationId.Value, ct);
                if (result.Success) return (true, false, null);
                return (false, result.NotFound || !result.Retryable, result.ErrorMessage);
            }

            case TypeTebligatSync:
            {
                var payload = Deserialize<TebligatPayload>(job.PayloadJson);
                var tebligatService = _services.GetRequiredService<ETebligatSyncService>();
                var result = await tebligatService.SyncAsync(payload?.ClientId, payload?.From, payload?.To, ct);
                if (result.Success)
                {
                    if (payload?.ClientId is not null && payload.ClientId != Guid.Empty)
                    {
                        var integration = await _db.ClientIntegrations
                            .FirstOrDefaultAsync(ci => ci.ClientId == payload.ClientId, ct);
                        await SetGibPortalWarningAsync(integration, null, ct);
                    }
                    return (true, false, null);
                }
                return (false, !result.Retryable, result.ErrorMessage);
            }

            case TypeFaturaSync:
            {
                var payload = Deserialize<FaturaSyncPayload>(job.PayloadJson);
                if (payload?.ClientId is null || payload.ClientId == Guid.Empty)
                    return (false, true, "İş verisi bozuk: mükellef kimliği yok.");

                var client = await _db.Clients.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == payload.ClientId, ct);
                if (client is null)
                    return (false, true, "Mükellef bulunamadı (silinmiş olabilir).");

                var to = payload.To ?? DateTime.UtcNow;
                var from = payload.From ?? to.AddDays(-30);
                var syncService = _services.GetRequiredService<EInvoiceSyncService>();
                var result = await syncService.SyncIncomingAsync(client, from, to, ct);
                if (result.Success) return (true, false, null);
                return (false, !result.Retryable, result.ErrorMessage);
            }

            case TypeReceiptOcr:
            {
                // Faz 14: fiş OCR işi. ReceiptService scope'tan LAZY çözülür (dairesel bağımlılık
                // yok: ReceiptService kuyruğa yazmak için OutboundJobService'i enjekte eder).
                var payload = Deserialize<ReceiptOcrPayload>(job.PayloadJson);
                if (payload?.ReceiptId is null || payload.ReceiptId == Guid.Empty)
                    return (false, true, "İş verisi bozuk: fiş kimliği yok.");

                var receiptService = _services.GetRequiredService<ReceiptService>();
                // Son deneme bilgisi taşınır: son denemede hâlâ geçici hata varsa fiş
                // "İşleniyor"da bırakılmaz — koru-ve-uyar ile NeedsReview'a düşürülür.
                var result = await receiptService.ProcessQueuedOcrAsync(
                    payload.ReceiptId.Value, job.TenantId,
                    isLastAttempt: job.AttemptCount >= job.MaxAttempts, ct);
                return (result.Success, result.Permanent, result.Error);
            }

            case TypeGibPortalSync:
            {
                // Faz 15: GİB Portal Robotu — tek işte hem belgeler hem tebligat çekilir.
                var payload = Deserialize<GibPortalSyncPayload>(job.PayloadJson);
                if (payload?.ClientId is null || payload.ClientId == Guid.Empty)
                    return (false, true, "İş verisi bozuk: mükellef kimliği yok.");

                var client = await _db.Clients.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == payload.ClientId, ct);
                if (client is null)
                    return (false, true, "Mükellef bulunamadı (silinmiş olabilir).");

                var integration = await _db.ClientIntegrations
                    .FirstOrDefaultAsync(ci => ci.ClientId == client.Id, ct);
                if (integration is null || integration.Provider != IntegrationProvider.GibPortal)
                {
                    // Faz 18 — MÜŞAVİR TEK GİRİŞ: mükellefin kendi GibPortal entegrasyonu
                    // olmasa bile tenant'ta müşavir GİB kimliği varsa iş GEÇERLİDİR — çekim
                    // müşavir oturumuyla yapılır (sync servisleri fallback'i kendisi seçer).
                    var hasAdvisor = await _db.TenantGibCredentials
                        .AsNoTracking()
                        .AnyAsync(c => c.TenantId == client.TenantId, ct);
                    if (!hasAdvisor)
                    {
                        // Entegrasyon arada değişmiş olabilir → iş anlamını yitirdi, tekrar denenmez.
                        return (false, true,
                            "Bu mükellef artık GİB Portalı entegrasyonuna bağlı değil ve müşavir GİB kimliği tanımlı değil.");
                    }
                    integration = null; // uyarı yazma adımları atlanır (kayıt yok)
                }

                var to = payload.To ?? DateTime.UtcNow;
                var from = payload.From ?? to.AddDays(-7);

                var errors = new List<string>();
                var retryable = false;
                var manual = false;

                // e-Tebligat önceliklidir. Belge ekranında takılan bir portal çağrısı,
                // kanuni süreli tebligatların kayda alınmasını geciktirmemelidir.
                var tebligatService = _services.GetRequiredService<ETebligatSyncService>();
                var tebligatResult = await tebligatService.SyncAsync(client.Id, from, to, ct);
                if (!tebligatResult.Success)
                {
                    errors.Add("Tebligat: " + tebligatResult.ErrorMessage);
                    retryable |= tebligatResult.Retryable;
                    manual |= GibPortalProvider.IsManualVerificationMessage(tebligatResult.ErrorMessage);
                }

                // Gelen belgeler e-Tebligat sonucunu değiştirmez; aynı kuyruk işinde geride
                // kalır. Yeni günlük tetikleyici yalnızca TypeTebligatSync kullanır.
                var invoiceService = _services.GetRequiredService<EInvoiceSyncService>();
                var invoiceResult = await invoiceService.SyncIncomingAsync(client, from, to, ct);
                if (!invoiceResult.Success)
                {
                    errors.Add("Belgeler: " + invoiceResult.ErrorMessage);
                    retryable |= invoiceResult.Retryable;
                    manual |= GibPortalProvider.IsManualVerificationMessage(invoiceResult.ErrorMessage);
                }

                if (errors.Count == 0)
                {
                    // Başarılı çekim → varsa eski "elle doğrulama" uyarısı temizlenir.
                    await SetGibPortalWarningAsync(integration, null, ct);
                    return (true, false, null);
                }

                var message = string.Join(" | ", errors);

                if (manual)
                {
                    // GÜVENLİK KURALI: elle doğrulama gerekiyorsa TEKRAR DENENMEZ; uyarı
                    // entegrasyon kaydına yazılır ve mükellef satırında rozet olarak görünür.
                    await SetGibPortalWarningAsync(integration, message, ct);
                    return (false, true, message);
                }

                return (false, !retryable, message);
            }

            default:
                return (false, true, $"Bilinmeyen iş tipi: {job.Type}");
        }
    }

    /// <summary>
    /// GİB Portal entegrasyonunun "Elle doğrulama gerekli" uyarısını yazar/temizler.
    /// ŞEMA DEĞİŞİKLİĞİ YOKTUR: uyarı, kimlik JSON'unun içinde (şifreli kolonda) taşınır.
    /// Şifre bu işlem sırasında da hiçbir yere loglanmaz.
    /// </summary>
    private async Task SetGibPortalWarningAsync(
        ClientIntegration? integration, string? warning, CancellationToken ct)
    {
        // Faz 18: müşavir modunda mükellefe ait entegrasyon kaydı olmayabilir — uyarı
        // yazılacak yer yoktur (oturum durumu "GİB Bağlantısı" sayfasındaki rozette görünür).
        if (integration is null || string.IsNullOrEmpty(integration.ApiKeyEncrypted)) return;

        var protector = _services.GetRequiredService<ISecretProtector>();

        string plain;
        try
        {
            plain = protector.Unprotect(integration.ApiKeyEncrypted);
        }
        catch
        {
            return; // çözülemeyen sıra dokunulmaz; kullanıcı kimliği yeniden kaydedebilir
        }

        if (!GibPortalProvider.TryParseCredentials(
                plain, out var username, out var password, out var existingWarning, out _))
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(warning) ? null : warning.Trim();
        if (string.Equals(existingWarning, normalized, StringComparison.Ordinal)) return;

        integration.ApiKeyEncrypted = protector.Protect(
            GibPortalProvider.PackCredentials(
                username, password, normalized, normalized is null ? null : DateTime.UtcNow));
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            normalized is null
                ? "GİB portal uyarısı temizlendi. ClientId: {ClientId}"
                : "GİB portal entegrasyonuna elle doğrulama uyarısı yazıldı. ClientId: {ClientId}",
            integration.ClientId);
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, PayloadJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"BeyannameGonder" iş verisi.</summary>
    public sealed class BeyannamePayload
    {
        public Guid? DeclarationId { get; set; }
    }

    /// <summary>"TebligatSync" iş verisi (ClientId null = tüm aktif mükellefler).</summary>
    public sealed class TebligatPayload
    {
        public Guid? ClientId { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
    }

    /// <summary>"FaturaSync" iş verisi.</summary>
    public sealed class FaturaSyncPayload
    {
        public Guid? ClientId { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
    }

    /// <summary>"ReceiptOcr" iş verisi (Faz 14 — arka plan fiş OCR).</summary>
    public sealed class ReceiptOcrPayload
    {
        public Guid? ReceiptId { get; set; }
    }

    /// <summary>"GibPortalSync" iş verisi (Faz 15 — GİB portal robotu; belge + tebligat).</summary>
    public sealed class GibPortalSyncPayload
    {
        public Guid? ClientId { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
    }
}
