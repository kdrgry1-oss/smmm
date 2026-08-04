using System.Security.Claims;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// İstek başına oturum açan kullanıcının kimlik/tenant bilgisini JWT claim'lerinden çözer.
/// DbContext tenant izolasyon filtresi ve controller'lar bunu kullanır.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? TenantId { get; }
    string? Email { get; }
    bool IsSuperAdmin { get; }
    bool IsAuthenticated { get; }

    // ---------- Faz 7 (E1): mükellef portal oturumu ----------
    /// <summary>Kısa ömürlü portal token'ı mı (claim: portal=true)? Normal API'lere erişemez.</summary>
    bool IsPortal { get; }
    /// <summary>Portal oturumunun bağlı olduğu mükellef (claim: client_id).</summary>
    Guid? PortalClientId { get; }
}

public class CurrentUser : ICurrentUser
{
    public CurrentUser(IHttpContextAccessor accessor)
    {
        var principal = accessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return;

        IsAuthenticated = true;
        Email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");

        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (Guid.TryParse(sub, out var uid)) UserId = uid;

        var tid = principal.FindFirstValue("tenant_id");
        if (Guid.TryParse(tid, out var t)) TenantId = t;

        var role = principal.FindFirstValue(ClaimTypes.Role) ?? principal.FindFirstValue("role");
        IsSuperAdmin = string.Equals(role, "SuperAdmin", StringComparison.OrdinalIgnoreCase);

        // Portal oturumu (E1): tenant_id claim'i DbContext izolasyon filtresini besler,
        // client_id ise PortalController'da mükellef daraltması için kullanılır.
        IsPortal = string.Equals(principal.FindFirstValue("portal"), "true", StringComparison.OrdinalIgnoreCase);
        var cid = principal.FindFirstValue("client_id");
        if (Guid.TryParse(cid, out var c)) PortalClientId = c;
    }

    public Guid? UserId { get; }
    public Guid? TenantId { get; }
    public string? Email { get; }
    public bool IsSuperAdmin { get; }
    public bool IsAuthenticated { get; }
    public bool IsPortal { get; }
    public Guid? PortalClientId { get; }
}
