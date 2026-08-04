namespace MaliMusavir.Api.Domain;

/// <summary>Ödeme işleminin yaşam döngüsü durumu.</summary>
public enum PaymentStatus { Pending = 0, Succeeded = 1, Failed = 2, Refunded = 3 }

/// <summary>
/// Tenant aboneliği (blueprint M7). Abonelik durumunun TEK GERÇEK KAYNAĞI bu tablodur;
/// Tenant.SubscriptionStatus geriye uyumluluk için her değişiklikte SENKRON tutulur
/// (SubscriptionService dışında elle güncellenmemelidir). Tenant başına tek kayıt vardır
/// (unique TenantId); plan/dönem değişiklikleri kayıt üzerinde güncellenir, tarihçe
/// PaymentTransaction ve BillingInvoice tablolarından izlenir.
/// </summary>
public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public Guid PlanId { get; set; }
    public Plan? Plan { get; set; }
    /// <summary>Entities.cs'teki SubscriptionStatus enum'u yeniden kullanılır (Trial/Active/PastDue/Canceled).</summary>
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;
    /// <summary>İçinde bulunulan fatura döneminin başlangıcı.</summary>
    public DateTime PeriodStart { get; set; } = DateTime.UtcNow;
    /// <summary>Dönem sonu; geçmişse SubscriptionService.CheckAndUpdateStatusAsync durumu günceller.</summary>
    public DateTime PeriodEnd { get; set; }
    /// <summary>14 günlük deneme süresinin bitişi (kayıtta otomatik başlatılır).</summary>
    public DateTime? TrialEndsAt { get; set; }
    /// <summary>İptal talep tarihi. Dolu ise abonelik dönem sonuna kadar aktif kalır, sonra Canceled olur.</summary>
    public DateTime? CanceledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Ödeme işlemi kaydı (başarılı/başarısız tüm denemeler — denetim izi).</summary>
public class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    /// <summary>Tahsil edilen toplam tutar (KDV dahil).</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    /// <summary>Ödeme sağlayıcısının adı (ör. "MockPay", "Iyzico").</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>Sağlayıcı tarafındaki işlem referansı (mock'ta üretilir).</summary>
    public string? ProviderRef { get; set; }
    /// <summary>Başarısızlıkta kullanıcıya gösterilebilir Türkçe gerekçe.</summary>
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Abonelik faturası (platformun SMMM'ye kestiği fatura). PDF üretimi sonraki fazda —
/// bu fazda yalnızca kayıt ve numara üretimi vardır (PdfPath null kalır).
/// </summary>
public class BillingInvoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Yıl bazlı, tenant-bağımsız GLOBAL sıra: "2026-000001" (InvoiceNumberSequence'ten üretilir).</summary>
    public string Number { get; set; } = string.Empty;
    /// <summary>KDV hariç tutar (plan aylık fiyatı).</summary>
    public decimal Amount { get; set; }
    /// <summary>%20 KDV tutarı.</summary>
    public decimal VatAmount { get; set; }
    /// <summary>KDV dahil toplam.</summary>
    public decimal TotalAmount { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    /// <summary>e-Arşiv PDF yolu (sonraki faz; şimdilik null).</summary>
    public string? PdfPath { get; set; }
}

/// <summary>
/// Fatura numarası sayacı: yıl başına tek satır, tenant-bağımsız (global sıra).
/// Eşzamanlılık notu: sayaç EF ile oku-artır-yaz deseniyle güncellenir; BillingInvoice.Number
/// üzerindeki unique index çakışmada son savunma hattıdır (SaveChanges fırlatır, istek tekrarlanır).
/// Yüksek hacimde PostgreSQL SEQUENCE / SELECT ... FOR UPDATE'e geçirilmelidir.
/// </summary>
public class InvoiceNumberSequence
{
    /// <summary>Yıl (PK), ör. 2026.</summary>
    public int Year { get; set; }
    /// <summary>Bu yıl verilen son sıra numarası.</summary>
    public int LastNumber { get; set; }
}
