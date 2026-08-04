namespace MaliMusavir.Api.Domain;

public enum UserRole { SuperAdmin = 0, TenantOwner = 1, TenantStaff = 2 }

public enum SubscriptionStatus { Trial = 0, Active = 1, PastDue = 2, Canceled = 3 }

/// <summary>Mali müşavir firması = kiracı (tenant).</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? TaxNumber { get; set; }
    public string Email { get; set; } = string.Empty;
    public Guid? PlanId { get; set; }
    public Plan? Plan { get; set; }
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Trial;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AppUser> Users { get; set; } = new List<AppUser>();
    public ICollection<Client> Clients { get; set; } = new List<Client>();
}

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; } // SuperAdmin için null
    public Tenant? Tenant { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.TenantOwner;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;   // baslangic / profesyonel / ofis
    public string Name { get; set; } = string.Empty;
    public decimal MonthlyPrice { get; set; }
    public int MaxClients { get; set; }
    public int MaxUsers { get; set; }
    public int MonthlyOcrQuota { get; set; }
    public bool ETebligat { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Faz 18 — MÜŞAVİR (SMMM) GİB KİMLİĞİ: mali müşavirin KENDİ GİB Dijital Vergi Dairesi
/// kullanıcı kodu/şifresi TENANT düzeyinde bir kez saklanır. GİB "müşavir/mükellef
/// işlemleri" özelliğiyle TEK oturum üzerinden müşavire bağlı tüm mükelleflerin
/// ekranlarına geçilebilir — mükellef başına ayrı şifre/captcha GEREKMEZ.
///
/// Şifre SecretProtector (AES-256) ile şifreli saklanır; düz metin ASLA yazılmaz,
/// yanıtlarda dönmez, loglanmaz.
///
/// DİKKAT: EnsureCreated mevcut veritabanına YENİ TABLO EKLEMEZ — dağıtımda rapor
/// notundaki CREATE TABLE SQL'i elle çalıştırılmalıdır.
/// </summary>
public class TenantGibCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Tenant başına TEK kayıt (unique index).</summary>
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    /// <summary>Müşavirin GİB kullanıcı kodu / TCKN (gizli değildir; maskeli gösterilir).</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>Müşavirin GİB şifresi — SecretProtector ile AES-256 şifreli.</summary>
    public string PasswordEncrypted { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Mükellef (mali müşavirin çalıştığı firma).</summary>
public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string Title { get; set; } = string.Empty;   // Unvan
    public string TaxId { get; set; } = string.Empty;   // VKN / TCKN
    public string? TaxOffice { get; set; }              // Vergi dairesi
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool EInvoiceEnabled { get; set; }           // e-Fatura mükellefi mi
    public bool EArchiveEnabled { get; set; }           // e-Arşiv mükellefi mi
    public string? IntegratorName { get; set; }         // Nilvera, Uyumsoft vb.
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
