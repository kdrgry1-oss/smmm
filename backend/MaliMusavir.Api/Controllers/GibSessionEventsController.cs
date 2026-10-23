using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MaliMusavir.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace MaliMusavir.Api.Controllers;

[ApiController]
[Route("internal/gib/session-expired")]
public sealed class GibSessionEventsController(IConfiguration configuration) : ControllerBase
{
    public sealed record ExpiredSession(string ClientKey);
    [AllowAnonymous]
    [HttpPost]
    public IActionResult Expired([FromBody] ExpiredSession request)
    {
        var expected = configuration["ROBOT_TOKEN"];
        var supplied = Request.Headers["X-Robot-Token"].ToString();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))) return Unauthorized();
        if (request.ClientKey is null || !Regex.IsMatch(request.ClientKey, "^[a-f0-9]{64}$")) return BadRequest();
        return GibSessionRenewalWorker.Enqueue(request.ClientKey) ? Accepted() : StatusCode(503);
    }
}
