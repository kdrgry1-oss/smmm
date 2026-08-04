using System.ComponentModel.DataAnnotations;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Contracts;

// ---------- Auth ----------

/// <summary>Yeni mali müşavir ofisi (tenant) kaydı.</summary>
public class RegisterRequest
{
    [Required(ErrorMessage = "Firma adı zorunludur.")]
    [MaxLength(200, ErrorMessage = "Firma adı en fazla 200 karakter olabilir.")]
    public string FirmName { get; set; } = string.Empty;

    [Required(ErrorMessage = "E-posta adresi zorunludur.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    [MaxLength(320, ErrorMessage = "E-posta adresi en fazla 320 karakter olabilir.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Şifre zorunludur.")]
    [MinLength(8, ErrorMessage = "Şifre en az 8 karakter olmalıdır.")]
    [MaxLength(100, ErrorMessage = "Şifre en fazla 100 karakter olabilir.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Ad soyad zorunludur.")]
    [MaxLength(200, ErrorMessage = "Ad soyad en fazla 200 karakter olabilir.")]
    public string FullName { get; set; } = string.Empty;
}

public class LoginRequest
{
    [Required(ErrorMessage = "E-posta adresi zorunludur.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Şifre zorunludur.")]
    public string Password { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public string FullName { get; set; } = string.Empty;
}

public class MeResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? PlanCode { get; set; }
    public string? PlanName { get; set; }
}

// ---------- Mükellef (Client) ----------

public class ClientCreateUpdateRequest
{
    [Required(ErrorMessage = "Unvan zorunludur.")]
    [MaxLength(300, ErrorMessage = "Unvan en fazla 300 karakter olabilir.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vergi/TC kimlik numarası zorunludur.")]
    [RegularExpression(@"^\d{10}(\d)?$", ErrorMessage = "Vergi/TC kimlik numarası 10 veya 11 haneli olmalıdır.")]
    public string TaxId { get; set; } = string.Empty;

    [MaxLength(150, ErrorMessage = "Vergi dairesi en fazla 150 karakter olabilir.")]
    public string? TaxOffice { get; set; }

    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    [MaxLength(320, ErrorMessage = "E-posta adresi en fazla 320 karakter olabilir.")]
    public string? Email { get; set; }

    [MaxLength(30, ErrorMessage = "Telefon en fazla 30 karakter olabilir.")]
    public string? Phone { get; set; }

    [MaxLength(500, ErrorMessage = "Adres en fazla 500 karakter olabilir.")]
    public string? Address { get; set; }

    public bool EInvoiceEnabled { get; set; }
    public bool EArchiveEnabled { get; set; }

    [MaxLength(100, ErrorMessage = "Entegratör adı en fazla 100 karakter olabilir.")]
    public string? IntegratorName { get; set; }
}

public class ClientResponse
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string? TaxOffice { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool EInvoiceEnabled { get; set; }
    public bool EArchiveEnabled { get; set; }
    public string? IntegratorName { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ClientResponse FromEntity(Client c) => new()
    {
        Id = c.Id,
        Title = c.Title,
        TaxId = c.TaxId,
        TaxOffice = c.TaxOffice,
        Email = c.Email,
        Phone = c.Phone,
        Address = c.Address,
        EInvoiceEnabled = c.EInvoiceEnabled,
        EArchiveEnabled = c.EArchiveEnabled,
        IntegratorName = c.IntegratorName,
        IsArchived = c.IsArchived,
        CreatedAt = c.CreatedAt
    };
}

// ---------- Faz 13: Mükellef araçları (plaka) ----------

/// <summary>
/// Araç kaydı isteği. Plaka sunucuda normalize edilir (büyük harf, boşluksuz) ve
/// Türk plaka desenine (^[0-9]{2}[A-Z]{1,3}[0-9]{2,5}$) uymuyorsa 400 döner.
/// </summary>
public class ClientVehicleCreateRequest
{
    [Required(ErrorMessage = "Plaka zorunludur.")]
    [MaxLength(20, ErrorMessage = "Plaka en fazla 20 karakter olabilir.")]
    public string Plate { get; set; } = string.Empty;

    /// <summary>Binek otomobil mi? KKEG %30 kısıtı yalnız binek araçta uygulanır.</summary>
    public bool IsPassengerCar { get; set; }

    [MaxLength(200, ErrorMessage = "Açıklama en fazla 200 karakter olabilir.")]
    public string? Description { get; set; }
}

public class ClientVehicleResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    /// <summary>Normalize plaka (ör. "34FPC630").</summary>
    public string Plate { get; set; } = string.Empty;
    public bool IsPassengerCar { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ClientVehicleResponse FromEntity(ClientVehicle v) => new()
    {
        Id = v.Id,
        ClientId = v.ClientId,
        Plate = v.Plate,
        IsPassengerCar = v.IsPassengerCar,
        Description = v.Description,
        CreatedAt = v.CreatedAt
    };
}

// ---------- Toplu içe aktarma: mükellef listesi + araç plakaları ----------

/// <summary>
/// Mükellef listesi içe aktarma özeti (api/clients/import). Satır hataları Türkçe listelenir;
/// VKN'si zaten kayıtlı mükellefler DEĞİŞTİRİLMEZ (skipped sayılır).
/// </summary>
public class ClientImportResultResponse
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>Türkçe özet, ör. "12 mükellef eklendi, 3 satır atlandı (VKN zaten kayıtlı)."</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Araç plakası içe aktarma özeti (api/vehicles/import). Mükellef VKN üzerinden eşleşir;
/// eşleşmeyen VKN ve geçersiz plaka/tip satırları Türkçe hata olarak listelenir.
/// </summary>
public class VehicleImportResultResponse
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>Türkçe özet, ör. "8 araç eklendi, 2 satır atlandı (plaka zaten kayıtlı)."</summary>
    public string Message { get; set; } = string.Empty;
}

// ---------- Plan ----------

public class PlanResponse
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal MonthlyPrice { get; set; }
    public int MaxClients { get; set; }
    public int MaxUsers { get; set; }
    public int MonthlyOcrQuota { get; set; }
    public bool ETebligat { get; set; }
    /// <summary>Sınırsız mükellef (MaxClients = int.MaxValue) planları için true.</summary>
    public bool UnlimitedClients { get; set; }

    public static PlanResponse FromEntity(Plan p) => new()
    {
        Id = p.Id,
        Code = p.Code,
        Name = p.Name,
        MonthlyPrice = p.MonthlyPrice,
        MaxClients = p.MaxClients,
        MaxUsers = p.MaxUsers,
        MonthlyOcrQuota = p.MonthlyOcrQuota,
        ETebligat = p.ETebligat,
        UnlimitedClients = p.MaxClients == int.MaxValue
    };
}

// ---------- Admin ----------

public class TenantAdminResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PlanCode { get; set; }
    public string? PlanName { get; set; }
    public string SubscriptionStatus { get; set; } = string.Empty;
    public int UserCount { get; set; }
    public int ClientCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdminStatsResponse
{
    public int TotalTenants { get; set; }
    public int TotalUsers { get; set; }
    public int TotalClients { get; set; }
    public List<PlanDistributionItem> PlanDistribution { get; set; } = new();
}

public class PlanDistributionItem
{
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public int TenantCount { get; set; }
}

public class TenantStatusUpdateRequest
{
    [Required(ErrorMessage = "Abonelik durumu zorunludur.")]
    [EnumDataType(typeof(SubscriptionStatus), ErrorMessage = "Geçersiz abonelik durumu.")]
    public SubscriptionStatus Status { get; set; }
}

// ---------- Entegrasyon (mükellef ↔ entegratör) ----------

public class IntegrationSaveRequest
{
    [Required(ErrorMessage = "Sağlayıcı zorunludur.")]
    [EnumDataType(typeof(IntegrationProvider), ErrorMessage = "Geçersiz sağlayıcı. Mock, Nilvera, Uyumsoft, DoganDonusum, Elogo, Edm, Parasut veya GibPortal seçin.")]
    public IntegrationProvider Provider { get; set; }

    /// <summary>Mock için opsiyonel; Nilvera için zorunludur (controller'da doğrulanır).</summary>
    [MaxLength(500, ErrorMessage = "API anahtarı en fazla 500 karakter olabilir.")]
    public string? ApiKey { get; set; }

    /// <summary>Uyumsoft/Doğan Dönüşüm/eLogo/EDM (ve Paraşüt: e-posta) için zorunlu kullanıcı adı (controller'da doğrulanır).</summary>
    [MaxLength(200, ErrorMessage = "Kullanıcı adı en fazla 200 karakter olabilir.")]
    public string? Username { get; set; }

    /// <summary>Kimlik-tabanlı sağlayıcılar (Uyumsoft/Doğan/eLogo/EDM/Paraşüt) için zorunlu şifre (controller'da doğrulanır).</summary>
    [MaxLength(200, ErrorMessage = "Şifre en fazla 200 karakter olabilir.")]
    public string? Password { get; set; }

    /// <summary>
    /// Uyumsoft/Doğan Dönüşüm/eLogo/EDM için opsiyonel servis adresi (entegratörün bildirdiği uç).
    /// Boş bırakılırsa varsayılan uç kullanılır; girildiyse geçerli bir http/https URL
    /// olmalıdır (controller'da doğrulanır).
    /// </summary>
    [MaxLength(300, ErrorMessage = "Servis adresi en fazla 300 karakter olabilir.")]
    public string? Endpoint { get; set; }

    // ---------- Paraşüt'e özgü kimlik alanları (yalnız Provider=Parasut iken kullanılır) ----------

    /// <summary>Paraşüt OAuth uygulaması Client ID (Ayarlar → API'den oluşturulur).</summary>
    [MaxLength(200, ErrorMessage = "Client ID en fazla 200 karakter olabilir.")]
    public string? ParasutClientId { get; set; }

    /// <summary>Paraşüt OAuth uygulaması Client Secret.</summary>
    [MaxLength(200, ErrorMessage = "Client Secret en fazla 200 karakter olabilir.")]
    public string? ParasutClientSecret { get; set; }

    /// <summary>Paraşüt firma numarası (adres çubuğundaki sayı; API yolundaki company_id).</summary>
    [MaxLength(50, ErrorMessage = "Firma ID en fazla 50 karakter olabilir.")]
    public string? ParasutCompanyId { get; set; }

    // ---------- Uyumsoft'a özgü belge türü bayrakları (yalnız Provider=Uyumsoft iken kullanılır) ----------

