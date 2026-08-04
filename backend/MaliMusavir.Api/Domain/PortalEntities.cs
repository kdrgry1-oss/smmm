namespace MaliMusavir.Api.Domain;

/// <summary>Portal yüklemesi belge kategorisi (E1 — mükellef basit dille seçer).</summary>
public enum PortalUploadCategory { Fis = 0, Fatura = 1, BankaEkstresi = 2, Sozlesme = 3, Diger = 4 }

/// <summary>Belge talebi durumu (E1 — eksik belge takibi).</summary>
public enum DocumentRequestStatus { Bekliyor = 0, Yuklendi = 1, Iptal = 2 }

/// <summary>
/// Mükellef belge portalı erişimi (E1). SMMM üretir: link (Token) + 6 haneli PIN.
/// PIN asla düz metin saklanmaz (PinHash, BCrypt); üretim anında BİR KEZ gösterilir.
/// Mükellef üyeliksizdir — bu kayıt onun tek kimliğidir. Tenant izolasyonu Client
/// üzerinden sağlanır (ClientIntegration deseni); anonim login sorgusu IgnoreQueryFilters kullanır.
/// </summary>
public class ClientPortalAccess
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Mükellef başına TEK portal erişimi (unique index).</summary>
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>URL-güvenli erişim anahtarı (Guid "N" biçimi) — portal linkinin son parçası.</summary>
    public string Token { get; set; } = string.Empty;
    /// <summary>6 haneli PIN'in BCrypt hash'i.</summary>
    public string PinHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Mükellefin son başarılı PIN girişi (SMMM "portalı kullanıyor mu" görür).</summary>
    public DateTime? LastAccessAt { get; set; }
}

/// <summary>
/// Portaldan gelen belge (E1). Fis kategorisi ReceiptService'e köprülenir (OCR hattına
/// otomatik girer) — başarılıysa ProcessedReceiptId dolar ve dosya fişin StoredPath'ini
/// paylaşır; diğer kategoriler yalnızca "portal/{tenantId}/" altında saklanır.
/// </summary>
public class PortalUpload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Mükellefin yüklediği özgün dosya adı.</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>IFileStorage köküne göre GÖRELİ yol (fişte Receipt ile aynı dosya).</summary>
    public string StoredPath { get; set; } = string.Empty;
    public PortalUploadCategory Category { get; set; } = PortalUploadCategory.Diger;
    /// <summary>Mükellefin isteğe bağlı Türkçe notu (ör. "Haziran yakıt fişleri").</summary>
    public string? Note { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Fis kategorisi ReceiptService'e otomatik aktarıldıysa oluşan fişin Id'si.</summary>
    public Guid? ProcessedReceiptId { get; set; }
}

/// <summary>
/// Eksik belge talebi (E1). SMMM açar (ör. "Haziran banka ekstresi"); mükellef portalda
/// kırmızı rozetle görür, yükleme sırasında talebi seçerse Yuklendi olur.
/// </summary>
public class DocumentRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Talep başlığı — mükellefin anlayacağı basit dille.</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Son yükleme tarihi (opsiyonel; portalda "son gün" olarak gösterilir).</summary>
    public DateTime? DueDate { get; set; }
    public DocumentRequestStatus Status { get; set; } = DocumentRequestStatus.Bekliyor;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Talebi karşılayan portal yüklemesi (Status = Yuklendi iken dolu).</summary>
    public Guid? FulfilledUploadId { get; set; }
}
