namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Faz 11 (E6): Arka plan işçisinin (OutboundJobWorker) tenant kimliği. HTTP isteği olmayan
/// scope'larda CurrentUser kimliksiz kalır ve DbContext tenant filtresi HER ŞEYİ gizler;
/// worker, iş kaydındaki TenantId ile bu sınıfı etkinleştirir ve scope içindeki tüm servisler
/// (DeclarationService, EInvoiceSyncService...) o tenant'ın verisini normal filtrelerle görür.
///
/// DİKKAT: ActAsTenant, scope içinde AppDbContext/servisler ÇÖZÜLMEDEN ÖNCE çağrılmalıdır
/// (AppDbContext, ICurrentUser'ı kurucusunda okur). Program.cs'teki ICurrentUser fabrikası,
/// bu sınıf etkinse HTTP tabanlı CurrentUser yerine bunu döndürür.
/// </summary>
public class WorkerCurrentUser : ICurrentUser
{
    /// <summary>ActAsTenant çağrıldı mı? (false ise ICurrentUser fabrikası CurrentUser'a düşer)</summary>
    public bool IsActive { get; private set; }

    public Guid? UserId => null;
    public Guid? TenantId { get; private set; }
    public string? Email => null;
    public bool IsSuperAdmin => false;
    public bool IsAuthenticated => IsActive;
    public bool IsPortal => false;
    public Guid? PortalClientId => null;

    /// <summary>Scope'u verilen tenant adına çalışır hale getirir (yalnızca worker kullanır).</summary>
    public void ActAsTenant(Guid tenantId)
    {
        IsActive = true;
        TenantId = tenantId;
    }
}
