using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MaliMusavir.Api.Domain;
using Microsoft.IdentityModel.Tokens;

namespace MaliMusavir.Api.Services;

/// <summary>
/// Oturum açan kullanıcı için JWT üretir. Claim'ler: sub, email, role, tenant_id.
/// </summary>
public class TokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string CreateToken(AppUser user)
    {
        var key = _configuration["Jwt:Key"]
                  ?? throw new InvalidOperationException("Jwt:Key yapılandırması eksik.");
        var issuer = _configuration["Jwt:Issuer"];
        var audience = _configuration["Jwt:Audience"];
        var expiryHours = double.TryParse(_configuration["Jwt:ExpiryHours"], out var h) ? h : 12;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, user.Role.ToString())
        };

        if (user.TenantId.HasValue)
            claims.Add(new Claim("tenant_id", user.TenantId.Value.ToString()));

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(expiryHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Portal token ömrü: kısa oturum (Faz 7 — E1).</summary>
    public static readonly TimeSpan PortalTokenLifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// Mükellef portalı için kısa ömürlü (2 saat) JWT üretir (Faz 7 — E1).
    /// Claim'ler: portal=true (normal API'ler varsayılan policy ile bu claim'i REDDEDER),
    /// client_id (portal uçlarında mükellef daraltması) ve tenant_id (DbContext izolasyon filtresi).
    /// Rol claim'i YOKTUR — SuperAdmin/rol bazlı uçlara hiçbir koşulda giremez.
    /// </summary>
    public string CreatePortalToken(Guid clientId, Guid tenantId)
    {
        var key = _configuration["Jwt:Key"]
                  ?? throw new InvalidOperationException("Jwt:Key yapılandırması eksik.");
        var issuer = _configuration["Jwt:Issuer"];
        var audience = _configuration["Jwt:Audience"];

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, clientId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("portal", "true"),
            new("client_id", clientId.ToString()),
            new("tenant_id", tenantId.ToString())
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(PortalTokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
