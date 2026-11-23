using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// GİB oturum kaybı olaylarına anında ve periyodik müdahale eden otomatik arka plan işçisi.
/// Kullanıcı sayfada işlem yapmasa dahi oturumun düştüğünü fark eder, robot OCR ile otomatik
/// giriş yapar ve veritabanındaki "Elle doğrulama gerekli" uyarısını derhal temizler.
/// </summary>
public sealed class GibSessionRenewalWorker(IServiceScopeFactory scopes, ILogger<GibSessionRenewalWorker> logger) : BackgroundService
{
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(256);
    private static readonly ConcurrentDictionary<string, DateTime> Pending = new();
    private static readonly ConcurrentDictionary<string, int> FailureCounts = new();
    private static DateTime _globalBackoffUntil = DateTime.MinValue;

    public static void TripGlobalBackoff(TimeSpan duration)
    {
        _globalBackoffUntil = DateTime.UtcNow.Add(duration);
    }

    public static bool IsGlobalBackoffActive => DateTime.UtcNow < _globalBackoffUntil;

    private static TimeSpan GetBackoffDuration(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromMinutes(15),
        2 => TimeSpan.FromMinutes(60),
        _ => TimeSpan.FromMinutes(240)
    };

    private static bool CheckRateLimitOrSpendCap(string? message, string? diagnostic)
    {
        var text = (message + " " + diagnostic).ToLowerInvariant();
        return text.Contains("spending cap") ||
               text.Contains("resource_exhausted") ||
               text.Contains("429") ||
               text.Contains("istek limiti") ||
               text.Contains("devre kesici");
    }

