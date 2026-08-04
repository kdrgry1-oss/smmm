namespace MaliMusavir.Api.Domain;

/// <summary>
/// Kuyruk işi durumu (E6): Bekliyor (sırada / yeniden denenecek) → Calisiyor →
/// Basarili / Basarisiz (deneme hakkı bitti) / Iptal (kullanıcı iptal etti).
/// </summary>
public enum OutboundJobStatus { Bekliyor = 0, Calisiyor = 1, Basarili = 2, Basarisiz = 3, Iptal = 4 }

/// <summary>
/// Dışa gönderim kuyruğu işi (E6 — dayanıklılık/retry). GİB/entegratör/UETS sistemleri
/// çöktüğünde gönderim burada bekler; OutboundJobWorker üstel geri çekilmeyle
/// (1, 5, 15, 60, 180 dk) otomatik yeniden dener — SMMM gece ekran başında beklemez.
///
/// Desteklenen iş tipleri (OutboundJobService.Dispatch):
///  - "BeyannameGonder": PayloadJson = {"declarationId":"..."} → DeclarationService.SendAsync
///  - "TebligatSync":    PayloadJson = {"clientId":"..."|null} → ETebligatSyncService.SyncAsync
///  - "FaturaSync":      PayloadJson = {"clientId":"...","from":"...","to":"..."} → EInvoiceSyncService
///  - "ReceiptOcr":      PayloadJson = {"receiptId":"..."} → ReceiptService.ProcessQueuedOcrAsync (Faz 14)
///  - "GibPortalSync":   PayloadJson = {"clientId":"...","from":"...","to":"..."} → GİB portal robotu
///                       ile hem gelen belgeler hem e-Tebligat (Faz 15). "Elle doğrulama gerekli"
///                       hatasında TEKRAR DENENMEZ; entegrasyon kaydına uyarı yazılır.
/// </summary>
public class OutboundJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>İş tipi: "BeyannameGonder" / "TebligatSync" / "FaturaSync" / "ReceiptOcr".</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>İşin parametreleri (JSON — tipe göre şema yukarıda).</summary>
    public string PayloadJson { get; set; } = "{}";
    public OutboundJobStatus Status { get; set; } = OutboundJobStatus.Bekliyor;
    /// <summary>Şu ana dek yapılan deneme sayısı.</summary>
    public int AttemptCount { get; set; }
    /// <summary>Azami deneme; aşılınca Basarisiz'e düşer.</summary>
    public int MaxAttempts { get; set; } = 5;
    /// <summary>Bir sonraki deneme zamanı (UTC). Worker yalnızca zamanı gelenleri alır.</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    /// <summary>Son denemenin Türkçe hata mesajı (başarıda temizlenir).</summary>
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Basarili/Basarisiz/Iptal'e geçiş zamanı.</summary>
    public DateTime? CompletedAt { get; set; }
}
