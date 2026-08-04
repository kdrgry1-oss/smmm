using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Payments;

/// <summary>
/// Ödeme başlatma sonucu. İki akış desteklenir:
/// - DirectSuccess = true → ödeme anında tahsil edildi (mock ve kart saklamasız basit akış).
/// - RedirectUrl dolu → kullanıcı sağlayıcının ödeme sayfasına yönlendirilir (iyzico checkout form).
/// Declined = true ise sağlayıcıya ulaşıldı ama ödeme REDDEDİLDİ (teknik hata değil);
/// FailureReason kullanıcıya gösterilebilir Türkçe gerekçedir.
/// </summary>
public class CheckoutResult
{
    public bool DirectSuccess { get; init; }
    public string? RedirectUrl { get; init; }
    /// <summary>Sağlayıcı tarafındaki işlem referansı (PaymentTransaction.ProviderRef'e yazılır).</summary>
    public string? ProviderRef { get; init; }
    public bool Declined { get; init; }
    public string? FailureReason { get; init; }

    public static CheckoutResult Paid(string providerRef) =>
        new() { DirectSuccess = true, ProviderRef = providerRef };

    public static CheckoutResult Redirect(string url, string providerRef) =>
        new() { RedirectUrl = url, ProviderRef = providerRef };

    public static CheckoutResult Decline(string failureReason, string? providerRef = null) =>
        new() { Declined = true, FailureReason = failureReason, ProviderRef = providerRef };
}

/// <summary>
/// Ödeme sağlayıcı adaptör arayüzü (blueprint M7 — iyzico/PayTR adapter arkasında).
/// Kart SAKLANMAZ; kart numarası yalnızca tek seferlik tahsilat için sağlayıcıya iletilir
/// ve platformda hiçbir yerde kalıcı tutulmaz. Gerçek sağlayıcı eklemek = bu arayüzü
/// uygulayan tek sınıf yazmak (Program.cs'te Payments:Provider ile seçilir).
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Sağlayıcının görünen adı (PaymentTransaction.Provider'a yazılır, ör. "MockPay", "Iyzico").</summary>
    string Name { get; }

    /// <summary>
    /// Plan bedeli (KDV dahil <paramref name="amount"/>) için ödeme başlatır.
    /// ProviderResult.Fail = teknik/yapılandırma hatası (retryable olabilir);
    /// CheckoutResult.Declined = sağlayıcı ödemeyi reddetti (kart hatası vb.).
    /// </summary>
    Task<ProviderResult<CheckoutResult>> CreateCheckoutAsync(
        Tenant tenant, Plan plan, decimal amount, string? cardNumber, CancellationToken ct);

    /// <summary>Başarılı bir işlemi iade eder (süper admin akışı; mock'ta her zaman başarılı).</summary>
    Task<ProviderResult<bool>> RefundAsync(PaymentTransaction transaction, CancellationToken ct);
}
