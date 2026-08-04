namespace MaliMusavir.Api.Domain;

/// <summary>Yükümlülük durumu (E2). JSON'da string taşınır ("Bekliyor"/"Tamamlandi").</summary>
public enum ObligationStatus { Bekliyor = 0, Tamamlandi = 1 }

/// <summary>Yükümlülük kaynağı: "Dönemi Oluştur" (Otomatik) veya SMMM'nin elle eklediği (Manuel).</summary>
public enum ObligationSource { Otomatik = 0, Manuel = 1 }

/// <summary>
/// Vergi takvimi yükümlülüğü (E2). Mükellef + dönem bazlı tek satır: tür (KDV Beyannamesi,
/// Muhtasar, SGK Bildirge...), son gün ve durum. Otomatik set TaxCalendarService'te üretilir;
/// aynı dönem KDV1 beyannamesi Approved olduğunda KDV yükümlülüğü otomatik Tamamlandi işaretlenir.
/// </summary>
public class TaxObligation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Yükümlülük türü, Türkçe serbest metin (ör. "KDV Beyannamesi", "Muhtasar").</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Yükümlülük dönemi "yyyy-MM" (ör. "2026-06").</summary>
    public string Period { get; set; } = string.Empty;
    /// <summary>Yasal son gün (UTC gün başı).</summary>
    public DateTime DueDate { get; set; }
    public ObligationStatus Status { get; set; } = ObligationStatus.Bekliyor;
    public DateTime? CompletedAt { get; set; }
    public ObligationSource Source { get; set; } = ObligationSource.Otomatik;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