    public static bool Enqueue(string key)
    {
        if (IsGlobalBackoffActive) return false;
        var now = DateTime.UtcNow;
        if (Pending.TryGetValue(key, out var until) && until > now) return true;
        if (!Pending.TryAdd(key, DateTime.MaxValue))
        {
            if (!Pending.TryUpdate(key, DateTime.MaxValue, until)) return true;
        }
        if (Queue.Writer.TryWrite(key)) return true;
        Pending.TryRemove(key, out _);
        return false;
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("GİB otomatik oturum yenileme ve proaktif takip işçisi başladı.");

        var queueTask = ProcessQueueAsync(ct);
        var periodicTask = PeriodicSweepAsync(ct);

        await Task.WhenAll(queueTask, periodicTask);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await Parallel.ForEachAsync(Queue.Reader.ReadAllAsync(ct), new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (key, token) =>
        {
            if (IsGlobalBackoffActive)
            {
                logger.LogInformation("GİB kuyruk işleme: Küresel bekleme devrede, atlanıyor. Oturum:{Key}", key[..Math.Min(8, key.Length)]);
                return;
            }

            try
            {
                var success = await RenewAsync(key, token);
                if (success)
                {
                    Pending.TryRemove(key, out _);
                    FailureCounts.TryRemove(key, out _);
                }
                else
                {
                    var fails = FailureCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
                    var backoff = GetBackoffDuration(fails);
                    Pending[key] = DateTime.UtcNow.Add(backoff);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning("GİB oturum yenileme başarısız: {Kind}, oturum:{Key}", ex.GetType().Name, key[..Math.Min(8, key.Length)]);
                var fails = FailureCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
                var backoff = GetBackoffDuration(fails);
                Pending[key] = DateTime.UtcNow.Add(backoff);
            }
        });
    }

    /// <summary>
    /// Periyodik tarama: Her 20 dakikada bir veritabanında uyarısı bulunan veya oturumu
    /// düşmüş mükellefleri kontrol eder; diskte geçerli oturum varsa AI çağırmadan temizler,
    /// yoksa artan aralıklarla (exponential backoff) otomatik giriş dener.
    /// </summary>
    private async Task PeriodicSweepAsync(CancellationToken ct)
    {
        // Başlangıçta servislerin oturması için kısa bekleme
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            if (IsGlobalBackoffActive)
            {
                logger.LogInformation("GİB periyodik tarama: Küresel bekleme devrede ({Until:HH:mm:ss} UTC). Bekleniyor...", _globalBackoffUntil);
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await SweepWarningsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("GİB periyodik oturum tarama döngüsünde hata: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(20), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepWarningsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var robot = scope.ServiceProvider.GetRequiredService<GibRobotClient>();

        // 1. Mükellef entegrasyonlarını tara
        var integrations = await db.ClientIntegrations
            .IgnoreQueryFilters()
            .Where(i => i.Provider == IntegrationProvider.GibPortal && !string.IsNullOrEmpty(i.ApiKeyEncrypted))
            .Join(db.Clients.IgnoreQueryFilters().Where(c => !c.IsArchived), i => i.ClientId, c => c.Id, (i, c) => i)
            .ToListAsync(ct);

        foreach (var integration in integrations)
        {
            if (IsGlobalBackoffActive) break;

            string plain;
            try { plain = protector.Unprotect(integration.ApiKeyEncrypted!); }
            catch { continue; }

            if (!GibPortalProvider.TryParseCredentials(plain, out var user, out var pass, out var manualWarning, out _))
                continue;

            // Eğer mükellefte elle doğrulama uyarısı varsa
            if (!string.IsNullOrWhiteSpace(manualWarning))
            {
                var key = Hash(user);
                var now = DateTime.UtcNow;

                // 1. Önce diskteki oturum durumunu sıfır maliyetle kontrol et
                var status = await robot.GetSessionStatusAsync(user, ct);
                if (status.Ok && status.Exists && !status.Expired)
                {
                    logger.LogInformation("GİB periyodik tarama: Mükellef oturumu diskte zaten geçerli. Elle doğrulama uyarısı kaldırılıyor. ClientId:{ClientId}", integration.ClientId);
                    integration.ApiKeyEncrypted = protector.Protect(GibPortalProvider.PackCredentials(user, pass, null, null));
                    await db.SaveChangesAsync(ct);
                    Pending.TryRemove(key, out _);
                    FailureCounts.TryRemove(key, out _);
                    continue;
                }

                if (Pending.TryGetValue(key, out var until) && until > now) continue;

                var fails = FailureCounts.GetValueOrDefault(key, 0);
                if (fails >= 3)
                {
                    // Art arda 3 kez başarısız olmuşsa API'yi dövmeyi bırak, uzun bekle
                    Pending[key] = now.AddHours(4);
                    continue;
                }

                logger.LogInformation("GİB periyodik tarama: Düşmüş oturum tespit edildi, otomatik OCR girişi deneniyor. ClientId:{ClientId}, Deneme:{Fails}", integration.ClientId, fails + 1);
                var result = await robot.AutoLoginAsync(user, pass, ct);
                if (result.Ok)
                {
                    integration.ApiKeyEncrypted = protector.Protect(GibPortalProvider.PackCredentials(user, pass, null, null));
                    await db.SaveChangesAsync(ct);
                    Pending.TryRemove(key, out _);
                    FailureCounts.TryRemove(key, out _);
                    logger.LogInformation("GİB periyodik tarama: Oturum otomatik açıldı ve elle doğrulama uyarısı kaldırıldı. ClientId:{ClientId}", integration.ClientId);
                }
                else
                {
                    fails++;
                    FailureCounts[key] = fails;
                    var backoff = GetBackoffDuration(fails);
                    Pending[key] = now.Add(backoff);
                    logger.LogWarning("GİB periyodik tarama: Otomatik giriş başarısız ({Message}). {Backoff} sonra tekrar denenecek. ClientId:{ClientId}", result.Message, backoff, integration.ClientId);

                    if (CheckRateLimitOrSpendCap(result.Message, result.Diagnostic))
                    {
                        TripGlobalBackoff(TimeSpan.FromHours(1));
                        break;
                    }
                }
            }
        }

        // 2. Müşavir oturumunu tara
        var advisors = await db.TenantGibCredentials.IgnoreQueryFilters().ToListAsync(ct);
        foreach (var advisor in advisors)
        {
            if (IsGlobalBackoffActive) break;

            var sessionKey = GibRobotClient.AdvisorSessionKey(advisor.TenantId);
            var key = Hash(sessionKey);
            var now = DateTime.UtcNow;

            var status = await robot.GetAdvisorSessionStatusAsync(sessionKey, ct);
            if (status.Ok && status.Exists && !status.Expired)
            {
                Pending.TryRemove(key, out _);
                FailureCounts.TryRemove(key, out _);
                continue;
            }

            if (Pending.TryGetValue(key, out var until) && until > now) continue;

            var fails = FailureCounts.GetValueOrDefault(key, 0);
            if (fails >= 3)
            {
                Pending[key] = now.AddHours(4);
                continue;
            }

            string pass;
            try { pass = protector.Unprotect(advisor.PasswordEncrypted); }
            catch { continue; }

            logger.LogInformation("GİB periyodik tarama: Müşavir oturumu kapalı veya süresi dolmuş, otomatik giriş deneniyor. TenantId:{TenantId}, Deneme:{Fails}", advisor.TenantId, fails + 1);
            var result = await robot.AutoAdvisorLoginAsync(advisor.Username, pass, sessionKey, ct);
            if (result.Ok)
            {
                Pending.TryRemove(key, out _);
                FailureCounts.TryRemove(key, out _);
                logger.LogInformation("GİB periyodik tarama: Müşavir oturumu başarıyla açıldı. TenantId:{TenantId}", advisor.TenantId);
            }
            else
            {
                fails++;
                FailureCounts[key] = fails;
                var backoff = GetBackoffDuration(fails);
                Pending[key] = now.Add(backoff);
                logger.LogWarning("GİB periyodik tarama: Müşavir otomatik giriş başarısız ({Message}). {Backoff} sonra tekrar denenecek. TenantId:{TenantId}", result.Message, backoff, advisor.TenantId);

                if (CheckRateLimitOrSpendCap(result.Message, result.Diagnostic))
                {
                    TripGlobalBackoff(TimeSpan.FromHours(1));
                    break;
                }
            }
        }
    }

    private async Task<bool> RenewAsync(string key, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var robot = scope.ServiceProvider.GetRequiredService<GibRobotClient>();

        var integrations = await db.ClientIntegrations
            .IgnoreQueryFilters()
            .Where(i => i.Provider == IntegrationProvider.GibPortal && !string.IsNullOrEmpty(i.ApiKeyEncrypted))
            .Join(db.Clients.IgnoreQueryFilters().Where(c => !c.IsArchived), i => i.ClientId, c => c.Id, (i, c) => i)
            .ToListAsync(ct);

        foreach (var integration in integrations)
        {
            string plain;
            try { plain = protector.Unprotect(integration.ApiKeyEncrypted!); }
            catch { continue; }

            if (!GibPortalProvider.TryParseCredentials(plain, out var user, out var pass, out var warning, out _) || Hash(user) != key)
                continue;

            // Önce diskteki oturumu kontrol et
            var status = await robot.GetSessionStatusAsync(user, ct);
            if (status.Ok && status.Exists && !status.Expired)
            {
                logger.LogInformation("GİB olay tabanlı yenileme: Oturum diskte zaten geçerli. Elle doğrulama uyarısı kaldırılıyor. ClientId:{ClientId}", integration.ClientId);
                integration.ApiKeyEncrypted = protector.Protect(GibPortalProvider.PackCredentials(user, pass, null, null));
                await db.SaveChangesAsync(ct);
                FailureCounts.TryRemove(key, out _);
                return true;
            }

            logger.LogInformation("GİB oturum kaybı tespit edildi; hemen giriş deneniyor. Oturum:{Key}", key[..Math.Min(8, key.Length)]);
            var result = await robot.AutoLoginAsync(user, pass, ct);
            logger.LogInformation("GİB olay tabanlı giriş tamamlandı. Oturum:{Key}, Başarılı:{Ok}", key[..Math.Min(8, key.Length)], result.Ok);

            if (result.Ok)
            {
                integration.ApiKeyEncrypted = protector.Protect(GibPortalProvider.PackCredentials(user, pass, null, null));
                await db.SaveChangesAsync(ct);
                FailureCounts.TryRemove(key, out _);
                logger.LogInformation("GİB elle doğrulama uyarısı veritabanından kaldırıldı. ClientId:{ClientId}", integration.ClientId);
                return true;
            }

            if (CheckRateLimitOrSpendCap(result.Message, null))
            {
                TripGlobalBackoff(TimeSpan.FromHours(1));
            }

            return false;
        }

        var advisors = await db.TenantGibCredentials.IgnoreQueryFilters().ToListAsync(ct);
        foreach (var advisor in advisors)
        {
            var sessionKey = GibRobotClient.AdvisorSessionKey(advisor.TenantId);
            if (Hash(sessionKey) != key) continue;

            var status = await robot.GetAdvisorSessionStatusAsync(sessionKey, ct);
            if (status.Ok && status.Exists && !status.Expired)
            {
                FailureCounts.TryRemove(key, out _);
                return true;
            }

            string pass;
            try { pass = protector.Unprotect(advisor.PasswordEncrypted); }
            catch { continue; }

            var result = await robot.AutoAdvisorLoginAsync(advisor.Username, pass, sessionKey, ct);
            logger.LogInformation("GİB müşavir olay tabanlı giriş tamamlandı. Oturum:{Key}, Başarılı:{Ok}", key[..Math.Min(8, key.Length)], result.Ok);

            if (result.Ok)
            {
                FailureCounts.TryRemove(key, out _);
                return true;
            }

            if (CheckRateLimitOrSpendCap(result.Message, null))
            {
                TripGlobalBackoff(TimeSpan.FromHours(1));
            }

            return false;
        }

        logger.LogInformation("GİB yenileme: etkin kayıtlı kimlik bulunamadı. Oturum:{Key}", key[..Math.Min(8, key.Length)]);
        return false;
    }
}
