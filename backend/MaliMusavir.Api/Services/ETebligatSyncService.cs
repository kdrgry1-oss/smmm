using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using MaliMusavir.Api.Tebligat;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>e-Tebligat senkron sonucu özeti (blueprint §6.4 akışının çıktısı).</summary>
public class ETebligatSyncResult
{
    public bool Success { get; init; }
    public int Added { get; init; }
    public int SkippedDuplicates { get; init; }
    /// <summary>Taranan mükellef sayısı.</summary>
    public int ClientCount { get; init; }
    /// <summary>Başarısızlıkta kullanıcıya gösterilecek Türkçe mesaj.</summary>
    public string? ErrorMessage { get; init; }
    public bool Retryable { get; init; }
}

/// <summary>
/// e-Tebligat senkronizasyonu (blueprint §6.4): seçili mükellef veya tenant'ın tüm aktif
/// mükellefleri için sağlayıcıdan tebligatları çeker ve (TenantId, ExternalId) mükerrer
/// kontrolüyle upsert eder. Platform içi okundu durumu (IsRead/ReadAt) mevcut kayıtlarda
/// EZİLMEZ — yalnızca yeni kayıtlar eklenir.
///
/// Faz 15: sağlayıcı MÜKELLEF BAZINDA seçilir — entegrasyonu GibPortal olan mükelleflerde
/// GibPortalTebligatProvider (gib-robot üzerinden GİB Dijital Vergi Dairesi), diğerlerinde
/// mevcut varsayılan sağlayıcı (MockUets / ileride UETS) kullanılır.
/// </summary>
public class ETebligatSyncService
{
    private readonly AppDbContext _db;
    private readonly IEnumerable<IETebligatProvider> _providers;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<ETebligatSyncService> _logger;

    public ETebligatSyncService(
        AppDbContext db,
        IEnumerable<IETebligatProvider> providers,
        ISecretProtector secretProtector,
        ILogger<ETebligatSyncService> logger)
    {
        _db = db;
        _providers = providers;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    /// <summary>clientId verilirse tek mükellef, verilmezse tüm aktif (arşivlenmemiş) mükellefler taranır.</summary>
    public Task<ETebligatSyncResult> SyncAsync(Guid? clientId, CancellationToken ct) =>
        SyncAsync(clientId, null, null, ct);

    /// <summary>
    /// Tarih aralıklı senkron (Faz 15 — "Tebligatları Çek" düğmesi). Aralık yalnızca
    /// GibPortal sağlayıcısında anlamlıdır; diğer sağlayıcılar tüm kutuyu döndürür.
    /// </summary>
    public async Task<ETebligatSyncResult> SyncAsync(
        Guid? clientId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        List<Client> clients;
        if (clientId.HasValue && clientId.Value != Guid.Empty)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId.Value, ct);
            if (client is null)
                return new ETebligatSyncResult { Success = false, ErrorMessage = "Mükellef bulunamadı." };
            clients = new List<Client> { client };
        }
        else
        {
            clients = await _db.Clients.AsNoTracking().Where(c => !c.IsArchived).ToListAsync(ct);
            if (clients.Count == 0)
                return new ETebligatSyncResult { Success = false, ErrorMessage = "Senkronize edilecek mükellef yok. Önce mükellef ekleyin." };
        }

        var added = 0;
        var skipped = 0;

        // Sağlayıcı seçimi için mükelleflerin entegrasyonları tek sorguda okunur.
        var clientIds = clients.Select(c => c.Id).ToList();
        var providerByClient = await _db.ClientIntegrations
            .AsNoTracking()
            .Where(ci => clientIds.Contains(ci.ClientId))
            .ToDictionaryAsync(ci => ci.ClientId, ci => ci.Provider, ct);

