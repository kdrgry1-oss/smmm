using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Tebligat;

/// <summary>
/// Sağlayıcıdan okunan normalize tebligat (sağlayıcı-bağımsız DTO).
/// </summary>
public class ProviderTebligat
{
    /// <summary>Sağlayıcı tarafındaki benzersiz tebligat kimliği (mükerrer kontrol anahtarı).</summary>
    public string ExternalId { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; }
    public DateTime? LegalDeadline { get; set; }
    /// <summary>"İtiraz" / "Ödeme" / "Bilgi".</summary>
    public string? DeadlineType { get; set; }
    /// <summary>Sağlayıcı tarafında okunmuş görünüyor mu (ilk senkronda platform durumuna yazılır).</summary>
    public bool IsRead { get; set; }
}

/// <summary>
/// e-Tebligat sağlayıcı adaptör arayüzü (blueprint M6). Gerçek UETS/PTT web servisi,
/// PTT kurumsal başvurusu + mükellef yetkilendirmesi tamamlanınca bu arayüzü uygulayan
/// tek sınıfla eklenecek; MockUetsProvider başvuru beklenmeden uçtan uca akışı çalıştırır.
/// </summary>
public interface IETebligatProvider
{
    /// <summary>Sağlayıcının görünen adı (ör. "MockUets", "PttUets").</summary>
    string Name { get; }

    /// <summary>Mükellefin UETS adresine gelen tebligatları çeker.</summary>
    Task<ProviderResult<List<ProviderTebligat>>> GetMessagesAsync(Client client, CancellationToken ct);
}
