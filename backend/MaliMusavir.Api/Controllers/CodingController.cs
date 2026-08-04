using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

/// <summary>
/// Faz 2 — Otomatik Hesap/Cari Kod Motoru uçları: tek/toplu kodlama, onay (öğrenme),
/// onay kuyruğu, kural CRUD, mükellef kodlama ayarları ve Luca CSV dışa aktarma.
/// </summary>
[ApiController]
[Authorize]
public class CodingController : ControllerBase
{
    private const int MaxRangeDays = 366;
    private const int MaxBatchInvoices = 500;

    private readonly AppDbContext _db;
    private readonly CodingEngine _engine;
    private readonly LucaExcelExporter _exporter;
    private readonly ICurrentUser _currentUser;
    private readonly ParameterService _parameters;
    private readonly ILogger<CodingController> _logger;

    public CodingController(
        AppDbContext db,
        CodingEngine engine,
        LucaExcelExporter exporter,
        ICurrentUser currentUser,
        ParameterService parameters,
        ILogger<CodingController> logger)
    {
        _db = db;
        _engine = engine;
        _exporter = exporter;
        _currentUser = currentUser;
        _parameters = parameters;
        _logger = logger;
    }

    // ---------- Kodlama ----------

    /// <summary>Tek faturayı motorla kodlar ve satır önerilerini döndürür.</summary>
    [HttpPost("api/einvoices/{id:guid}/code")]
    public async Task<ActionResult<CodingResultResponse>> Code(Guid id, CancellationToken ct = default)
    {
        var invoice = await _db.EInvoices
            .Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (invoice is null)
            return NotFound(new { message = "Fatura bulunamadı." });

        var outcome = await _engine.CodeInvoiceAsync(invoice, ct);
        if (!outcome.Success)
            return BadRequest(new { message = outcome.ErrorMessage ?? "Fatura kodlanamadı." });

        return Ok(BuildCodingResult(invoice, outcome));
    }

    /// <summary>
    /// Dönemdeki onaylanmamış (Uncoded + AutoCoded + NeedsReview) faturaları topluca kodlar.
    /// Daha önce kodlanmış ama onaylanMAmış faturaların önerileri de güncel hafızayla YENİDEN
    /// üretilir — mizan/fiş listesi içe aktarımından sonra eski 770.99 önerileri böyle tazelenir.
    /// Approved faturalara dokunulmaz. from/to verilmezse son 30 gün alınır.
    /// </summary>
    [HttpPost("api/einvoices/code-batch")]
    public async Task<ActionResult<CodeBatchResultResponse>> CodeBatch(
        [FromQuery] Guid? clientId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Toplu kodlama için bir mükellef seçin." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var range = ResolveRange(from, to, out var rangeError);
        if (rangeError is not null)
            return BadRequest(new { message = rangeError });

        // Approved HARİÇ tüm faturalar: önceden kodlanmış ama onaylanmamış öneriler de
        // güncel hafızayla yeniden üretilir (motor Approved'ı zaten yeniden kodlamaz).
        var invoices = await _db.EInvoices
            .Include(e => e.Lines)
            .Where(e => e.ClientId == clientId
                        && e.CodingStatus != CodingStatus.Approved
                        && e.IssueDate >= range.From && e.IssueDate <= range.To)
            .OrderBy(e => e.IssueDate)
            .Take(MaxBatchInvoices)
            .ToListAsync(ct);

        if (invoices.Count == 0)
        {
            return Ok(new CodeBatchResultResponse
            {
                Total = 0,
                AutoCoded = 0,
                NeedsReview = 0,
                Message = "Seçilen aralıkta kodlanacak fatura yok."
            });
        }

        var autoCoded = 0;
        var needsReview = 0;

        foreach (var invoice in invoices)
        {
            var outcome = await _engine.CodeInvoiceAsync(invoice, ct);
            if (!outcome.Success) continue; // kalemsiz fatura vb. — diğerleri kodlanmaya devam eder

            if (outcome.CodingStatus == CodingStatus.AutoCoded) autoCoded++;
            else if (outcome.CodingStatus == CodingStatus.NeedsReview) needsReview++;
        }

        return Ok(new CodeBatchResultResponse
        {
            Total = invoices.Count,
            AutoCoded = autoCoded,
            NeedsReview = needsReview,
            Message = $"{autoCoded} fatura otomatik kodlandı, {needsReview} onay bekliyor."
        });
    }

    /// <summary>
    /// Çoklu mükellef dönem kodlama (code-bulk): seçilen (boşsa TÜM aktif) mükelleflerin
    /// aralıktaki ONAYLANMAMIŞ (Uncoded + AutoCoded + NeedsReview) faturaları, mevcut dönem
    /// kodlama mantığıyla mükellef başına döngüde motordan geçirilir — içe aktarımla gelen yeni
    /// hafıza eski önerilerin (ör. 770.99) üzerine yazılır; Approved faturalara dokunulmaz.
    /// Kod motoru (TenantId, ClientId) izolasyonlu çalışır — mükellefler arası karışma olmaz.
    /// Yanıt: mükellef başına kırılım + toplamlar.
    /// </summary>
    [HttpPost("api/coding/code-bulk")]
    public async Task<ActionResult<CodeBulkResultResponse>> CodeBulk(
        [FromBody] BulkCodePeriodRequest request,
        CancellationToken ct = default)
    {
        var range = ResolveRange(request.From, request.To, out var rangeError);
        if (rangeError is not null)
            return BadRequest(new { message = rangeError });

        var requestedIds = request.ClientIds.Where(id => id != Guid.Empty).Distinct().ToList();

        // Boş liste = tüm aktif mükellefler (arşivlenmişler hariç — mevcut "tümü" kalıbı).
        var clients = requestedIds.Count == 0
            ? await _db.Clients.AsNoTracking().Where(c => !c.IsArchived).OrderBy(c => c.Title).ToListAsync(ct)
            : await _db.Clients.AsNoTracking().Where(c => requestedIds.Contains(c.Id)).OrderBy(c => c.Title).ToListAsync(ct);

        if (clients.Count == 0)
            return BadRequest(new { message = "Kodlanacak mükellef bulunamadı." });

        var response = new CodeBulkResultResponse();

        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();

            // Mevcut CodeBatch sorgusuyla birebir aynı seçim (mükellef başına üst sınır dahil):
            // Approved HARİÇ — onaylanmamış öneriler güncel hafızayla yeniden üretilir.
            var invoices = await _db.EInvoices
                .Include(e => e.Lines)
                .Where(e => e.ClientId == client.Id
                            && e.CodingStatus != CodingStatus.Approved
                            && e.IssueDate >= range.From && e.IssueDate <= range.To)
                .OrderBy(e => e.IssueDate)
                .Take(MaxBatchInvoices)
                .ToListAsync(ct);

            if (invoices.Count == 0) continue; // faturasız mükellef kırılıma girmez

            var autoCoded = 0;
            var needsReview = 0;

            foreach (var invoice in invoices)
            {
                var outcome = await _engine.CodeInvoiceAsync(invoice, ct);
                if (!outcome.Success) continue; // kalemsiz fatura vb. — diğerleri kodlanmaya devam eder

                if (outcome.CodingStatus == CodingStatus.AutoCoded) autoCoded++;
                else if (outcome.CodingStatus == CodingStatus.NeedsReview) needsReview++;
            }

            response.Clients.Add(new CodeBulkClientResultResponse
            {
                ClientId = client.Id,
                ClientTitle = client.Title,
                Total = invoices.Count,
                AutoCoded = autoCoded,
                NeedsReview = needsReview
            });
            response.Total += invoices.Count;
            response.AutoCoded += autoCoded;
            response.NeedsReview += needsReview;
        }

        response.Message = response.Total == 0
            ? "Seçilen aralıkta kodlanacak fatura yok."
            : $"{response.AutoCoded + response.NeedsReview} fatura kodlandı ({response.AutoCoded} otomatik, {response.NeedsReview} onay bekliyor).";

