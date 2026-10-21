namespace MaliMusavir.Api.Domain;

/// <summary>
/// Mükellefe gelen e-Tebligat kaydı (blueprint M6 — UETS tek pano).
/// (TenantId, ExternalId) benzersizdir: aynı tebligat ikinci senkronda mükerrer yazılmaz.
/// IsRead/ReadAt platform içi takip durumudur (UETS'teki resmi okuma durumundan bağımsız);
/// LegalDeadline, "tebliğ edilmiş sayılma" (tebliği izleyen 5. iş günü sonu — bkz. flow spec
/// faz4-etebligat.md kaynak notu) + itiraz/ödeme süresinden hesaplanan son gündür.
/// </summary>
public class ETebligatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>UETS/sağlayıcı tarafındaki tebligat kimliği. (TenantId, ExternalId) benzersizdir.</summary>
    public string ExternalId { get; set; } = string.Empty;
    /// <summary>Gönderen kurum (ör. "Gelir İdaresi Başkanlığı — Kozyatağı Vergi Dairesi").</summary>
    public string Sender { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary>Tebligatın muhatabın UETS adresine ulaştığı tarih.</summary>
    public DateTime DeliveredAt { get; set; }
    /// <summary>İtiraz/ödeme için son gün. null = süre takibi gerektirmeyen bilgilendirme.</summary>
    public DateTime? LegalDeadline { get; set; }
    /// <summary>Süre türü: "İtiraz" / "Ödeme" / "Bilgi".</summary>
    public string? DeadlineType { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>İndirilen resmi belgenin dosya yolu (storage köküne göre göreli).</summary>
    public string? DocumentPath { get; set; }
    /// <summary>Resmi belgenin orijinal dosya adı (ör. "Tebligat_ODEMEEMRI.pdf").</summary>
    public string? DocumentFileName { get; set; }
    /// <summary>Belge içeriği veya üst yazı metni / JSON (varsa).</summary>
    public string? DocumentContent { get; set; }
}