        // Faz 18 — MÜŞAVİR TEK GİRİŞ: tenant'ta müşavir GİB kimliği varsa, ClientIntegration'ı
        // olmayan (veya tebligat kaynağı vermeyen) mükellefler için de GİB portal sağlayıcısı
        // kullanılır (advisor:<tenantId> oturumu + mükellef VKN'siyle geçiş). Mock ASLA
        // devreye girmez — mevcut kural korunur.
        var tenantIds = clients.Select(c => c.TenantId).Distinct().ToList();
        var advisorTenantIds = (await _db.TenantGibCredentials
                .AsNoTracking()
                .Where(c => tenantIds.Contains(c.TenantId))
                .Select(c => c.TenantId)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var client in clients)
        {
            // GÜVENLİK/DOĞRULUK KURALI (kullanıcı bildirimi 31.07.2026): GİB Portalı bağlı
            // OLMAYAN mükellefte ASLA demo (Mock) tebligat üretilmez — aksi halde ekranda
            // mükellefe ait olmayan sahte kayıtlar görünür. Mock yalnız ETEBLIGAT_DEMO=true
            // ortamında (geliştirme) devreye girer.
            var hasIntegration = providerByClient.TryGetValue(client.Id, out var integrationProvider);
            var provider = hasIntegration ? ResolveProvider(integrationProvider, from, to) : null;

            // Müşavir fallback'i: kaynak yoksa ve tenant müşavir kimliği varsa GİB portal
            // sağlayıcısı kullanılır (sağlayıcı, mükellefin kendi kimliği olmadığını görüp
            // müşavir oturumuna kendisi düşer).
            if (provider is null && advisorTenantIds.Contains(client.TenantId))
            {
                provider = ResolveProvider(IntegrationProvider.GibPortal, from, to);
                // Uyarı yazma mantığı GİB portal mesajlarını tanısın diye sağlayıcı adı düzeltilir.
                if (provider is not null) integrationProvider = IntegrationProvider.GibPortal;
            }

            if (provider is null)
            {
                _logger.LogInformation(
                    "e-Tebligat kaynağı tanımlı olmayan mükellef atlandı. ClientId: {ClientId}", client.Id);
                if (clients.Count == 1)
                {
                    return new ETebligatSyncResult
                    {
                        Success = false,
                        ErrorMessage = "Bu mükellef için e-Tebligat kaynağı tanımlı değil. " +
                                       "Mükellef kartından \"GİB Portalı (entegratör yok)\" entegrasyonunu ekleyin " +
                                       "veya \"GİB Bağlantısı\" sayfasından müşavir girişini tanımlayın."
                    };
                }
                continue;
            }

            var result = await provider.GetMessagesAsync(client, ct);
            if (!result.Success || result.Data is null)
            {
                // Tek mükellef hatası tüm taramayı durdurmaz; kullanıcıya toplam sonuçla dönülür.
                _logger.LogWarning(
                    "Tebligat sağlayıcı hatası. ClientId: {ClientId}, Hata: {Error}",
                    client.Id, result.ErrorMessage);

                // Faz 17: GİB Portalı mükellefinde "Elle doğrulama gerekli / GİB oturumu
                // yenilenmeli" hatası entegrasyon kaydına uyarı olarak yazılır (mükellef
                // satırında sarı rozet; kuyruk tekrar denemez). Kullanıcı doğrulama kodunu
                // girince uyarı temizlenir (IntegrationsController.gib-login/complete).
                if (integrationProvider == IntegrationProvider.GibPortal &&
                    GibPortalProvider.IsManualVerificationMessage(result.ErrorMessage))
                {
                    await WriteGibPortalWarningAsync(client.Id, result.ErrorMessage!, ct);
                }

                if (clients.Count == 1)
                {
                    return new ETebligatSyncResult
                    {
                        Success = false,
                        ErrorMessage = result.ErrorMessage ?? "Tebligatlar alınamadı. Lütfen tekrar deneyin.",
                        Retryable = result.Retryable
                    };
                }
                continue;
            }

            if (integrationProvider == IntegrationProvider.GibPortal)
            {
                await WriteGibPortalWarningAsync(client.Id, null, ct);
            }

            var incomingIds = result.Data
                .Select(m => m.ExternalId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            if (incomingIds.Count == 0) continue;

            // Mükerrer kontrolü: (TenantId, ExternalId) — DB'de unique index ile de korunur.
            var existingIds = (await _db.ETebligatMessages
                    .Where(m => incomingIds.Contains(m.ExternalId))
                    .Select(m => m.ExternalId)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var message in result.Data)
            {
                if (string.IsNullOrWhiteSpace(message.ExternalId)) continue;

                if (!existingIds.Add(message.ExternalId))
                {
                    skipped++;
                    continue;
                }

                _db.ETebligatMessages.Add(new ETebligatMessage
                {
                    TenantId = client.TenantId,
                    ClientId = client.Id,
                    ExternalId = message.ExternalId,
                    Sender = message.Sender,
                    Subject = message.Subject,
                    DeliveredAt = DateTime.SpecifyKind(message.DeliveredAt, DateTimeKind.Utc),
                    LegalDeadline = message.LegalDeadline is null
                        ? null
                        : DateTime.SpecifyKind(message.LegalDeadline.Value, DateTimeKind.Utc),
                    DeadlineType = message.DeadlineType,
                    IsRead = message.IsRead,
                    ReadAt = message.IsRead ? DateTime.UtcNow : null
                });
                added++;
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "e-Tebligat senkronu tamamlandı. Mükellef: {Clients}, Yeni: {Added}, Mükerrer: {Skipped}",
            clients.Count, added, skipped);

        return new ETebligatSyncResult
        {
            Success = true,
            Added = added,
            SkippedDuplicates = skipped,
            ClientCount = clients.Count
        };
    }

    /// <summary>
    /// GİB Portal entegrasyonuna "Elle doğrulama gerekli / GİB oturumu yenilenmeli" uyarısını
    /// yazar veya temizler (OutboundJobService.SetGibPortalWarningAsync ile aynı kalıp — ŞEMA DEĞİŞİKLİĞİ
    /// YOKTUR: uyarı, kimlik JSON'unun içinde şifreli kolonda taşınır). Şifre LOGLANMAZ.
    /// </summary>
    private async Task WriteGibPortalWarningAsync(Guid clientId, string? warning, CancellationToken ct)
    {
        var integration = await _db.ClientIntegrations
            .FirstOrDefaultAsync(ci => ci.ClientId == clientId, ct);

        if (integration is null ||
            integration.Provider != IntegrationProvider.GibPortal ||
            string.IsNullOrEmpty(integration.ApiKeyEncrypted))
        {
            return;
        }

        string plain;
        try
        {
            plain = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
        }
        catch
        {
            return; // çözülemeyen sırra dokunulmaz; kullanıcı kimliği yeniden kaydedebilir
        }

        if (!GibPortalProvider.TryParseCredentials(
                plain, out var username, out var password, out var existingWarning, out _))
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(warning) ? null : warning.Trim();
        if (string.Equals(existingWarning, normalized, StringComparison.Ordinal)) return;

        integration.ApiKeyEncrypted = _secretProtector.Protect(
            GibPortalProvider.PackCredentials(
                username, password, normalized, normalized is null ? null : DateTime.UtcNow));
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            normalized is null
                ? "GİB portal uyarısı temizlendi. ClientId: {ClientId}"
                : "GİB portal entegrasyonuna oturum yenileme uyarısı yazıldı. ClientId: {ClientId}", clientId);

        if (normalized is not null)
        {
            GibSessionRenewalWorker.Enqueue(GibSessionRenewalWorker.Hash(username));
        }
    }

