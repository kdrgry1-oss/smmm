using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 7 — E1: Mükellef belge portalı uçları (mükellef tarafı, üyeliksiz hafif akış).
/// Kimlik: link token'ı + 6 haneli PIN → 2 saatlik portal-scope JWT (claim: portal=true).
/// Portal token'ı normal API'lere ERİŞEMEZ (varsayılan authorization policy portal claim'ini
/// reddeder); buradaki korumalı uçlar ise yalnızca "PortalOnly" policy ile açılır.
/// Anonim uçlarda tenant bağlamı olmadığından sorgular IgnoreQueryFilters + token daraltması kullanır.
/// </summary>
[ApiController]
[Route("api/portal")]
public class PortalController : ControllerBase
{
    private const int MaxFilesPerUpload = 20;

    private readonly AppDbContext _db;
    private readonly TokenService _tokenService;
    private readonly PortalRateLimiter _rateLimiter;
    private readonly ReceiptService _receiptService;
    private readonly IFileStorage _storage;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<PortalController> _logger;

    public PortalController(
        AppDbContext db,
        TokenService tokenService,
        PortalRateLimiter rateLimiter,
        ReceiptService receiptService,
        IFileStorage storage,
        ICurrentUser currentUser,
        ILogger<PortalController> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _rateLimiter = rateLimiter;
        _receiptService = receiptService;
        _storage = storage;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// Login ÖNCESİ jenerik bilgi: yalnızca linkin geçerli/aktif olduğunu söyler.
    /// Mükellef unvanı BİLİNÇLİ olarak dönmez — unvan ancak doğru PIN sonrası verilir
    /// (linki ele geçiren biri kimin portalı olduğunu öğrenememelidir).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{token}/info")]
    public async Task<ActionResult<PortalInfoResponse>> Info(string token, CancellationToken ct = default)
    {
        var access = await FindAccessAsync(token, ct);
        if (access is null || !access.IsActive)
            return NotFound(new { message = "Bu portal bağlantısı geçerli değil. Lütfen mali müşavirinizle iletişime geçin." });

        return Ok(new PortalInfoResponse { IsActive = true });
    }

    /// <summary>
    /// PIN girişi → kısa ömürlü (2 saat) portal token'ı + unvan + bekleyen belge talepleri.
    /// Kaba kuvvet koruması: 5 yanlış denemede 15 dakika kilit (bellek içi).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("{token}/login")]
    public async Task<ActionResult<PortalLoginResponse>> Login(
        string token,
        [FromBody] PortalLoginRequest request,
        CancellationToken ct = default)
    {
        if (_rateLimiter.IsLocked(token, out var remainingMinutes))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"Çok fazla yanlış deneme yapıldı. Lütfen {remainingMinutes} dakika sonra tekrar deneyin."
            });
        }

        var access = await _db.ClientPortalAccesses
            .IgnoreQueryFilters() // anonim istek: tenant bağlamı yok, token benzersiz anahtardır
            .Include(a => a.Client)
            .FirstOrDefaultAsync(a => a.Token == token, ct);

        if (access is null || !access.IsActive || access.Client is null)
            return NotFound(new { message = "Bu portal bağlantısı geçerli değil. Lütfen mali müşavirinizle iletişime geçin." });

        if (!BCrypt.Net.BCrypt.Verify(request.Pin, access.PinHash))
        {
            var lockedNow = _rateLimiter.RegisterFailure(token);
            _logger.LogWarning("Portal yanlış PIN denemesi. ClientId: {ClientId}, Kilit: {Locked}", access.ClientId, lockedNow);

            return Unauthorized(new
            {
                message = lockedNow
                    ? "Çok fazla yanlış deneme yapıldı. Giriş 15 dakika kilitlendi."
                    : "PIN hatalı. Lütfen mali müşavirinizin verdiği 6 haneli PIN'i kontrol edin."
            });
        }

        _rateLimiter.RegisterSuccess(token);
        access.LastAccessAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var pendingRequests = await _db.DocumentRequests
            .IgnoreQueryFilters() // anonim bağlam; ClientId daraltması güvenliği sağlar
            .AsNoTracking()
            .Where(r => r.ClientId == access.ClientId && r.Status == DocumentRequestStatus.Bekliyor)
            .OrderBy(r => r.DueDate == null)
            .ThenBy(r => r.DueDate)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);

        _logger.LogInformation("Portal girişi başarılı. ClientId: {ClientId}", access.ClientId);

        return Ok(new PortalLoginResponse
        {
            Token = _tokenService.CreatePortalToken(access.ClientId, access.Client.TenantId),
            ClientTitle = access.Client.Title,
            ExpiresAt = DateTime.UtcNow.Add(TokenService.PortalTokenLifetime),
            PendingRequests = pendingRequests.Select(PortalRequestItemResponse.FromEntity).ToList()
        });
    }

    /// <summary>Bekleyen belge talepleri (portal oturumu; yükleme sonrası liste tazelenir).</summary>
    [Authorize(Policy = "PortalOnly")]
    [HttpGet("requests")]
    public async Task<ActionResult<List<PortalRequestItemResponse>>> Requests(CancellationToken ct = default)
    {
        if (_currentUser.PortalClientId is null)
            return Forbid();

        // Portal token'ı tenant_id claim'i taşır → tenant izolasyon filtresi normal çalışır.
        var pending = await _db.DocumentRequests
            .AsNoTracking()
            .Where(r => r.ClientId == _currentUser.PortalClientId.Value
                        && r.Status == DocumentRequestStatus.Bekliyor)
            .OrderBy(r => r.DueDate == null)
            .ThenBy(r => r.DueDate)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);

        return Ok(pending.Select(PortalRequestItemResponse.FromEntity).ToList());
    }

    /// <summary>
    /// Çoklu belge yükleme (multipart/form-data; alanlar: files, category, note?, documentRequestId?).
    /// Fis kategorisi Faz 14'te ARKA PLAN OCR kuyruğuna girer: fiş Processing durumunda hemen
    /// oluşturulur, okuma kuyrukta yapılır ve SMMM onay kuyruğuna düşer. Kuyruğa alınamayan
    /// (doğrulama/kota) fiş dosyası KAYBEDİLMEZ — portal deposuna kaydedilir, SMMM elle inceler.
    /// Diğer kategoriler yalnızca saklanır. Açık talep seçildiyse ilk başarılı yükleme talebi
    /// "Yuklendi" yapar.
    /// </summary>
    [Authorize(Policy = "PortalOnly")]
    [HttpPost("upload")]
    [RequestSizeLimit(220 * 1024 * 1024)] // fiş yüklemesiyle aynı sınır: 20 dosya × 10 MB + form payı
    public async Task<ActionResult<PortalUploadResultResponse>> Upload(
        [FromForm] List<IFormFile> files,
        [FromForm] string category,
        [FromForm] string? note = null,
        [FromForm] Guid? documentRequestId = null,
        CancellationToken ct = default)
    {
        if (_currentUser.PortalClientId is null)
            return Forbid();

        var client = await _db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == _currentUser.PortalClientId.Value, ct);
        if (client is null)
            return NotFound(new { message = "Portal kaydınız bulunamadı. Lütfen mali müşavirinizle iletişime geçin." });

        if (!Enum.TryParse<PortalUploadCategory>(category?.Trim(), ignoreCase: true, out var parsedCategory))
            return BadRequest(new { message = "Geçersiz belge türü. Fiş, Fatura, Banka Ekstresi, Sözleşme veya Diğer seçin." });

        if (files.Count == 0)
            return BadRequest(new { message = "En az bir dosya seçin." });

        if (files.Count > MaxFilesPerUpload)
            return BadRequest(new { message = $"Tek seferde en fazla {MaxFilesPerUpload} dosya yükleyebilirsiniz." });

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var items = new List<PortalUploadItemResult>(files.Count);
        var savedUploads = new List<PortalUpload>();

        // Faz 14 (B): Fis kategorisinde kota kapısı istek başına BİR kez yüklenir
        // (kota aşan fişler kuyruğa alınmaz ama belge portal deposuna yine de kaydedilir).
        var quota = parsedCategory == PortalUploadCategory.Fis
            ? await _receiptService.GetQuotaGateAsync(client.TenantId, ct)
            : null;

        foreach (var file in files)
        {
            var safeName = Path.GetFileName(file.FileName);

            // Fis kategorisi: Faz 14 — arka plan OCR kuyruğuna köprü (fiş dosya kuralları geçerli).
            if (parsedCategory == PortalUploadCategory.Fis)
            {
                await using var receiptStream = file.OpenReadStream();
                var outcome = await _receiptService.UploadAndEnqueueAsync(
                    client, uploadedByUserId: null, safeName, file.Length, receiptStream, quota, ct);

                if (outcome.Success && outcome.Receipt is not null)
                {
                    var upload = new PortalUpload
                    {
                        TenantId = client.TenantId,
                        ClientId = client.Id,
                        FileName = outcome.Receipt.FileName,
                        // Fişle aynı dosya, çift kopya yok. Yükleme anında bu yol temp'tir;
                        // kuyruk işi kalıcıya taşıyınca bağlı kaydın yolu da eşitlenir
                        // (ReceiptService.SyncLinkedPortalUploadsAsync).
                        StoredPath = outcome.Receipt.StoredPath,
                        Category = parsedCategory,
                        Note = trimmedNote,
                        ProcessedReceiptId = outcome.Receipt.Id
                    };
                    _db.PortalUploads.Add(upload);
                    savedUploads.Add(upload);

                    items.Add(new PortalUploadItemResult
                    {
                        FileName = safeName,
                        Success = true,
                        Message = "Fişiniz alındı — arka planda otomatik okunuyor.",
                        Category = parsedCategory.ToString()
                    });
                    continue;
                }

                // Kuyruğa alınamadı (doğrulama/kota): belge KAYBEDİLMEZ — portal deposuna
                // kaydedilir, SMMM elle inceler.
                var fallback = await TrySavePortalFileAsync(client, file, safeName, parsedCategory, trimmedNote, ct);
                if (fallback.Upload is not null)
                {
                    savedUploads.Add(fallback.Upload);
                    items.Add(new PortalUploadItemResult
                    {
                        FileName = safeName,
                        Success = true,
                        Message = "Fiş otomatik okumaya alınamadı; belge yine de mali müşavirinize iletildi.",
                        Category = parsedCategory.ToString()
                    });
                }
                else
                {
                    items.Add(new PortalUploadItemResult
                    {
                        FileName = safeName,
                        Success = false,
                        Message = outcome.ErrorMessage ?? fallback.ErrorMessage ?? "Dosya yüklenemedi. Lütfen tekrar deneyin.",
                        Category = parsedCategory.ToString()
                    });
                }
                continue;
            }

            // Diğer kategoriler: yalnızca saklanır (ofis belgeleri için genişletilmiş uzantı listesi).
            var result = await TrySavePortalFileAsync(client, file, safeName, parsedCategory, trimmedNote, ct);
            if (result.Upload is not null)
            {
                savedUploads.Add(result.Upload);
                items.Add(new PortalUploadItemResult
                {
                    FileName = safeName,
                    Success = true,
                    Message = "Belgeniz alındı.",
                    Category = parsedCategory.ToString()
                });
            }
            else
            {
                items.Add(new PortalUploadItemResult
                {
                    FileName = safeName,
                    Success = false,
                    Message = result.ErrorMessage,
                    Category = parsedCategory.ToString()
                });
            }
        }

        // Açık talep eşleştirme (opsiyonel): ilk başarılı yükleme talebi kapatır.
        if (documentRequestId.HasValue && savedUploads.Count > 0)
        {
            var docRequest = await _db.DocumentRequests
                .FirstOrDefaultAsync(r => r.Id == documentRequestId.Value
                                          && r.ClientId == client.Id
                                          && r.Status == DocumentRequestStatus.Bekliyor, ct);
            if (docRequest is not null)
            {
                docRequest.Status = DocumentRequestStatus.Yuklendi;
                docRequest.FulfilledUploadId = savedUploads[0].Id;
            }
        }

        await _db.SaveChangesAsync(ct);

        var ok = items.Count(i => i.Success);
        var failed = items.Count - ok;

        _logger.LogInformation(
            "Portal yüklemesi. ClientId: {ClientId}, Kategori: {Category}, Başarılı: {Ok}, Hatalı: {Failed}",
            client.Id, parsedCategory, ok, failed);

        return Ok(new PortalUploadResultResponse
        {
            Message = failed > 0
                ? $"{ok} belge alındı, {failed} dosya hata verdi."
                : $"{ok} belge alındı. Teşekkürler!",
            Items = items
        });
    }

    // ---------- Yardımcılar ----------

    private async Task<ClientPortalAccess?> FindAccessAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;

        return await _db.ClientPortalAccesses
            .IgnoreQueryFilters() // anonim istek: token benzersiz anahtardır
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Token == token, ct);
    }

    /// <summary>Portal deposuna kaydeder; başarısızsa Türkçe hata döner (kayıt eklenmez).</summary>
    private async Task<(PortalUpload? Upload, string? ErrorMessage)> TrySavePortalFileAsync(
        Client client, IFormFile file, string safeName, PortalUploadCategory category, string? note, CancellationToken ct)
    {
        var validationError = _storage.ValidatePortalFile(safeName, file.Length);
        if (validationError is not null)
            return (null, validationError);

        try
        {
            await using var stream = file.OpenReadStream();
            // Faz 12: yalnız Fis kategorisindeki görseller normalize edilir (OCR okuyamasa da
            // fiş fotoğrafı küçültülmüş saklanır); diğer kategoriler olduğu gibi kaydedilir.
            // Not (Faz 12 revizyonu): OCR denemesi ReceiptService'te ORİJİNAL üzerinden zaten
            // yapıldı — burası yalnız depolama kopyasıdır, sıkıştırma OCR'ı etkilemez.
            var storedPath = await _storage.SavePortalDocumentAsync(
                client.TenantId, safeName, stream, normalizeImage: category == PortalUploadCategory.Fis, ct);

            // Normalizasyon uzantıyı ".jpg" yapmış olabilir; DB'deki ad kayıtla eşitlenir.
            var storedExtension = Path.GetExtension(storedPath);
            if (!storedExtension.Equals(Path.GetExtension(safeName), StringComparison.OrdinalIgnoreCase))
                safeName = Path.ChangeExtension(safeName, storedExtension);

            var upload = new PortalUpload
            {
                TenantId = client.TenantId,
                ClientId = client.Id,
                FileName = safeName,
                StoredPath = storedPath,
                Category = category,
                Note = note
            };
            _db.PortalUploads.Add(upload);
            return (upload, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Portal belgesi kaydedilemedi. ClientId: {ClientId}, Dosya: {File}", client.Id, safeName);
            return (null, "Dosya kaydedilemedi. Lütfen tekrar deneyin.");
        }
    }
}
