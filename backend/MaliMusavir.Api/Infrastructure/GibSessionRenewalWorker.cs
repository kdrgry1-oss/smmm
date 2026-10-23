using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Infrastructure;

// Separate from invoice jobs: a lost session must not wait for the hourly queue.
public sealed class GibSessionRenewalWorker(IServiceScopeFactory scopes, ILogger<GibSessionRenewalWorker> logger) : BackgroundService
{
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(256);
    private static readonly ConcurrentDictionary<string, DateTime> Pending = new();
    public static bool Enqueue(string key)
    {
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
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("GİB oturum kaybı olaylarına anında yenileme işçisi başladı.");
        await Parallel.ForEachAsync(Queue.Reader.ReadAllAsync(ct), new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (key, token) =>
        {
            try { await RenewAsync(key, token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning("GİB oturum yenileme başarısız: {Kind}, oturum:{Key}", ex.GetType().Name, key[..8]); }
            finally { Pending[key] = DateTime.UtcNow.AddMinutes(10); }
        });
    }
    private async Task RenewAsync(string key, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var robot = scope.ServiceProvider.GetRequiredService<GibRobotClient>();
        var own = await db.ClientIntegrations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Provider == IntegrationProvider.GibPortal)
            .Join(db.Clients.IgnoreQueryFilters().Where(c => !c.IsArchived), i => i.ClientId, c => c.Id, (i,c) => i.ApiKeyEncrypted).ToListAsync(ct);
        foreach (var encrypted in own)
        {
            string plain;
            try { plain = protector.Unprotect(encrypted); } catch { continue; }
            if (!GibPortalProvider.TryParseCredentials(plain, out var user, out var pass) || Hash(user) != key) continue;
            logger.LogInformation("GİB oturum kaybı tespit edildi; hemen giriş deneniyor. Oturum:{Key}", key[..8]);
            var result = await robot.AutoLoginAsync(user, pass, ct);
            logger.LogInformation("GİB olay tabanlı giriş tamamlandı. Oturum:{Key}, Başarılı:{Ok}", key[..8], result.Ok);
            return;
        }
        var advisors = await db.TenantGibCredentials.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        foreach (var advisor in advisors)
        {
            var sessionKey = GibRobotClient.AdvisorSessionKey(advisor.TenantId);
            if (Hash(sessionKey) != key) continue;
            var result = await robot.AutoAdvisorLoginAsync(advisor.Username, protector.Unprotect(advisor.PasswordEncrypted), sessionKey, ct);
            logger.LogInformation("GİB müşavir olay tabanlı giriş tamamlandı. Oturum:{Key}, Başarılı:{Ok}", key[..8], result.Ok);
            return;
        }
        logger.LogInformation("GİB yenileme: etkin kayıtlı kimlik bulunamadı. Oturum:{Key}", key[..8]);
    }
}
