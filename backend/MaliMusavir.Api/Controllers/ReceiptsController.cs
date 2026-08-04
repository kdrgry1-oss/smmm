using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 3 — Fiş OCR uçları: çoklu yükleme (multipart), liste/detay, dosya akışı,
/// onay (düzeltmelerle + öğrenme), red (gerekçeli) ve silme.
/// </summary>
[ApiController]
[Authorize]
public class ReceiptsController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxFilesPerUpload = 20;
    private const int MaxBatchReceipts = 500;

    private readonly AppDbContext _db;
    private readonly ReceiptService _receiptService;
    private readonly IFileStorage _storage;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ReceiptsController> _logger;

    public ReceiptsController(
        AppDbContext db,
        ReceiptService receiptService,
        IFileStorage storage,
        ICurrentUser currentUser,
        ILogger<ReceiptsController> logger)
    {
        _db = db;
        _receiptService = receiptService;
        _storage = storage;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// Çoklu fiş yükleme (multipart/form-data, alan adı: files). Faz 14 — ASENKRON akış:
    /// her dosya doğrulama (10 MB/uzantı/kota) sonrası temp'e yazılır, Processing durumlu
    /// fiş açılır ve "ReceiptOcr" kuyruk işi başlatılır — OCR beklenmeden HEMEN dönülür.
    /// Doğrulama/kota hataları eskisi gibi dosya bazlı anında döner; aylık kota tamamen
    /// doluysa istek 403 ile reddedilir.
    /// </summary>
    [HttpPost("api/clients/{clientId:guid}/receipts")]
    [RequestSizeLimit(220 * 1024 * 1024)] // 20 dosya × 10 MB + form payı
    public async Task<ActionResult<ReceiptUploadResponse>> Upload(
        Guid clientId,
        [FromForm] List<IFormFile> files,
        [FromForm] string? documentKind = null,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        if (files.Count == 0)
            return BadRequest(new { message = "En az bir dosya seçin." });

        // Faz 16: belge türü — "Fis" (varsayılan) veya "ZRaporu"; OCR prompt'u buna göre seçilir.
        var kind = ReceiptDocumentKind.Fis;
        if (!string.IsNullOrWhiteSpace(documentKind) &&
            !Enum.TryParse(documentKind.Trim(), ignoreCase: true, out kind))
        {
            return BadRequest(new { message = "Geçersiz belge türü. Geçerli değerler: Fis, ZRaporu." });
        }

        if (files.Count > MaxFilesPerUpload)
            return BadRequest(new { message = $"Tek seferde en fazla {MaxFilesPerUpload} dosya yükleyebilirsiniz." });

        // Faz 14 (B): kota kapısı istek başına BİR kez yüklenir; kota tamamen doluysa 403.
        // Kalan kota dosya sayısından azsa sığanlar kabul edilir (dosya bazlı red aşağıda).
        var quota = await _receiptService.GetQuotaGateAsync(client.TenantId, ct);
        if (!quota.Unlimited && quota.Remaining <= 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = quota.ExceededMessage });

        var items = new List<ReceiptUploadItemResponse>(files.Count);

        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            var outcome = await _receiptService.UploadAndEnqueueAsync(
                client, _currentUser.UserId, file.FileName, file.Length, stream, quota, ct, kind);

            items.Add(new ReceiptUploadItemResponse
            {
                FileName = outcome.FileName,
                Success = outcome.Success,
                Message = outcome.ErrorMessage,
                Receipt = outcome.Receipt is null ? null : ReceiptResponse.FromEntity(outcome.Receipt, client.Title)
            });
        }

        var ok = items.Count(i => i.Success);
        var failed = items.Count - ok;

        return Ok(new ReceiptUploadResponse
        {
            Message = failed > 0
                ? $"{ok} fiş kuyruğa alındı — arka planda okunuyor. {failed} dosya hata verdi."
                : $"{ok} fiş kuyruğa alındı — arka planda okunuyor.",
            Items = items
        });
    }

    /// <summary>
    /// Faz 14 (B): Aylık OCR kota durumu — {used, quota, unlimited}. Kullanılan sayı bu
    /// takvim ayında oluşturulmuş fiş sayısıdır (tenant geneli, mükelleften bağımsız).
    /// Plan yoksa veya kota sınırsız benzeriyse unlimited=true döner (denetim yok).
    /// </summary>
    [HttpGet("api/receipts/quota")]
    public async Task<ActionResult<ReceiptQuotaResponse>> Quota(CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
        {
            // SuperAdmin gibi tenant'sız kimlikler için denetim uygulanmaz.
            return Ok(new ReceiptQuotaResponse { Used = 0, Quota = 0, Unlimited = true });
        }

        var gate = await _receiptService.GetQuotaGateAsync(_currentUser.TenantId.Value, ct);
        return Ok(new ReceiptQuotaResponse
        {
            Used = gate.Used,
            Quota = gate.Quota,
            Unlimited = gate.Unlimited
        });
    }

    /// <summary>Fiş listesi: sayfalı, mükellef ve durum filtreli.</summary>
    [HttpGet("api/receipts")]
    public async Task<ActionResult<ReceiptPageResponse>> List(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var query = _db.Receipts.AsNoTracking().AsQueryable();

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            query = query.Where(r => r.ClientId == clientId.Value);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ReceiptStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
                return BadRequest(new { message = "Geçersiz durum filtresi." });

            query = query.Where(r => r.Status == parsedStatus);
        }

        var totalCount = await query.CountAsync(ct);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        var receipts = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // Mükellef etiketleri tek sorguda (kural listesindeki desenle aynı).
        var clientIds = receipts.Select(r => r.ClientId).Distinct().ToList();
        var titles = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        return Ok(new ReceiptPageResponse
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = receipts
                .Select(r => ReceiptResponse.FromEntity(r, titles.TryGetValue(r.ClientId, out var t) ? t : null))
                .ToList()
        });
    }

    /// <summary>Fiş detayı.</summary>
    [HttpGet("api/receipts/{id:guid}")]
    public async Task<ActionResult<ReceiptResponse>> Get(Guid id, CancellationToken ct = default)
    {
        var receipt = await _db.Receipts
            .AsNoTracking()
            .Include(r => r.Client)
            .Include(r => r.VatBreakdowns) // Faz 16: detay çekmecesindeki kırılım tablosu
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (receipt is null)
            return NotFound(new { message = "Fiş bulunamadı." });

        return Ok(ReceiptResponse.FromEntity(receipt, receipt.Client?.Title));
    }

    /// <summary>
    /// Fiş dosyasını akış olarak döner (görsel önizleme / PDF görüntüleme).
    /// Tenant izolasyonu DbContext query filter'ı ile sağlanır; yol doğrulaması IFileStorage'da.
    /// </summary>
    [HttpGet("api/receipts/{id:guid}/file")]
    public async Task<IActionResult> GetFile(Guid id, CancellationToken ct = default)
    {
        var receipt = await _db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (receipt is null)
            return NotFound(new { message = "Fiş bulunamadı." });

        if (!_storage.Exists(receipt.StoredPath))
            return NotFound(new { message = "Fiş dosyası bulunamadı. Dosya silinmiş olabilir." });

        var stream = _storage.OpenRead(receipt.StoredPath);
        // İndirme adı olarak kullanıcının özgün dosya adı verilir; tarayıcı içi önizleme desteklenir.
        return File(stream, _storage.GetContentType(receipt.FileName), receipt.FileName, enableRangeProcessing: true);
    }

    /// <summary>Onay: düzeltmeler uygulanır, öğrenme yazılır, fiş Approved olur.</summary>
    [HttpPost("api/receipts/{id:guid}/approve")]
    public async Task<ActionResult<ReceiptResponse>> Approve(
        Guid id,
        [FromBody] ApproveReceiptRequest request,
        CancellationToken ct = default)
    {
        // Faz 16: kırılım düzeltmesi satır sil/ekle yaptığından koleksiyon YÜKLÜ olmalıdır.
        var receipt = await _db.Receipts
            .Include(r => r.VatBreakdowns)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (receipt is null)
            return NotFound(new { message = "Fiş bulunamadı." });

        var outcome = await _receiptService.ApproveAsync(receipt, request, ct);
        if (!outcome.Success)
            return BadRequest(new { message = outcome.ErrorMessage ?? "Onay işlemi başarısız oldu." });

        return Ok(ReceiptResponse.FromEntity(receipt));
    }

    /// <summary>
    /// Onay kaldırma: Approved fişi onay öncesi durumuna (NeedsReview) döndürür. Alan/kod
    /// değerlerine DOKUNULMAZ (SMMM düzeltip yeniden onaylayabilir) ve onayda yazılan öğrenme
    /// kayıtları (AccountMemory/VendorDefault/VendorCariMap) GERİ ALINMAZ — hafıza bozulmaz,
    /// yeniden onayda üzerine yazılır. Fiş Luca export'a girmiş olsa bile durum geri döner
    /// (sonraki export'lara artık dahil edilmez).
    /// </summary>
    [HttpPost("api/receipts/{id:guid}/unapprove")]
    public async Task<ActionResult<ReceiptResponse>> Unapprove(Guid id, CancellationToken ct = default)
    {
        var receipt = await _db.Receipts
            .Include(r => r.Client)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (receipt is null)
            return NotFound(new { message = "Fiş bulunamadı." });

        if (receipt.Status != ReceiptStatus.Approved)
            return BadRequest(new { message = "Yalnızca onaylanmış fişin onayı kaldırılabilir." });

        receipt.Status = ReceiptStatus.NeedsReview;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Fiş onayı kaldırıldı. ReceiptId: {Id}", receipt.Id);
        return Ok(ReceiptResponse.FromEntity(receipt, receipt.Client?.Title));
    }

    /// <summary>Red: gerekçe zorunlu.</summary>
    [HttpPost("api/receipts/{id:guid}/reject")]
    public async Task<ActionResult<ReceiptResponse>> Reject(
        Guid id,
        [FromBody] RejectReceiptRequest request,
        CancellationToken ct = default)
    {
        var receipt = await _db.Receipts.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (receipt is null)
            return NotFound(new { message = "Fiş bulunamadı." });

        var outcome = await _receiptService.RejectAsync(receipt, request.Reason, ct);
        if (!outcome.Success)
            return BadRequest(new { message = outcome.ErrorMessage ?? "Red işlemi başarısız oldu." });

        return Ok(ReceiptResponse.FromEntity(receipt));
    }

    /// <summary>Silme: yalnızca Uploaded/NeedsReview durumundaki fişler (onaylı/reddedilmiş kayıt denetim izi için korunur).</summary>
    [HttpDelete("api/receipts/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var receipt = await _db.Receipts.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (receipt is null)
            return NotFound(new { message = "Fiş bulunamadı." });

        if (receipt.Status != ReceiptStatus.Uploaded && receipt.Status != ReceiptStatus.NeedsReview)
            return BadRequest(new { message = "Yalnızca yüklenmiş veya onay bekleyen fişler silinebilir." });

        _db.Receipts.Remove(receipt);
        await _db.SaveChangesAsync(ct);

        // Dosya en son silinir; dosya hatası kaydın silinmesini geri almaz (loglanır).
        _receiptService.TryDeleteStoredFile(receipt.StoredPath);

        return NoContent();
    }

    // ---------- Toplu işlemler (liste ekranı seçim kutuları) ----------

    /// <summary>
    /// Toplu onay: yalnızca NeedsReview durumundaki fişler DÜZELTMESİZ onaylanır (tekil onayla
    /// aynı iş mantığı — ApproveAsync: eksik matrah/KDV tamamlama + öğrenme). Uygun olmayan
    /// durumdaki fişler sessizce atlanır (skipped); onay kuralına takılanlar (tarih/tutar/hesap
    /// kodu eksik, gider gösterilemez) failed sayılır ve diğerlerini durdurmaz.
    /// </summary>
    [HttpPost("api/receipts/bulk-approve")]
    public async Task<ActionResult<BulkApproveResultResponse>> BulkApprove(
        [FromBody] BulkReceiptIdsRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { message = "Onaylanacak fiş seçilmedi." });
        if (ids.Count > MaxBatchReceipts)
            return BadRequest(new { message = $"Tek istekte en fazla {MaxBatchReceipts} fiş onaylanabilir." });

        // Global tenant filtresi: başka tenant'ın fişi sorgudan hiç dönmez → failed sayılır.
        var receipts = await _db.Receipts
            .Include(r => r.VatBreakdowns) // Faz 16: onay kuralları kırılımı okuyabilir
            .Where(r => ids.Contains(r.Id))
            .ToListAsync(ct);
        var byId = receipts.ToDictionary(r => r.Id);

        var approved = 0;
        var skipped = 0;
        var failed = 0;
        var noCorrections = new ApproveReceiptRequest(); // düzeltmesiz onay → OCR/öneri değerleri kesinleşir

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            if (!byId.TryGetValue(id, out var receipt))
            {
                failed++;
                _logger.LogWarning("Toplu fiş onayı: fiş bulunamadı veya bu tenant'a ait değil. ReceiptId: {Id}", id);
                continue;
            }

            // Yalnız onay bekleyen fişler onaylanabilir; diğer durumlar sessizce atlanır.
            if (receipt.Status != ReceiptStatus.NeedsReview)
            {
                skipped++;
                continue;
            }

            try
            {
                var outcome = await _receiptService.ApproveAsync(receipt, noCorrections, ct);
                if (outcome.Success)
                {
                    approved++;
                }
                else
                {
                    failed++;
                    _logger.LogWarning(
                        "Toplu fiş onayı: fiş onaylanamadı. ReceiptId: {Id}, Hata: {Error}",
                        receipt.Id, outcome.ErrorMessage);
                    // ApproveAsync başarısızlıkta alan tamamlaması yapmış olabilir (ör. matrah
                    // geri hesabı); sonraki fişin SaveChanges'ine sızmasın diye kayıt tazelenir.
                    await _db.Entry(receipt).ReloadAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // istek iptali yutulmaz
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Toplu fiş onayı: fiş işlenirken beklenmeyen hata. ReceiptId: {Id}", id);
            }
        }

        var message = $"{approved} fiş onaylandı";
        if (skipped > 0) message += $", {skipped} atlandı (onay bekleyen durumda değil)";
        if (failed > 0) message += $", {failed} hata";

        return Ok(new BulkApproveResultResponse
        {
            Approved = approved,
            Skipped = skipped,
            Failed = failed,
            Message = message + "."
        });
    }

    /// <summary>
    /// Toplu onay kaldırma: seçilenlerden yalnız Approved olanlar NeedsReview'a döndürülür;
    /// onaylı olmayanlar sessizce atlanır (skipped). Alan değerlerine ve öğrenme kayıtlarına
    /// dokunulmaz (tekil uçla aynı kural).
    /// </summary>
    [HttpPost("api/receipts/bulk-unapprove")]
    public async Task<ActionResult<BulkUnapproveResultResponse>> BulkUnapprove(
        [FromBody] BulkReceiptIdsRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { message = "Onayı kaldırılacak fiş seçilmedi." });
        if (ids.Count > MaxBatchReceipts)
            return BadRequest(new { message = $"Tek istekte en fazla {MaxBatchReceipts} fişin onayı kaldırılabilir." });

        // Global tenant filtresi: başka tenant'ın fişi sorgudan hiç dönmez → failed sayılır.
        var receipts = await _db.Receipts
            .Where(r => ids.Contains(r.Id))
            .ToListAsync(ct);

        var failed = ids.Count - receipts.Count;
        if (failed > 0)
        {
            _logger.LogWarning(
                "Toplu fiş onay kaldırma: {Failed} fiş bulunamadı veya bu tenant'a ait değil.", failed);
        }

        var reverted = 0;
        var skipped = 0;

        foreach (var receipt in receipts)
        {
            if (receipt.Status != ReceiptStatus.Approved)
            {
                skipped++;
                continue;
            }

            receipt.Status = ReceiptStatus.NeedsReview;
            reverted++;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Toplu fiş onay kaldırma tamamlandı. Geri alınan: {Reverted}, Atlanan: {Skipped}, Hata: {Failed}",
            reverted, skipped, failed);

        var message = $"{reverted} fişin onayı kaldırıldı";
        if (skipped > 0) message += $", {skipped} atlandı (onaylı değil)";
        if (failed > 0) message += $", {failed} hata";

        return Ok(new BulkUnapproveResultResponse
        {
            Reverted = reverted,
            Skipped = skipped,
            Failed = failed,
            Message = message + "."
        });
    }

    /// <summary>
    /// Toplu silme: yalnızca Uploaded/NeedsReview durumundaki fişler silinir (onaylı/reddedilmiş
    /// kayıt denetim izi için korunur → skipped). Tekil silme kalıbı izlenir: önce DB kayıtları
    /// tek SaveChanges ile silinir, sonra dosyalar; dosya hatası kaydın silinmesini geri almaz.
    /// </summary>
    [HttpPost("api/receipts/bulk-delete")]
    public async Task<ActionResult<BulkDeleteResultResponse>> BulkDelete(
        [FromBody] BulkReceiptIdsRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { message = "Silinecek fiş seçilmedi." });
        if (ids.Count > MaxBatchReceipts)
            return BadRequest(new { message = $"Tek istekte en fazla {MaxBatchReceipts} fiş silinebilir." });

        var receipts = await _db.Receipts
            .Where(r => ids.Contains(r.Id))
            .ToListAsync(ct);

        var failed = ids.Count - receipts.Count; // bulunamadı veya başka tenant'a ait
        if (failed > 0)
        {
            _logger.LogWarning("Toplu fiş silme: {Failed} fiş bulunamadı veya bu tenant'a ait değil.", failed);
        }

        var deletable = receipts
            .Where(r => r.Status is ReceiptStatus.Uploaded or ReceiptStatus.NeedsReview)
            .ToList();
        var skipped = receipts.Count - deletable.Count;

        _db.Receipts.RemoveRange(deletable);
        await _db.SaveChangesAsync(ct);

        // Dosyalar en son silinir; dosya hatası kaydın silinmesini geri almaz (loglanır).
        foreach (var receipt in deletable)
            _receiptService.TryDeleteStoredFile(receipt.StoredPath);

        _logger.LogInformation(
            "Toplu fiş silme tamamlandı. Silinen: {Deleted}, Atlanan: {Skipped}, Hata: {Failed}",
            deletable.Count, skipped, failed);

        var message = $"{deletable.Count} fiş silindi";
        if (skipped > 0) message += $", {skipped} atlandı (yalnız yüklenmiş/onay bekleyen fişler silinebilir)";
        if (failed > 0) message += $", {failed} hata";

        return Ok(new BulkDeleteResultResponse
        {
            Deleted = deletable.Count,
            Skipped = skipped,
            Failed = failed,
            Message = message + "."
        });
    }
}