    /// <summary>
    /// Mükellefin entegrasyonuna göre tebligat sağlayıcısını seçer.
    ///
    /// KURAL (kullanıcı kararı 04.08.2026): e-Tebligat verisi HANGİ entegratörle çalışılırsa
    /// çalışılsın GİB Dijital Vergi Dairesi'nde durur — entegratörlerin (Nilvera, Uyumsoft,
    /// eLogo, EDM, Doğan, Paraşüt) yayımlanmış bir e-Tebligat/UETS API'si YOKTUR ve PTT/UETS
    /// entegrasyonu kapsam dışında bırakılmıştır. Bu yüzden:
    ///
    ///   GibPortal        → GİB portal robotu (mevcut davranış, aynen korunur)
    ///   e-Arşiv Portalı  → GİB portal robotu (e-Arşiv portalında TEBLİGAT VERİSİ YOKTUR)
    ///   Entegratörler    → GİB portal robotu (entegratörlü mükellefin tebligatı da düşsün)
    ///   Mock             → yalnız ETEBLIGAT_DEMO=true ile (geliştirme)
    ///
    /// Entegratörlü mükellefte robotun çalışabilmesi için ya mükellefin kendi GİB kimliği
    /// kayıtlı olmalı ya da tenant'ta müşavir girişi (advisor:&lt;tenantId&gt;) tanımlı olmalıdır;
    /// ikisi de yoksa sağlayıcı çağrısı "kimlik tanımlı değil" hatası döner ve mükellef
    /// uyarıyla işaretlenir — sessizce atlanmaz.
    /// </summary>
    private IETebligatProvider? ResolveProvider(
        IntegrationProvider integrationProvider, DateTime? from, DateTime? to)
    {
        // Demo modu yalnızca Mock entegrasyonda ve açık istekle devreye girer.
        if (integrationProvider == IntegrationProvider.Mock)
        {
            var demoEnabled = string.Equals(
                Environment.GetEnvironmentVariable("ETEBLIGAT_DEMO")?.Trim(), "true",
                StringComparison.OrdinalIgnoreCase);
            if (!demoEnabled) return null;

            return _providers.FirstOrDefault(p => p is not GibPortalTebligatProvider);
        }

        // Diğer tüm sağlayıcılarda (GibPortal, e-Arşiv ve tüm entegratörler) kaynak GİB'dir.
        var portal = _providers.OfType<GibPortalTebligatProvider>().FirstOrDefault();
        if (portal is not null)
        {
            portal.RangeFrom = from;
            portal.RangeTo = to;
        }
        return portal;
    }
}
