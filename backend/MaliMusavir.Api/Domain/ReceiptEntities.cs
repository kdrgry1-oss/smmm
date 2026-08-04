namespace MaliMusavir.Api.Domain;

/// <summary>
/// Fişin yaşam döngüsü durumu (blueprint M3).
/// Uploaded: eski senkron akışın ara durumu (geriye uyumluluk için korunur).
/// Processing: Faz 14 — fiş kuyruğa alındı, OCR arka planda çalışıyor (OutboundJob "ReceiptOcr");
/// dosya bu sırada temp/ altındadır, iş bitince kalıcı konuma taşınır.
/// NeedsReview: OCR tamamlandı, SMMM onayı bekleniyor — fişte TAM otomatik onay yoktur,
/// insan onayı her zaman şarttır (güven yüksekse yalnızca "otomatik onaya hazır" rozeti gösterilir).
/// Approved: SMMM onayladı (öğrenme yazıldı, Luca export'a dahil). Rejected: gerekçeyle reddedildi.
/// </summary>
public enum ReceiptStatus { Uploaded = 0, Processing = 1, NeedsReview = 2, Approved = 3, Rejected = 4 }

/// <summary>
/// Faz 13: fişten okunan plakanın mükellefin kayıtlı araçlarıyla eşleşme durumu.
/// Yok: fişte plaka okunmadı (plakasız fişte davranış birebir eski akıştır).
/// Eslesti: plaka kayıtlı bir araçla eşleşti (binekse KKEG %30 otomatik uygulanır).
/// Eslesmedi: plaka okundu ama kayıtlı araç yok → SMMM uyarısı (KKEG kararı eski davranışta kalır).
/// </summary>
public enum PlateMatchStatus { Yok = 0, Eslesti = 1, Eslesmedi = 2 }

/// <summary>
/// Faz 16: Fiş kaydının belge türü. Fis (0): gider fişi / masraf belgesi (varsayılan — eski
/// kayıtların tamamı). ZRaporu (1): yazar kasa günlük kapanış (Z) raporu — GELİR belgesi;
/// Luca export'ta gelir fişi kalıbıyla yazılır (100/108 borç, 600/391 alacak).
/// Sayısal değerler Ocr.OcrDocumentKind ile birebir aynıdır.
/// </summary>
public enum ReceiptDocumentKind { Fis = 0, ZRaporu = 1 }

