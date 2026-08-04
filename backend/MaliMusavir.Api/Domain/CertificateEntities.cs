namespace MaliMusavir.Api.Domain;

/// <summary>Sertifika türü (E3): e-İmza, mali mühür, KEP.</summary>
public enum CertificateType { EImza = 0, MaliMuhur = 1, Kep = 2 }

/// <summary>
/// Sertifika süre takibi kaydı (E3). ClientId null = SMMM'nin (ofisin) kendi sertifikası.
/// Süresi dolan e-imza/mali mühür/KEP işleri durdurur (e-Defter berat hatalarının sık sebebi);
/// liste ekranı kalan süreye göre renkli uyarı verir (&lt;30 gün kırmızı, &lt;90 gün sarı).
/// </summary>
public class ClientCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>null = SMMM'nin kendisine (ofise) ait sertifika.</summary>
    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }
    public CertificateType Type { get; set; } = CertificateType.EImza;
    /// <summary>Kime ait olduğunun açıklaması (ör. "Ahmet Yılmaz — şirket imza yetkilisi").</summary>
    public string Owner { get; set; } = string.Empty;
    /// <summary>Sertifika geçerlilik sonu (UTC gün başı).</summary>
    public DateTime ExpiresAt { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
