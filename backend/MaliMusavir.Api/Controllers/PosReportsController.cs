using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

public record PosConnectionSaveRequest(
    PosProvider Provider, string DisplayName, string? MerchantId, string? TerminalId,
    string? DeviceSerialNumber, string? ApiCredential, bool ApiAccessEnabled);

/// <summary>POS / YNÖKC bağlantıları. Gerçek çekim yalnız sağlayıcının resmi API yetkisiyle açılır.</summary>
[ApiController]
[Authorize]
public class PosReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ISecretProtector _secrets;
    private readonly PosReportSyncService _sync;
    public PosReportsController(AppDbContext db, ICurrentUser currentUser, ISecretProtector secrets, PosReportSyncService sync)
        => (_db, _currentUser, _secrets, _sync) = (db, currentUser, secrets, sync);

    [HttpGet("api/pos/providers")]
    public ActionResult<object> Providers() => Ok(PosProviderCatalog.All);

    [HttpGet("api/clients/{clientId:guid}/pos-connections")]
    public async Task<ActionResult<object>> List(Guid clientId, CancellationToken ct) => Ok(await _db.PosConnections
        .AsNoTracking().Where(x => x.ClientId == clientId && !x.IsArchived).OrderBy(x => x.Provider)
        .Select(x => new { x.Id, x.Provider, x.DisplayName, x.MerchantId, x.TerminalId, x.DeviceSerialNumber,
            x.ApiAccessEnabled, x.ConnectionStatus, x.LastSyncAt, x.LastError, x.CreatedAt })
        .ToListAsync(ct));

    [HttpPost("api/clients/{clientId:guid}/pos-connections")]
    public async Task<ActionResult<object>> Save(Guid clientId, [FromBody] PosConnectionSaveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName)) return BadRequest(new { message = "Bağlantı adı zorunludur." });
        var client = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct);
        if (client is null) return NotFound(new { message = "Mükellef bulunamadı." });
        var item = new PosConnection {
            TenantId = client.TenantId, ClientId = client.Id, Provider = request.Provider,
            DisplayName = request.DisplayName.Trim(), MerchantId = Trim(request.MerchantId), TerminalId = Trim(request.TerminalId),
            DeviceSerialNumber = Trim(request.DeviceSerialNumber), ApiAccessEnabled = request.ApiAccessEnabled,
            ApiCredentialEncrypted = string.IsNullOrWhiteSpace(request.ApiCredential) ? null : _secrets.Protect(request.ApiCredential.Trim()),
            ConnectionStatus = request.ApiAccessEnabled ? "DoğrulamaBekliyor" : "YetkiBekliyor"
        };
        _db.PosConnections.Add(item); await _db.SaveChangesAsync(ct);
        return Created($"/api/pos-connections/{item.Id}", new { item.Id, item.ConnectionStatus });
    }

    [HttpPost("api/pos-connections/{id:guid}/sync")]
    public async Task<ActionResult<object>> Sync(Guid id, CancellationToken ct)
    {
        var item = await _db.PosConnections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return NotFound(new { message = "POS bağlantısı bulunamadı." });
        var result = await _sync.SyncAsync(item, DateTime.UtcNow.Date.AddDays(-7), DateTime.UtcNow.Date, ct);
        item.ConnectionStatus = result.Success ? "Aktif" : result.RequiresActivation ? "YetkiBekliyor" : "Hata";
        item.LastError = result.Success ? null : result.Message;
        if (result.Success) item.LastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return result.Success ? Ok(new { result.RecordCount, result.Message }) : Conflict(new { message = result.Message });
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class PosProviderCatalog
{
    public static readonly object[] All =
    {
        new { code = "TokenBeko", name = "Token / Beko", kind = "Fiziki YNÖKC", devices = "300 TR, X30 TR", access = "Token teknik entegrasyon / TSM yetkisi", training = "Cihaz menüsünden Gün Sonu > Z Raporu; entegrasyon için üretici aktivasyonu gerekir." },
        new { code = "Ingenico", name = "Ingenico", kind = "Fiziki YNÖKC", devices = "iDE280, Move/AXIUM", access = "Ingenico terminal uygulaması ve partner yetkisi", training = "Raporlar > Gün Sonu > Z Raporu. Terminal API için Ingenico geliştirici/iş ortağı yetkisi gerekir." },
        new { code = "ProfiloVerifone", name = "Profilo / VeriFone", kind = "Fiziki YNÖKC", devices = "VX680-E1, S900 ECR", access = "Üretici/işletici kuruluş teknik erişimi", training = "Cihaz Raporlar menüsünden Z raporunu alın; API için Profilo/VeriFone satış sonrası entegrasyon başvurusu yapın." },
        new { code = "Pavo", name = "Pavo", kind = "Android POS / YNÖKC", devices = "N86, N6 Pro, CT20", access = "Pavo harici satış entegrasyonu", training = "Raporlar > Gün Sonu/Z Raporu. Pavo hesabından harici satış entegrasyonu yetkisi talep edin." },
        new { code = "Hugin", name = "Hugin", kind = "Fiziki YNÖKC", devices = "Hugin YNÖKC", access = "Yazılımevi/üretici entegrasyonu", training = "Gün Sonu raporunu cihazdan alın; bulut/entegrasyon için Hugin çözüm ortağıyla cihaz eşleştirin." },
        new { code = "Inpos", name = "InPOS", kind = "Fiziki YNÖKC", devices = "InPOS cihazları", access = "İşletici kuruluş entegrasyon yetkisi", training = "Raporlar > Z Raporu. Restoran/POS yazılımı bağlantısı için InPOS teknik aktivasyon gerekir." },
        new { code = "Paygo", name = "Paygo", kind = "Fiziki YNÖKC", devices = "Paygo cihazları", access = "Yetkili entegratör", training = "Cihazdan Z raporunu yazdırın veya dışa aktarın; API/raporlama talebini Paygo teknik ekibine iletin." },
        new { code = "Pax", name = "PAX", kind = "Android POS", devices = "PAX terminal ailesi", access = "Banka/ödeme kuruluşu ve terminal uygulaması", training = "Terminal raporlarından gün sonu alın. İşlem raporu için POS'u sağlayan banka/ödeme kuruluşu panelini bağlayın." },
        new { code = "BankaPos", name = "Banka Fiziki POS", kind = "Banka POS", devices = "Tüm banka terminalleri", access = "Üye işyeri paneli veya kurumsal raporlama API'si", training = "Banka üye işyeri panelinden Gün Sonu/POS hareket raporunu indirin; API gerekiyorsa bankadan kurumsal raporlama yetkisi alın." },
        new { code = "PayTR", name = "PayTR", kind = "Sanal POS", devices = "Web / link ile ödeme", access = "Mağaza no, key, salt", training = "Mağaza paneli > Entegrasyon Bilgileri. Ödeme rapor API anahtarlarını yetkili kullanıcı oluşturur." },
        new { code = "Iyzico", name = "iyzico", kind = "Sanal POS", devices = "Web / link ile ödeme", access = "API key ve secret key", training = "iyzico Merchant Panel > API Anahtarları bölümünden teknik anahtarları oluşturun." },
        new { code = "Param", name = "Param", kind = "Sanal POS", devices = "Web / link ile ödeme", access = "Param teknik API yetkisi", training = "Param üye işyeri hesabından rapor/API erişimi için teknik yetki talep edin." },
        new { code = "Sipay", name = "Sipay", kind = "Sanal POS", devices = "Web / link ile ödeme", access = "Sipay teknik API yetkisi", training = "Sipay işyeri hesabından API kimlik bilgilerini alın; raporlama erişimini etkinleştirin." },
        new { code = "Moka", name = "Moka", kind = "Sanal POS", devices = "Web / link ile ödeme", access = "Moka API erişimi", training = "Moka üye işyeri paneli/teknik ekibi üzerinden raporlama anahtarlarını tanımlayın." },
        new { code = "Paynet", name = "Paynet", kind = "Sanal POS", devices = "Web / link ile ödeme", access = "Paynet API erişimi", training = "Paynet portalından yetkili teknik kullanıcı/API erişimi tanımlayın." },
    };
}
