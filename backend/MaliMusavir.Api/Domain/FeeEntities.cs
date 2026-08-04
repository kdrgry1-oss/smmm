namespace MaliMusavir.Api.Domain;

/// <summary>Tahsilat durumu (E7): Bekliyor → Odendi; vadesi geçen Bekliyor → Gecikti.</summary>
public enum FeeInvoiceStatus { Bekliyor = 0, Odendi = 1, Gecikti = 2 }

/// <summary>
/// Mükellef aylık hizmet ücreti tanımı (E7 — SMMM'nin KENDİ tahsilatı).
/// Mükellef başına TEK tanım vardır; "dönemi oluştur" bu tanımdan aylık FeeInvoice üretir.
/// </summary>
public class ClientFee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Aylık hizmet ücreti (KDV dahil kabul edilir; fatura kesimi bu fazda yok).</summary>
    public decimal MonthlyFee { get; set; }
    public string Currency { get; set; } = "TRY";
    /// <summary>Ücretin geçerli olduğu ilk ay (bu aydan önceki dönemlere tahakkuk üretilmez).</summary>
    public DateTime StartDate { get; set; } = DateTime.UtcNow.Date;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Aylık ücret tahakkuku (E7). Aynı mükellef + dönem için TEK kayıt (unique index) —
/// "dönemi oluştur" mükerrer çağrılsa da ikinci tahakkuk üretmez.
/// DÜRÜST KAPSAM NOTU: Bu fazda mükellefe e-Arşiv hizmet faturası KESİLMEZ; kayıt yalnızca
/// SMMM'nin tahsilat takibi içindir (ödendi işaretleme elle yapılır).
/// </summary>
public class FeeInvoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Tahakkuk dönemi "yyyy-MM".</summary>
    public string Period { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public FeeInvoiceStatus Status { get; set; } = FeeInvoiceStatus.Bekliyor;
    /// <summary>Vade: dönem ayının son günü.</summary>
    public DateTime DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    /// <summary>Tahsil edilen tutar (kısmi/farklı tahsilat girilebilir; boşsa Amount kabul edilir).</summary>
    public decimal? PaidAmount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
