using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Beyanname;

/// <summary>
/// Demo/test GİB e-Beyanname sağlayıcısı (blueprint M8). Davranış:
/// - Gönderim: "GIB-{unixSaniye}-{rastgele}" formatında paket no üretir (gönderim anı paket
///   no'nun içine gömülür — durum sorgusunda geçen süre buradan hesaplanır).
/// - Durum sorgusu: gönderimden sonraki ilk 30 saniyede "işleniyor" (Pending) döner;
///   30 saniye geçince paket no'dan DETERMİNİSTİK olarak %10 ihtimalle şema hatası (Error),
///   aksi halde Onaylandı (Approved) + tahakkuk tutarı döner.
/// - Aynı paket no her sorguda AYNI sonucu verir; yeniden gönderim YENİ paket no ürettiğinden
///   hata alan beyanname tekrar gönderilince genellikle onaylanır → retry akışı test edilir.
/// - Tahakkuk: beyannamenin SummaryJson'ındaki "odenecekKdv" alanı (devreden çıktıysa 0).
/// </summary>
public class MockGibProvider : IEBeyannameProvider
{
    /// <summary>GİB işleme süresi simülasyonu: paket bu süre dolmadan sorgulanırsa "işleniyor" döner.</summary>
    public static readonly TimeSpan ProcessingDuration = TimeSpan.FromSeconds(30);

    public string Name => "MockGib";

    public Task<ProviderResult<GibSendResult>> SendDeclarationAsync(Declaration declaration, CancellationToken ct)
    {
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var random = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var packageRef = $"GIB-{unixSeconds}-{random}";

        return Task.FromResult(ProviderResult<GibSendResult>.Ok(new GibSendResult { PackageRef = packageRef }));
    }

    public Task<ProviderResult<GibStatusResult>> GetStatusAsync(Declaration declaration, CancellationToken ct)
    {
        var packageRef = declaration.PackageRef;
        if (string.IsNullOrWhiteSpace(packageRef))
            return Task.FromResult(ProviderResult<GibStatusResult>.Fail("Paket numarası bulunamadı. Beyannameyi önce gönderin."));

        // Paket no'ya gömülü gönderim zamanı: GIB-{unixSaniye}-{rastgele}
        var parts = packageRef.Split('-');
        if (parts.Length < 3 || !long.TryParse(parts[1], out var sentUnix))
            return Task.FromResult(ProviderResult<GibStatusResult>.Fail("Paket numarası çözümlenemedi. Beyannameyi yeniden gönderin."));

        var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(sentUnix);
        if (elapsed < ProcessingDuration)
        {
            return Task.FromResult(ProviderResult<GibStatusResult>.Ok(new GibStatusResult
            {
                State = GibDeclarationState.Pending
            }));
        }

        // Deterministik %10 hata: aynı paket her sorguda aynı sonucu verir (test edilebilirlik).
        var seed = DeterministicSeed(packageRef);
        if (seed % 10 == 0)
        {
            var schemaErrors = new[]
            {
                "Şema doğrulama hatası: 'IndirilecekKDV' alanı toplamı kırılım satırlarıyla uyuşmuyor (BDP kural K-014).",
                "Şema doğrulama hatası: 'Matrah' alanı negatif olamaz (BDP kural K-003).",
                "Şema doğrulama hatası: Dönem bilgisi mükellefin beyanname yükümlülük dönemiyle eşleşmiyor (BDP kural K-021)."
            };
            return Task.FromResult(ProviderResult<GibStatusResult>.Ok(new GibStatusResult
            {
                State = GibDeclarationState.Error,
                ErrorMessage = schemaErrors[seed % schemaErrors.Length]
            }));
        }

        return Task.FromResult(ProviderResult<GibStatusResult>.Ok(new GibStatusResult
        {
            State = GibDeclarationState.Approved,
            TahakkukAmount = ReadPayableFromSummary(declaration.SummaryJson)
        }));
    }

    /// <summary>SummaryJson'daki ödenecek KDV'yi okur (yoksa/çözülemezse 0 — devreden senaryosu).</summary>
    private static decimal ReadPayableFromSummary(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return 0m;
        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            if (doc.RootElement.TryGetProperty("odenecekKdv", out var payable) &&
                payable.TryGetDecimal(out var value))
            {
                return Math.Round(value, 2);
            }
        }
        catch (JsonException)
        {
            // özet çözülemezse tahakkuk 0 kabul edilir (mock davranışı)
        }
        return 0m;
    }

    /// <summary>Verilen metinden her çalıştırmada aynı pozitif tohumu üretir (MD5 tabanlı — MockUets ile aynı desen).</summary>
    private static int DeterministicSeed(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var value = BitConverter.ToInt32(hash, 0);
        return value == int.MinValue ? 0 : Math.Abs(value);
    }
}
