using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Ocr;

/// <summary>
/// OCR'a giden belgenin türü — prompt ve alan seti bu türe göre seçilir.
/// Fis: gider fişi / masraf belgesi (varsayılan, eski davranış birebir).
/// ZRaporu: yazar kasa günlük kapanış (Z) raporu → GELİR belgesi olarak işlenir.
/// Sayısal değerler Domain.ReceiptDocumentKind ile birebir aynıdır.
/// </summary>
public enum OcrDocumentKind { Fis = 0, ZRaporu = 1 }

/// <summary>Oran bazlı KDV kırılım satırı (doğrulama sonrası, normalize).</summary>
public class OcrVatBreakdownRow
{
    /// <summary>KDV oranı, yüzde (0/1/10/20).</summary>
    public decimal Rate { get; set; }
    /// <summary>Bu oranın matrahı (KDV hariç, iskonto SONRASI).</summary>
    public decimal Net { get; set; }
    /// <summary>Bu oranın KDV tutarı.</summary>
    public decimal Vat { get; set; }
}

/// <summary>
/// OCR alan çıkarımı sonucu (sağlayıcı-bağımsız DTO). Blueprint M3 işleme hattının
/// "alan çıkarımı" adımının çıktısıdır: satıcı, VKN, tarih, toplam, KDV oranı+tutarı, matrah.
/// Çok oranlı fişlerde VatBreakdown oran bazlı kırılımı taşır (NetAmount/VatAmount kırılım
/// toplamlarıdır); Z raporlarında ek olarak ÖKC seri no, Z no ve nakit/kart kırılımı dolar.
/// </summary>
public class OcrReceiptResult
{
    public string VendorName { get; set; } = string.Empty;
    public string VendorTaxId { get; set; } = string.Empty;
    /// <summary>Fiş/belge numarası (Z no, fiş sıra no) — Luca Evrak No kolonu için. Okunamazsa null.</summary>
    public string? DocumentNumber { get; set; }
    /// <summary>Belge tarihi; sağlayıcı emin değilse null bırakır (SMMM onay formunda doldurur).</summary>
    public DateTime? ReceiptDate { get; set; }
    /// <summary>KDV dahil toplam.</summary>
    public decimal TotalAmount { get; set; }
    /// <summary>
    /// KDV oranı, yüzde (ör. 1, 10, 20). Çok oranlı fişte EN YÜKSEK TUTARLI kırılımın oranı
    /// (geriye uyumluluk — asıl kaynak VatBreakdown'dır).
    /// </summary>
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
    /// <summary>Matrah (KDV hariç). Kırılım varsa kırılım netlerinin toplamı.</summary>
    public decimal NetAmount { get; set; }
    /// <summary>
    /// Oran bazlı KDV kırılımı. Model kırılım vermediyse tek oranlı fişlerde
    /// netAmount/vatAmount/vatRate'ten TEK satırlık kırılım türetilir (geriye uyumluluk);
    /// hiç veri yoksa boş liste kalır.
    /// </summary>
    public List<OcrVatBreakdownRow> VatBreakdown { get; set; } = new();
    /// <summary>Fişteki toplam iskonto/indirim (pozitif); yoksa null. Net tutarlar iskonto SONRASIDIR.</summary>
    public decimal? DiscountTotal { get; set; }
    /// <summary>
    /// Faz 13: fişten okunan NORMALİZE araç plakası (ör. "34FPC630") — akaryakıt fişlerinde basılıdır.
    /// OcrValidator normalize eder; geçersiz/okunamayan plaka null'dır (sessiz düşürme, uyarı yok).
    /// </summary>
    public string? PlateNumber { get; set; }

    // ---------- Z raporu alanları (yalnız OcrDocumentKind.ZRaporu okumalarında dolar) ----------
    /// <summary>Yazar kasa (ÖKC) sicil/seri numarası.</summary>
    public string? OkcSerialNumber { get; set; }
    /// <summary>Z raporu sayaç numarası.</summary>
    public string? ZNumber { get; set; }
    /// <summary>Nakit tahsilat toplamı (KDV dahil); raporda kırılım yoksa null.</summary>
    public decimal? CashTotal { get; set; }
    /// <summary>Kart tahsilat toplamı (KDV dahil); raporda kırılım yoksa null.</summary>
    public decimal? CardTotal { get; set; }

    /// <summary>
    /// OCR güven skoru 0-1. LlmVision sağlayıcısında alan bazlı güvenlerin MİNİMUMU
    /// (doğrulama uyarıları skoru ayrıca düşürür); Mock'ta deterministik üretilir.
    /// </summary>
    public double Confidence { get; set; }
    /// <summary>
    /// Fişteki kalemlerin serbest metin özeti (ör. "akaryakıt motorin benzin").
    /// Kural motoru eşleşmesi ve gider gösterilebilirlik kontrolünde kullanılır; DB'ye yazılmaz.
    /// </summary>
    public string ItemsSummary { get; set; } = string.Empty;
    /// <summary>Doğrulama katmanının Türkçe uyarıları (log/teşhis; DB'ye yazılmaz).</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Fiş OCR sağlayıcı adaptör arayüzü (blueprint §1.5 madde 5: Google DocAI / AWS Textract).
/// Gerçek sağlayıcı eklemek = bu arayüzü uygulayan tek sınıf yazmak; MockOcrProvider
/// anahtar gerektirmeden uçtan uca akışı çalıştırır.
/// </summary>
public interface IReceiptOcrProvider
{
    /// <summary>Sağlayıcının görünen adı (ör. "Mock", "GoogleDocumentAI").</summary>
    string Name { get; }

    /// <summary>
    /// Kaydedilmiş fiş dosyasından alanları çıkarır. filePath: diskteki tam yol
    /// (gerçek sağlayıcı dosya içeriğini okur; Mock dosya adı/boyutundan deterministik üretir).
    /// kind: belge türü — Fis (varsayılan gider fişi prompt'u) veya ZRaporu (Z raporu prompt'u).
    /// </summary>
    Task<ProviderResult<OcrReceiptResult>> ExtractAsync(
        string filePath, string originalFileName, long fileSize, OcrDocumentKind kind, CancellationToken ct);
}