    /// <summary>
    /// Uyumsoft: mükellef e-SMM (serbest meslek makbuzu) mükellefi mi? true ise makbuzlar
    /// VoucherIntegration servisinden çekilir. null = kayıtlı bayrak korunur (geriye uyum;
    /// hiç kayıt yoksa varsayılan false).
    /// </summary>
    public bool? UyumsoftEsmm { get; set; }

    /// <summary>
    /// Uyumsoft: e-Fatura / e-Arşiv gelen kutusu da çekilsin mi? null = kayıtlı bayrak korunur
    /// (hiç kayıt yoksa varsayılan true). Yalnız e-SMM mükellefinde false gönderilir;
    /// iki bayrak birden false olamaz (controller doğrular).
    /// </summary>
    public bool? UyumsoftEinvoice { get; set; }

    public bool UseTestEnvironment { get; set; } = true;
}

/// <summary>
/// Faz 15 — Bağlantı testi isteği (şu an yalnız GİB Portalı). Kimlik gönderilmezse
/// kayıtlı kimlik kullanılır.
/// </summary>
public class IntegrationTestRequest
{
    /// <summary>Test edilecek sağlayıcı; boşsa kayıtlı sağlayıcı denenir.</summary>
    public IntegrationProvider? Provider { get; set; }

    /// <summary>GİB kullanıcı kodu / TCKN (boşsa kayıtlı kimlik kullanılır).</summary>
    [MaxLength(200, ErrorMessage = "Kullanıcı adı en fazla 200 karakter olabilir.")]
    public string? Username { get; set; }

    /// <summary>GİB şifresi (boşsa kayıtlı kimlik kullanılır). Yanıtta ASLA dönmez.</summary>
    [MaxLength(200, ErrorMessage = "Şifre en fazla 200 karakter olabilir.")]
    public string? Password { get; set; }
}

/// <summary>Bağlantı testi sonucu (Türkçe mesaj + insan doğrulaması gerekiyor mu).</summary>
public class IntegrationTestResponse
{
    public bool Success { get; set; }

    /// <summary>
    /// true → GİB captcha/SMS gibi ek doğrulama istedi. Sistem bunu güvenlik gereği ÇÖZMEZ;
    /// mükellef "Elle doğrulama gerekli" olarak işaretlenir.
    /// </summary>
    public bool RequiresManual { get; set; }

    /// <summary>
    /// Faz 17: true → kayıtlı GİB oturumu yok/geçersiz; kullanıcı "GİB Oturumu Aç"
    /// akışıyla (gib-login/begin + complete) doğrulama kodunu girmeli.
    /// </summary>
    public bool NeedsCaptchaLogin { get; set; }

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Faz 17 — GİB insan destekli giriş, 1. aşama (gib-login/begin) yanıtı: robot giriş
/// ekranını açıp kimliği doldurdu; kullanıcı görseldeki doğrulama kodunu okuyup girecek.
/// Şifre bu yanıtta ASLA yer almaz.
/// </summary>
public class GibLoginBeginResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Bekleyen giriş oturumu kimliği (robotta 5 dk bekletilir). Başarısızsa null.</summary>
    public string? PendingId { get; set; }

    /// <summary>Doğrulama kodu görseli (base64 PNG, "data:" öneki YOK). Yoksa null.</summary>
    public string? CaptchaImage { get; set; }

    /// <summary>Kodun girilmesi için tanınan süre (saniye).</summary>
    public int? ExpiresInSeconds { get; set; }

    /// <summary>true → kayıtlı GİB oturumu hâlâ geçerli; doğrulama koduna gerek yok.</summary>
    public bool AlreadyLoggedIn { get; set; }
}

/// <summary>Faz 17 — GİB insan destekli giriş, 2. aşama isteği (kullanıcının okuduğu kod).</summary>
public class GibLoginCompleteRequest
{
    [Required(ErrorMessage = "Bekleyen giriş oturumu kimliği (pendingId) zorunludur.")]
    [MaxLength(64, ErrorMessage = "pendingId geçersiz.")]
    public string PendingId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Doğrulama kodu zorunludur.")]
    [MaxLength(16, ErrorMessage = "Doğrulama kodu en fazla 16 karakter olabilir.")]
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Faz 17 — GİB insan destekli giriş, 2. aşama yanıtı. Hatalı kodda AYNI pendingId ile
/// YENİ captcha görseli döner; pendingExpired=true ise "Yeni Kod" ile baştan başlanmalı.
/// </summary>
public class GibLoginCompleteResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Hatalı denemede YENİ doğrulama kodu görseli (base64 PNG); yoksa null.</summary>
    public string? CaptchaImage { get; set; }

    /// <summary>Kalan deneme hakkı (en fazla 3); bilinmiyorsa null.</summary>
    public int? AttemptsLeft { get; set; }

