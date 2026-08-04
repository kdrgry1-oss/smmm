namespace MaliMusavir.Api.Domain;

/// <summary>
/// e-Defter dönem durumu (E5):
/// Hazirlaniyor: dönem açıldı, kontroller henüz temiz değil.
/// KontrolTamam: son ön-kontrol hatasız geçti (uyarılar olabilir) — berat yüklemeye hazır.
/// BeratYuklendi: SMMM, GİB e-Defter portalında beratı yükledikten sonra ELLE işaretledi.
/// Hata: son ön-kontrolde en az bir kırmızı (Hata) kontrol var.
/// </summary>
public enum EDefterPeriodStatus { Hazirlaniyor = 0, KontrolTamam = 1, BeratYuklendi = 2, Hata = 3 }

/// <summary>
/// e-Defter berat dönem kaydı (E5). Aynı mükellef + dönem için TEK kayıt vardır
/// (unique index); her ön-kontrol mevcut kaydın durumunu günceller.
///
/// DÜRÜST KAPSAM NOTU: Bu fazda gerçek e-Defter XML (XBRL-GL) üretimi ve mali mühürle
/// imzalama YOKTUR. Modül, berat YÜKLEMEDEN ÖNCE sahada iptal/yeniden sürecine yol açan
/// hataları (yanlış dönem, VKN/unvan uyuşmazlığı, sertifika süresi, eksik fişleştirme,
/// dengesiz mizan) yakalayan ön-kontrol + durum takibidir. Gerçek GİB entegrasyonu
/// geldiğinde "BeratYuklendi" durumu elle işaretleme yerine servisten beslenecek.
/// </summary>
public class EDefterPeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>Defter dönemi "yyyy-MM" (e-Defter aylık dönemlidir).</summary>
    public string Period { get; set; } = string.Empty;
    public EDefterPeriodStatus Status { get; set; } = EDefterPeriodStatus.Hazirlaniyor;
    /// <summary>Son ön-kontrol zamanı.</summary>
    public DateTime? CheckedAt { get; set; }
    /// <summary>SMMM'nin "berat yüklendi" olarak işaretlediği zaman.</summary>
    public DateTime? MarkedUploadedAt { get; set; }
    /// <summary>Serbest not (ör. GİB portal onay numarası).</summary>
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
