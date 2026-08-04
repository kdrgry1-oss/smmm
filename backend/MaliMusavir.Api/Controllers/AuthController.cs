using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TokenService _tokenService;
    private readonly ICurrentUser _currentUser;
    private readonly SubscriptionService _subscriptionService;

    public AuthController(
        AppDbContext db,
        TokenService tokenService,
        ICurrentUser currentUser,
        SubscriptionService subscriptionService)
    {
        _db = db;
        _tokenService = tokenService;
        _currentUser = currentUser;
        _subscriptionService = subscriptionService;
    }

    /// <summary>Yeni mali müşavir ofisi (tenant) + sahip kullanıcı kaydı. Deneme (trial) abonelik ile başlar.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        // Kayıt/giriş sırasında henüz tenant bağlamı olmadığından filtre devre dışı bırakılır.
        var emailTaken = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email);
        if (emailTaken)
            return Conflict(new { message = "Bu e-posta adresi zaten kayıtlı." });

        var trialPlan = await _db.Plans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Code == "baslangic" && p.IsActive);

        var tenant = new Tenant
        {
            Name = request.FirmName.Trim(),
            Email = email,
            PlanId = trialPlan?.Id,
            SubscriptionStatus = SubscriptionStatus.Trial
        };

        var user = new AppUser
        {
            TenantId = tenant.Id,
            Tenant = tenant,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FullName = request.FullName.Trim(),
            Role = UserRole.TenantOwner,
            IsActive = true
        };

        _db.Tenants.Add(tenant);
        _db.Users.Add(user);

        // Faz 5: 14 günlük deneme aboneliği otomatik başlar (Subscription tablosu tek gerçek kaynak).
        _subscriptionService.StartTrial(tenant, trialPlan);

        await _db.SaveChangesAsync();

        var token = _tokenService.CreateToken(user);
        return Ok(new AuthResponse
        {
            Token = token,
            Role = user.Role.ToString(),
            TenantId = user.TenantId,
            FullName = user.FullName
        });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { message = "E-posta veya şifre hatalı." });

        if (!user.IsActive)
            return Unauthorized(new { message = "Hesabınız devre dışı bırakılmış. Lütfen destek ekibiyle iletişime geçin." });

        // Faz 5: yenileme kontrolü login'de tetiklenir (süresi geçen abonelik PastDue'ya düşer).
        if (user.TenantId is not null)
            await _subscriptionService.CheckAndUpdateStatusAsync(user.TenantId.Value);

        var token = _tokenService.CreateToken(user);
        return Ok(new AuthResponse
        {
            Token = token,
            Role = user.Role.ToString(),
            TenantId = user.TenantId,
            FullName = user.FullName
        });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeResponse>> Me()
    {
        if (_currentUser.UserId is null)
            return Unauthorized(new { message = "Oturum bilgisi çözümlenemedi. Lütfen tekrar giriş yapın." });

        // Faz 5: panel özet çağrısı (her panel açılışında) yenileme kontrolünü de tetikler.
        if (_currentUser.TenantId is not null)
            await _subscriptionService.CheckAndUpdateStatusAsync(_currentUser.TenantId.Value);

        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Tenant)
            .ThenInclude(t => t!.Plan)
            .FirstOrDefaultAsync(u => u.Id == _currentUser.UserId.Value);

        if (user is null)
            return Unauthorized(new { message = "Kullanıcı bulunamadı. Lütfen tekrar giriş yapın." });

        return Ok(new MeResponse
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.Role.ToString(),
            TenantId = user.TenantId,
            TenantName = user.Tenant?.Name,
            PlanCode = user.Tenant?.Plan?.Code,
            PlanName = user.Tenant?.Plan?.Name
        });
    }
}
