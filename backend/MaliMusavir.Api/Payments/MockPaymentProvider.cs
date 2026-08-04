using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Payments;

/// <summary>
/// Demo/test ödeme sağlayıcısı: gerçek sağlayıcı gecikmesini taklit etmek için ~2 sn bekler,
/// ardından ödemeyi başarılı sayar. Test senaryoları (uçtan uca hata yolu testi için):
/// - "4111..." ile başlayan kart → BAŞARILI (klasik test kartı).
/// - "4000..." ile başlayan kart → REDDEDİLİR ("Ödeme alınamadı" akışı test edilir).
/// - Kart numarası boş → başarılı (hızlı demo).
/// Kart numarası hiçbir yerde saklanmaz/loglanmaz.
/// </summary>
public class MockPaymentProvider : IPaymentProvider
{
    /// <summary>Gerçekçi demo için sağlayıcı gecikmesi.</summary>
    private static readonly TimeSpan SimulatedDelay = TimeSpan.FromSeconds(2);

    public string Name => "MockPay";

    public async Task<ProviderResult<CheckoutResult>> CreateCheckoutAsync(
        Tenant tenant, Plan plan, decimal amount, string? cardNumber, CancellationToken ct)
    {
        await Task.Delay(SimulatedDelay, ct);

        var providerRef = $"MOCKPAY-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        var digits = new string((cardNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("4000", StringComparison.Ordinal))
        {
            // Başarısızlık test kartı: kullanıcıya Türkçe, eyleme dönük gerekçe döner.
            return ProviderResult<CheckoutResult>.Ok(CheckoutResult.Decline(
                "Kart bankanız tarafından reddedildi (yetersiz bakiye — test kartı 4000...). " +
                "Farklı bir kartla tekrar deneyin.",
                providerRef));
        }

        // "4111..." ve diğer tüm girişler (boş dahil) başarılı sayılır.
        return ProviderResult<CheckoutResult>.Ok(CheckoutResult.Paid(providerRef));
    }

    public async Task<ProviderResult<bool>> RefundAsync(PaymentTransaction transaction, CancellationToken ct)
    {
        await Task.Delay(SimulatedDelay, ct);
        return ProviderResult<bool>.Ok(true);
    }
}