    /// <summary>true → bekleyen oturum iptal/zaman aşımı; begin ucu yeniden çağrılmalı.</summary>
    public bool PendingExpired { get; set; }
}

// ---------- Faz 18: Müşavir (SMMM) tek GİB girişi ----------

/// <summary>Müşavir GİB kimliği kaydetme isteği. Şifre yanıtlarda ASLA dönmez.</summary>
public class GibAdvisorCredentialSaveRequest
{
    [Required(ErrorMessage = "Müşavir GİB kullanıcı kodu / TCKN zorunludur.")]
    [MaxLength(100, ErrorMessage = "Kullanıcı kodu en fazla 100 karakter olabilir.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Müşavir GİB şifresi zorunludur.")]
    [MaxLength(200, ErrorMessage = "Şifre en fazla 200 karakter olabilir.")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Müşavir GİB kimliği durumu (şifre maskeli — hiç dönmez).</summary>
public class GibAdvisorCredentialResponse
{
    public bool HasCredential { get; set; }
    /// <summary>Maskeli kullanıcı kodu (ilk 2 + son 2 karakter). Kayıt yoksa null.</summary>
    public string? MaskedUsername { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Müşavir GİB oturum durumu rozeti (robot diskteki oturum dosyasından okur).</summary>
public class GibAdvisorSessionStatusResponse
{
    /// <summary>true → oturum kayıtlı ve süresi geçmemiş (aktif).</summary>
    public bool Active { get; set; }
    /// <summary>true → oturum var ama düşmüş/eskimiş (yenilenmeli).</summary>
    public bool Expired { get; set; }
    /// <summary>Hiç giriş yapılmamış (oturum dosyası yok).</summary>
    public bool Exists { get; set; }
    /// <summary>Son başarılı hareket (ISO). Yoksa null.</summary>
    public string? LastSeenAt { get; set; }
    public string? Message { get; set; }
}

/// <summary>GİB'den gelen tek müşavir mükellefi (sistemdeki Client eşleşmesiyle).</summary>
public class GibAdvisorClientItem
{
    public string TaxId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>true → VKN sistemdeki bir mükellefle eşleşti.</summary>
    public bool Matched { get; set; }
    /// <summary>Eşleşen mükellefin kimliği; eşleşme yoksa null ("sisteme eklenebilir").</summary>
    public Guid? ClientId { get; set; }
    /// <summary>Eşleşen mükellefin sistemdeki unvanı; yoksa null.</summary>
    public string? ClientTitle { get; set; }
}

/// <summary>"Mükelleflerimi GİB'den Getir" yanıtı.</summary>
public class GibAdvisorClientsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<GibAdvisorClientItem> Items { get; set; } = new();
    /// <summary>true → müşavir GİB oturumu yok/düşmüş; önce "GİB Oturumu Aç".</summary>
    public bool NeedsCaptchaLogin { get; set; }
}

public class IntegrationResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Provider { get; set; } = string.Empty;
    /// <summary>Maskeli anahtar (yalnızca son 4 hane, ör. "••••3f7a"). Anahtar yoksa null.</summary>
    public string? MaskedApiKey { get; set; }
    /// <summary>Kimlik-tabanlı sağlayıcılar (Uyumsoft/Doğan/eLogo/EDM/Paraşüt): maskeli kullanıcı adı (ilk 2 + son 2 karakter, ör. "uy••••um"). Diğerlerinde null.</summary>
    public string? MaskedUsername { get; set; }
    /// <summary>Kimlik-tabanlı SOAP sağlayıcıları: kayıtlı servis adresi (gizli değildir, maskesiz döner). Boşsa varsayılan uç kullanılır.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Uyumsoft: e-SMM (serbest meslek makbuzu) bayrağı — kimlik paketinden okunur. Diğer sağlayıcılarda null.</summary>
    public bool? UyumsoftEsmm { get; set; }
    /// <summary>Uyumsoft: e-Fatura / e-Arşiv gelen kutusu bayrağı. Diğer sağlayıcılarda null.</summary>
    public bool? UyumsoftEinvoice { get; set; }

    /// <summary>
    /// Faz 15 (GİB Portalı): son otomatik çekimde captcha/SMS gibi insan gerektiren bir adım
    /// çıktıysa Türkçe uyarı metni. Gizli bilgi DEĞİLDİR; UI'da sarı rozet olarak gösterilir.
    /// Diğer sağlayıcılarda ve sorunsuz çekimde null.
    /// </summary>
    public string? ManualWarning { get; set; }

    /// <summary>Uyarının oluştuğu an (UTC). Uyarı yoksa null.</summary>
    public DateTime? ManualWarningAt { get; set; }

    public bool UseTestEnvironment { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSyncAt { get; set; }

    public static IntegrationResponse FromEntity(
        ClientIntegration ci, string? plainApiKey, string? plainUsername = null, string? endpoint = null,
        bool? uyumsoftEsmm = null, bool? uyumsoftEinvoice = null,
        string? manualWarning = null, DateTime? manualWarningAt = null) => new()
    {
        Id = ci.Id,
        ClientId = ci.ClientId,
        Provider = ci.Provider.ToString(),
        MaskedApiKey = MaskApiKey(plainApiKey),
        MaskedUsername = MaskUsername(plainUsername),
        Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint,
        UyumsoftEsmm = uyumsoftEsmm,
        UyumsoftEinvoice = uyumsoftEinvoice,
        ManualWarning = string.IsNullOrWhiteSpace(manualWarning) ? null : manualWarning,
        ManualWarningAt = manualWarningAt,
        UseTestEnvironment = ci.UseTestEnvironment,
        CreatedAt = ci.CreatedAt,
        LastSyncAt = ci.LastSyncAt
    };

    public static string? MaskApiKey(string? plainApiKey)
    {
        if (string.IsNullOrEmpty(plainApiKey)) return null;
        var last4 = plainApiKey.Length <= 4 ? plainApiKey : plainApiKey[^4..];
        return $"••••{last4}";
    }

    /// <summary>Kullanıcı adının ilk 2 + son 2 karakteri görünür kalır (kısa adlarda tamamı maskelenir).</summary>
    public static string? MaskUsername(string? plainUsername)
    {
        if (string.IsNullOrEmpty(plainUsername)) return null;
        if (plainUsername.Length <= 4) return "••••";
        return $"{plainUsername[..2]}••••{plainUsername[^2..]}";
    }
}

// ---------- e-Fatura ----------

public class SyncResultResponse
{
    public int Added { get; set; }
    public int SkippedDuplicates { get; set; }
    /// <summary>Kullanıcıya gösterilecek Türkçe özet, ör. "5 yeni fatura alındı, 2 mükerrer atlandı."</summary>
    public string Message { get; set; } = string.Empty;
}

public class EInvoiceListItemResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public string SenderTaxId { get; set; } = string.Empty;
    public string SenderTitle { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = string.Empty;
    public decimal TaxExclusiveAmount { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal PayableAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    /// <summary>Kodlama durumu: Uncoded / AutoCoded / NeedsReview / Approved (Faz 2).</summary>
    public string CodingStatus { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Satırların etkili hesap kodlarının (AccountCode ?? SuggestedAccountCode) DISTINCT özeti:
    /// en fazla 3 kod virgülle, fazlası " +N" olarak (ör. "770.01, 153.01 +2"). Kod yoksa null.
    /// Yalnızca satırlar sorguya dahil edildiyse (Include) dolar.
    /// </summary>
    public string? AccountCodeSummary { get; set; }

    /// <summary>İlk satırın cari kodu (ör. "320.001"); satırlar yüklenmediyse veya kod yoksa null.</summary>
    public string? CariCode { get; set; }

    public static EInvoiceListItemResponse FromEntity(EInvoice e) => new()
    {
        Id = e.Id,
        ClientId = e.ClientId,
        InvoiceNumber = e.InvoiceNumber,
        IssueDate = e.IssueDate,
        SenderTaxId = e.SenderTaxId,
        SenderTitle = e.SenderTitle,
        CurrencyCode = e.CurrencyCode,
        TaxExclusiveAmount = e.TaxExclusiveAmount,
        TaxTotal = e.TaxTotal,
        PayableAmount = e.PayableAmount,
        Status = e.Status.ToString(),
        CodingStatus = e.CodingStatus.ToString(),
        CreatedAt = e.CreatedAt,
        AccountCodeSummary = BuildAccountCodeSummary(e.Lines),
        CariCode = e.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => l.CariCode)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
    };

    /// <summary>Etkili kodların (nihai ?? öneri) DISTINCT birleşimi — en fazla 3 kod + " +N".</summary>
    private static string? BuildAccountCodeSummary(IEnumerable<EInvoiceLine> lines)
    {
        var codes = lines
            .OrderBy(l => l.LineNumber)
            .Select(l => string.IsNullOrWhiteSpace(l.AccountCode) ? l.SuggestedAccountCode : l.AccountCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct()
            .ToList();

        if (codes.Count == 0) return null;

        var visible = string.Join(", ", codes.Take(3));
        return codes.Count > 3 ? $"{visible} +{codes.Count - 3}" : visible;
    }
}

public class EInvoiceLineResponse
{
    public Guid Id { get; set; }
    public int LineNumber { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string UnitCode { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal LineAmount { get; set; }
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
    public string? SuggestedAccountCode { get; set; }
    public string? AccountCode { get; set; }
    /// <summary>Motor güven skoru 0-1 (Faz 2). Onaylı satırlarda 1.0.</summary>
    public double? Confidence { get; set; }
    /// <summary>Satıcının cari hesap kodu (ör. "320.001").</summary>
    public string? CariCode { get; set; }

    // ---------- Faz 8 — B2: Tevkifat / B3: KKEG ----------
    /// <summary>GİB kısmi tevkifat kodu (ör. "601"); yoksa null.</summary>
    public string? WithholdingCode { get; set; }
    /// <summary>Tevkifat oranı pay/payda (ör. "9/10").</summary>
    public string? WithholdingRate { get; set; }
    /// <summary>Tevkif edilen KDV (sorumlu sıfatıyla KDV2'de beyan edilir).</summary>
    public decimal? WithholdingVatAmount { get; set; }
    /// <summary>Satırda KKEG kısmı var mı?</summary>
    public bool IsKkeg { get; set; }
    public string? KkegReason { get; set; }
    /// <summary>KKEG tutarı (matrahın indirilemeyen kısmı, KDV hariç).</summary>
    public decimal? KkegAmount { get; set; }

    public static EInvoiceLineResponse FromEntity(EInvoiceLine l) => new()
    {
        Id = l.Id,
        LineNumber = l.LineNumber,
        ItemName = l.ItemName,
        Quantity = l.Quantity,
        UnitCode = l.UnitCode,
        UnitPrice = l.UnitPrice,
        LineAmount = l.LineAmount,
        VatRate = l.VatRate,
        VatAmount = l.VatAmount,
        SuggestedAccountCode = l.SuggestedAccountCode,
        AccountCode = l.AccountCode,
        Confidence = l.Confidence,
        CariCode = l.CariCode,
        WithholdingCode = l.WithholdingCode,
        WithholdingRate = l.WithholdingRate,
        WithholdingVatAmount = l.WithholdingVatAmount,
        IsKkeg = l.IsKkeg,
        KkegReason = l.KkegReason,
        KkegAmount = l.KkegAmount
    };
}

public class EInvoiceDetailResponse : EInvoiceListItemResponse
{
    public string ExternalUuid { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public List<EInvoiceLineResponse> Lines { get; set; } = new();

    public static EInvoiceDetailResponse FromEntityWithLines(EInvoice e)
    {
        var baseDto = FromEntity(e);
        return new EInvoiceDetailResponse
        {
            Id = baseDto.Id,
            ClientId = baseDto.ClientId,
            InvoiceNumber = baseDto.InvoiceNumber,
            IssueDate = baseDto.IssueDate,
            SenderTaxId = baseDto.SenderTaxId,
            SenderTitle = baseDto.SenderTitle,
            CurrencyCode = baseDto.CurrencyCode,
            TaxExclusiveAmount = baseDto.TaxExclusiveAmount,
            TaxTotal = baseDto.TaxTotal,
            PayableAmount = baseDto.PayableAmount,
            Status = baseDto.Status,
            CodingStatus = baseDto.CodingStatus,
            CreatedAt = baseDto.CreatedAt,
            AccountCodeSummary = baseDto.AccountCodeSummary,
            CariCode = baseDto.CariCode,
            ExternalUuid = e.ExternalUuid,
            Direction = e.Direction.ToString(),
            Lines = e.Lines
                .OrderBy(l => l.LineNumber)
                .Select(EInvoiceLineResponse.FromEntity)
                .ToList()
        };
    }
}

public class EInvoicePageResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<EInvoiceListItemResponse> Items { get; set; } = new();
}

// ---------- Faz 2: Otomatik Hesap/Cari Kod Motoru ----------

/// <summary>Tek fatura kodlama sonucu: satır önerileri + fatura kodlama durumu.</summary>
public class CodingResultResponse
{
    public Guid EInvoiceId { get; set; }
    public string CodingStatus { get; set; } = string.Empty;
    /// <summary>Kullanıcıya gösterilecek Türkçe özet.</summary>
    public string Message { get; set; } = string.Empty;
    public List<CodingLineSuggestionResponse> Lines { get; set; } = new();
}

/// <summary>Satır önerisi: hangi katman ("Hafıza", "Kural"...) hangi güvenle hangi kodu atadı.</summary>
public class CodingLineSuggestionResponse
{
    public Guid LineId { get; set; }
    public int LineNumber { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? SuggestedAccountCode { get; set; }
    public double? Confidence { get; set; }
    public string? CariCode { get; set; }
    /// <summary>Öneri kaynağı katmanın Türkçe etiketi.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>Toplu kodlama özeti.</summary>
public class CodeBatchResultResponse
{
    public int Total { get; set; }
    public int AutoCoded { get; set; }
    public int NeedsReview { get; set; }
    /// <summary>Ör. "12 fatura otomatik kodlandı, 3 onay bekliyor."</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Onay isteğinde tek satır düzeltmesi. AccountCode boşsa mevcut öneri kabul edilir.</summary>
public class ApproveLineRequest
{
    [Required(ErrorMessage = "Satır kimliği zorunludur.")]
    public Guid LineId { get; set; }

    [RegularExpression(@"^\d{3}(\.\d{1,4}){0,3}$", ErrorMessage = "Geçerli bir hesap kodu girin (ör. 770.01).")]
    public string? AccountCode { get; set; }

    [RegularExpression(@"^\d{3}(\.\d{1,4}){0,3}$", ErrorMessage = "Geçerli bir cari kodu girin (ör. 320.001).")]
    public string? CariCode { get; set; }

    // ---------- Faz 8 — B2: Tevkifat düzeltmesi ----------
    /// <summary>null = dokunma; "" (boş dize) = tevkifatı temizle; dolu = kodu uygula.</summary>
    [RegularExpression(@"^\d{3}$", ErrorMessage = "Tevkifat kodu 3 haneli olmalıdır (ör. 601).")]
    public string? WithholdingCode { get; set; }

    [RegularExpression(@"^\d{1,2}/\d{1,2}$", ErrorMessage = "Tevkifat oranı pay/payda biçiminde olmalıdır (ör. 9/10).")]
    public string? WithholdingRate { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "Tevkifat KDV tutarı negatif olamaz.")]
    public decimal? WithholdingVatAmount { get; set; }

    // ---------- Faz 8 — B3: KKEG düzeltmesi ----------
    /// <summary>null = dokunma; false = KKEG işaretini kaldır; true = işaretle.</summary>
    public bool? IsKkeg { get; set; }

    [MaxLength(300, ErrorMessage = "KKEG gerekçesi en fazla 300 karakter olabilir.")]
    public string? KkegReason { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "KKEG tutarı negatif olamaz.")]
    public decimal? KkegAmount { get; set; }
}

/// <summary>Faz 8 — B2: Tevkifat kodu kuralı (salt okunur liste — onay çekmecesi açılır menüsü).</summary>
public class WithholdingRuleResponse
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>Pay/payda (ör. "9/10").</summary>
    public string Fraction { get; set; } = string.Empty;

    public static WithholdingRuleResponse FromEntity(WithholdingRule r) => new()
    {
        Code = r.Code,
        Description = r.Description,
        Fraction = r.Fraction
    };
}

/// <summary>
/// Faz 15: KKEG (binek gider kısıtı) yüzdesi — parametre merkezindeki binek.gider.kisit.oran
/// (indirilebilir %) değerinden türetilir: rate = 100 − indirilebilir. Parametre yoksa 30.
/// Fiş detayındaki "KKEG tutarı = matrah × oran" otomatik doldurma bu ucu okur.
/// </summary>
public class KkegParametersResponse
{
    /// <summary>KKEG yüzdesi (0-100; varsayılan 30).</summary>
    public decimal Rate { get; set; }
}

/// <summary>Fatura kodlama onayı: satır düzeltmeleri (boş liste = öneriler aynen onaylanır).</summary>
public class ApproveCodingRequest
{
    public List<ApproveLineRequest> Lines { get; set; } = new();
}

// ---------- Toplu onay / toplu kodlama (liste ekranı seçim kutuları) ----------

/// <summary>Toplu işlem isteği: seçilen fatura kimlikleri.</summary>
public class BulkInvoiceIdsRequest
{
    [Required(ErrorMessage = "Fatura listesi zorunludur.")]
    public List<Guid> Ids { get; set; } = new();
}

/// <summary>
/// Toplu onay özeti. Approved: onaylanan; Skipped: zaten onaylı olduğu için atlanan;
/// Failed: hata alan (diğerlerini durdurmaz, log'a yazılır).
/// </summary>
public class BulkApproveResultResponse
{
    public int Approved { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    /// <summary>Türkçe özet, ör. "42 fatura onaylandı, 3 atlandı, 2 hata."</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Toplu kodlama (onaysız) özeti — seçilen faturalar motordan geçirilir.</summary>
public class BulkCodeResultResponse
{
    public int AutoCoded { get; set; }
    public int NeedsReview { get; set; }
    /// <summary>Zaten onaylı olduğu için atlanan fatura sayısı.</summary>
    public int Skipped { get; set; }
    public int Failed { get; set; }
    /// <summary>Türkçe özet, ör. "5 fatura otomatik kodlandı, 2 onay bekliyor."</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Toplu fiş işlemi isteği: seçilen fiş kimlikleri.</summary>
public class BulkReceiptIdsRequest
{
    [Required(ErrorMessage = "Fiş listesi zorunludur.")]
    public List<Guid> Ids { get; set; } = new();
}

/// <summary>
/// Toplu silme özeti. Deleted: silinen; Skipped: durumu silmeye uygun olmadığı için atlanan
/// (fişte onaylı/reddedilmiş kayıtlar); Failed: bulunamayan veya hata alan kayıt sayısı.
/// </summary>
public class BulkDeleteResultResponse
{
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    /// <summary>Türkçe özet, ör. "5 fatura silindi, 1 hata."</summary>
    public string Message { get; set; } = string.Empty;
}

// ---------- Çoklu mükellef toplu akışlar (senkron / dönem kodlama) ----------

/// <summary>
/// Toplu senkron isteği (sync-bulk): ClientIds boşsa tüm aktif (arşivlenmemiş) mükellefler.
/// From/To verilmezse son 30 gün (tekil senkron ucuyla aynı varsayılan).
/// </summary>
public class BulkSyncRequest
{
    public List<Guid> ClientIds { get; set; } = new();
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

/// <summary>
/// Toplu senkron özeti: senkronlar tek tek ÇALIŞTIRILMAZ, mevcut "FaturaSync" kuyruk işine
/// (OutboundJob) mükellef başına bir kayıt açılır — 50 mükellefte istek zaman aşımına düşmez.
/// SkippedNoIntegration: entegrasyonu olmadığı için sessizce atlanan mükellef sayısı.
/// </summary>
public class BulkSyncResultResponse
{
    public int Queued { get; set; }
    public int SkippedNoIntegration { get; set; }
    /// <summary>Türkçe özet, ör. "8 mükellef için senkron kuyruğa alındı, 2 mükellefte entegrasyon yok."</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Toplu dönem kodlama isteği (code-bulk): ClientIds boşsa tüm aktif mükellefler.</summary>
public class BulkCodePeriodRequest
{
    public List<Guid> ClientIds { get; set; } = new();
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

/// <summary>code-bulk: tek mükellefin kodlama kırılımı (yalnız faturası olan mükellefler listelenir).</summary>
public class CodeBulkClientResultResponse
{
    public Guid ClientId { get; set; }
    public string ClientTitle { get; set; } = string.Empty;
    public int Total { get; set; }
    public int AutoCoded { get; set; }
    public int NeedsReview { get; set; }
}

/// <summary>code-bulk özeti: mükellef başına kırılım + toplamlar.</summary>
public class CodeBulkResultResponse
{
    public List<CodeBulkClientResultResponse> Clients { get; set; } = new();
    public int Total { get; set; }
    public int AutoCoded { get; set; }
    public int NeedsReview { get; set; }
    /// <summary>Türkçe özet, ör. "59 fatura kodlandı (47 otomatik, 12 onay bekliyor)."</summary>
    public string Message { get; set; } = string.Empty;
}

// ---------- Onay kaldırma (fatura + fiş ortak özet) ----------

/// <summary>
/// Toplu onay kaldırma özeti. Reverted: Approved → NeedsReview'a döndürülen; Skipped: onaylı
/// olmadığı için atlanan; Failed: bulunamayan (başka tenant'a ait) kayıt sayısı.
/// Kod değerlerine ve öğrenme kayıtlarına (AccountMemory/VendorDefault) DOKUNULMAZ.
/// </summary>
public class BulkUnapproveResultResponse
{
    public int Reverted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    /// <summary>Türkçe özet, ör. "5 faturanın onayı kaldırıldı, 2 atlandı (onaylı değil)."</summary>
    public string Message { get; set; } = string.Empty;
}

// ---------- Kodlama kuralları (Katman 2) ----------

public class CodingRuleSaveRequest
{
    [Required(ErrorMessage = "Desen zorunludur.")]
    [MaxLength(500, ErrorMessage = "Desen en fazla 500 karakter olabilir.")]
    public string Pattern { get; set; } = string.Empty;

    [Required(ErrorMessage = "Hesap kodu zorunludur.")]
    [RegularExpression(@"^\d{3}(\.\d{1,4}){0,3}$", ErrorMessage = "Geçerli bir hesap kodu girin (ör. 770.01).")]
    public string AccountCode { get; set; } = string.Empty;

    [Range(0, 1000, ErrorMessage = "Öncelik 0 ile 1000 arasında olmalıdır.")]
    public int Priority { get; set; }

    /// <summary>null = tenant geneli (tüm mükellefler).</summary>
    public Guid? ClientId { get; set; }

    public bool IsActive { get; set; } = true;
}

public class CodingRuleResponse
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    /// <summary>Kapsam etiketi: mükellef unvanı ya da null (tüm mükellefler).</summary>
    public string? ClientTitle { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }

    public static CodingRuleResponse FromEntity(CodingRule r, string? clientTitle) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        ClientTitle = clientTitle,
        Pattern = r.Pattern,
        AccountCode = r.AccountCode,
        Priority = r.Priority,
        IsActive = r.IsActive,
        CreatedAt = r.CreatedAt
    };
}

// ---------- Mükellef kodlama ayarları ----------

public class ClientCodingSettingsUpdateRequest
{
    [Required(ErrorMessage = "Varsayılan gider hesabı zorunludur.")]
    [RegularExpression(@"^\d{3}(\.\d{1,4}){0,3}$", ErrorMessage = "Geçerli bir varsayılan gider hesabı girin (ör. 770.99).")]
    public string DefaultExpenseAccount { get; set; } = "770.99";

    [Required(ErrorMessage = "Cari ön eki zorunludur.")]
    [RegularExpression(@"^\d{3}$", ErrorMessage = "Cari ön eki 3 haneli olmalıdır (ör. 320).")]
    public string CariPrefix { get; set; } = "320";

    [Range(1, 999999, ErrorMessage = "Cari sıra numarası 1 ile 999999 arasında olmalıdır.")]
    public int CariNextNumber { get; set; } = 1;

    [Range(0.5, 1.0, ErrorMessage = "Otomatik uygulama eşiği 0,5 ile 1,0 arasında olmalıdır.")]
    public double AutoApplyThreshold { get; set; } = 0.85;

    /// <summary>Faz 8 (B3): mükellefin binek aracı var mı? (akaryakıt KKEG kuralının anahtarı)</summary>
    public bool HasCompanyVehicle { get; set; }
}

public class ClientCodingSettingsResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string DefaultExpenseAccount { get; set; } = string.Empty;
    public string CariPrefix { get; set; } = string.Empty;
    public int CariNextNumber { get; set; }
    public double AutoApplyThreshold { get; set; }
    /// <summary>Faz 8 (B3): mükellefin binek aracı var mı?</summary>
    public bool HasCompanyVehicle { get; set; }

    public static ClientCodingSettingsResponse FromEntity(ClientCodingSettings s) => new()
    {
        Id = s.Id,
        ClientId = s.ClientId,
        DefaultExpenseAccount = s.DefaultExpenseAccount,
        CariPrefix = s.CariPrefix,
        CariNextNumber = s.CariNextNumber,
        AutoApplyThreshold = s.AutoApplyThreshold,
        HasCompanyVehicle = s.HasCompanyVehicle
    };
}

// ---------- Luca dışa aktarma ----------

/// <summary>İndirme öncesi önizleme: seçilen aralıktaki onaylanmış fatura + fiş sayısı ve toplamları.</summary>
public class LucaExportPreviewResponse
{
    public int ApprovedCount { get; set; }
    public decimal TotalAmount { get; set; }
    /// <summary>Aralıktaki onaylanmış fiş sayısı (Faz 3 — Luca dosyasına fiş belge türüyle dahil edilir).</summary>
    public int ApprovedReceiptCount { get; set; }
    public decimal ReceiptTotalAmount { get; set; }
    /// <summary>
    /// Fiş Aktarım Şablonu zorunlu alan ön-kontrolü: eksik alanlı belgelerin Türkçe listesi.
    /// Liste doluysa dışa aktarma sunucu tarafında ENGELLENİR (hangi belge/alan eksik gösterilir).
    /// </summary>
    public List<string> MissingFields { get; set; } = new();
}

// ---------- Faz 3: Fiş OCR ----------

/// <summary>
/// Faz 16: Oran bazlı KDV kırılım satırı — hem yanıt (ReceiptResponse.VatBreakdown) hem
/// onay düzeltmesi (ApproveReceiptRequest.VatBreakdown) için ortak DTO.
/// </summary>
public class ReceiptVatBreakdownDto
{
    [Range(0, 100, ErrorMessage = "KDV oranı 0 ile 100 arasında olmalıdır.")]
    public decimal VatRate { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "Matrah negatif olamaz.")]
    public decimal NetAmount { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "KDV tutarı negatif olamaz.")]
    public decimal VatAmount { get; set; }
}

public class ReceiptResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    /// <summary>Liste ekranındaki mükellef etiketi (controller doldurur; detayda null olabilir).</summary>
    public string? ClientTitle { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }
    /// <summary>Fiş/belge numarası (Z no, fiş no) — Luca şablonunda Evrak No kolonuna gider.</summary>
    public string? DocumentNumber { get; set; }
    public DateTime? ReceiptDate { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal? VatRate { get; set; }
    public decimal? VatAmount { get; set; }
    public decimal? NetAmount { get; set; }
    // ---------- Faz 16: Çok oranlı KDV + iskonto + Z raporu ----------
    /// <summary>"Fis" (gider fişi) | "ZRaporu" (yazar kasa günlük kapanış — gelir).</summary>
    public string DocumentKind { get; set; } = nameof(Domain.ReceiptDocumentKind.Fis);
    /// <summary>Fişteki toplam iskonto/indirim (pozitif); yoksa null.</summary>
    public decimal? DiscountTotal { get; set; }
    /// <summary>Oran bazlı KDV kırılımı (oran / matrah / KDV). Boş liste olabilir.</summary>
    public List<ReceiptVatBreakdownDto> VatBreakdown { get; set; } = new();
    /// <summary>Z raporu: nakit tahsilat toplamı (KDV dahil); fişlerde null.</summary>
    public decimal? CashTotal { get; set; }
    /// <summary>Z raporu: kart tahsilat toplamı (KDV dahil); fişlerde null.</summary>
    public decimal? CardTotal { get; set; }
    public string? SuggestedAccountCode { get; set; }
    public string? AccountCode { get; set; }
    public string? CariCode { get; set; }
    public double? Confidence { get; set; }
    public bool? ExpenseEligible { get; set; }
    public string? IneligibleReason { get; set; }
    public string? RejectReason { get; set; }
    // ---------- Faz 8 — B3: KKEG ----------
    public bool IsKkeg { get; set; }
    public string? KkegReason { get; set; }
    /// <summary>KKEG tutarı (matrahın indirilemeyen kısmı, KDV hariç).</summary>
    public decimal? KkegAmount { get; set; }
    // ---------- Faz 13: Araç plakası ----------
    /// <summary>Fişten okunan normalize plaka (ör. "34FPC630"); okunamadıysa null.</summary>
    public string? PlateNumber { get; set; }
    /// <summary>"Yok" (plaka okunmadı) | "Eslesti" | "Eslesmedi".</summary>
    public string PlateMatch { get; set; } = nameof(PlateMatchStatus.Yok);
    /// <summary>
    /// Fişe ait Türkçe uyarılar (plaka uyarıları + Faz 14 kuyruk OCR hata uyarısı; boş liste olabilir).
    /// </summary>
    public List<string> Warnings { get; set; } = new();
    /// <summary>
    /// "Otomatik onaya hazır" rozeti: onay bekleyen, güveni eşik üstü ve gider gösterilebilir fiş.
    /// Fişte TAM otomatik onay yoktur — bu yalnızca hızlı onay için görsel işarettir.
    /// </summary>
    public bool AutoApproveReady { get; set; }
    /// <summary>Belge tarihi 30 günden eskiyse true (uyarı işareti; gider gösterilebilirlik etkilenmez).</summary>
    public bool IsOldDocument { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ReceiptResponse FromEntity(Domain.Receipt r, string? clientTitle = null) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        ClientTitle = clientTitle,
        FileName = r.FileName,
        Status = r.Status.ToString(),
        VendorName = r.VendorName,
        VendorTaxId = r.VendorTaxId,
        DocumentNumber = r.DocumentNumber,
        ReceiptDate = r.ReceiptDate,
        TotalAmount = r.TotalAmount,
        VatRate = r.VatRate,
        VatAmount = r.VatAmount,
        NetAmount = r.NetAmount,
        DocumentKind = r.DocumentKind.ToString(),
        DiscountTotal = r.DiscountTotal,
        // Kırılım yalnız Include ile yüklendiyse dolar (liste ucunda boş kalması normaldir).
        VatBreakdown = (r.VatBreakdowns ?? new List<Domain.ReceiptVatBreakdown>())
            .OrderByDescending(b => b.VatRate)
            .Select(b => new ReceiptVatBreakdownDto
            {
                VatRate = b.VatRate,
                NetAmount = b.NetAmount,
                VatAmount = b.VatAmount
            })
            .ToList(),
        CashTotal = r.CashTotal,
        CardTotal = r.CardTotal,
        SuggestedAccountCode = r.SuggestedAccountCode,
        AccountCode = r.AccountCode,
        CariCode = r.CariCode,
        Confidence = r.Confidence,
        ExpenseEligible = r.ExpenseEligible,
        IneligibleReason = r.IneligibleReason,
        RejectReason = r.RejectReason,
        IsKkeg = r.IsKkeg,
        KkegReason = r.KkegReason,
        KkegAmount = r.KkegAmount,
        PlateNumber = r.PlateNumber,
        PlateMatch = r.PlateMatch.ToString(),
        Warnings = BuildWarnings(r),
        AutoApproveReady = r.Status == ReceiptStatus.NeedsReview
                           && r.ExpenseEligible != false
                           && (r.Confidence ?? 0) >= Services.ReceiptService.AutoApproveReadyThreshold,
        IsOldDocument = r.ReceiptDate.HasValue
                        && r.ReceiptDate.Value.Date < DateTime.UtcNow.Date.AddDays(-Services.ReceiptService.OldDocumentWarningDays),
        CreatedAt = r.CreatedAt
    };

    /// <summary>Uyarı listesi: Faz 14 kuyruk OCR hatası (varsa) + Faz 13 plaka uyarısı (varsa).</summary>
    private static List<string> BuildWarnings(Domain.Receipt r)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.OcrWarning)) warnings.Add(r.OcrWarning);
        if (!string.IsNullOrWhiteSpace(r.PlateWarning)) warnings.Add(r.PlateWarning);
        return warnings;
    }
}

/// <summary>
/// Faz 14 (B): Aylık OCR kota durumu. used = bu takvim ayında oluşturulmuş fiş sayısı
/// (tenant geneli); unlimited=true ise denetim yok (plansız eski kayıt veya sınırsız kota).
/// </summary>
public class ReceiptQuotaResponse
{
    public int Used { get; set; }
    public int Quota { get; set; }
    public bool Unlimited { get; set; }
}

/// <summary>Çoklu yüklemede tek dosyanın sonucu (dosya bazlı Türkçe hata mesajı).</summary>
public class ReceiptUploadItemResponse
{
    public string FileName { get; set; } = string.Empty;
    public bool Success { get; set; }
    /// <summary>Başarısızsa kullanıcıya gösterilecek Türkçe mesaj.</summary>
    public string? Message { get; set; }
    public ReceiptResponse? Receipt { get; set; }
}

public class ReceiptUploadResponse
{
    /// <summary>Ör. "3 fiş yüklendi ve işlendi, 1 dosya hata verdi."</summary>
    public string Message { get; set; } = string.Empty;
    public List<ReceiptUploadItemResponse> Items { get; set; } = new();
}

public class ReceiptPageResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<ReceiptResponse> Items { get; set; } = new();
}

/// <summary>
/// Fiş onayı: yalnızca dolu gönderilen alanlar OCR değerini ezer (düzeltme).
/// AccountCode boşsa mevcut öneri kullanılır.
/// </summary>
public class ApproveReceiptRequest
{
    [MaxLength(300, ErrorMessage = "Satıcı adı en fazla 300 karakter olabilir.")]
    public string? VendorName { get; set; }

    [RegularExpression(@"^\d{10}(\d)?$", ErrorMessage = "Vergi/TC kimlik numarası 10 veya 11 haneli olmalıdır.")]
    public string? VendorTaxId { get; set; }

    /// <summary>Fiş/belge numarası düzeltmesi (Luca Evrak No). Boş gönderilirse mevcut değer korunur.</summary>
    [MaxLength(60, ErrorMessage = "Belge numarası en fazla 60 karakter olabilir.")]
    public string? DocumentNumber { get; set; }

    public DateTime? ReceiptDate { get; set; }

    [Range(0.01, 100_000_000, ErrorMessage = "Toplam tutar 0'dan büyük olmalıdır.")]
    public decimal? TotalAmount { get; set; }

    [Range(0, 100, ErrorMessage = "KDV oranı 0 ile 100 arasında olmalıdır.")]
    public decimal? VatRate { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "KDV tutarı negatif olamaz.")]
    public decimal? VatAmount { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "Matrah negatif olamaz.")]
    public decimal? NetAmount { get; set; }

    // ---------- Faz 16: iskonto + oran bazlı kırılım düzeltmesi ----------
    /// <summary>Toplam iskonto düzeltmesi; 0 gönderilirse alan temizlenir, null = dokunma.</summary>
    [Range(0, 100_000_000, ErrorMessage = "İskonto tutarı negatif olamaz.")]
    public decimal? DiscountTotal { get; set; }

    /// <summary>
    /// Oran bazlı KDV kırılımı düzeltmesi. null = dokunma; dolu liste = satırlar birebir
    /// değiştirilir ve matrah/KDV/oran alanları kırılım toplamlarından türetilir.
    /// </summary>
    public List<ReceiptVatBreakdownDto>? VatBreakdown { get; set; }

    [RegularExpression(@"^\d{3}(\.\d{1,4}){0,3}$", ErrorMessage = "Geçerli bir hesap kodu girin (ör. 770.01).")]
    public string? AccountCode { get; set; }

    [RegularExpression(@"^\d{3}(\.\d{1,4}){0,3}$", ErrorMessage = "Geçerli bir cari kodu girin (ör. 320.001).")]
    public string? CariCode { get; set; }

    /// <summary>
    /// Gider gösterilemez işaretli fişi SMMM sorumluluğu üstlenerek yine de onaylar
    /// ("Yine de gider göster"). Varsayılan false — uygunsuz fiş onayı engellenir.
    /// </summary>
    public bool OverrideEligibility { get; set; }

    // ---------- Faz 8 — B3: KKEG düzeltmesi ----------
    /// <summary>null = dokunma; false = KKEG işaretini kaldır; true = işaretle.</summary>
    public bool? IsKkeg { get; set; }

    [MaxLength(300, ErrorMessage = "KKEG gerekçesi en fazla 300 karakter olabilir.")]
    public string? KkegReason { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "KKEG tutarı negatif olamaz.")]
    public decimal? KkegAmount { get; set; }
}

public class RejectReceiptRequest
{
    [Required(ErrorMessage = "Red gerekçesi zorunludur.")]
    [MaxLength(300, ErrorMessage = "Red gerekçesi en fazla 300 karakter olabilir.")]
    public string Reason { get; set; } = string.Empty;
}

// ---------- Faz 4: e-Tebligat ----------

public class ETebligatSyncResponse
{
    public int Added { get; set; }
    public int SkippedDuplicates { get; set; }
    public int ClientCount { get; set; }
    /// <summary>Ör. "3 yeni tebligat alındı (5 mükellef tarandı)."</summary>
    public string Message { get; set; } = string.Empty;
}

public class ETebligatItemResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string? ClientTitle { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; }
    public DateTime? LegalDeadline { get; set; }
    public string? DeadlineType { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    /// <summary>Son güne kalan gün (yukarı yuvarlanır; 0 ve altı = süre doldu, null = süresiz).</summary>
    public int? DaysRemaining { get; set; }
    /// <summary>Kalan süre ≤ 3 gün (süresi geçmişler dahil) — kırmızı sayaç.</summary>
    public bool IsUrgent { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ETebligatItemResponse FromEntity(ETebligatMessage m, string? clientTitle)
    {
        int? daysRemaining = m.LegalDeadline is null
            ? null
            : (int)Math.Ceiling((m.LegalDeadline.Value.Date - DateTime.UtcNow.Date).TotalDays);

        return new ETebligatItemResponse
        {
            Id = m.Id,
            ClientId = m.ClientId,
            ClientTitle = clientTitle,
            Sender = m.Sender,
            Subject = m.Subject,
            DeliveredAt = m.DeliveredAt,
            LegalDeadline = m.LegalDeadline,
            DeadlineType = m.DeadlineType,
            IsRead = m.IsRead,
            ReadAt = m.ReadAt,
            DaysRemaining = daysRemaining,
            IsUrgent = daysRemaining is not null && daysRemaining <= 3,
            CreatedAt = m.CreatedAt
        };
    }
}

/// <summary>
/// Tek pano yanıtı: özet sayaçlar filtreden bağımsız (yalnız mükellef seçimine göre)
/// hesaplanır ki üstteki kartlar filtre değişince kaybolmasın.
/// </summary>
public class ETebligatListResponse
{
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
    /// <summary>Kalan süre ≤ 3 gün olan (süresi geçmişler dahil) tebligat sayısı.</summary>
    public int UrgentCount { get; set; }
    public List<ETebligatItemResponse> Items { get; set; } = new();
}

// ---------- Faz 5: Abonelik / Ödeme ----------

public class SubscribeRequest
{
    [Required(ErrorMessage = "Paket seçimi zorunludur.")]
    public Guid PlanId { get; set; }

    /// <summary>
    /// Mock sağlayıcı test kartı (SAKLANMAZ): "4111..." başarı, "4000..." başarısızlık senaryosu.
    /// Boş bırakılabilir (mock'ta başarılı sayılır).
    /// </summary>
    [RegularExpression(@"^\d{12,19}$", ErrorMessage = "Kart numarası 12-19 haneli rakamlardan oluşmalıdır.")]
    public string? MockCardNumber { get; set; }
}

public class BillingSummaryResponse
{
    /// <summary>Abonelik kaydı henüz yoksa false (Faz 5 öncesi tenant'lar).</summary>
    public bool HasSubscription { get; set; }
    public string? PlanCode { get; set; }
    public string? PlanName { get; set; }
    public decimal? MonthlyPrice { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    /// <summary>Deneme süresinden kalan gün (deneme değilse veya süre dolduysa null/0).</summary>
    public int? TrialDaysRemaining { get; set; }
    /// <summary>İptal talebi verildiyse dolu; abonelik dönem sonunda kapanacak demektir.</summary>
    public DateTime? CanceledAt { get; set; }
    public bool CancelsAtPeriodEnd { get; set; }
}

public class SubscribeResponse
{
    public string Message { get; set; } = string.Empty;
    /// <summary>Yönlendirmeli sağlayıcıda (iyzico) ödeme sayfası adresi; mock'ta null.</summary>
    public string? RedirectUrl { get; set; }
    public BillingSummaryResponse Summary { get; set; } = new();
}

public class BillingInvoiceResponse
{
    public Guid Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime IssuedAt { get; set; }
    /// <summary>PDF üretimi sonraki fazda; şimdilik hep null.</summary>
    public string? PdfPath { get; set; }

    public static BillingInvoiceResponse FromEntity(BillingInvoice i) => new()
    {
        Id = i.Id,
        Number = i.Number,
        Amount = i.Amount,
        VatAmount = i.VatAmount,
        TotalAmount = i.TotalAmount,
        IssuedAt = i.IssuedAt,
        PdfPath = i.PdfPath
    };
}

public class PaymentTransactionResponse
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? ProviderRef { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }

    public static PaymentTransactionResponse FromEntity(PaymentTransaction t) => new()
    {
        Id = t.Id,
        Amount = t.Amount,
        Currency = t.Currency,
        Status = t.Status.ToString(),
        Provider = t.Provider,
        ProviderRef = t.ProviderRef,
        FailureReason = t.FailureReason,
        CreatedAt = t.CreatedAt
    };
}

/// <summary>Süper admin gelir özeti (blueprint M7 — gelir/tahsilat panosu).</summary>
public class AdminBillingOverviewResponse
{
    /// <summary>İçinde bulunulan takvim ayında başarılı tahsilat toplamı (KDV dahil, TRY).</summary>
    public decimal MonthlyRevenue { get; set; }
    /// <summary>Tüm zamanların başarılı tahsilat toplamı (KDV dahil, TRY).</summary>
    public decimal TotalRevenue { get; set; }
    public int ActiveCount { get; set; }
    public int TrialCount { get; set; }
    public int PastDueCount { get; set; }
    public int CanceledCount { get; set; }
    /// <summary>Bu ay kesilen abonelik faturası sayısı.</summary>
    public int MonthlyInvoiceCount { get; set; }
}

// ---------- Faz 5: e-Beyanname ----------

public class PrepareDeclarationRequest
{
    [Required(ErrorMessage = "Mükellef seçimi zorunludur.")]
    public Guid ClientId { get; set; }

    [Required(ErrorMessage = "Beyanname türü zorunludur.")]
    [EnumDataType(typeof(DeclarationType), ErrorMessage = "Geçersiz beyanname türü (KDV1, KDV2, Muhtasar, Damga).")]
    public DeclarationType Type { get; set; }

    [Required(ErrorMessage = "Dönem zorunludur.")]
    [RegularExpression(@"^\d{4}-(0[1-9]|1[0-2])$", ErrorMessage = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).")]
    public string Period { get; set; } = string.Empty;
}

/// <summary>KDV1 özeti — Services.Kdv1Summary tip olarak doğrudan taşınır (camelCase JSON).</summary>
public class DeclarationResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string? ClientTitle { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? PackageRef { get; set; }
    public decimal? TahakkukAmount { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>KDV1 taslak hesap özeti (kırılımlı); diğer türlerde/bozuk kayıtta null.</summary>
    public Services.Kdv1Summary? KdvSummary { get; set; }
    /// <summary>Faz 8 (B2): KDV2 (sorumlu sıfatıyla) taslak özeti; tür KDV2 değilse null.</summary>
    public Services.Kdv2Summary? Kdv2Summary { get; set; }
    public DateTime PreparedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public static DeclarationResponse FromEntity(Declaration d, string? clientTitle, bool includeSummary) => new()
    {
        Id = d.Id,
        ClientId = d.ClientId,
        ClientTitle = clientTitle,
        Type = d.Type.ToString(),
        Period = d.Period,
        Status = d.Status.ToString(),
        PackageRef = d.PackageRef,
        TahakkukAmount = d.TahakkukAmount,
        ErrorMessage = d.ErrorMessage,
        KdvSummary = includeSummary && d.Type == DeclarationType.KDV1
            ? Services.DeclarationService.ParseSummary(d.SummaryJson)
            : null,
        Kdv2Summary = includeSummary && d.Type == DeclarationType.KDV2
            ? Services.DeclarationService.ParseKdv2Summary(d.SummaryJson)
            : null,
        PreparedAt = d.PreparedAt,
        SentAt = d.SentAt,
        ApprovedAt = d.ApprovedAt,
        CreatedAt = d.CreatedAt
    };
}

/// <summary>Faz 8 — B5: "Yoksay" isteği.</summary>
public class DismissFindingRequest
{
    [Required(ErrorMessage = "Bulgu anahtarı zorunludur.")]
    [MaxLength(64, ErrorMessage = "Bulgu anahtarı en fazla 64 karakter olabilir.")]
    public string FindingKey { get; set; } = string.Empty;
}

/// <summary>İşlem yanıtı: güncel beyanname + kullanıcıya gösterilecek Türkçe mesaj.</summary>
public class DeclarationActionResponse
{
    public string Message { get; set; } = string.Empty;
    public DeclarationResponse Declaration { get; set; } = new();
}

// ---------- Faz 7 — E1: Mükellef belge portalı ----------

/// <summary>Login ÖNCESİ jenerik yanıt: unvan sızdırılmaz (linki ele geçiren kimlik öğrenemez).</summary>
public class PortalInfoResponse
{
    public bool IsActive { get; set; }
}

public class PortalLoginRequest
{
    [Required(ErrorMessage = "PIN zorunludur.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "PIN 6 haneli rakamlardan oluşmalıdır.")]
    public string Pin { get; set; } = string.Empty;
}

/// <summary>Portal bekleyen belge talebi (mükellefin gördüğü sade satır).</summary>
public class PortalRequestItemResponse
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public DateTime CreatedAt { get; set; }

    public static PortalRequestItemResponse FromEntity(DocumentRequest r) => new()
    {
        Id = r.Id,
        Title = r.Title,
        DueDate = r.DueDate,
        CreatedAt = r.CreatedAt
    };
}

/// <summary>Başarılı PIN girişi: kısa ömürlü portal token'ı + unvan (login SONRASI verilir).</summary>
public class PortalLoginResponse
{
    public string Token { get; set; } = string.Empty;
    public string ClientTitle { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public List<PortalRequestItemResponse> PendingRequests { get; set; } = new();
}

/// <summary>Portal çoklu yüklemede tek dosyanın sonucu (dosya bazlı Türkçe mesaj).</summary>
public class PortalUploadItemResult
{
    public string FileName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string Category { get; set; } = string.Empty;
}

public class PortalUploadResultResponse
{
    /// <summary>Ör. "3 belge alındı, 1 dosya hata verdi."</summary>
    public string Message { get; set; } = string.Empty;
    public List<PortalUploadItemResult> Items { get; set; } = new();
}

/// <summary>SMMM tarafı: portal erişim durumu. PIN burada ASLA dönmez.</summary>
public class PortalAccessResponse
{
    public bool Exists { get; set; }
    public bool IsActive { get; set; }
    public string? Token { get; set; }
    /// <summary>Web uygulamasındaki portal yolu (ör. "/portal/abc123..."); UI tam linki oluşturur.</summary>
    public string? PortalPath { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? LastAccessAt { get; set; }
}

/// <summary>Erişim üretim/yenileme yanıtı: PIN YALNIZCA burada, bir kez döner.</summary>
public class PortalAccessCreatedResponse
{
    public string Token { get; set; } = string.Empty;
    public string PortalPath { get; set; } = string.Empty;
    /// <summary>6 haneli PIN — DB'de yalnızca hash saklanır; bu yanıttan sonra bir daha gösterilemez.</summary>
    public string Pin { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class DocumentRequestCreateRequest
{
    [Required(ErrorMessage = "Talep başlığı zorunludur.")]
    [MaxLength(300, ErrorMessage = "Talep başlığı en fazla 300 karakter olabilir.")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Son yükleme tarihi (opsiyonel).</summary>
    public DateTime? DueDate { get; set; }
}

public class DocumentRequestResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Guid? FulfilledUploadId { get; set; }

    public static DocumentRequestResponse FromEntity(DocumentRequest r) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        Title = r.Title,
        DueDate = r.DueDate,
        Status = r.Status.ToString(),
        CreatedAt = r.CreatedAt,
        FulfilledUploadId = r.FulfilledUploadId
    };
}

/// <summary>SMMM "gelen belgeler" satırı; fiş kategorisindekiler Receipt linkiyle gösterilir.</summary>
public class PortalUploadResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string? ClientTitle { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime UploadedAt { get; set; }
    /// <summary>Fis kategorisi OCR hattına aktarıldıysa oluşan fişin Id'si ("Fişlerde görüntüle" linki).</summary>
    public Guid? ProcessedReceiptId { get; set; }

    public static PortalUploadResponse FromEntity(PortalUpload u, string? clientTitle = null) => new()
    {
        Id = u.Id,
        ClientId = u.ClientId,
        ClientTitle = clientTitle,
        FileName = u.FileName,
        Category = u.Category.ToString(),
        Note = u.Note,
        UploadedAt = u.UploadedAt,
        ProcessedReceiptId = u.ProcessedReceiptId
    };
}

// ---------- Faz 7 — E2: Vergi takvimi ----------

public class GenerateCalendarRequest
{
    [Required(ErrorMessage = "Dönem zorunludur.")]
    [RegularExpression(@"^\d{4}-(0[1-9]|1[0-2])$", ErrorMessage = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).")]
    public string Period { get; set; } = string.Empty;
}

public class ManualObligationCreateRequest
{
    [Required(ErrorMessage = "Mükellef seçimi zorunludur.")]
    public Guid ClientId { get; set; }

    [Required(ErrorMessage = "Yükümlülük türü zorunludur.")]
    [MaxLength(100, ErrorMessage = "Yükümlülük türü en fazla 100 karakter olabilir.")]
    public string Type { get; set; } = string.Empty;

    [Required(ErrorMessage = "Dönem zorunludur.")]
    [RegularExpression(@"^\d{4}-(0[1-9]|1[0-2])$", ErrorMessage = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).")]
    public string Period { get; set; } = string.Empty;

    [Required(ErrorMessage = "Son gün zorunludur.")]
    public DateTime DueDate { get; set; }
}

public class TaxObligationResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string? ClientTitle { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? CompletedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    /// <summary>Son güne kalan gün (tebligat sayacıyla aynı mantık; 0 ve altı = geçti).</summary>
    public int DaysRemaining { get; set; }
    /// <summary>Bekliyor + son günü geçmiş → geciken (kırmızı).</summary>
    public bool IsOverdue { get; set; }
    public DateTime CreatedAt { get; set; }

    public static TaxObligationResponse FromEntity(TaxObligation o, string? clientTitle)
    {
        var daysRemaining = (int)Math.Ceiling((o.DueDate.Date - DateTime.UtcNow.Date).TotalDays);

        return new TaxObligationResponse
        {
            Id = o.Id,
            ClientId = o.ClientId,
            ClientTitle = clientTitle,
            Type = o.Type,
            Period = o.Period,
            DueDate = o.DueDate,
            Status = o.Status.ToString(),
            CompletedAt = o.CompletedAt,
            Source = o.Source.ToString(),
            DaysRemaining = daysRemaining,
            IsOverdue = o.Status == ObligationStatus.Bekliyor && daysRemaining < 0,
            CreatedAt = o.CreatedAt
        };
    }
}

/// <summary>Tek pano yanıtı: özet sayaçlar onlyPending filtresinden bağımsız hesaplanır (tebligat deseni).</summary>
public class TaxCalendarListResponse
{
    public int TotalCount { get; set; }
    public int PendingCount { get; set; }
    /// <summary>Bekleyen + son günü geçmiş yükümlülük sayısı (kırmızı kart).</summary>
    public int OverdueCount { get; set; }
    public List<TaxObligationResponse> Items { get; set; } = new();
}

public class GenerateCalendarResultResponse
{
    /// <summary>Ör. "24 yükümlülük oluşturuldu (8 mükellef), 3 mevcut kayıt atlandı."</summary>
    public string Message { get; set; } = string.Empty;
    public int Added { get; set; }
    public int SkippedDuplicates { get; set; }
    public int AutoCompleted { get; set; }
}

// ---------- Faz 7 — E3: Sertifika süre takibi ----------

public class CertificateSaveRequest
{
    /// <summary>null = SMMM'nin (ofisin) kendi sertifikası.</summary>
    public Guid? ClientId { get; set; }

    [Required(ErrorMessage = "Sertifika türü zorunludur.")]
    [EnumDataType(typeof(CertificateType), ErrorMessage = "Geçersiz sertifika türü (EImza, MaliMuhur, Kep).")]
    public CertificateType Type { get; set; }

    [Required(ErrorMessage = "Sahip açıklaması zorunludur.")]
    [MaxLength(200, ErrorMessage = "Sahip açıklaması en fazla 200 karakter olabilir.")]
    public string Owner { get; set; } = string.Empty;

    [Required(ErrorMessage = "Geçerlilik sonu tarihi zorunludur.")]
    public DateTime ExpiresAt { get; set; }

    [MaxLength(500, ErrorMessage = "Not en fazla 500 karakter olabilir.")]
    public string? Note { get; set; }
}

public class CertificateResponse
{
    public Guid Id { get; set; }
    /// <summary>null = SMMM'nin (ofisin) kendi sertifikası.</summary>
    public Guid? ClientId { get; set; }
    public string? ClientTitle { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string? Note { get; set; }
    /// <summary>Bitişe kalan gün (0 ve altı = süresi doldu).</summary>
    public int DaysRemaining { get; set; }
    public DateTime CreatedAt { get; set; }

    public static CertificateResponse FromEntity(ClientCertificate c, string? clientTitle) => new()
    {
        Id = c.Id,
        ClientId = c.ClientId,
        ClientTitle = clientTitle,
        Type = c.Type.ToString(),
        Owner = c.Owner,
        ExpiresAt = c.ExpiresAt,
        Note = c.Note,
        DaysRemaining = (int)Math.Ceiling((c.ExpiresAt.Date - DateTime.UtcNow.Date).TotalDays),
        CreatedAt = c.CreatedAt
    };
}

// ---------- Faz 7 — E4: Parametre merkezi ----------

public class TaxParameterResponse
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static TaxParameterResponse FromEntity(TaxParameter p) => new()
    {
        Id = p.Id,
        Key = p.Key,
        Value = p.Value,
        Description = p.Description,
        EffectiveFrom = p.EffectiveFrom,
        UpdatedAt = p.UpdatedAt
    };
}

public class TaxParameterCreateRequest
{
    [Required(ErrorMessage = "Parametre anahtarı zorunludur.")]
    [MaxLength(150, ErrorMessage = "Parametre anahtarı en fazla 150 karakter olabilir.")]
    [RegularExpression(@"^[a-z0-9]+(\.[a-z0-9]+)*$", ErrorMessage = "Anahtar küçük harf/rakam ve nokta içerebilir (ör. kdv.oran.standart).")]
    public string Key { get; set; } = string.Empty;

    [Required(ErrorMessage = "Değer zorunludur.")]
    [MaxLength(500, ErrorMessage = "Değer en fazla 500 karakter olabilir.")]
    public string Value { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Açıklama en fazla 500 karakter olabilir.")]
    public string? Description { get; set; }

    public DateTime? EffectiveFrom { get; set; }
}

public class TaxParameterUpdateRequest
{
    [Required(ErrorMessage = "Değer zorunludur.")]
    [MaxLength(500, ErrorMessage = "Değer en fazla 500 karakter olabilir.")]
    public string Value { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Açıklama en fazla 500 karakter olabilir.")]
    public string? Description { get; set; }

    public DateTime? EffectiveFrom { get; set; }
}

// ---------- Faz 11 — E5: e-Defter berat ön-kontrol ----------

/// <summary>e-Defter dönem kaydı satırı (durum + geçmiş tablosu).</summary>
public class EDefterPeriodResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string? ClientTitle { get; set; }
    public string Period { get; set; } = string.Empty;
    /// <summary>Hazirlaniyor / KontrolTamam / BeratYuklendi / Hata.</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>Durumun Türkçe etiketi (UI rozeti).</summary>
    public string StatusLabel { get; set; } = string.Empty;
    public DateTime? CheckedAt { get; set; }
    public DateTime? MarkedUploadedAt { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public static EDefterPeriodResponse FromEntity(EDefterPeriod p, string? clientTitle) => new()
    {
        Id = p.Id,
        ClientId = p.ClientId,
        ClientTitle = clientTitle,
        Period = p.Period,
        Status = p.Status.ToString(),
        StatusLabel = Services.EDefterPrecheckService.StatusLabel(p.Status),
        CheckedAt = p.CheckedAt,
        MarkedUploadedAt = p.MarkedUploadedAt,
        Note = p.Note,
        CreatedAt = p.CreatedAt
    };
}

/// <summary>"Berat yüklendi olarak işaretle" isteği (SMMM, GİB portalında yükledikten sonra).</summary>
public class EDefterMarkUploadedRequest
{
    [MaxLength(500, ErrorMessage = "Not en fazla 500 karakter olabilir.")]
    public string? Note { get; set; }
}

// ---------- Faz 11 — E6: Gönderim kuyruğu ----------

public class OutboundJobResponse
{
    public Guid Id { get; set; }
    /// <summary>Makine tipi: "BeyannameGonder" / "TebligatSync" / "FaturaSync".</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Türkçe tip etiketi (UI listesi).</summary>
    public string TypeLabel { get; set; } = string.Empty;
    /// <summary>Bekliyor / Calisiyor / Basarili / Basarisiz / Iptal.</summary>
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    /// <summary>Bir sonraki otomatik deneme zamanı (yalnızca Bekliyor'da anlamlı).</summary>
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public static OutboundJobResponse FromEntity(OutboundJob j) => new()
    {
        Id = j.Id,
        Type = j.Type,
        TypeLabel = Services.OutboundJobService.TypeLabel(j.Type),
        Status = j.Status.ToString(),
        AttemptCount = j.AttemptCount,
        MaxAttempts = j.MaxAttempts,
        NextAttemptAt = j.NextAttemptAt,
        LastError = j.LastError,
        CreatedAt = j.CreatedAt,
        CompletedAt = j.CompletedAt
    };
}

/// <summary>Kuyruk listesi: bekleyen sayısı filtreden bağımsızdır (sidebar rozeti buradan okur).</summary>
public class OutboundJobListResponse
{
    public int PendingCount { get; set; }
    public List<OutboundJobResponse> Items { get; set; } = new();
}

// ---------- Faz 11 — E7: SMMM ücret / tahsilat takibi ----------

public class ClientFeeSaveRequest
{
    [Required(ErrorMessage = "Aylık ücret zorunludur.")]
    [Range(0.01, 10_000_000, ErrorMessage = "Aylık ücret 0'dan büyük olmalıdır.")]
    public decimal MonthlyFee { get; set; }

    /// <summary>Ücretin geçerli olduğu ilk ay (boşsa bugün kabul edilir).</summary>
    public DateTime? StartDate { get; set; }

    [MaxLength(500, ErrorMessage = "Not en fazla 500 karakter olabilir.")]
    public string? Note { get; set; }
}

public class ClientFeeResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public decimal MonthlyFee { get; set; }
    public string Currency { get; set; } = "TRY";
    public DateTime StartDate { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ClientFeeResponse FromEntity(ClientFee f) => new()
    {
        Id = f.Id,
        ClientId = f.ClientId,
        MonthlyFee = f.MonthlyFee,
        Currency = f.Currency,
        StartDate = f.StartDate,
        Note = f.Note,
        CreatedAt = f.CreatedAt
    };
}

public class FeeGenerateRequest
{
    [Required(ErrorMessage = "Dönem zorunludur.")]
    [RegularExpression(@"^\d{4}-(0[1-9]|1[0-2])$", ErrorMessage = "Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).")]
    public string Period { get; set; } = string.Empty;
}

/// <summary>"Ödendi" işareti isteği (PaidAmount boşsa tahakkuk tutarı tahsil edilmiş sayılır).</summary>
public class MarkFeePaidRequest
{
    [Range(0.01, 10_000_000, ErrorMessage = "Tahsil edilen tutar 0'dan büyük olmalıdır.")]
    public decimal? PaidAmount { get; set; }
}

public class FeeInvoiceResponse
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string? ClientTitle { get; set; }
    public string Period { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    /// <summary>Bekliyor / Odendi / Gecikti.</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? Note { get; set; }
    /// <summary>Vadeye kalan gün (0 ve altı = geçti; ödenmişse anlamsız).</summary>
    public int DaysRemaining { get; set; }
    public DateTime CreatedAt { get; set; }

    public static FeeInvoiceResponse FromEntity(FeeInvoice i, string? clientTitle) => new()
    {
        Id = i.Id,
        ClientId = i.ClientId,
        ClientTitle = clientTitle,
        Period = i.Period,
        Amount = i.Amount,
        Status = i.Status.ToString(),
        DueDate = i.DueDate,
        PaidAt = i.PaidAt,
        PaidAmount = i.PaidAmount,
        Note = i.Note,
        DaysRemaining = (int)Math.Ceiling((i.DueDate.Date - DateTime.UtcNow.Date).TotalDays),
        CreatedAt = i.CreatedAt
    };
}

/// <summary>Tahsilat panosu: özet kartlar status filtresinden bağımsızdır (tebligat deseni).</summary>
public class FeeListResponse
{
    public decimal ExpectedTotal { get; set; }
    public decimal PaidTotal { get; set; }
    public decimal OverdueTotal { get; set; }
    public int TotalCount { get; set; }
    public int PaidCount { get; set; }
    public int OverdueCount { get; set; }
    public int PendingCount { get; set; }
    public List<FeeInvoiceResponse> Items { get; set; } = new();
}

// ---------- Hesap kodu arama / autocomplete + toplu içe aktarma ----------

/// <summary>
/// Hesap kodu autocomplete öneri satırı (GET api/coding/accounts).
/// Kaynak: ChartAccount (mükellef hesap planı); plan boşsa geçmişte kullanılan kodlardan
/// (AccountMemory / VendorDefault / VendorCariMap / onaylı satırlar) türetilir.
/// </summary>
public class AccountSearchItemResponse
{
    public string Code { get; set; } = string.Empty;
    /// <summary>Hesap adı (cari kodlarda satıcı unvanı); bilinmiyorsa null.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Liste ekranı yerinde düzenleme: faturanın TÜM satırlarına hesap/cari kodu uygular
/// (POST api/einvoices/{id}/set-codes). En az bir alan dolu olmalıdır. Fatura Approved ise
/// 409 döner ("Önce onayı kaldırın."); değilse durum NeedsReview olur/kalır.
/// Kod deseni onay akışından bilinçli olarak daha esnektir: SMMM hesap planlarında harfli
/// kırılımlar da (ör. "320.A01") görülür.
/// </summary>
public class SetInvoiceCodesRequest
{
    [MaxLength(30, ErrorMessage = "Hesap kodu en fazla 30 karakter olabilir.")]
    [RegularExpression(@"^[0-9A-Za-zÇĞİÖŞÜçğıöşü]{1,10}(\.[0-9A-Za-zÇĞİÖŞÜçğıöşü]{1,10}){0,5}$",
        ErrorMessage = "Geçerli bir hesap kodu girin (ör. 770.01).")]
    public string? AccountCode { get; set; }

    [MaxLength(30, ErrorMessage = "Cari kodu en fazla 30 karakter olabilir.")]
    [RegularExpression(@"^[0-9A-Za-zÇĞİÖŞÜçğıöşü]{1,10}(\.[0-9A-Za-zÇĞİÖŞÜçğıöşü]{1,10}){0,5}$",
        ErrorMessage = "Geçerli bir cari kodu girin (ör. 320.001).")]
    public string? CariCode { get; set; }
}

/// <summary>
/// Toplu cari/hesap planı içe aktarma özeti (POST api/coding/import).
/// Imported: yeni açılan kayıt; Updated: mevcut kaydı güncellenen; Skipped: boş/işlenecek
/// alanı olmayan satır; Errors: satır numarası + Türkçe neden (hatalı satır atlanır,
/// kalan satırlar işlenmeye devam eder).
/// </summary>
public class CodingImportResultResponse
{
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>Türkçe özet, ör. "12 kayıt eklendi, 3 güncellendi, 1 satır atlandı, 2 satırda hata."</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Mizan içe aktarma özeti (api/coding/import-mizan).</summary>
public class MizanImportResultResponse
{
    /// <summary>Hesap planına işlenen YAPRAK hesap sayısı.</summary>
    public int Accounts { get; set; }
    /// <summary>Cari hafızaya (VendorCariMap, unvan anahtarlı) yazılan 120.*/320.* hesap sayısı.</summary>
    public int CariCount { get; set; }
    /// <summary>Türkçe özet, ör. "121 yaprak hesap hesap planına işlendi (121 yeni), 37 cari hesap ... yazıldı."</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Geçmiş fiş listesi içe aktarma özeti (api/coding/import-fis-listesi).</summary>
public class FisListesiImportResultResponse
{
    /// <summary>İşlenen (satış hariç) fiş grubu sayısı.</summary>
    public int Vouchers { get; set; }
    /// <summary>Unvan → gider hesabı / cari kodu öğrenmesi yazılan satıcı sayısı.</summary>
    public int VendorMappings { get; set; }
    /// <summary>Hesap planına eklenen YENİ hesap kodu sayısı (KDV kırılımları dahil).</summary>
    public int Accounts { get; set; }
    /// <summary>"satış faturası" olduğu için atlanan fiş grubu sayısı.</summary>
    public int SkippedSales { get; set; }
    /// <summary>Türkçe özet.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Luca hesap planı (CSV) içe aktarma özeti (api/coding/import-hesap-plani).</summary>
public class HesapPlaniImportResultResponse
{
    /// <summary>Hesap planına işlenen AD DOLU satır sayısı (grup başlıkları dahil).</summary>
    public int Accounts { get; set; }
    /// <summary>Cari hafızaya (VKN + normalize unvan anahtarlı) yazılan 120.*/320.* satır sayısı.</summary>
    public int CariCount { get; set; }
    /// <summary>Türkçe özet.</summary>
    public string Message { get; set; } = string.Empty;
}
