using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Senkron sonucu özeti (blueprint §6.1 akışının çıktısı).</summary>
public class SyncResult
{
    public bool Success { get; init; }
    public int Added { get; init; }
    public int SkippedDuplicates { get; init; }
    /// <summary>Başarısızlıkta kullanıcıya gösterilecek Türkçe mesaj.</summary>
    public string? ErrorMessage { get; init; }
    public bool Retryable { get; init; }
    /// <summary>
    /// Başarılı senkrona eklenen bilgi notu (sağlayıcıdan; ör. sayfa sınırına takılınca
    /// "İlk 5.000 fatura alındı; daha eski kayıtlar için tarih aralığını daraltın.").
    /// </summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Gelen fatura senkronizasyonu: mükellefin entegrasyonunu çözer, uygun sağlayıcıdan
/// faturaları çeker, (TenantId, ExternalUuid) mükerrer kontrolüyle upsert eder,
/// satırları yazar ve LastSyncAt'i günceller.
/// </summary>
public class EInvoiceSyncService
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly IEnumerable<IEInvoiceProvider> _providers;
    private readonly ILogger<EInvoiceSyncService> _logger;

    public EInvoiceSyncService(
        AppDbContext db,
        ISecretProtector secretProtector,
        IEnumerable<IEInvoiceProvider> providers,
        ILogger<EInvoiceSyncService> logger)
    {
        _db = db;
        _secretProtector = secretProtector;
        _providers = providers;
        _logger = logger;
    }

    public async Task<SyncResult> SyncIncomingAsync(Client client, DateTime from, DateTime to, CancellationToken ct)
    {
        var integration = await _db.ClientIntegrations
            .FirstOrDefaultAsync(ci => ci.ClientId == client.Id, ct);

        // Faz 18 — MÜŞAVİR TEK GİRİŞ FALLBACK'i: mükellefin kendi entegrasyonu YOKSA (veya
        // GİB Portalı seçilmiş ama kimlik kaydedilmemişse) ve tenant'ta müşavir GİB kimliği
        // VARSA, çekim müşavir oturumuyla (advisor:<tenantId> + targetTaxId) yapılır.
        IEInvoiceProvider? provider;
        string apiKey;
        bool useTestEnvironment;

        var needsAdvisorFallback =
            integration is null ||
            (integration.Provider == IntegrationProvider.GibPortal &&
             string.IsNullOrEmpty(integration.ApiKeyEncrypted));

        if (needsAdvisorFallback)
        {
            var hasAdvisor = await _db.TenantGibCredentials
                .AsNoTracking()
                .AnyAsync(c => c.TenantId == client.TenantId, ct);

            if (!hasAdvisor)
            {
                return new SyncResult
                {
                    Success = false,
                    ErrorMessage = "Önce mükellefe bir entegratör bağlayın veya \"GİB Bağlantısı\" " +
                                   "sayfasından müşavir GİB girişini tanımlayın."
                };
            }

            provider = _providers.FirstOrDefault(p => p.Provider == IntegrationProvider.GibPortal);
            if (provider is null)
            {
                _logger.LogError("GİB Portalı sağlayıcı adaptörü bulunamadı (müşavir modu).");
                return new SyncResult
                {
                    Success = false,
                    ErrorMessage = "GİB Portalı sağlayıcı desteği bulunamadı."
                };
            }

            apiKey = GibPortalProvider.PackAdvisorCredentials(client.TenantId, client.TaxId);
            useTestEnvironment = false;
        }
        else
        {
            provider = _providers.FirstOrDefault(p => p.Provider == integration!.Provider);
            if (provider is null)
            {
                _logger.LogError("Sağlayıcı adaptörü bulunamadı: {Provider}", integration!.Provider);
                return new SyncResult
                {
                    Success = false,
                    ErrorMessage = "Bu entegratör için sağlayıcı desteği bulunamadı."
                };
            }

            // API anahtarını çöz (Mock için anahtar gerekmez).
            apiKey = string.Empty;
            useTestEnvironment = integration!.UseTestEnvironment;
            if (!string.IsNullOrEmpty(integration.ApiKeyEncrypted))
            {
                try
                {
                    apiKey = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "API anahtarı çözülemedi. ClientId: {ClientId}", client.Id);
                    return new SyncResult
                    {
                        Success = false,
                        ErrorMessage = "Kayıtlı API anahtarı çözülemedi. Lütfen entegrasyonu yeniden kaydedin."
                    };
                }
            }
        }

        var result = await provider.GetIncomingInvoicesAsync(apiKey, useTestEnvironment, from, to, ct);
        if (!result.Success || result.Data is null)
        {
            return new SyncResult
            {
                Success = false,
                ErrorMessage = result.ErrorMessage ?? "Entegratörden fatura alınamadı. Lütfen tekrar deneyin.",
                Retryable = result.Retryable
            };
        }

        // Mükerrer kontrolü: (TenantId, ExternalUuid) — DB'de unique index ile de korunur.
        var incomingUuids = result.Data
            .Select(i => i.ExternalUuid)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct()
            .ToList();

        var existingUuids = await _db.EInvoices
            .Where(e => incomingUuids.Contains(e.ExternalUuid))
            .Select(e => e.ExternalUuid)
            .ToListAsync(ct);

        var existingSet = existingUuids.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var skipped = 0;

        foreach (var invoice in result.Data)
        {
            if (string.IsNullOrWhiteSpace(invoice.ExternalUuid))
                continue;

            if (!existingSet.Add(invoice.ExternalUuid))
            {
                skipped++;
                continue;
            }

            var entity = new EInvoice
            {
                TenantId = client.TenantId,
                ClientId = client.Id,
                Direction = InvoiceDirection.Incoming,
                ExternalUuid = invoice.ExternalUuid,
                InvoiceNumber = invoice.InvoiceNumber,
                IssueDate = DateTime.SpecifyKind(invoice.IssueDate, DateTimeKind.Utc),
                SenderTaxId = invoice.SenderTaxId,
                SenderTitle = invoice.SenderTitle,
                CurrencyCode = invoice.CurrencyCode,
                TaxExclusiveAmount = invoice.TaxExclusiveAmount,
                TaxTotal = invoice.TaxTotal,
                PayableAmount = invoice.PayableAmount,
                Status = EInvoiceStatus.New,
                RawXmlPath = null
            };

            foreach (var line in invoice.Lines.OrderBy(l => l.LineNumber))
            {
                entity.Lines.Add(new EInvoiceLine
                {
                    EInvoiceId = entity.Id,
                    LineNumber = line.LineNumber,
                    ItemName = line.ItemName,
                    Quantity = line.Quantity,
                    UnitCode = line.UnitCode,
                    UnitPrice = line.UnitPrice,
                    LineAmount = line.LineAmount,
                    VatRate = line.VatRate,
                    VatAmount = line.VatAmount
                });
            }

            _db.EInvoices.Add(entity);
            added++;
        }

        // Müşavir fallback'inde entegrasyon kaydı olmayabilir (LastSyncAt yazılacak yer yok).
        if (integration is not null) integration.LastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Gelen fatura senkronu tamamlandı. ClientId: {ClientId}, Yeni: {Added}, Mükerrer: {Skipped}",
            client.Id, added, skipped);

        return new SyncResult
        {
            Success = true,
            Added = added,
            SkippedDuplicates = skipped,
            Warning = result.Warning
        };
    }
}