        return Ok(response);
    }

    /// <summary>
    /// Kodlamayı onaylar: satır düzeltmeleri uygulanır, öğrenme (hafıza + satıcı varsayılanı)
    /// tetiklenir, fatura Approved olur.
    /// </summary>
    [HttpPost("api/einvoices/{id:guid}/approve")]
    public async Task<ActionResult<EInvoiceDetailResponse>> Approve(
        Guid id,
        [FromBody] ApproveCodingRequest request,
        CancellationToken ct = default)
    {
        var invoice = await _db.EInvoices
            .Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (invoice is null)
            return NotFound(new { message = "Fatura bulunamadı." });

        // Aynı satır birden fazla kez gönderilirse son düzeltme geçerli olur (500 yerine tolere edilir).
        var corrections = new Dictionary<Guid, ApproveLineCorrection>();
        foreach (var line in request.Lines)
        {
            corrections[line.LineId] = new ApproveLineCorrection(
                line.AccountCode,
                line.CariCode,
                line.WithholdingCode,
                line.WithholdingRate,
                line.WithholdingVatAmount,
                line.IsKkeg,
                line.KkegReason,
                line.KkegAmount);
        }

        var outcome = await _engine.ApproveCodingAsync(invoice, corrections, ct);
        if (!outcome.Success)
            return BadRequest(new { message = outcome.ErrorMessage ?? "Onay işlemi başarısız oldu." });

        return Ok(EInvoiceDetailResponse.FromEntityWithLines(invoice));
    }

    /// <summary>
    /// Toplu onay: seçilen faturaların ÖNERİLEN kodları düzeltmesiz kesinleştirilir
    /// (ApproveCodingAsync → öğrenme çalışır). Uncoded fatura önce motordan geçirilir;
    /// zaten Approved olan atlanır. Tek faturanın hatası diğerlerini durdurmaz (failed + log).
    /// </summary>
    [HttpPost("api/einvoices/bulk-approve")]
    public async Task<ActionResult<BulkApproveResultResponse>> BulkApprove(
        [FromBody] BulkInvoiceIdsRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { message = "Onaylanacak fatura seçilmedi." });
        if (ids.Count > MaxBatchInvoices)
            return BadRequest(new { message = $"Tek istekte en fazla {MaxBatchInvoices} fatura onaylanabilir." });

        // Global tenant filtresi: başka tenant'ın faturası sorgudan hiç dönmez → failed sayılır.
        var invoices = await _db.EInvoices
            .Include(e => e.Lines)
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);
        var byId = invoices.ToDictionary(e => e.Id);

        var approved = 0;
        var skipped = 0;
        var failed = 0;
        var noCorrections = new Dictionary<Guid, ApproveLineCorrection>(); // düzeltmesiz onay → öneriler kesinleşir

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            if (!byId.TryGetValue(id, out var invoice))
            {
                failed++;
                _logger.LogWarning("Toplu onay: fatura bulunamadı veya bu tenant'a ait değil. EInvoiceId: {Id}", id);
                continue;
            }

            try
            {
                if (invoice.CodingStatus == CodingStatus.Approved)
                {
                    skipped++; // zaten onaylı — öğrenme tekrarlanmaz
                    continue;
                }

                // Uncoded: önce motor çalıştırılır ki onaylanacak öneri oluşsun.
                if (invoice.CodingStatus == CodingStatus.Uncoded)
                {
                    var codeOutcome = await _engine.CodeInvoiceAsync(invoice, ct);
                    if (!codeOutcome.Success)
                    {
                        failed++;
                        _logger.LogWarning(
                            "Toplu onay: fatura kodlanamadı. EInvoiceId: {Id}, Hata: {Error}",
                            invoice.Id, codeOutcome.ErrorMessage);
                        continue;
                    }
                }

                var outcome = await _engine.ApproveCodingAsync(invoice, noCorrections, ct);
                if (outcome.Success)
                {
                    approved++;
                }
                else
                {
                    failed++;
                    _logger.LogWarning(
                        "Toplu onay: fatura onaylanamadı. EInvoiceId: {Id}, Hata: {Error}",
                        invoice.Id, outcome.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // istek iptali yutulmaz
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Toplu onay: fatura işlenirken beklenmeyen hata. EInvoiceId: {Id}", id);
            }
        }

        return Ok(new BulkApproveResultResponse
        {
            Approved = approved,
            Skipped = skipped,
            Failed = failed,
            Message = BuildBulkMessage($"{approved} fatura onaylandı", skipped, failed)
        });
    }

    /// <summary>
    /// Onay kaldırma: Approved faturayı onay öncesi öneri durumuna (NeedsReview) döndürür.
    /// Satırlardaki kod değerlerine DOKUNULMAZ (SMMM düzeltip yeniden onaylayabilir) ve onayda
    /// yazılan öğrenme kayıtları (AccountMemory/VendorDefault/VendorCariMap) GERİ ALINMAZ —
    /// hafıza bozulmaz, yeniden onayda üzerine yazılır.
    /// </summary>
    [HttpPost("api/einvoices/{id:guid}/unapprove")]
    public async Task<ActionResult<EInvoiceDetailResponse>> Unapprove(Guid id, CancellationToken ct = default)
    {
        var invoice = await _db.EInvoices
            .Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (invoice is null)
            return NotFound(new { message = "Fatura bulunamadı." });

        if (invoice.CodingStatus != CodingStatus.Approved)
            return BadRequest(new { message = "Yalnızca onaylanmış faturanın onayı kaldırılabilir." });

        invoice.CodingStatus = CodingStatus.NeedsReview;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Fatura onayı kaldırıldı. EInvoiceId: {Id}", invoice.Id);
        return Ok(EInvoiceDetailResponse.FromEntityWithLines(invoice));
    }

    /// <summary>
    /// Toplu onay kaldırma: seçilenlerden yalnız Approved olanlar NeedsReview'a döndürülür;
    /// onaylı olmayanlar sessizce atlanır (skipped). Kod değerlerine ve öğrenme kayıtlarına
    /// dokunulmaz (tekil uçla aynı kural).
    /// </summary>
    [HttpPost("api/einvoices/bulk-unapprove")]
    public async Task<ActionResult<BulkUnapproveResultResponse>> BulkUnapprove(
        [FromBody] BulkInvoiceIdsRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { message = "Onayı kaldırılacak fatura seçilmedi." });
        if (ids.Count > MaxBatchInvoices)
            return BadRequest(new { message = $"Tek istekte en fazla {MaxBatchInvoices} faturanın onayı kaldırılabilir." });

        // Global tenant filtresi: başka tenant'ın faturası sorgudan hiç dönmez → failed sayılır.
        var invoices = await _db.EInvoices
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);

        var failed = ids.Count - invoices.Count;
        if (failed > 0)
        {
            _logger.LogWarning(
                "Toplu onay kaldırma: {Failed} fatura bulunamadı veya bu tenant'a ait değil.", failed);
        }

        var reverted = 0;
        var skipped = 0;

        foreach (var invoice in invoices)
        {
            if (invoice.CodingStatus != CodingStatus.Approved)
            {
                skipped++;
                continue;
            }

            invoice.CodingStatus = CodingStatus.NeedsReview;
            reverted++;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Toplu fatura onay kaldırma tamamlandı. Geri alınan: {Reverted}, Atlanan: {Skipped}, Hata: {Failed}",
            reverted, skipped, failed);

        var message = $"{reverted} faturanın onayı kaldırıldı";
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
    /// Toplu kodlama (onaysız): seçilen faturalar motordan geçirilir, öneriler yazılır ama
    /// onaylanmaz. Zaten Approved olan atlanır; tek faturanın hatası diğerlerini durdurmaz.
    /// </summary>
    [HttpPost("api/einvoices/bulk-code")]
    public async Task<ActionResult<BulkCodeResultResponse>> BulkCode(
        [FromBody] BulkInvoiceIdsRequest request,
        CancellationToken ct = default)
    {
        var ids = request.Ids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { message = "Kodlanacak fatura seçilmedi." });
        if (ids.Count > MaxBatchInvoices)
            return BadRequest(new { message = $"Tek istekte en fazla {MaxBatchInvoices} fatura kodlanabilir." });

        var invoices = await _db.EInvoices
            .Include(e => e.Lines)
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);
        var byId = invoices.ToDictionary(e => e.Id);

        var autoCoded = 0;
        var needsReview = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            if (!byId.TryGetValue(id, out var invoice))
            {
                failed++;
                _logger.LogWarning("Toplu kodlama: fatura bulunamadı veya bu tenant'a ait değil. EInvoiceId: {Id}", id);
                continue;
            }

            try
            {
                if (invoice.CodingStatus == CodingStatus.Approved)
                {
                    skipped++; // onaylı fatura yeniden kodlanmaz (mevcut motor kuralı)
                    continue;
                }

                var outcome = await _engine.CodeInvoiceAsync(invoice, ct);
                if (!outcome.Success)
                {
                    failed++;
                    _logger.LogWarning(
                        "Toplu kodlama: fatura kodlanamadı. EInvoiceId: {Id}, Hata: {Error}",
                        invoice.Id, outcome.ErrorMessage);
                    continue;
                }

                if (outcome.CodingStatus == CodingStatus.AutoCoded) autoCoded++;
                else needsReview++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Toplu kodlama: fatura işlenirken beklenmeyen hata. EInvoiceId: {Id}", id);
            }
        }

        return Ok(new BulkCodeResultResponse
        {
            AutoCoded = autoCoded,
            NeedsReview = needsReview,
            Skipped = skipped,
            Failed = failed,
            Message = BuildBulkMessage(
                $"{autoCoded} fatura otomatik kodlandı, {needsReview} onay bekliyor", skipped, failed)
        });
    }

    /// <summary>Toplu işlem özet mesajı: gövde + ", N atlandı" + ", N hata" + nokta.</summary>
    private static string BuildBulkMessage(string core, int skipped, int failed)
    {
        var message = core;
        if (skipped > 0) message += $", {skipped} atlandı (zaten onaylı)";
        if (failed > 0) message += $", {failed} hata";
        return message + ".";
    }

    /// <summary>Onay kuyruğu: NeedsReview durumundaki faturalar (isteğe bağlı mükellef filtresi).</summary>
    [HttpGet("api/coding/review-queue")]
    public async Task<ActionResult<List<EInvoiceListItemResponse>>> ReviewQueue(
        [FromQuery] Guid? clientId = null,
        CancellationToken ct = default)
    {
        var query = _db.EInvoices
            .AsNoTracking()
            .Include(e => e.Lines) // önerilen kod özeti listede görünsün (tek sorgu, N+1 yok)
            .Where(e => e.CodingStatus == CodingStatus.NeedsReview);

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            query = query.Where(e => e.ClientId == clientId.Value);

        var invoices = await query
            .OrderByDescending(e => e.IssueDate)
            .ThenByDescending(e => e.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        return Ok(invoices.Select(EInvoiceListItemResponse.FromEntity).ToList());
    }

    // ---------- Hesap kodu arama (autocomplete) + toplu cari/hesap planı içe aktarma ----------

    /// <summary>Türkçe büyük/küçük harf dönüşümleri (İ/i, I/ı) için sabit kültür.</summary>
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Hesap/cari kodu deseni (harfli kırılımlar dahil — SetInvoiceCodesRequest ile aynı).</summary>
    private static readonly Regex ImportCodeRegex = new(
        @"^[0-9A-Za-zÇĞİÖŞÜçğıöşü]{1,10}(\.[0-9A-Za-zÇĞİÖŞÜçğıöşü]{1,10}){0,5}$",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>VKN 10 hane / TCKN 11 hane.</summary>
    private static readonly Regex ImportTaxIdRegex = new(@"^\d{10,11}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private const long MaxImportFileBytes = 2 * 1024 * 1024; // 2 MB
    private const int MaxImportRows = 2000;

    /// <summary>
    /// Hesap kodu autocomplete: q rakam veya nokta ile başlıyorsa KOD ÖN EKİ eşleşmesi
    /// ("7" → 7 ile başlayanlar, "700." → 700. ile başlayanlar), aksi halde hesap ADINDA
    /// Türkçe büyük/küçük harf duyarsız arama ("kira" → "KİRA GİDERİ"). Boş q ilk kayıtları
    /// döndürür (alan odaklanınca öneri). Kaynak: ChartAccount (mükellef hesap planı);
    /// plan boşsa geçmişte görülen kodlar (hafıza/satıcı varsayılanı/cari haritası/kurallar).
    /// clientId boşsa tenant genelinde aranır (kurallar sayfası "tüm mükellefler" kapsamı).
    /// </summary>
    [HttpGet("api/coding/accounts")]
    public async Task<ActionResult<List<AccountSearchItemResponse>>> SearchAccounts(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? q = null,
        [FromQuery] int limit = 15,
        CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 50) limit = 50;
        var term = (q ?? string.Empty).Trim();

        // Türkçe duyarsız ad araması Postgres lower/upper'ına güvenilemediğinden bellekte
        // yapılır; mükellef hesap planları küçük olduğundan üst sınır güvenlik payıdır.
        var chartQuery = _db.ChartAccounts.AsNoTracking().AsQueryable();
        if (clientId.HasValue && clientId.Value != Guid.Empty)
            chartQuery = chartQuery.Where(a => a.ClientId == clientId.Value);

        var candidates = await chartQuery
            .OrderBy(a => a.Code)
            .Take(5000)
            .Select(a => new AccountSearchItemResponse { Code = a.Code, Name = a.Name })
            .ToListAsync(ct);

        // Hesap planı boşsa geçmiş kayıtlardan tekilleştirilmiş kod listesi kaynak olur —
        // SMMM daha plan yüklemeden de öneri alır.
        if (candidates.Count == 0)
            candidates = await LoadHistoricalCodesAsync(clientId, ct);

        var filtered = FilterAccountCandidates(candidates, term);

        return Ok(filtered
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name)) ?? g.First())
            .OrderBy(a => a.Code, StringComparer.Ordinal)
            .Take(limit)
            .ToList());
    }

    /// <summary>Arama filtresi: kod ön eki (rakam/nokta) ya da Türkçe duyarsız ad içermesi.</summary>
    private static List<AccountSearchItemResponse> FilterAccountCandidates(
        List<AccountSearchItemResponse> candidates, string term)
    {
        if (term.Length == 0) return candidates;

        if (char.IsDigit(term[0]) || term[0] == '.')
        {
            return candidates
                .Where(a => a.Code.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Çift taraflı Türkçe duyarsızlık: "kira" → "KİRA" (upper) ve "KİRA" → "kira" (lower)
        // dönüşümlerinin ikisi de denenir (İ/i ve I/ı eşleşmeleri iki yönde de yakalanır).
        var upper = term.ToUpper(TurkishCulture);
        var lower = term.ToLower(TurkishCulture);
        return candidates
            .Where(a => !string.IsNullOrEmpty(a.Name) &&
                        (a.Name!.ToUpper(TurkishCulture).Contains(upper, StringComparison.Ordinal) ||
                         a.Name!.ToLower(TurkishCulture).Contains(lower, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// ChartAccount boşken autocomplete kaynağı: öğrenme hafızası + satıcı varsayılanları
    /// (hesap kodları), cari haritası (cari kod + satıcı unvanı) ve aktif kurallar.
    /// </summary>
    private async Task<List<AccountSearchItemResponse>> LoadHistoricalCodesAsync(
        Guid? clientId, CancellationToken ct)
    {
        var memoryQuery = _db.AccountMemories.AsNoTracking().AsQueryable();
        var vendorQuery = _db.VendorDefaults.AsNoTracking().AsQueryable();
        var cariQuery = _db.VendorCariMaps.AsNoTracking().AsQueryable();
        var ruleQuery = _db.CodingRules.AsNoTracking().Where(r => r.IsActive).AsQueryable();

        if (clientId.HasValue && clientId.Value != Guid.Empty)
        {
            var id = clientId.Value;
            memoryQuery = memoryQuery.Where(m => m.ClientId == id);
            vendorQuery = vendorQuery.Where(v => v.ClientId == id);
            cariQuery = cariQuery.Where(m => m.ClientId == id);
            ruleQuery = ruleQuery.Where(r => r.ClientId == null || r.ClientId == id);
        }

        var result = new List<AccountSearchItemResponse>();

        result.AddRange((await memoryQuery.Select(m => m.AccountCode).Distinct().Take(500).ToListAsync(ct))
            .Select(c => new AccountSearchItemResponse { Code = c, Name = null }));
        result.AddRange((await vendorQuery.Select(v => v.AccountCode).Distinct().Take(500).ToListAsync(ct))
            .Select(c => new AccountSearchItemResponse { Code = c, Name = null }));
        result.AddRange((await ruleQuery.Select(r => r.AccountCode).Distinct().Take(500).ToListAsync(ct))
            .Select(c => new AccountSearchItemResponse { Code = c, Name = null }));
        // Cari kodlarda satıcı unvanı ad olarak gösterilir (soluk metin).
        result.AddRange(await cariQuery
            .OrderBy(m => m.CariCode)
            .Take(500)
            .Select(m => new AccountSearchItemResponse { Code = m.CariCode, Name = m.SenderTitle })
            .ToListAsync(ct));

        return result;
    }

    /// <summary>
    /// Toplu cari/hesap planı içe aktarma ŞABLONU (.xlsx). MinimalXlsxWriter tek sayfa
    /// desteklediğinden cari eşleme + hesap planı TEK sayfada birleşiktir: A-C cari eşlemesi,
    /// D/E opsiyonel hesap planı kaydı. 2 örnek satır içerir (içe aktarmadan önce silinmeli).
    /// </summary>
    [HttpGet("api/coding/import-template")]
    public async Task<IActionResult> DownloadImportTemplate(
        [FromQuery] Guid? clientId = null,
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Şablonu indirmek için bir mükellef seçin." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var headers = new[] { "Satıcı VKN", "Satıcı Unvanı", "Cari Kod", "Hesap Kodu (ops.)", "Hesap Adı (ops.)" };
        var rows = new List<IReadOnlyList<MinimalXlsxWriter.Cell>>
        {
            new[]
            {
                MinimalXlsxWriter.Cell.Text("6120034142"),
                MinimalXlsxWriter.Cell.Text("ÖRNEK TİCARET LTD. ŞTİ."),
                MinimalXlsxWriter.Cell.Text("320.01.005"),
                MinimalXlsxWriter.Cell.Text("770.02.011"),
                MinimalXlsxWriter.Cell.Text("KÜÇÜK DEMİRBAŞ GİDERLERİ"),
            },
            new[]
            {
                MinimalXlsxWriter.Cell.Text("1234567890"),
                MinimalXlsxWriter.Cell.Text("ÖRNEK GIDA PAZARLAMA A.Ş."),
                MinimalXlsxWriter.Cell.Text("320.01.006"),
                MinimalXlsxWriter.Cell.Empty,
                MinimalXlsxWriter.Cell.Empty,
            },
        };

        var bytes = MinimalXlsxWriter.Build("Cari ve Hesap Kodlari", headers, rows);
        var fileName = $"cari-kod-sablonu-{LucaExcelExporter.Slugify(client.Title)}.xlsx";

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>
    /// Toplu cari/hesap planı içe aktarma: şablondaki her satır için
    ///  - VKN + Cari Kod doluysa satıcı→cari eşlemesi kod motorunun cari hafızasına
    ///    (VendorCariMap) upsert edilir — sonraki faturalarda motor bu kodu kullanır,
    ///  - Cari Kod (+ Unvan) mükellef hesap planına (ChartAccount, autocomplete kaynağı) yazılır,
    ///  - D/E doluysa hesap kodu + adı da hesap planına yazılır.
    /// Hatalı satır (VKN 10-11 hane değil, kod desene uymuyor) Türkçe nedenle raporlanır ve
    /// işlem kalan satırlarla DEVAM eder. Sınırlar: 2 MB, yalnızca .xlsx, en fazla 2000 satır.
    /// </summary>
    [HttpPost("api/coding/import")]
    public async Task<ActionResult<CodingImportResultResponse>> ImportCodes(
        [FromForm] Guid clientId,
        IFormFile? file,
        CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();
        var tenantId = _currentUser.TenantId.Value;

        if (clientId == Guid.Empty)
            return BadRequest(new { message = "İçe aktarma için bir mükellef seçin." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Bir .xlsx dosyası seçin." });
        if (file.Length > MaxImportFileBytes)
            return BadRequest(new { message = "Dosya boyutu en fazla 2 MB olabilir." });
        if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Yalnızca .xlsx dosyaları içe aktarılabilir. Önce şablonu indirin." });

        List<List<string>> rows;
        try
        {
            // ZipArchive seekable + senkron okuma ister; 2 MB sınırı belleğe almayı güvenli kılar.
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            rows = MinimalXlsxReader.ReadFirstSheet(buffer, MaxImportRows + 1); // +1 başlık satırı
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return BadRequest(new { message = "Dosya okunamadı. Geçerli bir .xlsx dosyası yükleyin (şablonu kullanın)." });
        }

        // Mevcut kayıtlar tek sorguda çekilir (satır başına sorgu yok).
        var cariMaps = (await _db.VendorCariMaps
                .Where(m => m.ClientId == clientId)
                .ToListAsync(ct))
            .GroupBy(m => m.SenderTaxId)
            .ToDictionary(g => g.Key, g => g.First());

        var chartAccounts = (await _db.ChartAccounts
                .Where(a => a.ClientId == clientId)
                .ToListAsync(ct))
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new CodingImportResultResponse();

        // Yerel upsert: hesap planına kod+ad yazar; yeni kayıt/ad güncellemesi bilgisini döner.
        (bool Added, bool Updated) UpsertChartAccount(string code, string name, AccountType type)
        {
            if (chartAccounts.TryGetValue(code, out var existing))
            {
                // Ad yalnızca boşken doldurulur (SMMM'nin verdiği mevcut ad ezilmez).
                if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(name))
                {
                    existing.Name = Truncate(name, 200);
                    return (false, true);
                }
                return (false, false);
            }

            var account = new ChartAccount
            {
                TenantId = tenantId,
                ClientId = clientId,
                Code = code,
                Name = Truncate(name, 200),
                Type = type
            };
            _db.ChartAccounts.Add(account);
            chartAccounts[code] = account;
            return (true, false);
        }

        // 1. satır başlıktır; veri 2. satırdan başlar.
        for (var i = 1; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var rowNumber = i + 1;

            string CellAt(int col) => col < row.Count ? row[col].Trim() : string.Empty;

            var taxId = CellAt(0);
            var title = CellAt(1);
            var cariCode = CellAt(2);
            var accountCode = CellAt(3);
            var accountName = CellAt(4);

            // Tamamen boş satır sessizce geçilir (Excel'in bıraktığı kuyruk satırları).
            if (taxId.Length == 0 && title.Length == 0 && cariCode.Length == 0 &&
                accountCode.Length == 0 && accountName.Length == 0)
            {
                continue;
            }

            // Satır doğrulamaları: hata kaydedilir, satır atlanır, işlem devam eder.
            if (taxId.Length > 0 && !ImportTaxIdRegex.IsMatch(taxId))
            {
                result.Errors.Add($"Satır {rowNumber}: Satıcı VKN 10 veya 11 haneli rakam olmalıdır (\"{taxId}\").");
                continue;
            }
            if (cariCode.Length > 0 && !ImportCodeRegex.IsMatch(cariCode))
            {
                result.Errors.Add($"Satır {rowNumber}: Geçersiz cari kod (\"{cariCode}\"). Örnek biçim: 320.01.005.");
                continue;
            }
            if (accountCode.Length > 0 && !ImportCodeRegex.IsMatch(accountCode))
            {
                result.Errors.Add($"Satır {rowNumber}: Geçersiz hesap kodu (\"{accountCode}\"). Örnek biçim: 770.02.011.");
                continue;
            }
            if (cariCode.Length == 0 && accountCode.Length == 0)
            {
                result.Skipped++; // işlenecek kod yok (yalnız VKN/unvan girilmiş)
                continue;
            }

            var rowAdded = false;
            var rowUpdated = false;

            // (a) VKN + Cari Kod → kod motorunun cari hafızası (VendorCariMap upsert,
            //     (TenantId, ClientId) izolasyonu mevcut motor kalıbıyla).
            if (taxId.Length > 0 && cariCode.Length > 0)
            {
                if (cariMaps.TryGetValue(taxId, out var map))
                {
                    if (!string.Equals(map.CariCode, cariCode, StringComparison.Ordinal))
                    {
                        map.CariCode = cariCode;
                        rowUpdated = true;
                    }
                    if (string.IsNullOrWhiteSpace(map.SenderTitle) && title.Length > 0)
                    {
                        map.SenderTitle = Truncate(title, 300);
                        rowUpdated = true;
                    }
                }
                else
                {
                    var newMap = new VendorCariMap
                    {
                        TenantId = tenantId,
                        ClientId = clientId,
                        SenderTaxId = taxId,
                        SenderTitle = Truncate(title, 300),
                        VendorNameNormalized = TextNormalizer.NormalizeCompanyName(title),
                        CariCode = cariCode
                    };
                    _db.VendorCariMaps.Add(newMap);
                    cariMaps[taxId] = newMap;
                    rowAdded = true;
                }
            }

            // (b) Cari kod + unvan → hesap planı (autocomplete cari önerilerinin kaynağı).
            if (cariCode.Length > 0)
            {
                var (added, updated) = UpsertChartAccount(cariCode, title, AccountType.Cari);
                rowAdded |= added;
                rowUpdated |= updated;
            }

            // (c) D/E doluysa hesap kodu + adı → hesap planı.
            if (accountCode.Length > 0)
            {
                var (added, updated) = UpsertChartAccount(accountCode, accountName, AccountType.Diger);
                rowAdded |= added;
                rowUpdated |= updated;
            }

            if (rowAdded) result.Imported++;
            else if (rowUpdated) result.Updated++;
            else result.Skipped++; // satır zaten birebir kayıtlı
        }

        await _db.SaveChangesAsync(ct);

        var parts = new List<string> { $"{result.Imported} kayıt eklendi" };
        if (result.Updated > 0) parts.Add($"{result.Updated} güncellendi");
        if (result.Skipped > 0) parts.Add($"{result.Skipped} satır atlandı");
        if (result.Errors.Count > 0) parts.Add($"{result.Errors.Count} satırda hata");
        result.Message = string.Join(", ", parts) + ".";

        _logger.LogInformation(
            "Cari/hesap planı içe aktarma tamamlandı. ClientId: {ClientId}, Eklenen: {Imported}, Güncellenen: {Updated}, Atlanan: {Skipped}, Hata: {Errors}",
            clientId, result.Imported, result.Updated, result.Skipped, result.Errors.Count);

        return Ok(result);
    }

    /// <summary>Metni en fazla verilen uzunlukta keser (kolon sınırları için).</summary>
    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    // ---------- Mizan + geçmiş fiş listesi içe aktarma (Luca dosyaları olduğu gibi) ----------

    /// <summary>Mizan/fiş listesi dosya sınırı (mizanlar cari şablonundan büyüktür).</summary>
    private const long MaxLucaImportFileBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxLucaImportRows = 20000;

    /// <summary>Mizan hesap kodu deseni: rakamla başlar, ör. "1", "100", "100.00.001", "320.01.A5".</summary>
    private static readonly Regex MizanCodeRegex = new(
        @"^\d{1,10}(\.[0-9A-Za-zÇĞİÖŞÜçğıöşü]{1,10}){0,5}$",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>Ortak dosya doğrulama + belleğe okuma (mizan ve fiş listesi uçları).</summary>
    private async Task<(List<List<string>>? Rows, ActionResult? Error)> ReadLucaXlsxAsync(
        Guid clientId, IFormFile? file, CancellationToken ct)
    {
        if (clientId == Guid.Empty)
            return (null, BadRequest(new { message = "İçe aktarma için bir mükellef seçin." }));

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return (null, NotFound(new { message = "Mükellef bulunamadı." }));

        if (file is null || file.Length == 0)
            return (null, BadRequest(new { message = "Bir .xlsx dosyası seçin." }));
        if (file.Length > MaxLucaImportFileBytes)
            return (null, BadRequest(new { message = "Dosya boyutu en fazla 5 MB olabilir." }));
        if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return (null, BadRequest(new { message = "Yalnızca .xlsx dosyaları içe aktarılabilir." }));

        try
        {
            // ZipArchive seekable + senkron okuma ister; 5 MB sınırı belleğe almayı güvenli kılar.
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return (MinimalXlsxReader.ReadFirstSheet(buffer, MaxLucaImportRows), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return (null, BadRequest(new { message = "Dosya okunamadı. Geçerli bir .xlsx dosyası yükleyin." }));
        }
    }

    /// <summary>Hücre metni tr-TR büyük harf (başlık araması için).</summary>
    private static string UpperTr(string value) => value.Trim().ToUpper(TurkishCulture);

    /// <summary>
    /// Mizan hiyerarşisinde alt hesap testi: next, parent'ın devamı mı?
    /// "100" → "100.00" (nokta ile derinleşme) ve "1" → "10" → "100" (noktasız grup
    /// seviyeleri, önek uzaması) alt hesaptır; "320.1" → "320.10" DEĞİLDİR.
    /// </summary>
    private static bool IsChildAccountCode(string parent, string next)
        => next.StartsWith(parent, StringComparison.Ordinal)
           && next.Length > parent.Length
           && (next[parent.Length] == '.' || !parent.Contains('.'));

    /// <summary>Hesap kodundan tür tahmini (mizan/fiş listesi içe aktarımı).</summary>
    private static AccountType GuessAccountType(string code)
    {
        if (code.StartsWith("120.", StringComparison.Ordinal) || code.StartsWith("320.", StringComparison.Ordinal))
            return AccountType.Cari;
        if (code.StartsWith("191", StringComparison.Ordinal) || code.StartsWith("391", StringComparison.Ordinal))
            return AccountType.Kdv;
        if (code.StartsWith("7", StringComparison.Ordinal))
            return AccountType.Gider;
        if (code.StartsWith("15", StringComparison.Ordinal))
            return AccountType.Stok;
        return AccountType.Diger;
    }

    /// <summary>Luca tutar hücresi çözümü: invariant ("548.08") ve Türkçe ("3.600.000,00") biçimler.</summary>
    private static decimal ParseLucaAmount(string value)
    {
        var text = value.Trim().Replace(" ", string.Empty);
        if (text.Length == 0) return 0m;
        if (text.Contains(',')) // Türkçe biçim: binlik "." + ondalık ","
            text = text.Replace(".", string.Empty).Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0m;
    }

    /// <summary>
    /// MİZAN İÇE AKTARMA: Luca'dan alınan mizan .xlsx dosyası OLDUĞU GİBİ yüklenir.
    /// Başlık satırı "HESAP KODU" hücresi aranarak bulunur (üstteki unvan/dönem satırları ve
    /// "GENEL TOPLAM" satırı atlanır). YAPRAK hesaplar (altında daha derin kırılımı olmayanlar):
    ///  - tümü → ChartAccount upsert (autocomplete + hesap planı kaynağı; mevcut kaydın adı boşsa doldurulur),
    ///  - 120.* / 320.* olanlar → kod motorunun cari hafızasına (VendorCariMap) NORMALİZE UNVAN
    ///    anahtarıyla yazılır — VKN'siz eşleşme, faturada satıcı unvanı üzerinden yakalanır.
    /// Sınırlar: 5 MB, yalnızca .xlsx.
    /// </summary>
    [HttpPost("api/coding/import-mizan")]
    public async Task<ActionResult<MizanImportResultResponse>> ImportMizan(
        [FromForm] Guid clientId,
        IFormFile? file,
        CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();
        var tenantId = _currentUser.TenantId.Value;

        var (rows, error) = await ReadLucaXlsxAsync(clientId, file, ct);
        if (error is not null) return error;

        // Başlık satırı: herhangi bir hücresi "HESAP KODU" olan ilk satır (esnek konum).
        var headerIndex = -1;
        var codeCol = 0;
        var nameCol = 1;
        for (var i = 0; i < rows!.Count && headerIndex < 0; i++)
        {
            for (var j = 0; j < rows[i].Count; j++)
            {
                if (UpperTr(rows[i][j]) != "HESAP KODU") continue;
                headerIndex = i;
                codeCol = j;
                for (var k = 0; k < rows[i].Count; k++)
                {
                    if (UpperTr(rows[i][k]).Contains("HESAP ADI")) { nameCol = k; break; }
                }
                break;
            }
        }

        if (headerIndex < 0)
            return BadRequest(new { message = "Mizan başlık satırı bulunamadı (\"HESAP KODU\" kolonu yok). Luca mizan dosyasını olduğu gibi yükleyin." });

        // Hesap satırları: kod rakamla başlar; "GENEL TOPLAM" ve diğer toplam/boş satırlar atlanır.
        var accounts = new List<(string Code, string Name)>();
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var code = codeCol < row.Count ? row[codeCol].Trim() : string.Empty;
            if (code.Length == 0) continue;
            if (UpperTr(code).Contains("TOPLAM")) continue;
            if (!char.IsDigit(code[0]) || !MizanCodeRegex.IsMatch(code)) continue;

            var name = nameCol < row.Count ? row[nameCol].Trim() : string.Empty;
            accounts.Add((code, name));
        }

        if (accounts.Count == 0)
            return BadRequest(new { message = "Mizanda hesap satırı bulunamadı. Dosyanın Luca mizan dökümü olduğundan emin olun." });

        // Yaprak hesap: bir sonraki satırın kodu kendi kodunun devamı DEĞİLSE yapraktır
        // (mizan hiyerarşik sıralıdır — "100" → "100.00" → "100.00.001").
        var leaves = new List<(string Code, string Name)>();
        for (var i = 0; i < accounts.Count; i++)
        {
            if (i + 1 >= accounts.Count || !IsChildAccountCode(accounts[i].Code, accounts[i + 1].Code))
                leaves.Add(accounts[i]);
        }

        // Mevcut kayıtlar tek sorguda (satır başına sorgu yok) — mevcut import kalıbı.
        var chartAccounts = (await _db.ChartAccounts
                .Where(a => a.ClientId == clientId)
                .ToListAsync(ct))
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var cariMapsByName = (await _db.VendorCariMaps
                .Where(m => m.ClientId == clientId)
                .ToListAsync(ct))
            .Where(m => m.VendorNameNormalized.Length > 0)
            .GroupBy(m => m.VendorNameNormalized, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var addedAccounts = 0;
        var cariCount = 0;

        foreach (var (code, name) in leaves)
        {
            ct.ThrowIfCancellationRequested();

            // (a) TÜM yaprak hesaplar → hesap planı (mevcutsa yalnız boş ad doldurulur).
            if (chartAccounts.TryGetValue(code, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.Name) && name.Length > 0)
                    existing.Name = Truncate(name, 200);
            }
            else
            {
                var account = new ChartAccount
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    Code = code,
                    Name = Truncate(name, 200),
                    Type = GuessAccountType(code)
                };
                _db.ChartAccounts.Add(account);
                chartAccounts[code] = account;
                addedAccounts++;
            }

            // (b) 120.* / 320.* yaprakları → cari hafıza (unvan-normalize anahtar; VKN mizanda yoktur).
            if (!code.StartsWith("120.", StringComparison.Ordinal) &&
                !code.StartsWith("320.", StringComparison.Ordinal))
            {
                continue;
            }

            var nameKey = TextNormalizer.NormalizeCompanyName(name);
            if (nameKey.Length == 0) continue; // eşleşme anahtarı üretilemeyen unvan atlanır

            if (cariMapsByName.TryGetValue(nameKey, out var map))
            {
                map.CariCode = code; // mizandaki kod esas alınır (SMMM'nin defterindeki güncel kod)
                if (string.IsNullOrWhiteSpace(map.SenderTitle) && name.Length > 0)
                    map.SenderTitle = Truncate(name, 300);
            }
            else
            {
                var newMap = new VendorCariMap
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    SenderTaxId = string.Empty, // mizanda VKN yok — ilk VKN'li faturada geri doldurulur
                    SenderTitle = Truncate(name, 300),
                    VendorNameNormalized = nameKey,
                    CariCode = code
                };
                _db.VendorCariMaps.Add(newMap);
                cariMapsByName[nameKey] = newMap;
            }
            cariCount++;
        }

        await _db.SaveChangesAsync(ct);

        var result = new MizanImportResultResponse
        {
            Accounts = leaves.Count,
            CariCount = cariCount,
            Message = $"{leaves.Count} yaprak hesap hesap planına işlendi ({addedAccounts} yeni), " +
                      $"{cariCount} cari hesap (120/320) kod motorunun cari hafızasına yazıldı."
        };

        _logger.LogInformation(
            "Mizan içe aktarma tamamlandı. ClientId: {ClientId}, Yaprak: {Leaves}, Yeni hesap: {Added}, Cari: {Cari}",
            clientId, leaves.Count, addedAccounts, cariCount);

        return Ok(result);
    }

    /// <summary>
    /// GEÇMİŞ FİŞ LİSTESİ İÇE AKTARMA: Luca "Fiş Aktarım Şablonu" biçimindeki (14 kolon)
    /// geçmiş fiş dökümü OLDUĞU GİBİ yüklenir. Satırlar Evrak No'ya göre fiş gruplarına ayrılır;
    /// her ALIŞ grubunda:
    ///  - 320/120/100 ile başlayan alacaklı satır karşı hesaptır (320.* ise satıcı carisi),
    ///  - 191* satırları KDV'dir (öğrenmeye girmez; kod hesap planına yine eklenir),
    ///  - kalan borçlu satır gider hesabıdır; "Detay Açıklama" satıcı unvanıdır.
    /// Öğrenmeler (hepsi (TenantId, ClientId) izolasyonlu): normalize unvan → gider hesabı
    /// (VendorDefault) ve normalize unvan → cari kodu (VendorCariMap); görülen tüm hesap kodları
    /// → ChartAccount. "satış faturası" grupları atlanır ve sayısı raporlanır (alış odaklıyız).
    /// </summary>
    [HttpPost("api/coding/import-fis-listesi")]
    public async Task<ActionResult<FisListesiImportResultResponse>> ImportFisListesi(
        [FromForm] Guid clientId,
        IFormFile? file,
        CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();
        var tenantId = _currentUser.TenantId.Value;

        var (rows, error) = await ReadLucaXlsxAsync(clientId, file, ct);
        if (error is not null) return error;

        // Başlık satırı: "Hesap Kodu" + "Evrak No" hücrelerini birlikte içeren ilk satır.
        var headerIndex = -1;
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows!.Count && headerIndex < 0; i++)
        {
            var upper = rows[i].Select(UpperTr).ToList();
            if (!upper.Contains("HESAP KODU") || !upper.Contains("EVRAK NO")) continue;

            headerIndex = i;
            for (var j = 0; j < upper.Count; j++)
                columns.TryAdd(upper[j], j);
        }

        if (headerIndex < 0)
            return BadRequest(new { message = "Fiş listesi başlık satırı bulunamadı (\"Hesap Kodu\" ve \"Evrak No\" kolonları yok). Luca fiş dökümünü olduğu gibi yükleyin." });

        int Col(string header) => columns.TryGetValue(header, out var index) ? index : -1;
        var codeCol = Col("HESAP KODU");
        var evrakCol = Col("EVRAK NO");
        var descCol = Col("FİŞ AÇIKLAMA");
        var vendorCol = Col("DETAY AÇIKLAMA");
        var debitCol = Col("BORÇ");
        var creditCol = Col("ALACAK");

        if (descCol < 0 || vendorCol < 0 || debitCol < 0 || creditCol < 0)
            return BadRequest(new { message = "Fiş listesinde beklenen kolonlar eksik (Fiş Açıklama, Detay Açıklama, Borç, Alacak)." });

        string CellAt(List<string> row, int col) => col >= 0 && col < row.Count ? row[col].Trim() : string.Empty;

        // Evrak No'ya göre grupla (dosya sırası korunur — bir evrak = bir fiş).
        var groups = new Dictionary<string, List<List<string>>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var evrak = CellAt(rows[i], evrakCol);
            if (evrak.Length == 0) continue;
            if (!groups.TryGetValue(evrak, out var list))
            {
                list = new List<List<string>>();
                groups[evrak] = list;
                groupOrder.Add(evrak);
            }
            list.Add(rows[i]);
        }

        if (groupOrder.Count == 0)
            return BadRequest(new { message = "Fiş listesinde işlenecek satır bulunamadı (Evrak No kolonu boş)." });

        // Mevcut kayıtlar tek sorguda — mizan ucundaki kalıpla birebir.
        var chartAccounts = (await _db.ChartAccounts
                .Where(a => a.ClientId == clientId)
                .ToListAsync(ct))
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var cariMapsByName = (await _db.VendorCariMaps
                .Where(m => m.ClientId == clientId)
                .ToListAsync(ct))
            .Where(m => m.VendorNameNormalized.Length > 0)
            .GroupBy(m => m.VendorNameNormalized, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var vendorDefaultsByName = (await _db.VendorDefaults
                .Where(v => v.ClientId == clientId)
                .ToListAsync(ct))
            .Where(v => v.VendorNameNormalized.Length > 0)
            .GroupBy(v => v.VendorNameNormalized, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var accountsSeen = 0;

        // Yerel upsert: görülen her hesap kodu hesap planına eklenir (ad yalnız boşken dolar).
        void UpsertChart(string code, string name)
        {
            if (chartAccounts.TryGetValue(code, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.Name) && name.Length > 0)
                    existing.Name = Truncate(name, 200);
                return;
            }
            var account = new ChartAccount
            {
                TenantId = tenantId,
                ClientId = clientId,
                Code = code,
                Name = Truncate(name, 200),
                Type = GuessAccountType(code)
            };
            _db.ChartAccounts.Add(account);
            chartAccounts[code] = account;
            accountsSeen++;
        }

        var vouchers = 0;
        var skippedSales = 0;
        var vendorMappings = 0;

        foreach (var evrak in groupOrder)
        {
            ct.ThrowIfCancellationRequested();
            var groupRows = groups[evrak];

            // Görülen TÜM hesap kodları (KDV kırılımı dahil) hesap planına eklenir.
            foreach (var row in groupRows)
            {
                var code = CellAt(row, codeCol);
                if (code.Length > 0 && char.IsDigit(code[0]) && MizanCodeRegex.IsMatch(code))
                    UpsertChart(code, string.Empty);
            }

            // "satış faturası" grupları öğrenmeye girmez (alış odaklıyız) — sayıyla raporlanır.
            var fisDescription = TextNormalizer.Normalize(CellAt(groupRows[0], descCol));
            if (fisDescription.Contains("satış faturası", StringComparison.Ordinal))
            {
                skippedSales++;
                continue;
            }

            vouchers++;

            // Grup çözümü: karşı hesap (320/120/100 alacaklı), KDV (191*), gider (kalan borçlu).
            string? cariCode = null;
            string? expenseCode = null;
            var expenseDebit = 0m;
            var vendorTitle = string.Empty;

            foreach (var row in groupRows)
            {
                var code = CellAt(row, codeCol);
                if (code.Length == 0) continue;

                if (vendorTitle.Length == 0)
                    vendorTitle = CellAt(row, vendorCol);

                var debit = ParseLucaAmount(CellAt(row, debitCol));
                var credit = ParseLucaAmount(CellAt(row, creditCol));

                var isCounterPrefix = code.StartsWith("320", StringComparison.Ordinal) ||
                                      code.StartsWith("120", StringComparison.Ordinal) ||
                                      code.StartsWith("100", StringComparison.Ordinal);

                if (isCounterPrefix && credit > 0)
                {
                    if (code.StartsWith("320", StringComparison.Ordinal))
                        cariCode = code; // satıcı carisi
                    continue;
                }

                if (code.StartsWith("191", StringComparison.Ordinal)) continue; // KDV satırı

                // Kalan borçlu satırlar gider adayıdır; en büyük borç esas alınır.
                if (!isCounterPrefix && debit > expenseDebit)
                {
                    expenseDebit = debit;
                    expenseCode = code;
                }
            }

            var nameKey = TextNormalizer.NormalizeCompanyName(vendorTitle);
            if (nameKey.Length == 0) continue; // satıcı unvanı yoksa öğrenme yazılamaz

            var mapped = false;

            // (a) normalize unvan → gider hesabı (VendorDefault; VKN dökümde yoktur → unvan anahtarı).
            if (expenseCode is not null)
            {
                if (vendorDefaultsByName.TryGetValue(nameKey, out var vendorDefault))
                {
                    if (vendorDefault.AccountCode == expenseCode)
                    {
                        vendorDefault.ApproveCount++;
                    }
                    else
                    {
                        vendorDefault.AccountCode = expenseCode; // dökümdeki son alışkanlık esas alınır
                        vendorDefault.ApproveCount = 1;
                    }
                }
                else
                {
                    var newDefault = new VendorDefault
                    {
                        TenantId = tenantId,
                        ClientId = clientId,
                        SenderTaxId = string.Empty, // fiş dökümünde VKN yok — unvan anahtarı kullanılır
                        VendorNameNormalized = nameKey,
                        AccountCode = expenseCode,
                        ApproveCount = 1
                    };
                    _db.VendorDefaults.Add(newDefault);
                    vendorDefaultsByName[nameKey] = newDefault;
                }
                mapped = true;
            }

            // (b) normalize unvan → cari kodu (VendorCariMap — mizan ucundaki mekanizmayla aynı).
            if (cariCode is not null)
            {
                if (cariMapsByName.TryGetValue(nameKey, out var map))
                {
                    map.CariCode = cariCode;
                    if (string.IsNullOrWhiteSpace(map.SenderTitle) && vendorTitle.Length > 0)
                        map.SenderTitle = Truncate(vendorTitle, 300);
                }
                else
                {
                    var newMap = new VendorCariMap
                    {
                        TenantId = tenantId,
                        ClientId = clientId,
                        SenderTaxId = string.Empty,
                        SenderTitle = Truncate(vendorTitle, 300),
                        VendorNameNormalized = nameKey,
                        CariCode = cariCode
                    };
                    _db.VendorCariMaps.Add(newMap);
                    cariMapsByName[nameKey] = newMap;
                }
                mapped = true;

                // Satıcı carisi + unvanı hesap planındaki cari kaydına da işlenir (autocomplete).
                if (chartAccounts.TryGetValue(cariCode, out var cariAccount) &&
                    string.IsNullOrWhiteSpace(cariAccount.Name) && vendorTitle.Length > 0)
                {
                    cariAccount.Name = Truncate(vendorTitle, 200);
                }
            }

            if (mapped) vendorMappings++;
        }

        await _db.SaveChangesAsync(ct);

        var messageParts = new List<string>
        {
            $"{vouchers} fiş işlendi",
            $"{vendorMappings} satıcı alışkanlığı öğrenildi (unvan → hesap/cari)",
            $"{accountsSeen} yeni hesap kodu hesap planına eklendi"
        };
        if (skippedSales > 0) messageParts.Add($"{skippedSales} satış faturası fişi atlandı");

        var result = new FisListesiImportResultResponse
        {
            Vouchers = vouchers,
            VendorMappings = vendorMappings,
            Accounts = accountsSeen,
            SkippedSales = skippedSales,
            Message = string.Join(", ", messageParts) + "."
        };

        _logger.LogInformation(
            "Geçmiş fiş listesi içe aktarma tamamlandı. ClientId: {ClientId}, Fiş: {Vouchers}, Eşleme: {Mappings}, Hesap: {Accounts}, Satış atlanan: {Skipped}",
            clientId, vouchers, vendorMappings, accountsSeen, skippedSales);

        return Ok(result);
    }

    // ---------- Luca hesap planı (CSV) içe aktarma ----------

    /// <summary>
    /// Luca hesap planı CSV çözümü: baştaki UTF-8 BOM varsa UTF-8, yoksa ISO-8859-9 (Latin-5).
    /// ISO-8859-9, Latin-1'in altı Türkçe harf ikamesidir (Ğ İ Ş ğ ı ş) — kod sayfası paketi
    /// gerektirmeden elle çözülür (2 MB sınırı belleğe almayı güvenli kılar).
    /// </summary>
    private static string DecodeHesapPlaniCsv(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = bytes[i] switch
            {
                0xD0 => 'Ğ',
                0xDD => 'İ',
                0xDE => 'Ş',
                0xF0 => 'ğ',
                0xFD => 'ı',
                0xFE => 'ş',
                var b => (char)b // kalan tüm baytlar Latin-1 ile birebir
            };
        }
        return new string(chars);
    }

    /// <summary>
    /// Luca CSV satır bölme: CRLF/CR/LF ve NEL (U+0085, ISO-8859-9'da 0x85) — dışa aktarımlar
    /// ikisini karışık kullanabilir. NEL bazı dosyalarda hesap ADI içinde dolgu karakteri olarak
    /// da görünür ("…" yer tutucular); bu bölmeden doğan kırpıntı parçalar hesap kodu deseninden
    /// geçemediği için ayrıştırmada zaten elenir.
    /// </summary>
    private static string[] SplitHesapPlaniLines(string text)
        => text.Split(
            new[] { "\r\n", "\r", "\n", "\u0085" },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// HESAP PLANI İÇE AKTARMA: Luca "Hesap Planı → Dışa Aktar" CSV dosyası OLDUĞU GİBİ yüklenir.
    /// Biçim: ';' ayraçlı (alanlarda ';' ve tırnak yoktur), başlık "Hesap Kodu*;Hesap Adı*;Vergi No;
    /// Vergi Dairesi;T.C. Kimlik No;...". Ad DOLU tüm satırlar (grup başlıkları dahil — ad araması
    /// için değerli) hesap planına (ChartAccount) upsert edilir; mevcut kaydın adı boşsa doldurulur.
    /// Vergi No veya TCKN dolu 120.*/320.* satırları kod motorunun cari hafızasına (VendorCariMap)
    /// hem VKN hem NORMALİZE UNVAN anahtarını taşıyan tek kayıtla yazılır — mizandan VKN'siz gelmiş
    /// unvan kaydı varsa VKN'si geri doldurulur. Sınırlar: 2 MB, yalnızca .csv.
    /// </summary>
    [HttpPost("api/coding/import-hesap-plani")]
    public async Task<ActionResult<HesapPlaniImportResultResponse>> ImportHesapPlani(
        [FromForm] Guid clientId,
        IFormFile? file,
        CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();
        var tenantId = _currentUser.TenantId.Value;

        if (clientId == Guid.Empty)
            return BadRequest(new { message = "İçe aktarma için bir mükellef seçin." });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Bir .csv dosyası seçin." });
        if (file.Length > MaxImportFileBytes)
            return BadRequest(new { message = "Dosya boyutu en fazla 2 MB olabilir." });
        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Yalnızca .csv dosyaları içe aktarılabilir. Luca'daki Hesap Planı → Dışa Aktar dosyasını olduğu gibi yükleyin." });

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await file.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        // Kodlama seçimi (BOM'a göre) + karışık satır sonu bölme; başlık satırı ve NEL
        // kırpıntıları hesap kodu deseninden geçemediği için aşağıda kendiliğinden elenir.
        var lines = SplitHesapPlaniLines(DecodeHesapPlaniCsv(bytes));

        var parsed = new List<(string Code, string Name, string TaxId)>();
        foreach (var line in lines.Take(MaxLucaImportRows))
        {
            var fields = line.Split(';');
            var code = fields[0].Trim();
            if (code.Length == 0 || !char.IsDigit(code[0]) || !MizanCodeRegex.IsMatch(code))
                continue; // başlık ("Hesap Kodu*"), boş ve kırpıntı satırlar

            var name = fields.Length > 1 ? fields[1].Trim() : string.Empty;
            var vergiNo = fields.Length > 2 ? fields[2].Trim() : string.Empty;
            var tckn = fields.Length > 4 ? fields[4].Trim() : string.Empty;

            // Cari eşleşme anahtarı: Vergi No öncelikli, yoksa TCKN; 10-11 hane değilse yok sayılır.
            var taxId = vergiNo.Length > 0 ? vergiNo : tckn;
            if (!ImportTaxIdRegex.IsMatch(taxId)) taxId = string.Empty;

            parsed.Add((code, name, taxId));
        }

        if (parsed.Count == 0)
            return BadRequest(new { message = "Dosyada hesap satırı bulunamadı. Luca'nın Hesap Planı → Dışa Aktar CSV dosyasını olduğu gibi yükleyin." });

        // Mevcut kayıtlar tek sorguda (satır başına sorgu yok) — mevcut import kalıbı.
        var chartAccounts = (await _db.ChartAccounts
                .Where(a => a.ClientId == clientId)
                .ToListAsync(ct))
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var allCariMaps = await _db.VendorCariMaps
            .Where(m => m.ClientId == clientId)
            .ToListAsync(ct);
        var cariMapsByTaxId = allCariMaps
            .Where(m => m.SenderTaxId.Length > 0)
            .GroupBy(m => m.SenderTaxId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var cariMapsByName = allCariMaps
            .Where(m => m.VendorNameNormalized.Length > 0)
            .GroupBy(m => m.VendorNameNormalized, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var namedRows = 0;
        var addedAccounts = 0;
        var cariCount = 0;

        foreach (var (code, name, taxId) in parsed)
        {
            ct.ThrowIfCancellationRequested();

            // (a) Ad DOLU tüm satırlar (grup başlıkları dahil) → hesap planı; ad boş satırın
            //     hesap planına katkısı yoktur (mevcut kaydın adı da ezilmez).
            if (name.Length > 0)
            {
                namedRows++;
                if (chartAccounts.TryGetValue(code, out var existing))
                {
                    if (string.IsNullOrWhiteSpace(existing.Name))
                        existing.Name = Truncate(name, 200);
                }
                else
                {
                    var account = new ChartAccount
                    {
                        TenantId = tenantId,
                        ClientId = clientId,
                        Code = code,
                        Name = Truncate(name, 200),
                        Type = GuessAccountType(code)
                    };
                    _db.ChartAccounts.Add(account);
                    chartAccounts[code] = account;
                    addedAccounts++;
                }
            }

            // (b) VKN/TCKN dolu 120.*/320.* satırları → cari hafıza (VKN + normalize unvan
            //     anahtarlarını TEK kayıt taşır; motor iki anahtarla da eşleştirebilir).
            if (taxId.Length == 0) continue;
            if (!code.StartsWith("120.", StringComparison.Ordinal) &&
                !code.StartsWith("320.", StringComparison.Ordinal))
            {
                continue;
            }

            var nameKey = TextNormalizer.NormalizeCompanyName(name);

            if (cariMapsByTaxId.TryGetValue(taxId, out var map))
            {
                map.CariCode = code; // hesap planındaki kod esas alınır (SMMM'nin güncel kodu)
                if (string.IsNullOrWhiteSpace(map.SenderTitle) && name.Length > 0)
                    map.SenderTitle = Truncate(name, 300);
                if (map.VendorNameNormalized.Length == 0 && nameKey.Length > 0)
                {
                    map.VendorNameNormalized = nameKey;
                    cariMapsByName.TryAdd(nameKey, map);
                }
            }
            else if (nameKey.Length > 0 && cariMapsByName.TryGetValue(nameKey, out var byName))
            {
                // Mizan/fiş listesinden VKN'siz gelmiş unvan kaydı: VKN geri doldurulur.
                byName.CariCode = code;
                if (string.IsNullOrWhiteSpace(byName.SenderTaxId))
                {
                    byName.SenderTaxId = taxId;
                    cariMapsByTaxId[taxId] = byName;
                }
                if (string.IsNullOrWhiteSpace(byName.SenderTitle) && name.Length > 0)
                    byName.SenderTitle = Truncate(name, 300);
            }
            else
            {
                var newMap = new VendorCariMap
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    SenderTaxId = taxId,
                    SenderTitle = Truncate(name, 300),
                    VendorNameNormalized = nameKey,
                    CariCode = code
                };
                _db.VendorCariMaps.Add(newMap);
                cariMapsByTaxId[taxId] = newMap;
                if (nameKey.Length > 0) cariMapsByName[nameKey] = newMap;
            }
            cariCount++;
        }

        await _db.SaveChangesAsync(ct);

        var result = new HesapPlaniImportResultResponse
        {
            Accounts = namedRows,
            CariCount = cariCount,
            Message = $"{namedRows} hesap satırı hesap planına işlendi ({addedAccounts} yeni), " +
                      $"{cariCount} VKN/TCKN'li cari (120/320) kod motorunun cari hafızasına yazıldı."
        };

        _logger.LogInformation(
            "Hesap planı (CSV) içe aktarma tamamlandı. ClientId: {ClientId}, Adlı satır: {Named}, Yeni hesap: {Added}, Cari: {Cari}",
            clientId, namedRows, addedAccounts, cariCount);

        return Ok(result);
    }

    // ---------- Faz 8 — B2: Tevkifat kodu listesi (onay çekmecesi açılır menüsü) ----------

    /// <summary>Global tevkifat kodu kuralları (kod + açıklama + oran). Salt okunur.</summary>
    [HttpGet("api/coding/withholding-rules")]
    public async Task<ActionResult<List<WithholdingRuleResponse>>> ListWithholdingRules(CancellationToken ct = default)
    {
        var rules = await _db.WithholdingRules
            .AsNoTracking()
            .OrderBy(r => r.Code)
            .ToListAsync(ct);

        return Ok(rules.Select(WithholdingRuleResponse.FromEntity).ToList());
    }

    // ---------- Faz 15: KKEG oranı (fiş detayı otomatik doldurma) ----------

    /// <summary>
    /// KKEG (binek gider kısıtı) yüzdesi: parametre merkezindeki binek.gider.kisit.oran
    /// (İNDİRİLEBİLİR %; varsayılan 70) değerinden türetilir → rate = 100 − indirilebilir
    /// (varsayılan 30). Tüm oturum açmış kullanıcılara açıktır; web fiş detayı bu değeri
    /// bir kez çekip "KKEG tutarı = matrah × oran" önerisini hesaplar.
    /// </summary>
    [HttpGet("api/coding/parameters/kkeg")]
    public async Task<ActionResult<KkegParametersResponse>> GetKkegParameters(CancellationToken ct = default)
    {
        await _parameters.EnsureLoadedAsync(ct);

        // KkegAnalyzer kural (a) ile AYNI parametre ve varsayılan (indirilebilir %70 → KKEG %30).
        var deductiblePercent = _parameters.GetDecimal("binek.gider.kisit.oran", 70m);
        var rate = Math.Clamp(100m - deductiblePercent, 0m, 100m);

        return Ok(new KkegParametersResponse { Rate = rate });
    }

    // ---------- Kurallar (Katman 2) ----------

    [HttpGet("api/coding/rules")]
    public async Task<ActionResult<List<CodingRuleResponse>>> ListRules(
        [FromQuery] Guid? clientId = null,
        CancellationToken ct = default)
    {
        var query = _db.CodingRules.AsNoTracking().AsQueryable();

        if (clientId.HasValue && clientId.Value != Guid.Empty)
            query = query.Where(r => r.ClientId == null || r.ClientId == clientId.Value);

        var rules = await query
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);

        // Kapsam etiketi için mükellef unvanları (tek sorguda).
        var clientIds = rules.Where(r => r.ClientId.HasValue).Select(r => r.ClientId!.Value).Distinct().ToList();
        var titles = await _db.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        return Ok(rules
            .Select(r => CodingRuleResponse.FromEntity(
                r, r.ClientId.HasValue && titles.TryGetValue(r.ClientId.Value, out var t) ? t : null))
            .ToList());
    }

    [HttpPost("api/coding/rules")]
    public async Task<ActionResult<CodingRuleResponse>> CreateRule(
        [FromBody] CodingRuleSaveRequest request,
        CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var pattern = request.Pattern.Trim();

        // Desen geçerlilik kontrolü: ";" içermiyorsa regex olarak derlenebilmeli.
        if (!pattern.Contains(';'))
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                return BadRequest(new { message = "Geçersiz desen." });
            }
        }
        else if (pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0)
        {
            return BadRequest(new { message = "Geçersiz desen." });
        }

        // Mükellef kapsamı seçildiyse mükellef bu tenant'ta var olmalı.
        if (request.ClientId.HasValue)
        {
            var clientExists = await _db.Clients.AnyAsync(c => c.Id == request.ClientId.Value, ct);
            if (!clientExists)
                return BadRequest(new { message = "Seçilen mükellef bulunamadı." });
        }

        var rule = new CodingRule
        {
            TenantId = _currentUser.TenantId.Value,
            ClientId = request.ClientId,
            Pattern = pattern,
            AccountCode = request.AccountCode.Trim(),
            Priority = request.Priority,
            IsActive = request.IsActive
        };

        _db.CodingRules.Add(rule);
        await _db.SaveChangesAsync(ct);

        string? clientTitle = null;
        if (rule.ClientId.HasValue)
        {
            clientTitle = await _db.Clients
                .AsNoTracking()
                .Where(c => c.Id == rule.ClientId.Value)
                .Select(c => c.Title)
                .FirstOrDefaultAsync(ct);
        }

        return Ok(CodingRuleResponse.FromEntity(rule, clientTitle));
    }

    [HttpDelete("api/coding/rules/{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct = default)
    {
        var rule = await _db.CodingRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
            return NotFound(new { message = "Kural bulunamadı." });

        _db.CodingRules.Remove(rule);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Mükellef kodlama ayarları ----------

    [HttpGet("api/coding/settings/{clientId:guid}")]
    public async Task<ActionResult<ClientCodingSettingsResponse>> GetSettings(Guid clientId, CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var settings = await _engine.GetOrCreateSettingsAsync(clientId, ct);
        return Ok(ClientCodingSettingsResponse.FromEntity(settings));
    }

    [HttpPut("api/coding/settings/{clientId:guid}")]
    public async Task<ActionResult<ClientCodingSettingsResponse>> UpdateSettings(
        Guid clientId,
        [FromBody] ClientCodingSettingsUpdateRequest request,
        CancellationToken ct = default)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var settings = await _engine.GetOrCreateSettingsAsync(clientId, ct);

        settings.DefaultExpenseAccount = request.DefaultExpenseAccount.Trim();
        settings.CariPrefix = request.CariPrefix.Trim();
        settings.CariNextNumber = request.CariNextNumber;
        settings.AutoApplyThreshold = request.AutoApplyThreshold;
        settings.HasCompanyVehicle = request.HasCompanyVehicle; // Faz 8 (B3): akaryakıt KKEG kuralı anahtarı

        await _db.SaveChangesAsync(ct);
        return Ok(ClientCodingSettingsResponse.FromEntity(settings));
    }

    // ---------- Luca dışa aktarma (Fiş Aktarım Şablonu) ----------

    /// <summary>
    /// İndirme öncesi önizleme: aralıktaki onaylanmış fatura/fiş sayısı, toplam tutarlar VE
    /// zorunlu alan ön-kontrolü (MissingFields doluysa export engellenecektir — Türkçe liste).
    /// Faz 14 (C): source=all|fis|fatura kaynak filtresi (varsayılan all — mevcut davranış).
    /// </summary>
    [HttpGet("api/export/luca/preview")]
    public async Task<ActionResult<LucaExportPreviewResponse>> ExportPreview(
        [FromQuery] Guid? clientId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string source = "all",
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Dışa aktarma için bir mükellef seçin." });

        var normalizedSource = NormalizeSource(source, out var sourceError);
        if (sourceError is not null)
            return BadRequest(new { message = sourceError });

        var range = ResolveRange(from, to, out var rangeError);
        if (rangeError is not null)
            return BadRequest(new { message = rangeError });

        var (invoices, receipts) = await LoadApprovedDocumentsAsync(clientId.Value, range, normalizedSource, ct);

        // Zorunlu alan ön-kontrolü: satırlar üretilir ama dosya yazılmaz (yalnızca hata listesi).
        var build = await _exporter.BuildTemplateAsync(invoices, receipts, ct);

        return Ok(new LucaExportPreviewResponse
        {
            ApprovedCount = invoices.Count,
            TotalAmount = invoices.Sum(e => e.PayableAmount),
            ApprovedReceiptCount = receipts.Count,
            ReceiptTotalAmount = receipts.Sum(r => r.TotalAmount ?? 0m),
            MissingFields = build.Errors
        });
    }

    /// <summary>
    /// Onaylanmış fatura + fişleri Luca "Fiş Aktarım Şablonu"na birebir uygun dosya olarak indirir.
    /// format=xlsx (varsayılan): sayfa adı "Fiş Aktarım Şablon", 14 kolon (A→N);
    /// format=csv: AYNI kolon sırası/başlıklarla ;-ayraçlı UTF-8 BOM'lu CSV.
    /// Faz 14 (C): source=all|fis|fatura — fis: yalnız onaylı fişler, fatura: yalnız onaylı
    /// faturalar, all (varsayılan): mevcut davranış birebir korunur.
    /// Zorunlu alanı eksik belge varsa export ENGELLENİR ve eksikler Türkçe listelenir.
    /// </summary>
    [HttpGet("api/export/luca")]
    public async Task<IActionResult> ExportLuca(
        [FromQuery] Guid? clientId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string format = "xlsx",
        [FromQuery] string source = "all",
        CancellationToken ct = default)
    {
        if (clientId is null || clientId == Guid.Empty)
            return BadRequest(new { message = "Dışa aktarma için bir mükellef seçin." });

        var normalizedFormat = format.Trim().ToLowerInvariant();
        if (normalizedFormat != "xlsx" && normalizedFormat != "csv")
            return BadRequest(new { message = "Geçersiz dosya biçimi. 'xlsx' veya 'csv' seçin." });

        var normalizedSource = NormalizeSource(source, out var sourceError);
        if (sourceError is not null)
            return BadRequest(new { message = sourceError });

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var range = ResolveRange(from, to, out var rangeError);
        if (rangeError is not null)
            return BadRequest(new { message = rangeError });

        var (invoices, receipts) = await LoadApprovedDocumentsAsync(clientId.Value, range, normalizedSource, ct);

        if (invoices.Count == 0 && receipts.Count == 0)
        {
            return BadRequest(new
            {
                message = normalizedSource switch
                {
                    "fis" => "Seçilen aralıkta onaylanmış fiş yok.",
                    "fatura" => "Seçilen aralıkta onaylanmış fatura yok.",
                    _ => "Seçilen aralıkta onaylanmış fatura veya fiş yok."
                }
            });
        }

        // Faz 7 (E4): KDV hesap eşlemesi + Luca belge türü/para birimi parametre merkezinden okunur.
        var build = await _exporter.BuildTemplateAsync(invoices, receipts, ct);

        // Ön-kontrol: zorunlu alanı eksik belge varsa export ENGELLENİR (hangi belge/alan, Türkçe).
        if (!build.IsValid)
        {
            return BadRequest(new
            {
                message = "Dışa aktarma engellendi: zorunlu alanları eksik belgeler var. " +
                          string.Join(" ", build.Errors.Take(3)) +
                          (build.Errors.Count > 3 ? $" (+{build.Errors.Count - 3} eksik daha)" : string.Empty),
                errors = build.Errors
            });
        }

        var slug = LucaExcelExporter.Slugify(client.Title);
        // Faz 14 (C): kaynak filtresi dosya adında da görünür (all → ek yok, mevcut ad birebir).
        var sourceSuffix = normalizedSource switch
        {
            "fis" => "-fisler",
            "fatura" => "-faturalar",
            _ => string.Empty
        };
        var baseName = $"luca-fis-aktarim-{slug}{sourceSuffix}-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}";

        return normalizedFormat == "xlsx"
            ? File(_exporter.ToXlsx(build.Rows),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"{baseName}.xlsx")
            : File(_exporter.ToCsv(build.Rows), "text/csv; charset=utf-8", $"{baseName}.csv");
    }

    /// <summary>
    /// Çoklu mükellef Luca export (ZIP): seçilen (boşsa TÜM aktif) mükelleflerin her biri için
    /// MEVCUT LucaExcelExporter ile AYRI bir XLSX üretilir (şablon birebir aynı) ve tek ZIP
    /// olarak indirilir. Zip içi ad: "{MukellefAdi}_{başlangıç}_{bitiş}_luca.xlsx" (Türkçe
    /// karakterler korunur — SanitizeFileName). Verisi olmayan mükellef zip'e girmez; hiç veri
    /// yoksa 404. Zorunlu alanı eksik belge varsa export tekil uçtaki kuralla ENGELLENİR
    /// (hangi mükellef/belge, Türkçe listelenir). Tek mükellef seçiliyken web mevcut tekil
    /// XLSX ucunu kullanır — bu uç davranışı değiştirmez.
    /// </summary>
    [HttpGet("api/export/luca-bulk")]
    public async Task<IActionResult> ExportLucaBulk(
        [FromQuery] List<Guid>? clientIds = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string source = "all",
        CancellationToken ct = default)
    {
        var normalizedSource = NormalizeSource(source, out var sourceError);
        if (sourceError is not null)
            return BadRequest(new { message = sourceError });

        var range = ResolveRange(from, to, out var rangeError);
        if (rangeError is not null)
            return BadRequest(new { message = rangeError });

        var requestedIds = (clientIds ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();

        // Boş liste = tüm aktif mükellefler (arşivlenmişler hariç — mevcut "tümü" kalıbı).
        var clients = requestedIds.Count == 0
            ? await _db.Clients.AsNoTracking().Where(c => !c.IsArchived).OrderBy(c => c.Title).ToListAsync(ct)
            : await _db.Clients.AsNoTracking().Where(c => requestedIds.Contains(c.Id)).OrderBy(c => c.Title).ToListAsync(ct);

        if (clients.Count == 0)
            return NotFound(new { message = "Dışa aktarılacak mükellef bulunamadı." });

        // Önce tüm mükelleflerin şablonları üretilir; herhangi birinde zorunlu alan eksiği
        // varsa TEK belge bile indirilmez (tekil uçla aynı çift-validasyon ilkesi).
        var files = new List<(string EntryName, byte[] Content)>();
        var allErrors = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();

            var (invoices, receipts) = await LoadApprovedDocumentsAsync(client.Id, range, normalizedSource, ct);
            if (invoices.Count == 0 && receipts.Count == 0)
                continue; // verisi olmayan mükellef zip'e girmez

            var build = await _exporter.BuildTemplateAsync(invoices, receipts, ct);
            if (!build.IsValid)
            {
                allErrors.AddRange(build.Errors.Select(e => $"{client.Title}: {e}"));
                continue;
            }

            // Zip içi ad: Türkçe karakter korunur; yalnız geçersiz karakterler temizlenir.
            var baseName = $"{LucaExcelExporter.SanitizeFileName(client.Title)}_{range.From:yyyyMMdd}_{range.To:yyyyMMdd}_luca";
            var entryName = $"{baseName}.xlsx";
            var counter = 2;
            while (!usedNames.Add(entryName))
                entryName = $"{baseName}_{counter++}.xlsx"; // aynı unvanlı iki mükellef çakışmasın

            files.Add((entryName, _exporter.ToXlsx(build.Rows)));
        }

        if (allErrors.Count > 0)
        {
            return BadRequest(new
            {
                message = "Dışa aktarma engellendi: zorunlu alanları eksik belgeler var. " +
                          string.Join(" ", allErrors.Take(3)) +
                          (allErrors.Count > 3 ? $" (+{allErrors.Count - 3} eksik daha)" : string.Empty),
                errors = allErrors
            });
        }

        if (files.Count == 0)
        {
            return NotFound(new
            {
                message = normalizedSource switch
                {
                    "fis" => "Seçilen aralıkta hiçbir mükellefte onaylanmış fiş yok.",
                    "fatura" => "Seçilen aralıkta hiçbir mükellefte onaylanmış fatura yok.",
                    _ => "Seçilen aralıkta hiçbir mükellefte onaylanmış fatura veya fiş yok."
                }
            });
        }

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in files)
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(content, ct);
            }
        }

        return File(
            zipStream.ToArray(),
            "application/zip",
            $"Luca_Fisleri_{range.From:yyyyMMdd}-{range.To:yyyyMMdd}.zip");
    }

    /// <summary>
    /// Aralıktaki onaylanmış faturaları (kalemleriyle) ve fişleri yükler (export + önizleme ortak).
    /// Faz 14 (C): source filtresi — "fis": faturalar hiç sorgulanmaz (boş liste), "fatura":
    /// fişler hiç sorgulanmaz; "all": mevcut davranış birebir (her iki liste de dolu).
    /// LucaExcelExporter DEĞİŞMEDİ — boş liste alan taraf şablona satır üretmez.
    /// </summary>
    private async Task<(List<EInvoice> Invoices, List<Receipt> Receipts)> LoadApprovedDocumentsAsync(
        Guid clientId, (DateTime From, DateTime To) range, string source, CancellationToken ct)
    {
        var invoices = source == "fis"
            ? new List<EInvoice>()
            : await _db.EInvoices
                .AsNoTracking()
                .Include(e => e.Lines)
                .Where(e => e.ClientId == clientId
                            && e.CodingStatus == CodingStatus.Approved
                            && e.IssueDate >= range.From && e.IssueDate <= range.To)
                .OrderBy(e => e.IssueDate)
                .ToListAsync(ct);

        // Faz 3: onaylanmış fişler de aynı dosyaya dahil edilir (Belge Türü kolonu ayırt eder).
        var receipts = source == "fatura"
            ? new List<Receipt>()
            : await _db.Receipts
                .AsNoTracking()
                .Include(r => r.VatBreakdowns) // Faz 16: export KDV satırlarını oran bazında üretir
                .Where(r => r.ClientId == clientId
                            && r.Status == ReceiptStatus.Approved
                            && r.ReceiptDate != null
                            && r.ReceiptDate >= range.From && r.ReceiptDate <= range.To)
                .OrderBy(r => r.ReceiptDate)
                .ToListAsync(ct);

        return (invoices, receipts);
    }

    /// <summary>Faz 14 (C): kaynak filtresi doğrulaması — all | fis | fatura (küçük/büyük harf duyarsız).</summary>
    private static string NormalizeSource(string source, out string? error)
    {
        error = null;
        var normalized = (source ?? "all").Trim().ToLowerInvariant();
        if (normalized.Length == 0) normalized = "all";
        if (normalized is not ("all" or "fis" or "fatura"))
        {
            error = "Geçersiz kaynak filtresi. 'all', 'fis' veya 'fatura' seçin.";
            return "all";
        }
        return normalized;
    }

    // ---------- Yardımcılar ----------

    private static (DateTime From, DateTime To) ResolveRange(DateTime? from, DateTime? to, out string? error)
    {
        error = null;

        // Varsayılan aralık: son 30 gün (senkron ucuyla tutarlı).
        var toDate = (to ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1); // gün sonu dahil
        var fromDate = (from ?? toDate.AddDays(-30)).Date;
        toDate = DateTime.SpecifyKind(toDate, DateTimeKind.Utc);
        fromDate = DateTime.SpecifyKind(fromDate, DateTimeKind.Utc);

        if (fromDate > toDate)
            error = "Başlangıç tarihi bitiş tarihinden sonra olamaz.";
        else if ((toDate - fromDate).TotalDays > MaxRangeDays)
            error = $"Tarih aralığı en fazla {MaxRangeDays} gün olabilir.";

        return (fromDate, toDate);
    }

    private static CodingResultResponse BuildCodingResult(EInvoice invoice, CodingOutcome outcome)
    {
        var sources = outcome.Lines.ToDictionary(l => l.LineId, l => l.Source);

        return new CodingResultResponse
        {
            EInvoiceId = invoice.Id,
            CodingStatus = invoice.CodingStatus.ToString(),
            Message = invoice.CodingStatus == CodingStatus.AutoCoded
                ? "Fatura otomatik kodlandı."
                : "Fatura kodlandı; düşük güvenli satırlar onay bekliyor.",
            Lines = invoice.Lines
                .OrderBy(l => l.LineNumber)
                .Select(l => new CodingLineSuggestionResponse
                {
                    LineId = l.Id,
                    LineNumber = l.LineNumber,
                    ItemName = l.ItemName,
                    SuggestedAccountCode = l.SuggestedAccountCode,
                    Confidence = l.Confidence,
                    CariCode = l.CariCode,
                    Source = sources.TryGetValue(l.Id, out var s) ? s : string.Empty
                })
                .ToList()
        };
    }
}
