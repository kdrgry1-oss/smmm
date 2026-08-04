using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>Gelen e-fatura senkronu ve gelen kutusu uçları.</summary>
[ApiController]
[Authorize]
public class EInvoicesController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxRangeDays = 366;
    private const int MaxBatchInvoices = 500;

    private readonly AppDbContext _db;
    private readonly EInvoiceSyncService _syncService;
    private readonly OutboundJobService _jobService;
    private readonly ILogger<EInvoicesController> _logger;

    public EInvoicesController(
        AppDbContext db,
        EInvoiceSyncService syncService,
        OutboundJobService jobService,
        ILogger<EInvoicesController> logger)
    {
        _db = db;
        _syncService = syncService;
        _jobService = jobService;
        _logger = logger;
    }

    /// <summary>
    /// Mükellef için gelen fatura senkronu başlatır. from/to verilmezse son 30 gün alınır.
    /// </summary>
    [HttpPost("api/clients/{clientId:guid}/einvoices/sync")]
    public async Task<ActionResult<SyncResultResponse>> Sync(
        Guid clientId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        // Varsayılan aralık: son 30 gün.
        var toDate = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1); // gün sonu dahil
        var fromDate = (from ?? toDate.AddDays(-30)).Date;
        toDate = DateTime.SpecifyKind(toDate, DateTimeKind.Utc);
        fromDate = DateTime.SpecifyKind(fromDate, DateTimeKind.Utc);

        if (fromDate > toDate)
            return BadRequest(new { message = "Başlangıç tarihi bitiş tarihinden sonra olamaz." });

        if ((toDate - fromDate).TotalDays > MaxRangeDays)
            return BadRequest(new { message = $"Tarih aralığı en fazla {MaxRangeDays} gün olabilir." });

        if (toDate.Date > DateTime.UtcNow.Date.AddDays(1))
            return BadRequest(new { message = "Bitiş tarihi gelecekte olamaz." });

        var result = await _syncService.SyncIncomingAsync(client, fromDate, toDate, ct);

        if (!result.Success)
        {
            // Entegrasyon yok / doğrulama benzeri hatalar 400; entegratör kaynaklı geçici hatalar 502.
            var message = result.ErrorMessage ?? "Senkronizasyon başarısız oldu. Lütfen tekrar deneyin.";
            return result.Retryable
                ? StatusCode(StatusCodes.Status502BadGateway, new { message })
                : BadRequest(new { message });
        }

        var successMessage = result.SkippedDuplicates > 0
            ? $"{result.Added} yeni fatura alındı, {result.SkippedDuplicates} mükerrer atlandı."
            : $"{result.Added} yeni fatura alındı.";

        // Sağlayıcı bilgi notu (ör. sayfa sınırına takıldı: "İlk 5.000 fatura alındı;
        // daha eski kayıtlar için tarih aralığını daraltın.") mesaja eklenir.
        if (!string.IsNullOrWhiteSpace(result.Warning))
            successMessage = $"{successMessage} {result.Warning}";

        return Ok(new SyncResultResponse
        {
            Added = result.Added,
            SkippedDuplicates = result.SkippedDuplicates,
            Message = successMessage
        });
    }

    /// <summary>
    /// Çoklu mükellef toplu senkron: seçilen (boşsa TÜM aktif) mükelleflerden AKTİF
    /// entegrasyonu olanlar için mevcut "FaturaSync" kuyruk işine (OutboundJob) mükellef
    /// başına kayıt açılır — senkronlar arka planda sırayla çalışır, istek zaman aşımına
    /// düşmez. Entegrasyonu olmayanlar sessizce atlanır (sayıyla raporlanır).
    /// from/to verilmezse son 30 gün (tekil senkron ucuyla aynı varsayılan).
    /// </summary>
    [HttpPost("api/einvoices/sync-bulk")]
    public async Task<ActionResult<BulkSyncResultResponse>> SyncBulk(
        [FromBody] BulkSyncRequest request,
        CancellationToken ct = default)
    {
        // Varsayılan aralık: son 30 gün (tekil Sync ucuyla birebir aynı kurallar).
        var toDate = (request.To ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1); // gün sonu dahil
        var fromDate = (request.From ?? toDate.AddDays(-30)).Date;
        toDate = DateTime.SpecifyKind(toDate, DateTimeKind.Utc);
        fromDate = DateTime.SpecifyKind(fromDate, DateTimeKind.Utc);

        if (fromDate > toDate)
            return BadRequest(new { message = "Başlangıç tarihi bitiş tarihinden sonra olamaz." });

        if ((toDate - fromDate).TotalDays > MaxRangeDays)
            return BadRequest(new { message = $"Tarih aralığı en fazla {MaxRangeDays} gün olabilir." });

        if (toDate.Date > DateTime.UtcNow.Date.AddDays(1))
            return BadRequest(new { message = "Bitiş tarihi gelecekte olamaz." });

        var requestedIds = request.ClientIds.Where(id => id != Guid.Empty).Distinct().ToList();

        // Boş liste = tüm aktif mükellefler (arşivlenmişler hariç — mevcut "tümü" kalıbı).
        var clients = requestedIds.Count == 0
            ? await _db.Clients.AsNoTracking().Where(c => !c.IsArchived).ToListAsync(ct)
            : await _db.Clients.AsNoTracking().Where(c => requestedIds.Contains(c.Id)).ToListAsync(ct);

        if (clients.Count == 0)
            return BadRequest(new { message = "Senkronlanacak mükellef bulunamadı." });

        // Entegrasyonlar tek sorguda (mükellef başına sorgu yok).
        var clientIds = clients.Select(c => c.Id).ToList();
        var integratedIds = (await _db.ClientIntegrations
                .AsNoTracking()
                .Where(ci => clientIds.Contains(ci.ClientId))
                .Select(ci => ci.ClientId)
                .ToListAsync(ct))
            .ToHashSet();

        var queued = 0;
        var skippedNoIntegration = 0;

        foreach (var client in clients.OrderBy(c => c.Title))
        {
            if (!integratedIds.Contains(client.Id))
            {
                skippedNoIntegration++;
                continue;
            }

            // Mevcut kuyruk altyapısı: "FaturaSync" işi (EnqueueAsync aynı payload'lı bekleyen
            // işi mükerrer açmaz). Worker, EInvoiceSyncService.SyncIncomingAsync'i çalıştırır.
            await _jobService.EnqueueAsync(
                client.TenantId,
                OutboundJobService.TypeFaturaSync,
                new OutboundJobService.FaturaSyncPayload { ClientId = client.Id, From = fromDate, To = toDate },
                ct);
            queued++;
        }

        var message = $"{queued} mükellef için senkron kuyruğa alındı";
        if (skippedNoIntegration > 0) message += $", {skippedNoIntegration} mükellefte entegrasyon yok";
        message += ".";

        _logger.LogInformation(
            "Toplu fatura senkronu kuyruğa alındı. Kuyruk: {Queued}, Entegrasyonsuz: {Skipped}",
            queued, skippedNoIntegration);

        return Ok(new BulkSyncResultResponse
        {
            Queued = queued,
            SkippedNoIntegration = skippedNoIntegration,
            Message = message
        });
    }

    /// <summary>
    /// Gelen kutusu: sayfalı fatura listesi. q: fatura no veya satıcı unvan/VKN araması.
    /// clientIds: çoklu mükellef filtresi (çoklu seçici); clientId tekil parametresi geriye
    /// uyumluluk için korunur — ikisi birlikte gelirse kesişim uygulanır.
    /// </summary>
    [HttpGet("api/einvoices")]
    public async Task<ActionResult<EInvoicePageResponse>> List(
        [FromQuery] Guid? clientId = null,
        [FromQuery] List<Guid>? clientIds = null,
        [FromQuery] string? status = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var query = _db.EInvoices.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
            query = query.Where(e => e.ClientId == clientId.Value);

        var clientIdSet = (clientIds ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        if (clientIdSet.Count > 0)
            query = query.Where(e => clientIdSet.Contains(e.ClientId));

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<EInvoiceStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
                return BadRequest(new { message = "Geçersiz durum filtresi." });

            query = query.Where(e => e.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(e =>
                e.InvoiceNumber.ToLower().Contains(term) ||
                e.SenderTitle.ToLower().Contains(term) ||
                e.SenderTaxId.Contains(term));
        }

        var totalCount = await query.CountAsync(ct);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        // Hesap kodu özeti + cari kolonu için satırlar sayfayla birlikte TEK sorguda çekilir
        // (Include → join; fatura başına ayrı sorgu/N+1 yok).
        var items = await query
            .Include(e => e.Lines)
            .OrderByDescending(e => e.IssueDate)
            .ThenByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new EInvoicePageResponse
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = items.Select(EInvoiceListItemResponse.FromEntity).ToList()
        });
    }

    /// <summary>Fatura detayı (kalem satırlarıyla birlikte).</summary>
    [HttpGet("api/einvoices/{id:guid}")]
    public async Task<ActionResult<EInvoiceDetailResponse>> Get(Guid id, CancellationToken ct = default)
    {
        var invoice = await _db.EInvoices
            .AsNoTracking()
            .Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (invoice is null)
            return NotFound(new { message = "Fatura bulunamadı." });

        return Ok(EInvoiceDetailResponse.FromEntityWithLines(invoice));
    }

    /// <summary>
    /// Liste ekranı yerinde düzenleme: gönderilen hesap ve/veya cari kodu faturanın TÜM
    /// satırlarına uygular. Fatura Approved ise 409 döner (önce onay kaldırılmalı); değilse
    /// kod SMMM eliyle girildiğinden fatura Onay Bekliyor (NeedsReview) durumuna geçer/kalır
    /// — öğrenme YAZILMAZ (öğrenme yalnızca onay akışındadır).
    /// </summary>
    [HttpPost("api/einvoices/{id:guid}/set-codes")]
    public async Task<ActionResult<EInvoiceDetailResponse>> SetCodes(
        Guid id,
        [FromBody] SetInvoiceCodesRequest request,
        CancellationToken ct = default)
    {
        var accountCode = request.AccountCode?.Trim();
        var cariCode = request.CariCode?.Trim();

        if (string.IsNullOrEmpty(accountCode) && string.IsNullOrEmpty(cariCode))
            return BadRequest(new { message = "Hesap kodu veya cari kodu girin." });

        var invoice = await _db.EInvoices
            .Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (invoice is null)
            return NotFound(new { message = "Fatura bulunamadı." });

        if (invoice.CodingStatus == CodingStatus.Approved)
            return Conflict(new { message = "Onaylanmış faturanın kodu değiştirilemez. Önce onayı kaldırın." });

        if (invoice.Lines.Count == 0)
            return BadRequest(new { message = "Faturada kod uygulanacak kalem satırı yok." });

        foreach (var line in invoice.Lines)
        {
            if (!string.IsNullOrEmpty(accountCode)) line.AccountCode = accountCode;
            if (!string.IsNullOrEmpty(cariCode)) line.CariCode = cariCode;
        }

        // Elle girilen kod motor önerisi değildir → SMMM onayına düşer (NeedsReview olur/kalır).
        invoice.CodingStatus = CodingStatus.NeedsReview;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Fatura kodları listeden güncellendi. EInvoiceId: {Id}, HesapKodu: {AccountCode}, CariKodu: {CariCode}",
            invoice.Id, accountCode ?? "-", cariCode ?? "-");

        return Ok(EInvoiceDetailResponse.FromEntityWithLines(invoice));
    }

    /// <summary>
    /// Toplu silme: seçilen faturalar kalem satırlarıyla birlikte silinir (EInvoiceLine
    /// cascade — bkz. AppDbContext). Global tenant filtresi: başka tenant'ın faturası sorgudan
    /// hiç dönmez → failed sayılır. Silme geri alınamaz; onay penceresi UI tarafındadır.
    /// </summary>
    [HttpPost("api/einvoices/bulk-delete")]
    public async Task<ActionResult<BulkDeleteResultResponse>> BulkDelete(
        [FromBody] BulkInvoiceIdsRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { message = "Silinecek fatura seçilmedi." });
        if (ids.Count > MaxBatchInvoices)
            return BadRequest(new { message = $"Tek istekte en fazla {MaxBatchInvoices} fatura silinebilir." });

        var invoices = await _db.EInvoices
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);

        var failed = ids.Count - invoices.Count;
        if (failed > 0)
        {
            _logger.LogWarning(
                "Toplu fatura silme: {Failed} fatura bulunamadı veya bu tenant'a ait değil.", failed);
        }

        _db.EInvoices.RemoveRange(invoices);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Toplu fatura silme tamamlandı. Silinen: {Deleted}, Hata: {Failed}",
            invoices.Count, failed);

        var message = $"{invoices.Count} fatura silindi";
        if (failed > 0) message += $", {failed} hata";
        return Ok(new BulkDeleteResultResponse
        {
            Deleted = invoices.Count,
            Skipped = 0,
            Failed = failed,
            Message = message + "."
        });
    }
}
