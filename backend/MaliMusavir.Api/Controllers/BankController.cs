using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

public class BankAccountDto
{
    public Guid Id { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string Iban { get; set; } = string.Empty;
    public string IbanFormatted { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "TRY";
    public string? AccountCode { get; set; }
    public decimal? LastBalance { get; set; }
    public DateTime? LastBalanceAt { get; set; }
    public int TransactionCount { get; set; }
    public int PendingCount { get; set; }
}

public class BankTransactionDto
{
    public Guid Id { get; set; }
    public Guid BankAccountId { get; set; }
    public string BankName { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    /// <summary>"Gelen" | "Giden".</summary>
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "TRY";
    public decimal? BalanceAfter { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyIban { get; set; }
    public string? CounterpartyTaxId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ReferenceNo { get; set; }
    public string? TransactionType { get; set; }
    public string? SuggestedAccountCode { get; set; }
    public string? AccountCode { get; set; }
    public string? CariCode { get; set; }
    public double? Confidence { get; set; }
    public string CodingStatus { get; set; } = string.Empty;
}

public class BankTransactionListResponse
{
    public List<BankTransactionDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    /// <summary>Aralıktaki toplam giriş/çıkış (filtrelenmiş kümenin tamamı üzerinden).</summary>
    public decimal TotalIncoming { get; set; }
    public decimal TotalOutgoing { get; set; }
    public int PendingCount { get; set; }
    public string? Warning { get; set; }
}

public class BankApproveRequest
{
    public string? AccountCode { get; set; }
    public string? CariCode { get; set; }
    public string? CounterpartyName { get; set; }
}

public class BankAccountUpdateRequest
{
    public string? BankName { get; set; }
    public string? AccountCode { get; set; }
}

/// <summary>
/// Banka hesap hareketleri uçları: ekstre içe aktarma, hareket listesi
/// ("kimden ne gelmiş / kime ne gitmiş"), kodlama onayı ve hesap yönetimi.
///
/// KAYNAK STRATEJİSİ: Bugün birincil yol DOSYA İÇE AKTARMADIR — her banka MT940/CSV/Excel
/// ekstre verir, hiçbir sözleşme gerektirmez. Banka kurumsal API'leri sözleşmeyle geldiğinde
/// aynı sözleşmeyi (ParsedBankLine) üreten bir adaptör eklenir; bu uçlar DEĞİŞMEZ.
/// </summary>
[ApiController]
[Authorize]
public class BankController : ControllerBase
{
    /// <summary>Tek listede döndürülecek azami hareket (UI sayfalama sınırı).</summary>
    private const int MaxListItems = 500;

    /// <summary>Ekstre dosyası azami boyutu (10 MB) ve izinli uzantılar.</summary>
    private const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly string[] AllowedExtensions =
        { ".sta", ".mt940", ".txt", ".csv", ".xlsx", ".xls" };

    private readonly AppDbContext _db;
    private readonly BankStatementService _service;

    public BankController(AppDbContext db, BankStatementService service)
    {
        _db = db;
        _service = service;
    }

    // ---------- Ekstre içe aktarma ----------

    /// <summary>Ekstre dosyası yükler (MT940 / CSV / Excel) ve hareketleri içe aktarır.</summary>
    [HttpPost("api/clients/{clientId:guid}/bank/import")]
    [RequestSizeLimit(MaxFileBytes)]
    public async Task<ActionResult<BankImportResult>> Import(
        Guid clientId,
        IFormFile file,
        [FromQuery] Guid? bankAccountId = null,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Ekstre dosyası seçilmedi." });

        if (file.Length > MaxFileBytes)
            return BadRequest(new { message = "Dosya 10 MB sınırını aşıyor. Ekstreyi tarih aralığına bölerek yükleyin." });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            return BadRequest(new
            {
                message = "Desteklenmeyen dosya türü. Kabul edilen biçimler: " +
                          "MT940 (.sta/.mt940/.txt), CSV (.csv), Excel (.xlsx).",
            });
        }

        // Ayrıştırıcı akışta konum değiştirdiği için içerik belleğe alınır (sınır 10 MB).
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var result = await _service.ImportAsync(clientId, buffer, file.FileName, bankAccountId, ct);
        return result.Success ? Ok(result) : BadRequest(new { message = result.ErrorMessage });
    }

    // ---------- Hesaplar ----------

    /// <summary>Mükellefin banka hesapları + hareket sayaçları.</summary>
    [HttpGet("api/clients/{clientId:guid}/bank/accounts")]
    public async Task<ActionResult<List<BankAccountDto>>> Accounts(Guid clientId, CancellationToken ct)
    {
        var accounts = await _db.BankAccounts
            .AsNoTracking()
            .Where(a => a.ClientId == clientId && !a.IsArchived)
            .OrderBy(a => a.BankName)
            .ToListAsync(ct);

        var ids = accounts.Select(a => a.Id).ToList();

        var counts = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => ids.Contains(t.BankAccountId))
            .GroupBy(t => t.BankAccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                Total = g.Count(),
                Pending = g.Count(t => t.CodingStatus != CodingStatus.Approved),
            })
            .ToListAsync(ct);

        return Ok(accounts.Select(a =>
        {
            var stat = counts.FirstOrDefault(c => c.AccountId == a.Id);
            return new BankAccountDto
            {
                Id = a.Id,
                BankName = a.BankName,
                Iban = a.Iban,
                IbanFormatted = BankStatementService.FormatIban(a.Iban),
                CurrencyCode = a.CurrencyCode,
                AccountCode = a.AccountCode,
                LastBalance = a.LastBalance,
                LastBalanceAt = a.LastBalanceAt,
                TransactionCount = stat?.Total ?? 0,
                PendingCount = stat?.Pending ?? 0,
            };
        }).ToList());
    }

    /// <summary>Hesabın adını / muhasebe kodunu (102.XX) günceller.</summary>
    [HttpPut("api/bank/accounts/{accountId:guid}")]
    public async Task<ActionResult<object>> UpdateAccount(
        Guid accountId, [FromBody] BankAccountUpdateRequest request, CancellationToken ct)
    {
        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return NotFound(new { message = "Banka hesabı bulunamadı." });

        if (!string.IsNullOrWhiteSpace(request.BankName))
            account.BankName = request.BankName.Trim();

        account.AccountCode = string.IsNullOrWhiteSpace(request.AccountCode)
            ? null
            : request.AccountCode.Trim();

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Banka hesabı güncellendi." });
    }

    // ---------- Hareketler ----------

    /// <summary>
    /// Hareket listesi. Filtreler: hesap, tarih aralığı, yön, kodlama durumu, serbest arama
    /// (karşı taraf adı / IBAN / açıklama / referans).
    /// </summary>
    [HttpGet("api/clients/{clientId:guid}/bank/transactions")]
    public async Task<ActionResult<BankTransactionListResponse>> Transactions(
        Guid clientId,
        [FromQuery] Guid? accountId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? direction = null,
        [FromQuery] string? status = null,
        [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        var query = _db.BankTransactions.AsNoTracking().Where(t => t.ClientId == clientId);

        if (accountId.HasValue && accountId.Value != Guid.Empty)
            query = query.Where(t => t.BankAccountId == accountId.Value);

        if (from.HasValue) query = query.Where(t => t.TransactionDate >= from.Value.Date);
        if (to.HasValue) query = query.Where(t => t.TransactionDate <= to.Value.Date);

        if (string.Equals(direction, "Gelen", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.Direction == BankDirection.Gelen);
        else if (string.Equals(direction, "Giden", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.Direction == BankDirection.Giden);

        if (string.Equals(status, "Bekleyen", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.CodingStatus != CodingStatus.Approved);
        else if (string.Equals(status, "Onayli", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.CodingStatus == CodingStatus.Approved);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(t =>
                (t.CounterpartyName != null && EF.Functions.ILike(t.CounterpartyName, $"%{term}%")) ||
                (t.CounterpartyIban != null && EF.Functions.ILike(t.CounterpartyIban, $"%{term}%")) ||
                (t.CounterpartyTaxId != null && t.CounterpartyTaxId == term) ||
                EF.Functions.ILike(t.Description, $"%{term}%") ||
                (t.ReferenceNo != null && EF.Functions.ILike(t.ReferenceNo, $"%{term}%")));
        }

        // Özet rakamlar filtrelenmiş kümenin TAMAMI üzerinden hesaplanır (sayfa değil).
        var totalCount = await query.CountAsync(ct);
        var totalIncoming = await query
            .Where(t => t.Direction == BankDirection.Gelen)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
        var totalOutgoing = await query
            .Where(t => t.Direction == BankDirection.Giden)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
        var pendingCount = await query.CountAsync(t => t.CodingStatus != CodingStatus.Approved, ct);

        var accountNames = await _db.BankAccounts
            .AsNoTracking()
            .Where(a => a.ClientId == clientId)
            .ToDictionaryAsync(a => a.Id, a => a.BankName, ct);

        var items = await query
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.CreatedAt)
            .Take(MaxListItems)
            .ToListAsync(ct);

        var response = new BankTransactionListResponse
        {
            TotalCount = totalCount,
            TotalIncoming = totalIncoming,
            TotalOutgoing = totalOutgoing,
            PendingCount = pendingCount,
            Items = items.Select(t => new BankTransactionDto
            {
                Id = t.Id,
                BankAccountId = t.BankAccountId,
                BankName = accountNames.GetValueOrDefault(t.BankAccountId, string.Empty),
                TransactionDate = t.TransactionDate,
                Direction = t.Direction.ToString(),
                Amount = t.Amount,
                CurrencyCode = t.CurrencyCode,
                BalanceAfter = t.BalanceAfter,
                CounterpartyName = t.CounterpartyName,
                CounterpartyIban = t.CounterpartyIban,
                CounterpartyTaxId = t.CounterpartyTaxId,
                Description = t.Description,
                ReferenceNo = t.ReferenceNo,
                TransactionType = t.TransactionType,
                SuggestedAccountCode = t.SuggestedAccountCode,
                AccountCode = t.AccountCode,
                CariCode = t.CariCode,
                Confidence = t.Confidence,
                CodingStatus = t.CodingStatus.ToString(),
            }).ToList(),
        };

        if (totalCount > MaxListItems)
        {
            response.Warning = $"{totalCount} hareketten en yeni {MaxListItems} tanesi listeleniyor. " +
                               "Tarih aralığını daraltarak tamamını görebilirsiniz.";
        }

        return Ok(response);
    }

    /// <summary>Hareketin hesap/cari kodunu onaylar ve karşı taraf hafızasına öğretir.</summary>
    [HttpPost("api/bank/transactions/{transactionId:guid}/approve")]
    public async Task<ActionResult<object>> Approve(
        Guid transactionId, [FromBody] BankApproveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AccountCode) && string.IsNullOrWhiteSpace(request.CariCode))
            return BadRequest(new { message = "En az bir hesap kodu veya cari kod girin." });

        var ok = await _service.ApproveAsync(
            transactionId, request.AccountCode, request.CariCode, request.CounterpartyName, ct);

        return ok
            ? Ok(new { message = "Hareket onaylandı ve bu karşı taraf için öğrenildi." })
            : NotFound(new { message = "Hareket bulunamadı." });
    }

    /// <summary>İçe aktarma geçmişi (denetim izi).</summary>
    [HttpGet("api/clients/{clientId:guid}/bank/imports")]
    public async Task<ActionResult<object>> Imports(Guid clientId, CancellationToken ct)
    {
        var imports = await _db.BankStatementImports
            .AsNoTracking()
            .Where(i => i.ClientId == clientId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(50)
            .Select(i => new
            {
                i.Id,
                i.FileName,
                i.Format,
                i.ParsedCount,
                i.ImportedCount,
                i.SkippedDuplicates,
                i.PeriodFrom,
                i.PeriodTo,
                i.Warning,
                i.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(imports);
    }
}
