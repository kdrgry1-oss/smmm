using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

// ---------- İstek/yanıt sözleşmeleri ----------

/// <summary>Fatura kesme isteği (panelden gelir).</summary>
public class EArsivIssueRequestDto
{
    public Guid ClientId { get; set; }
    public DateTime? IssueDate { get; set; }
    public string InvoiceType { get; set; } = "Satis";
    public string CurrencyCode { get; set; } = "TRY";
    public decimal ExchangeRate { get; set; }

    public string BuyerTaxId { get; set; } = string.Empty;
    public string? BuyerTitle { get; set; }
    public string? BuyerFirstName { get; set; }
    public string? BuyerLastName { get; set; }
    public string? TaxOffice { get; set; }
    public string? Address { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Note { get; set; }

    public List<EArsivIssueLineDto> Lines { get; set; } = new();
}

public class EArsivIssueLineDto
{
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
    public string Unit { get; set; } = "Adet";
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 20m;
    public decimal DiscountRate { get; set; }
}

public class EArsivIssueResponse
{
    public string Ettn { get; set; } = string.Empty;
    public decimal TaxExclusiveAmount { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal PayableAmount { get; set; }
    /// <summary>Kullanıcıya gösterilecek Türkçe bilgi notu.</summary>
    public string Message { get; set; } = string.Empty;
}

public class EArsivSmsStartRequest
{
    public Guid ClientId { get; set; }
}

public class EArsivSmsStartResponse
{
    public string OperationId { get; set; } = string.Empty;
    /// <summary>Maskeli telefon (son 4 hane) — kullanıcı hangi numaraya gittiğini görsün.</summary>
    public string MaskedPhone { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class EArsivSmsCompleteRequest
{
    public Guid ClientId { get; set; }
    public string SmsCode { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public List<string> Ettns { get; set; } = new();
}

/// <summary>
/// GİB e-Arşiv Portalı üzerinden FATURA KESME uçları.
///
/// AKIŞ (iki aşamalı, ikinci aşama İNSAN ONAYLI):
///   1. POST /api/earsiv/issue          → taslak oluşur, ETTN döner (resmî belge DEĞİL)
///   2. POST /api/earsiv/sms/start      → GİB mükellefin telefonuna kod gönderir
///      POST /api/earsiv/sms/complete   → kullanıcının girdiği kodla imzalanır (RESMÎ)
///
/// GÜVENLİK: SMS kodu sistem tarafından üretilmez/çözülmez — mükellefin telefonuna gider.
/// İmzalanan belge geri alınamaz; yalnız iptal talebi açılabilir.
/// </summary>
[ApiController]
[Authorize]
public class EArsivController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly EArsivInvoiceIssuer _issuer;
    private readonly ILogger<EArsivController> _logger;

    public EArsivController(
        AppDbContext db,
        ISecretProtector secretProtector,
        EArsivInvoiceIssuer issuer,
        ILogger<EArsivController> logger)
    {
        _db = db;
        _secretProtector = secretProtector;
        _issuer = issuer;
        _logger = logger;
    }

    /// <summary>Fatura taslağı oluşturur. Taslak imzalanana kadar resmî belge değildir.</summary>
    [HttpPost("api/earsiv/issue")]
    public async Task<ActionResult<EArsivIssueResponse>> Issue(
        [FromBody] EArsivIssueRequestDto request, CancellationToken ct)
    {
        var credentials = await ResolveCredentialsAsync(request.ClientId, ct);
        if (credentials.Error is not null) return BadRequest(new { message = credentials.Error });

        var loginResult = await _issuer.LoginAsync(credentials.ApiKey!, credentials.TestEnv, ct);
        if (!loginResult.Success || loginResult.Data is null)
            return BadRequest(new { message = loginResult.ErrorMessage });

        var token = loginResult.Data;
        try
        {
            var issueRequest = new EArsivIssueRequest
            {
                IssueDate = request.IssueDate ?? DateTime.Now,
                InvoiceType = request.InvoiceType,
                CurrencyCode = request.CurrencyCode,
                ExchangeRate = request.ExchangeRate,
                BuyerTaxId = request.BuyerTaxId,
                BuyerTitle = request.BuyerTitle,
                BuyerFirstName = request.BuyerFirstName,
                BuyerLastName = request.BuyerLastName,
                TaxOffice = request.TaxOffice,
                Address = request.Address,
                District = request.District,
                City = request.City,
                PostalCode = request.PostalCode,
                Email = request.Email,
                Phone = request.Phone,
                Note = request.Note,
                Lines = request.Lines.Select(l => new EArsivIssueLine
                {
                    ItemName = l.ItemName,
                    Quantity = l.Quantity,
                    Unit = l.Unit,
                    UnitPrice = l.UnitPrice,
                    VatRate = l.VatRate,
                    DiscountRate = l.DiscountRate,
                }).ToList(),
            };

            var result = await _issuer.CreateDraftAsync(token, issueRequest, credentials.TestEnv, ct);
            if (!result.Success || result.Data is null)
                return BadRequest(new { message = result.ErrorMessage });

            return Ok(new EArsivIssueResponse
            {
                Ettn = result.Data.Ettn,
                TaxExclusiveAmount = result.Data.TaxExclusiveAmount,
                TaxTotal = result.Data.TaxTotal,
                PayableAmount = result.Data.PayableAmount,
                Message = "Fatura taslağı oluşturuldu. Belge henüz RESMÎ DEĞİLDİR — " +
                          "SMS onayı verildiğinde kesinleşir.",
            });
        }
        finally
        {
            await _issuer.LogoutAsync(token, credentials.TestEnv, ct);
        }
    }

    /// <summary>SMS doğrulama kodu gönderilmesini tetikler (kod mükellefin telefonuna gider).</summary>
    [HttpPost("api/earsiv/sms/start")]
    public async Task<ActionResult<EArsivSmsStartResponse>> StartSms(
        [FromBody] EArsivSmsStartRequest request, CancellationToken ct)
    {
        var credentials = await ResolveCredentialsAsync(request.ClientId, ct);
        if (credentials.Error is not null) return BadRequest(new { message = credentials.Error });

        var loginResult = await _issuer.LoginAsync(credentials.ApiKey!, credentials.TestEnv, ct);
        if (!loginResult.Success || loginResult.Data is null)
            return BadRequest(new { message = loginResult.ErrorMessage });

        var token = loginResult.Data;
        try
        {
            var phoneResult = await _issuer.GetPhoneNumberAsync(token, credentials.TestEnv, ct);
            if (!phoneResult.Success || phoneResult.Data is null)
                return BadRequest(new { message = phoneResult.ErrorMessage });

            var startResult = await _issuer.StartSmsVerificationAsync(
                token, phoneResult.Data, credentials.TestEnv, ct);
            if (!startResult.Success || startResult.Data is null)
                return BadRequest(new { message = startResult.ErrorMessage });

            return Ok(new EArsivSmsStartResponse
            {
                OperationId = startResult.Data,
                MaskedPhone = MaskPhone(phoneResult.Data),
                Message = "Doğrulama kodu mükellefin GİB'de kayıtlı telefonuna gönderildi.",
            });
        }
        finally
        {
            await _issuer.LogoutAsync(token, credentials.TestEnv, ct);
        }
    }

    /// <summary>
    /// SMS koduyla belgeleri imzalar. BU İŞLEM GERİ ALINAMAZ — imzalanan belge resmî
    /// mali kayıttır, silinemez/değiştirilemez.
    /// </summary>
    [HttpPost("api/earsiv/sms/complete")]
    public async Task<ActionResult<object>> CompleteSms(
        [FromBody] EArsivSmsCompleteRequest request, CancellationToken ct)
    {
        var credentials = await ResolveCredentialsAsync(request.ClientId, ct);
        if (credentials.Error is not null) return BadRequest(new { message = credentials.Error });

        var loginResult = await _issuer.LoginAsync(credentials.ApiKey!, credentials.TestEnv, ct);
        if (!loginResult.Success || loginResult.Data is null)
            return BadRequest(new { message = loginResult.ErrorMessage });

        var token = loginResult.Data;
        try
        {
            var result = await _issuer.CompleteSmsVerificationAsync(
                token, request.SmsCode, request.OperationId, request.Ettns, credentials.TestEnv, ct);

            if (!result.Success)
                return BadRequest(new { message = result.ErrorMessage });

            _logger.LogInformation(
                "e-Arşiv belgeleri imzalandı. ClientId: {ClientId}, Adet: {Count}",
                request.ClientId, result.Data);

            return Ok(new
            {
                signedCount = result.Data,
                message = $"{result.Data} belge onaylandı. Belgeler artık resmîdir ve değiştirilemez.",
            });
        }
        finally
        {
            await _issuer.LogoutAsync(token, credentials.TestEnv, ct);
        }
    }

    /// <summary>VKN/TCKN'den alıcı bilgisi sorgular (fatura formunu otomatik doldurmak için).</summary>
    [HttpGet("api/earsiv/recipient")]
    public async Task<ActionResult<object>> Recipient(
        [FromQuery] Guid clientId, [FromQuery] string taxId, CancellationToken ct)
    {
        var credentials = await ResolveCredentialsAsync(clientId, ct);
        if (credentials.Error is not null) return BadRequest(new { message = credentials.Error });

        var loginResult = await _issuer.LoginAsync(credentials.ApiKey!, credentials.TestEnv, ct);
        if (!loginResult.Success || loginResult.Data is null)
            return BadRequest(new { message = loginResult.ErrorMessage });

        var token = loginResult.Data;
        try
        {
            var result = await _issuer.GetRecipientAsync(token, taxId, credentials.TestEnv, ct);
            if (!result.Success)
                return BadRequest(new { message = result.ErrorMessage });

            return Ok(result.Data);
        }
        finally
        {
            await _issuer.LogoutAsync(token, credentials.TestEnv, ct);
        }
    }

    /// <summary>İmzalanmış belge için iptal talebi açar.</summary>
    [HttpPost("api/earsiv/cancel")]
    public async Task<ActionResult<object>> Cancel(
        [FromQuery] Guid clientId, [FromQuery] string ettn, [FromQuery] string? explanation,
        CancellationToken ct)
    {
        var credentials = await ResolveCredentialsAsync(clientId, ct);
        if (credentials.Error is not null) return BadRequest(new { message = credentials.Error });

        var loginResult = await _issuer.LoginAsync(credentials.ApiKey!, credentials.TestEnv, ct);
        if (!loginResult.Success || loginResult.Data is null)
            return BadRequest(new { message = loginResult.ErrorMessage });

        var token = loginResult.Data;
        try
        {
            var result = await _issuer.CancellationRequestAsync(
                token, ettn, explanation ?? string.Empty, credentials.TestEnv, ct);

            return result.Success
                ? Ok(new { message = "İptal talebi oluşturuldu." })
                : BadRequest(new { message = result.ErrorMessage });
        }
        finally
        {
            await _issuer.LogoutAsync(token, credentials.TestEnv, ct);
        }
    }

    // ---------- Yardımcılar ----------

    private sealed record CredentialLookup(string? ApiKey, bool TestEnv, string? Error);

    /// <summary>
    /// Mükellefin e-Arşiv entegrasyonunu bulur ve şifreli kimliği çözer. Tenant izolasyonu
    /// global query filter ile sağlanır (başka tenant'ın mükellefi görünmez).
    /// </summary>
    private async Task<CredentialLookup> ResolveCredentialsAsync(Guid clientId, CancellationToken ct)
    {
        if (clientId == Guid.Empty)
            return new CredentialLookup(null, false, "Mükellef seçilmedi.");

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return new CredentialLookup(null, false, "Mükellef bulunamadı.");

        var integration = await _db.ClientIntegrations
            .AsNoTracking()
            .FirstOrDefaultAsync(ci => ci.ClientId == clientId, ct);

        if (integration is null || integration.Provider != IntegrationProvider.EArsivPortal)
        {
            return new CredentialLookup(null, false,
                "Bu mükellefte GİB e-Arşiv Portalı entegrasyonu tanımlı değil. " +
                "Mükellefler → Entegrasyon ekranından \"GİB e-Arşiv Portalı\" seçip " +
                "kullanıcı kodu ve şifresini kaydedin.");
        }

        if (string.IsNullOrWhiteSpace(integration.ApiKeyEncrypted))
            return new CredentialLookup(null, false, "e-Arşiv kimlik bilgileri kayıtlı değil.");

        string plain;
        try
        {
            plain = _secretProtector.Unprotect(integration.ApiKeyEncrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "e-Arşiv kimliği çözülemedi. ClientId: {ClientId}", clientId);
            return new CredentialLookup(null, false,
                "Kayıtlı kimlik bilgisi okunamadı. Entegrasyonu yeniden kaydedin.");
        }

        return new CredentialLookup(plain, integration.UseTestEnvironment, null);
    }

    /// <summary>Telefonu maskeler — yalnız son 4 hane gösterilir (KVKK).</summary>
    private static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? "****" : $"{new string('*', digits.Length - 4)}{digits[^4..]}";
    }
}
