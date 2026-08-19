namespace MaliMusavir.Api.Domain;

/// <summary>Fiziki POS / YNÖKC ve sanal POS sağlayıcıları için bağlantı kaydı.</summary>
public enum PosProvider
{
    TokenBeko = 0, Ingenico = 1, ProfiloVerifone = 2, Pavo = 3, Hugin = 4,
    Inpos = 5, Paygo = 6, Pax = 7,
    PayTR = 20, Iyzico = 21, Param = 22, Sipay = 23, Moka = 24, Paynet = 25,
    BankaPos = 40, Diger = 99
}

/// <summary>
/// Mükellefin bir POS hesabı ya da YNÖKC cihazı. Gizli alanlar AES ile şifrelenir;
/// cihaz ekranına doğrudan erişim ya da kullanıcı parolası saklanmaz.
/// </summary>
public class PosConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    public PosProvider Provider { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? MerchantId { get; set; }
    public string? TerminalId { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? ApiCredentialEncrypted { get; set; }
    public bool ApiAccessEnabled { get; set; }
    public string ConnectionStatus { get; set; } = "YetkiBekliyor";
    public DateTime? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
