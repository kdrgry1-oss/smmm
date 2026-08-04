namespace MaliMusavir.Api.Domain;

/// <summary>Beyanname türü (blueprint M8/B9). Bu fazda yalnızca KDV1 hesaplanır; diğerleri iskelet.</summary>
public enum DeclarationType { KDV1 = 0, KDV2 = 1, Muhtasar = 2, Damga = 3 }

/// <summary>
/// Beyanname yaşam döngüsü: Draft (taslak hesaplandı) → Ready (kontrol edildi, gönderime hazır)
/// → Sent (GİB'e gönderildi, paket no alındı) → Approved (tahakkuk alındı) / Error (GİB hatası —
/// yeniden gönderilebilir).
/// </summary>
public enum DeclarationStatus { Draft = 0, Ready = 1, Sent = 2, Approved = 3, Error = 4 }

/// <summary>
/// Beyanname kaydı (blueprint M8 — GİB e-Beyanname web servisi). Aynı mükellef + tür + dönem
/// için tek kayıt vardır (unique index); "hazırla" mükerrer çağrısı mevcut taslağı yeniden hesaplar.
/// </summary>
public class Declaration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    public DeclarationType Type { get; set; } = DeclarationType.KDV1;
    /// <summary>Beyan dönemi "yyyy-MM" (ör. "2026-06").</summary>
    public string Period { get; set; } = string.Empty;
    public DeclarationStatus Status { get; set; } = DeclarationStatus.Draft;
    /// <summary>GİB paket referans numarası (gönderimde sağlayıcıdan alınır).</summary>
    public string? PackageRef { get; set; }
    /// <summary>GİB tahakkuk tutarı (onay sonrası; devreden çıktığında 0 olabilir).</summary>
    public decimal? TahakkukAmount { get; set; }
    /// <summary>GİB'den dönen Türkçe hata açıklaması (Status = Error iken dolu).</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>KDV taslak hesap özeti (Kdv1Summary JSON — bkz. DeclarationService).</summary>
    public string? SummaryJson { get; set; }
    /// <summary>Taslağın son hesaplanma zamanı.</summary>
    public DateTime PreparedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
