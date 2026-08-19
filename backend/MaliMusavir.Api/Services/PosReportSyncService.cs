using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Services;

public sealed class PosReportSyncService
{
    private readonly IEnumerable<IPosReportProvider> _providers;
    private readonly ISecretProtector _secrets;
    public PosReportSyncService(IEnumerable<IPosReportProvider> providers, ISecretProtector secrets)
        => (_providers, _secrets) = (providers, secrets);

    public async Task<PosProviderSyncResult> SyncAsync(PosConnection connection, DateTime from, DateTime to, CancellationToken ct)
    {
        if (!connection.ApiAccessEnabled || string.IsNullOrWhiteSpace(connection.ApiCredentialEncrypted))
            return new(false, true, 0, "Bu hesapta resmi API/raporlama erişimi tanımlı değil.");
        string credential;
        try { credential = _secrets.Unprotect(connection.ApiCredentialEncrypted); }
        catch { return new(false, true, 0, "Kayıtlı API erişim bilgisi çözülemedi; sağlayıcı bilgilerini yeniden kaydedin."); }
        var provider = _providers.FirstOrDefault(x => x.Provider == connection.Provider)
            ?? new PartnerActivationPosProvider(connection.Provider);
        var temporary = new PosConnection { MerchantId = connection.MerchantId, ApiCredentialEncrypted = credential };
        return await provider.SyncAsync(temporary, from, to, ct);
    }
}
