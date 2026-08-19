using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>POS sağlayıcılarından alınan günlük hareket/rapor sorgusunun normalize sonucu.</summary>
public record PosProviderSyncResult(bool Success, bool RequiresActivation, int RecordCount, string Message);

public interface IPosReportProvider
{
    PosProvider Provider { get; }
    Task<PosProviderSyncResult> SyncAsync(PosConnection connection, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// PayTR resmi ödeme rapor servisinin adaptörü. Bu sanal POS hareket raporudur;
/// mali Z raporu değildir. Z raporu yalnız YNÖKC/ÖKC kaynağından gelir.
/// ApiCredential formatı: merchant_key|merchant_salt.
/// </summary>
public sealed class PayTrReportProvider : IPosReportProvider
{
    public const string HttpClientName = "PayTRReports";
    private readonly IHttpClientFactory _http;
    public PayTrReportProvider(IHttpClientFactory http) => _http = http;
    public PosProvider Provider => PosProvider.PayTR;

    public async Task<PosProviderSyncResult> SyncAsync(PosConnection connection, DateTime from, DateTime to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.MerchantId) || string.IsNullOrWhiteSpace(connection.ApiCredentialEncrypted))
            return new(false, true, 0, "PayTR için mağaza no, merchant_key ve merchant_salt gereklidir.");

        // Şifre çözme servis katmanında yapılır; bu sınıfa yalnız geçici düz metin verilir.
        var credential = connection.ApiCredentialEncrypted.Split('|', 2, StringSplitOptions.TrimEntries);
        if (credential.Length != 2 || credential.Any(string.IsNullOrWhiteSpace))
            return new(false, true, 0, "PayTR erişim bilgisi 'merchant_key|merchant_salt' biçiminde olmalıdır.");

        var start = from.ToString("yyyy-MM-dd");
        var end = to.ToString("yyyy-MM-dd");
        var tokenText = connection.MerchantId + start + end + credential[1];
        var bytes = new HMACSHA256(Encoding.UTF8.GetBytes(credential[0])).ComputeHash(Encoding.UTF8.GetBytes(tokenText));
        var token = Convert.ToBase64String(bytes);
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["merchant_id"] = connection.MerchantId,
            ["start_date"] = start,
            ["end_date"] = end,
            ["paytr_token"] = token
        });

        try
        {
            var response = await _http.CreateClient(HttpClientName).PostAsync("rapor/odeme-dokumu", body, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new(false, false, 0, $"PayTR rapor servisi HTTP {(int)response.StatusCode} döndü.");
            using var json = JsonDocument.Parse(raw);
            var status = json.RootElement.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var error = json.RootElement.TryGetProperty("err_msg", out var errorElement) ? errorElement.GetString() : null;
                return new(false, false, 0, error ?? "PayTR bu aralık için rapor döndürmedi.");
            }
            var count = json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : 1;
            return new(true, false, count, $"PayTR ödeme özeti alındı ({count} kayıt). Bu kayıtlar mali Z raporu değildir.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, false, 0, "PayTR rapor isteği başarısız: " + ex.Message);
        }
    }
}

/// <summary>Herkese açık rapor API'si olmayan fiziksel POS/YNÖKC sağlayıcıları için dürüst adaptör.</summary>
public sealed class PartnerActivationPosProvider : IPosReportProvider
{
    public PartnerActivationPosProvider(PosProvider provider) => Provider = provider;
    public PosProvider Provider { get; }
    public Task<PosProviderSyncResult> SyncAsync(PosConnection connection, DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult(new PosProviderSyncResult(false, true, 0,
            "Bu sağlayıcıda otomatik Z raporu için üretici, banka veya işletici kuruluşun resmi TSM/raporlama API yetkisi gerekir. Yetki açılmadan cihaz verisi çekilmez."));
}
