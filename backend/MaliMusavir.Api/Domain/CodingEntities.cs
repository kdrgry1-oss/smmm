namespace MaliMusavir.Api.Domain;

/// <summary>Hesap türü (mükellefin kanonik hesap planı için).</summary>
public enum AccountType { Gider = 0, Stok = 1, Cari = 2, Kdv = 3, Diger = 4 }

/// <summary>
/// Faturanın kodlama yaşam döngüsü durumu (motor tasarımı §1 Katman 4).
/// Uncoded: motor hiç çalışmadı. AutoCoded: tüm satırlar eşik üstü güvenle kodlandı.
/// NeedsReview: en az bir satır eşik altı → onay kuyruğunda. Approved: SMMM onayladı (öğrenme yazıldı).
/// </summary>
public enum CodingStatus { Uncoded = 0, AutoCoded = 1, NeedsReview = 2, Approved = 3 }

/// <summary>
/// Mükellefin kanonik hesap planı kaydı (motor tasarımı §2 chart_of_accounts).
/// Katman 3 (AI sınıflandırıcı) bağlandığında "önerilen hesap planda var olmalı" kısıtı burada doğrulanır.
/// </summary>
public class ChartAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Hesap kodu, ör. "770.01", "153", "320.001".</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; } = AccountType.Diger;
}

/// <summary>
/// Katman 1a — öğrenen hafıza (motor tasarımı §2 account_memory).
/// Anahtar: (mükellef, satıcı VKN, normalize kalem imzası, KDV oranı) → onaylanmış hesap kodu.
/// Her SMMM onayı ApproveCount'u artırır; motorun en yüksek güvenli katmanıdır.
/// </summary>
public class AccountMemory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    public string SenderTaxId { get; set; } = string.Empty;
    /// <summary>TextNormalizer.BuildItemSignature çıktısı (normalize edilmiş ilk 6 anlamlı kelime).</summary>
    public string ItemSignature { get; set; } = string.Empty;
    /// <summary>KDV oranı, yüzde (ör. 1, 10, 20). Aynı kalem farklı oranla farklı hesaba gidebilir.</summary>
    public decimal VatRate { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public int ApproveCount { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Katman 1b — satıcı seviyesi varsayılan hesap (motor tasarımı §1: "elektrik şirketi → hep 770 enerji").
/// AccountMemory onaylarından türetilir; en çok onaylanan hesap satıcının varsayılanı olur.
/// </summary>
public class VendorDefault
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Satıcı VKN. Geçmiş fiş listesi içe aktarımında VKN bilinmez → boş dize (unvan anahtarı kullanılır).</summary>
    public string SenderTaxId { get; set; } = string.Empty;
    /// <summary>
    /// TextNormalizer.NormalizeCompanyName çıktısı — VKN eşleşmesi bulunamadığında ikincil
    /// eşleşme anahtarı (geçmiş fiş listesi içe aktarımının öğrenme kanalı). Boş = yalnız VKN ile eşleşir.
    /// </summary>
    public string VendorNameNormalized { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public int ApproveCount { get; set; } = 1;
}

/// <summary>
/// Katman 2 — kural motoru kaydı (motor tasarımı §2 coding_rules).
/// ClientId null ise kural tenant genelidir (SMMM'nin tüm mükelleflerine uygulanır).
/// Pattern: regex ya da ";" ile ayrılmış anahtar kelimeler (ör. "akaryakıt;benzin;motorin").
/// </summary>
public class CodingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>null = tenant geneli; dolu = yalnızca o mükellefe uygulanır.</summary>
    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    /// <summary>Büyük öncelik önce denenir.</summary>
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Satıcı VKN → cari hesap kodu haritası (motor tasarımı §2 vendor_cari_map).
/// Harita boşsa motor ClientCodingSettings şemasına göre yeni cari kod üretip buraya yazar.
/// </summary>
public class VendorCariMap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Satıcı VKN. Mizan/fiş listesi içe aktarımında VKN bilinmez → boş dize; ilk VKN'li faturada geri doldurulur.</summary>
    public string SenderTaxId { get; set; } = string.Empty;
    public string SenderTitle { get; set; } = string.Empty;
    /// <summary>
    /// TextNormalizer.NormalizeCompanyName çıktısı — VKN eşleşmesi bulunamadığında ikincil
    /// (unvan-bazlı) cari eşleşme anahtarı. Mizan 120.*/320.* içe aktarımı bu alanla yazar.
    /// </summary>
    public string VendorNameNormalized { get; set; } = string.Empty;
    /// <summary>Ör. "320.001" (CariPrefix + '.' + 3 haneli sıra numarası).</summary>
    public string CariCode { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Mükellef bazlı kodlama ayarları: garantili doldurma (fallback) hesabı, cari kod şeması
/// ve otomatik uygulama güven eşiği (motor tasarımı §1 Katman 4).
/// </summary>
public class ClientCodingSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Hiçbir katman eşleşmezse atanacak varsayılan gider hesabı (boşluksuz doldurma ilkesi).</summary>
    public string DefaultExpenseAccount { get; set; } = "770.99";
    /// <summary>Cari ana hesap ön eki (satıcılar için 320, alıcılar için 120 olabilir).</summary>
    public string CariPrefix { get; set; } = "320";
    /// <summary>Bir sonraki otomatik cari alt hesap sıra numarası (3 haneli basılır: 320.001).</summary>
    public int CariNextNumber { get; set; } = 1;
    /// <summary>Tüm satırlar bu güvenin üstündeyse fatura AutoCoded; altındaysa NeedsReview olur.</summary>
    public double AutoApplyThreshold { get; set; } = 0.85;
    /// <summary>
    /// Faz 8 — B3: Mükellefin aktifinde/kiralamasında binek otomobil var mı?
    /// TRUE ise akaryakıt giderleri de KKEG %30 kısıtına girer (KkegAnalyzer kural a);
    /// FALSE iken akaryakıt kalemleri KKEG işaretlenmez (ticari araç varsayımı).
    /// </summary>
    public bool HasCompanyVehicle { get; set; }
}