/// <summary>
/// Kağıt fiş / masraf belgesi (blueprint M3 — fiş OCR → gider).
/// Görsel dosya webroot dışında saklanır (StoredPath, IFileStorage üzerinden);
/// OCR alanları nullable'dır — okunamayan alanları SMMM doğrulama ekranında elle doldurur.
/// </summary>
public class Receipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Kullanıcının yüklediği özgün dosya adı (ör. "fis-akaryakit.jpg").</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>IFileStorage köküne göre GÖRELİ yol (ör. "receipts/{tenantId}/{guid}.jpg").</summary>
    public string StoredPath { get; set; } = string.Empty;
    public Guid? UploadedByUserId { get; set; }
    public ReceiptStatus Status { get; set; } = ReceiptStatus.Uploaded;

    // ---------- OCR alan çıkarımı ----------
    public string? VendorName { get; set; }
    /// <summary>Satıcı VKN/TCKN (fişte okunamayabilir → null).</summary>
    public string? VendorTaxId { get; set; }
    /// <summary>
    /// Fiş/belge numarası (Z no, fiş sıra no, fatura no). OCR doldurur, SMMM onay formunda
    /// düzeltebilir. Luca Fiş Aktarım Şablonu'nda Evrak No kolonuna gider; boşsa export
    /// "FIS-{Id ilk 8 hane}" üretir.
    /// </summary>
    public string? DocumentNumber { get; set; }
    public DateTime? ReceiptDate { get; set; }
    /// <summary>KDV dahil toplam tutar.</summary>
    public decimal? TotalAmount { get; set; }
    /// <summary>KDV oranı, yüzde (ör. 1, 10, 20).</summary>
    public decimal? VatRate { get; set; }
    public decimal? VatAmount { get; set; }
    /// <summary>Matrah (KDV hariç tutar). Kırılım varsa kırılım netlerinin toplamı.</summary>
    public decimal? NetAmount { get; set; }

    // ---------- Faz 16: Çok oranlı KDV + iskonto + Z raporu ----------
    /// <summary>Belge türü: 0 = gider fişi (varsayılan), 1 = Z raporu (gelir). Int kolon.</summary>
    public ReceiptDocumentKind DocumentKind { get; set; } = ReceiptDocumentKind.Fis;
    /// <summary>Fişteki toplam iskonto/indirim (pozitif); yoksa null. Net tutarlar iskonto SONRASIDIR.</summary>
    public decimal? DiscountTotal { get; set; }
    /// <summary>
    /// Oran bazlı KDV kırılımı (ilişkisel tablo — export ve doğrulama buradan okur).
    /// Tek oranlı fişte tek satırdır; OCR kırılım okuyamadıysa boş kalabilir.
    /// </summary>
    public List<ReceiptVatBreakdown> VatBreakdowns { get; set; } = new();
    /// <summary>Z raporu: nakit tahsilat toplamı (KDV dahil); fişlerde null.</summary>
    public decimal? CashTotal { get; set; }
    /// <summary>Z raporu: kart tahsilat toplamı (KDV dahil); fişlerde null.</summary>
    public decimal? CardTotal { get; set; }

    // ---------- Kod motoru (Faz 2 kademesiyle aynı mantık) ----------
    public string? SuggestedAccountCode { get; set; }
    /// <summary>SMMM'nin onayladığı nihai gider hesabı kodu.</summary>
    public string? AccountCode { get; set; }
    /// <summary>Satıcının cari hesap kodu (VendorCariMap'te varsa; fişte otomatik cari açılmaz).</summary>
    public string? CariCode { get; set; }
    /// <summary>Birleşik güven skoru 0-1: min(OCR güveni, kod önerisi güveni). Onaylı fişte 1.0.</summary>
    public double? Confidence { get; set; }

    // ---------- Faz 8 — B3: KKEG / gider kısıtı ----------
    /// <summary>Fişte kanunen kabul edilmeyen gider (KKEG) kısmı var mı (KkegAnalyzer işaretler).</summary>
    public bool IsKkeg { get; set; }
    /// <summary>KKEG gerekçesi (Türkçe).</summary>
    public string? KkegReason { get; set; }
    /// <summary>KKEG tutarı — matrahın (NetAmount) indirilemeyen kısmı, KDV hariç.</summary>
    public decimal? KkegAmount { get; set; }

    // ---------- Faz 13: Araç plakası / akaryakıt eşleşmesi ----------
    /// <summary>Fişten okunan NORMALİZE plaka (ör. "34FPC630"); okunamadıysa null.</summary>
    public string? PlateNumber { get; set; }
    /// <summary>Plakanın mükellefin kayıtlı araçlarıyla eşleşme durumu (int kolon).</summary>
    public PlateMatchStatus PlateMatch { get; set; } = PlateMatchStatus.Yok;
    /// <summary>
    /// Plaka kaynaklı Türkçe uyarı (eşleşmeyen plaka / akaryakıt fişinde okunamayan plaka).
    /// SMMM doğrulama ekranında Warnings listesi olarak gösterilir; uyarı yoksa null.
    /// </summary>
    public string? PlateWarning { get; set; }

    // ---------- Faz 14: Arka plan OCR kuyruğu ----------
    /// <summary>
    /// Kuyruk işlemesinde OCR kalıcı olarak başarısız olduysa Türkçe hata metni.
    /// Faz 14 davranış farkı: senkron akış okunamayan fişi SİLİYORDU; kuyruk akışında fiş
    /// korunur, bu uyarıyla NeedsReview'a düşer ve SMMM alanları elle doldurur.
    /// </summary>
    public string? OcrWarning { get; set; }

    // ---------- Gider gösterilebilirlik ----------
    /// <summary>Gider olarak gösterilebilir mi? null = henüz değerlendirilmedi.</summary>
    public bool? ExpenseEligible { get; set; }
    /// <summary>Gösterilemiyorsa Türkçe gerekçe (ör. "Alkol/tütün giderleri indirim konusu yapılamaz.").</summary>
    public string? IneligibleReason { get; set; }
    /// <summary>Reddedildiyse SMMM'nin zorunlu gerekçesi.</summary>
    public string? RejectReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Faz 16: Fişin ORAN BAZLI KDV kırılım satırı (çok oranlı fişler + Z raporları).
/// İlişkisel tablo tercih edildi (JSON kolon DEĞİL): Luca export'u ve doğrulama buradan okur.
/// Receipt silinince kırılım da silinir (cascade). Tek oranlı fişte tek satır bulunur;
/// Receipt.NetAmount/VatAmount her zaman bu satırların toplamına eşit tutulur.
/// </summary>
public class ReceiptVatBreakdown
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
    /// <summary>KDV oranı, yüzde (0/1/10/20).</summary>
    public decimal VatRate { get; set; }
    /// <summary>Bu oranın matrahı (KDV hariç, iskonto sonrası).</summary>
    public decimal NetAmount { get; set; }
    /// <summary>Bu oranın KDV tutarı.</summary>
    public decimal VatAmount { get; set; }
}
