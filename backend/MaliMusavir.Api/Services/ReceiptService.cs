using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using MaliMusavir.Api.Ocr;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Tek dosyanın yükleme+işleme sonucu (dosya bazlı Türkçe hata mesajı için).</summary>
public class ReceiptUploadOutcome
{
    public bool Success { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public Receipt? Receipt { get; init; }
}

/// <summary>Onay/red işlemi sonucu.</summary>
public class ReceiptOperationOutcome
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Faz 14 (B): Aylık OCR kota kapısı. İstek başına BİR kez yüklenir (GetQuotaGateAsync);
/// çoklu dosya yüklemesinde TryConsume her kabul edilen dosyada sayacı bellekte artırır —
/// böylece "kalan kota &lt; dosya sayısı" durumunda sığanlar kabul edilir, kalanlar dosya
/// bazlı reddedilir (ek DB sorgusu olmadan). Plan yoksa/limitsizse Unlimited=true olur.
/// </summary>
public class ReceiptQuotaGate
{
    public int Used { get; private set; }
    public int Quota { get; init; }
    public bool Unlimited { get; init; }
    public int Remaining => Unlimited ? int.MaxValue : Math.Max(0, Quota - Used);

    public ReceiptQuotaGate(int used, int quota, bool unlimited)
    {
        Used = used;
        Quota = quota;
        Unlimited = unlimited;
    }

    /// <summary>Bir fişlik kota tüketmeyi dener; kota doluysa false döner (sayaç artmaz).</summary>
    public bool TryConsume()
    {
        if (Unlimited) return true;
        if (Used >= Quota) return false;
        Used++;
        return true;
    }

    /// <summary>Kota dolduğunda kullanıcıya gösterilen Türkçe mesaj.</summary>
    public string ExceededMessage =>
        $"Aylık fiş kotanız doldu ({Used}/{Quota}). Paketinizi yükseltin veya gelecek ayı bekleyin.";
}

/// <summary>Kuyruk OCR işinin sonucu — OutboundJobService dispatcher sözleşmesiyle birebir.</summary>
public sealed record ReceiptOcrJobResult(bool Success, bool Permanent, string? Error)
{
    public static ReceiptOcrJobResult Ok() => new(true, false, null);
    public static ReceiptOcrJobResult Retry(string error) => new(false, false, error);
    public static ReceiptOcrJobResult Fail(string error) => new(false, true, error);
}

/// <summary>
/// Fiş işleme hattı (blueprint M3 / §6.2). Faz 14 revizyonu — SENKRON → ASENKRON:
/// yükleme ucu artık yalnızca doğrula → temp'e orijinali yaz → Receipt (Processing) oluştur →
/// OutboundJob "ReceiptOcr" kuyruğuna at → HEMEN döner. OCR + alan çıkarımı + gider
/// gösterilebilirlik + kod motoru önerisi + plaka/KKEG analizi ARKA PLANDA
/// (ProcessQueuedOcrAsync, OutboundJobWorker tenant scope'unda) çalışır ve fiş
/// NeedsReview'a düşer. Fişte TAM otomatik onay yoktur — insan onayı şarttır;
/// güven eşiği üstündeki fişler yalnızca "otomatik onaya hazır" rozetiyle işaretlenir.
/// Onay, fatura onayıyla aynı öğrenme yoluna (AccountMemory/VendorDefault) yazılır.
///
/// DAVRANIŞ FARKI (Faz 14): senkron akış OCR okuyamadığında dosyayı siliyordu ve kullanıcıya
/// anında hata dönüyordu; kuyruk akışında fiş SİLİNMEZ — görsel kalıcıya taşınır, fiş
/// OcrWarning uyarısıyla NeedsReview'a düşer ve SMMM alanları elle doldurur (koru-ve-uyar).
/// </summary>
public class ReceiptService
{
    /// <summary>Belge bu kadar günden eskiyse doğrulama ekranında uyarı gösterilir (eligible kalır).</summary>
    public const int OldDocumentWarningDays = 30;

    /// <summary>"Otomatik onaya hazır" rozeti için birleşik güven eşiği (fişte oto-onay YOK, yalnız rozet).</summary>
    public const double AutoApproveReadyThreshold = 0.85;

    public const string IneligibleAlcoholTobaccoReason = "Alkol/tütün giderleri indirim konusu yapılamaz.";

    /// <summary>
    /// Gider gösterilebilirlik reddini tetikleyen anahtar kelimeler (normalize metinde aranır).
    /// Basit kural seti — KKEG/gider kısıtı otomatiği (B3) sonraki fazda genişletilecek.
    /// </summary>
    private static readonly string[] IneligibleKeywords =
    {
        "alkol", "bira", "şarap", "rakı", "viski", "votka", "cin", "likör",
        "tütün", "sigara", "puro", "nargile", "tekel"
    };

    private readonly AppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IReceiptOcrProvider _ocr;
    private readonly CodingEngine _engine;
    private readonly KkegAnalyzer _kkegAnalyzer;
    private readonly OutboundJobService _jobs;
    private readonly ILogger<ReceiptService> _logger;

    public ReceiptService(
        AppDbContext db,
        IFileStorage storage,
        IReceiptOcrProvider ocr,
        CodingEngine engine,
        KkegAnalyzer kkegAnalyzer,
        OutboundJobService jobs,
        ILogger<ReceiptService> logger)
    {
        _db = db;
        _storage = storage;
        _ocr = ocr;
        _engine = engine;
        _kkegAnalyzer = kkegAnalyzer;
        _jobs = jobs;
        _logger = logger;
    }

    // ---------- Faz 14 (B): Aylık OCR kota denetimi ----------

    /// <summary>
    /// Tenant'ın bu takvim ayı kota durumunu yükler (tenant GENELİ — mükelleften bağımsız):
    /// kullanılan = bu ay oluşturulmuş Receipt sayısı, kota = tenant planının MonthlyOcrQuota'sı.
    /// Plan yoksa (eski kayıt) veya kota sınırsız benzeri (&lt;=0 ya da int.MaxValue) ise
    /// denetim atlanır (Unlimited=true).
    /// </summary>
    public async Task<ReceiptQuotaGate> GetQuotaGateAsync(Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = monthStart.AddMonths(1);

        // Tenant filtresinden bağımsız güvenli sayım (worker/HTTP her iki bağlamda da doğru).
        var used = await _db.Receipts
            .IgnoreQueryFilters()
            .CountAsync(r => r.TenantId == tenantId
                             && r.CreatedAt >= monthStart && r.CreatedAt < nextMonthStart, ct);

        var quota = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Plan != null ? (int?)t.Plan.MonthlyOcrQuota : null)
            .FirstOrDefaultAsync(ct);

        // Plan yoksa denetim atlanır; 0/negatif veya int.MaxValue kota "sınırsız" kabul edilir.
        var unlimited = quota is null || quota <= 0 || quota == int.MaxValue;
        return new ReceiptQuotaGate(used, quota ?? 0, unlimited);
    }

    /// <summary>
    /// Faz 14 (A): Tek dosyayı doğrular, orijinali temp'e yazar, Processing durumlu Receipt
    /// oluşturur ve "ReceiptOcr" kuyruk işini açar — OCR BEKLENMEZ, hemen döner.
    /// Doğrulama (10 MB/uzantı) ve kota hataları eskisi gibi dosya bazlı anında döner.
    /// quota null verilirse kota denetimi atlanır (çağıran kapıyı kendisi yönetmiyordur).
    /// </summary>
    public async Task<ReceiptUploadOutcome> UploadAndEnqueueAsync(
        Client client, Guid? uploadedByUserId, string fileName, long length, Stream content,
        ReceiptQuotaGate? quota, CancellationToken ct, ReceiptDocumentKind documentKind = ReceiptDocumentKind.Fis)
    {
        var safeName = Path.GetFileName(fileName); // istemci yol bilgisi ayıklanır

        var validationError = _storage.ValidateReceiptFile(safeName, length);
        if (validationError is not null)
            return new ReceiptUploadOutcome { Success = false, FileName = safeName, ErrorMessage = validationError };

        // Faz 14 (B): kota kapısı — kalan kota dosya sayısından azsa sığanlar kabul edilir,
        // kalanlar bu Türkçe mesajla dosya bazlı reddedilir.
        if (quota is not null && !quota.TryConsume())
            return new ReceiptUploadOutcome { Success = false, FileName = safeName, ErrorMessage = quota.ExceededMessage };

        // Faz 12 kuralı korunur — "önce OCR, sonra sıkıştırma": orijinal temp/ altına
        // DOKUNULMADAN yazılır; kuyruktaki iş OCR'ı bu orijinalden çalıştırır, bitince
        // görseli normalize edip kalıcıya taşır ve temp'i siler (ProcessQueuedOcrAsync).
        string tempPath;
        try
        {
            tempPath = await _storage.SaveReceiptTempAsync(safeName, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fiş dosyası kaydedilemedi. ClientId: {ClientId}, Dosya: {File}", client.Id, safeName);
            return new ReceiptUploadOutcome
            {
                Success = false,
                FileName = safeName,
                ErrorMessage = "Dosya kaydedilemedi. Lütfen tekrar deneyin."
            };
        }

        // Fiş kaydı: OCR alanları BOŞ, StoredPath geçici yolu gösterir (önizleme bu yoldan
        // çalışır); kuyruk işi bitince StoredPath kalıcı sıkıştırılmış kopyaya güncellenir.
        var receipt = new Receipt
        {
            TenantId = client.TenantId,
            ClientId = client.Id,
            FileName = safeName,
            StoredPath = tempPath,
            UploadedByUserId = uploadedByUserId,
            Status = ReceiptStatus.Processing,
            // Faz 16: belge türü (Gider Fişi / Z Raporu) — kuyruk işi OCR prompt'unu buna göre seçer.
            DocumentKind = documentKind
        };

        _db.Receipts.Add(receipt);
        await _db.SaveChangesAsync(ct);

        // Kuyruğa al: payload benzersiz (receiptId) olduğundan mükerrer iş açılmaz.
        await _jobs.EnqueueAsync(client.TenantId, OutboundJobService.TypeReceiptOcr,
            new OutboundJobService.ReceiptOcrPayload { ReceiptId = receipt.Id }, ct);

        _logger.LogInformation(
            "Fiş kuyruğa alındı. ReceiptId: {Id}, ClientId: {ClientId}, Dosya: {File}",
            receipt.Id, client.Id, safeName);

        return new ReceiptUploadOutcome { Success = true, FileName = safeName, Receipt = receipt };
    }

    /// <summary>
    /// Faz 14 (A): Kuyruk işinin OCR adımı — OutboundJobWorker'ın tenant scope'unda çalışır.
    /// Akış: fişi yükle (IgnoreQueryFilters + TenantId kontrolü) → temp'teki ORİJİNALDEN OCR →
    /// görseli normalize edip kalıcıya taşı → temp'i sil → alanları doldur → NeedsReview.
    /// Hata sözleşmesi:
    ///  - OCR geçici (retryable) hata + deneme hakkı varsa → Retry (üstel geri çekilme; temp KORUNUR).
    ///  - OCR kalıcı hata (okunamadı vb.) → fiş SİLİNMEZ: OcrWarning + NeedsReview (koru-ve-uyar,
    ///    Faz 14 davranış farkı — senkron akış dosyayı silip anında hata dönüyordu), iş Basarili.
    ///  - Son denemede hâlâ geçici hata → fiş yine OcrWarning + NeedsReview'a düşürülür
    ///    ("İşleniyor"da sonsuza dek takılı fiş kalmaz), iş Basarisiz (hata kuyruğunda görünür).
    /// </summary>
    public async Task<ReceiptOcrJobResult> ProcessQueuedOcrAsync(
        Guid receiptId, Guid tenantId, bool isLastAttempt, CancellationToken ct)
    {
        // Worker tenant scope deseni: filtreden bağımsız yükle + TenantId'yi işin tenant'ıyla doğrula.
        var receipt = await _db.Receipts
            .IgnoreQueryFilters()
            .Include(r => r.VatBreakdowns)
            .FirstOrDefaultAsync(r => r.Id == receiptId && r.TenantId == tenantId, ct);

        if (receipt is null)
            return ReceiptOcrJobResult.Fail("Fiş bulunamadı (silinmiş olabilir).");

        if (receipt.Status != ReceiptStatus.Processing)
            return ReceiptOcrJobResult.Ok(); // kuyrukta beklerken elle işlenmiş/reddedilmiş — iş amacına ulaştı

        var client = await _db.Clients
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == receipt.ClientId && c.TenantId == tenantId, ct);

        if (client is null)
            return ReceiptOcrJobResult.Fail("Fişin mükellefi bulunamadı (silinmiş olabilir).");

        var tempPath = receipt.StoredPath;
        if (!_storage.Exists(tempPath))
        {
            // Geçici dosya kaybolmuş (disk temizliği vb.) — fiş korunur, elle doldurulur.
            receipt.OcrWarning = "Fiş görseli bulunamadığı için otomatik okunamadı — alanları elle doldurun.";
            receipt.Status = ReceiptStatus.NeedsReview;
            await _db.SaveChangesAsync(ct);
            return ReceiptOcrJobResult.Ok();
        }

        var fileLength = new FileInfo(_storage.GetFullPath(tempPath)).Length;

        // OCR HER ZAMAN temp'teki orijinalden çalışır (Faz 12: görüntü kalitesi kaybı sıfır).
        // Faz 16: belge türü sağlayıcıya iletilir — Z raporu farklı prompt'la okunur.
        var ocrResult = await _ocr.ExtractAsync(
            _storage.GetFullPath(tempPath), receipt.FileName, fileLength,
            (Ocr.OcrDocumentKind)receipt.DocumentKind, ct);

        if (!ocrResult.Success || ocrResult.Data is null)
        {
            var error = ocrResult.ErrorMessage ?? "Fiş okunamadı. Daha net bir görselle tekrar deneyin.";

            // Geçici hata + deneme hakkı var → job'ın üstel yeniden deneme mekanizması devrede
            // kalır; temp dosya SİLİNMEZ (sonraki deneme aynı orijinali kullanır).
            if (ocrResult.Retryable && !isLastAttempt)
                return ReceiptOcrJobResult.Retry(error);

            // Kalıcı hata (veya son denemede hâlâ geçici hata): koru-ve-uyar.
            await PromoteAndBindAsync(receipt, client.TenantId, tempPath, ct);
            receipt.OcrWarning = $"Fiş otomatik okunamadı: {error} Alanları elle doldurup onaylayabilirsiniz.";
            receipt.Status = ReceiptStatus.NeedsReview;
            await SyncLinkedPortalUploadsAsync(receipt, tenantId, ct);
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Fiş OCR kalıcı hata — koru-ve-uyar uygulandı. ReceiptId: {Id}, Hata: {Error}", receipt.Id, error);

            return ocrResult.Retryable && isLastAttempt
                ? ReceiptOcrJobResult.Fail(error)   // altyapı hatası kuyrukta dürüstçe görünsün
                : ReceiptOcrJobResult.Ok();          // okunamayan fiş: iş görevini tamamladı
        }

        // OCR başarılı → kalıcı sıkıştırılmış kopya + alan doldurma + analizler.
        await PromoteAndBindAsync(receipt, client.TenantId, tempPath, ct);
        if (receipt.DocumentKind == ReceiptDocumentKind.ZRaporu)
            ApplyZReportExtraction(receipt, ocrResult.Data);
        else
            await ApplyOcrExtractionAsync(receipt, client, ocrResult.Data, ct);
        receipt.Status = ReceiptStatus.NeedsReview;
        await SyncLinkedPortalUploadsAsync(receipt, tenantId, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Fiş kuyrukta işlendi. ReceiptId: {Id}, Satıcı: {Vendor}, Güven: {Confidence}",
            receipt.Id, receipt.VendorName, receipt.Confidence);

        return ReceiptOcrJobResult.Ok();
    }

    /// <summary>
    /// Temp'teki orijinali normalize edip kalıcı konuma taşır, fişin StoredPath/FileName'ini
    /// nihai kopyaya bağlar ve temp'i siler. Taşıma başarısız olursa fiş TEMP yolunda kalır
    /// (önizleme çalışmaya devam eder) — akış bozulmaz, yalnızca loglanır.
    /// </summary>
    private async Task PromoteAndBindAsync(Receipt receipt, Guid tenantId, string tempPath, CancellationToken ct)
    {
        try
        {
            var storedPath = await _storage.PromoteReceiptFromTempAsync(tenantId, tempPath, ct);

            // Faz 12: normalizasyon uzantıyı değiştirmiş olabilir (PNG/JPG → ".jpg").
            var storedExtension = Path.GetExtension(storedPath);
            if (!storedExtension.Equals(Path.GetExtension(receipt.FileName), StringComparison.OrdinalIgnoreCase))
                receipt.FileName = Path.ChangeExtension(receipt.FileName, storedExtension);

            receipt.StoredPath = storedPath;
            TryDeleteStoredFile(tempPath); // kalıcı kopya yazıldı → geçici orijinal silinir
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Fiş görseli kalıcıya taşınamadı; fiş geçici yolda bırakıldı. ReceiptId: {Id}", receipt.Id);
        }
    }

    /// <summary>
    /// Portal yüklemesiyle gelen fişlerde PortalUpload kaydı fişle AYNI dosyayı paylaşır;
    /// kuyruk işi dosyayı kalıcıya taşıyınca bağlı kayıtların yolu/adı da eşitlenir
    /// (SaveChanges çağıranda — bu metod yalnızca izlenen entity'leri günceller).
    /// </summary>
    private async Task SyncLinkedPortalUploadsAsync(Receipt receipt, Guid tenantId, CancellationToken ct)
    {
        var linked = await _db.PortalUploads
            .IgnoreQueryFilters()
            .Where(p => p.ProcessedReceiptId == receipt.Id && p.TenantId == tenantId)
            .ToListAsync(ct);

        foreach (var upload in linked)
        {
            upload.StoredPath = receipt.StoredPath;
            upload.FileName = receipt.FileName;
        }
    }

    /// <summary>
    /// OCR sonucunu fişe uygular: alan doldurma, gider gösterilebilirlik, kod motoru önerisi,
    /// plaka eşleştirme (Faz 13) ve KKEG analizi (Faz 8). Senkron akıştan ORTAK metoda
    /// çıkarıldı (Faz 14) — kuyruk işlemesi aynı mantığı birebir kullanır. SaveChanges ÇAĞIRANDA.
    /// </summary>
    private async Task ApplyOcrExtractionAsync(
        Receipt receipt, Client client, OcrReceiptResult ocr, CancellationToken ct)
    {
        receipt.VendorName = ocr.VendorName;
        receipt.VendorTaxId = string.IsNullOrWhiteSpace(ocr.VendorTaxId) ? null : ocr.VendorTaxId;
        // Evrak No (Luca şablonu): OCR belge/fiş/Z numarası; okunamadıysa null kalır
        // (SMMM onay formunda doldurabilir; export "FIS-{id}" üretir).
        receipt.DocumentNumber = string.IsNullOrWhiteSpace(ocr.DocumentNumber) ? null : ocr.DocumentNumber.Trim();
        // Tarih: sağlayıcı emin değilse null bırakır (LlmVision); onayda zorunlu alan kontrolü var.
        receipt.ReceiptDate = ocr.ReceiptDate.HasValue
            ? DateTime.SpecifyKind(ocr.ReceiptDate.Value, DateTimeKind.Utc)
            : null;
        receipt.TotalAmount = ocr.TotalAmount;
        receipt.VatRate = ocr.VatBreakdown.Select(b => b.Rate).Distinct().Count() == 1 ? ocr.VatBreakdown[0].Rate : null;
        receipt.VatAmount = ocr.VatAmount;
        receipt.NetAmount = ocr.NetAmount;

        // Faz 16: iskonto + oran bazlı KDV kırılımı. Kırılım varsa NetAmount/VatAmount kırılım
        // toplamlarına, VatRate en yüksek tutarlı orana eşitlenir (geriye uyumluluk).
        receipt.DiscountTotal = ocr.DiscountTotal;
        ApplyVatBreakdown(receipt, ocr.VatBreakdown.Select(b => (b.Rate, b.Net, b.Vat)));
        // Preserve independently read header totals so a mismatch cannot disappear.
        receipt.NetAmount = ocr.NetAmount;
        receipt.VatAmount = ocr.VatAmount;
        receipt.OcrWarning = ocr.Warnings.Count > 0 ? string.Join(" ", ocr.Warnings.Distinct()) : null;

        // Gider gösterilebilirlik: alkol/tütün anahtar kelimeleri → red (belge eski uyarısı DTO'da türetilir).
        var eligibilityText = TextNormalizer.Normalize($"{ocr.VendorName} {ocr.ItemsSummary}");
        var ineligibleHit = IneligibleKeywords
            .Select(k => TextNormalizer.Normalize(k))
            .FirstOrDefault(k => k.Length > 0 && eligibilityText.Contains(k, StringComparison.Ordinal));

        if (ineligibleHit is not null)
        {
            receipt.ExpenseEligible = false;
            receipt.IneligibleReason = IneligibleAlcoholTobaccoReason;
        }
        else
        {
            receipt.ExpenseEligible = true;
        }

        // Kod önerisi: Faz 2 motorunun fiş overload'u (hafıza → satıcı varsayılanı → kural → fallback).
        var suggestion = await _engine.SuggestForReceiptAsync(
            client.Id, receipt.VendorTaxId, receipt.VendorName,
            $"{ocr.VendorName} {ocr.ItemsSummary}", receipt.VatRate, ct);

        receipt.SuggestedAccountCode = suggestion.AccountCode;
        receipt.CariCode = suggestion.CariCode;

        // Faz 8 (B3): KKEG analizi — satıcı adı + OCR kalem özeti üzerinden.
        // İşaret yoksa alanlar boş kalır; KKEG'siz fişte akış birebir eskisi gibidir.
        // (SuggestForReceiptAsync parametre önbelleğini zaten yükledi.)
        var settings = await _engine.GetOrCreateSettingsAsync(client.Id, ct);
        var kkegText = $"{ocr.VendorName} {ocr.ItemsSummary}";

        // ---------- Faz 13: plaka eşleştirme (akaryakıt fişi + kayıtlı araç) ----------
        // Akaryakıt tespiti KkegAnalyzer'daki mevcut kelime setiyle yapılır (motorin/benzin/lpg/akaryakıt).
        var isFuelReceipt = KkegAnalyzer.IsFuelExpense(kkegText);
        ClientVehicle? matchedVehicle = null;

        receipt.PlateNumber = ocr.PlateNumber; // OcrValidator normalize etti; geçersiz plaka zaten null
        if (!string.IsNullOrEmpty(ocr.PlateNumber))
        {
            var vehicles = await _db.ClientVehicles
                .AsNoTracking()
                .Where(v => v.ClientId == client.Id)
                .ToListAsync(ct);

            // Kayıtlar normalize saklanır; okuma tarafında da normalize karşılaştırılır (savunmacı).
            matchedVehicle = vehicles.FirstOrDefault(
                v => PlateNormalizer.Normalize(v.Plate) == ocr.PlateNumber);

            if (matchedVehicle is not null)
            {
                receipt.PlateMatch = PlateMatchStatus.Eslesti;
            }
            else
            {
                receipt.PlateMatch = PlateMatchStatus.Eslesmedi;
                receipt.PlateWarning =
                    $"Fişteki plaka ({ocr.PlateNumber}) mükellefin kayıtlı araçlarıyla eşleşmiyor — " +
                    "aracı sisteme ekleyin veya gideri kontrol edin.";
            }
        }
        else if (isFuelReceipt)
        {
            // Akaryakıt fişi ama plaka okunamadı → bilgi uyarısı (KKEG kararı eski davranışta kalır).
            receipt.PlateWarning =
                "Akaryakıt fişinde plaka okunamadı — binek/ticari ayrımı için elle kontrol edin.";
        }

        // KKEG kararı (Faz 13 öncelik sırası):
        //  - Akaryakıt fişi + plaka kayıtlı BİNEK araçla eşleşti → %30 KKEG otomatik
        //    (binek.gider.kisit.oran parametresi; HasCompanyVehicle bayrağından BAĞIMSIZ —
        //    plaka eşleşmesi daha güçlü kanıttır).
        //  - Akaryakıt fişi + plaka kayıtlı TİCARİ araçla eşleşti → akaryakıt KKEG'i uygulanmaz
        //    (tam gider); akaryakıt-dışı KKEG kuralları (ceza, temsil...) yine değerlendirilir.
        //  - Diğer tüm durumlar (plaka yok / eşleşmedi / akaryakıt-dışı fiş) → MEVCUT davranış
        //    (HasCompanyVehicle kuralı) birebir korunur.
        KkegFinding? kkeg;
        if (isFuelReceipt && matchedVehicle is not null)
        {
            kkeg = matchedVehicle.IsPassengerCar
                ? _kkegAnalyzer.AnalyzeFuelForPassengerVehicle(matchedVehicle.Plate, receipt.NetAmount ?? 0m)
                : _kkegAnalyzer.Analyze(kkegText, receipt.NetAmount ?? 0m, hasCompanyVehicle: false);
        }
        else
        {
            kkeg = _kkegAnalyzer.Analyze(kkegText, receipt.NetAmount ?? 0m, settings.HasCompanyVehicle);
        }

        if (kkeg is not null)
        {
            receipt.IsKkeg = true;
            receipt.KkegReason = kkeg.Reason;
            receipt.KkegAmount = kkeg.Amount;
        }
        else if (isFuelReceipt && receipt.PlateMatch != PlateMatchStatus.Eslesti)
        {
            // Faz 15 (kullanıcı kararı): akaryakıt fişinde KKEG %30 OTOMATİK İŞARETLENİR —
            // plaka eşleşmese ya da hiç okunamasa bile (Türkiye pratiğinde akaryakıt fişleri
            // ağırlıkla binek araçlara aittir; SMMM ticari araca aitse onay ekranında kutuyu
            // tek tıkla kaldırır). Kayıtlı TİCARİ araçla eşleşen fiş bu dala GİRMEZ
            // (PlateMatch=Eslesti — tam gider, üstteki dalda değerlendirildi).
            // Plaka uyarı metinleri (eşleşmedi / okunamadı) aynen gösterilmeye devam eder.
            var autoKkeg = _kkegAnalyzer.AnalyzeFuelForPassengerVehicle(
                receipt.PlateNumber ?? "plaka okunamadı", receipt.NetAmount ?? 0m);
            if (autoKkeg is not null)
            {
                receipt.IsKkeg = true;
                receipt.KkegReason = autoKkeg.Reason;
                receipt.KkegAmount = autoKkeg.Amount;
            }
        }

        // Birleşik güven: OCR alan çıkarımı ile kod önerisinin düşük olanı
        // ("otomatik onaya hazır" rozeti ancak ikisi de güçlüyse görünür).
        receipt.Confidence = Math.Round(Math.Min(ocr.Confidence, suggestion.Confidence), 2);
    }

    /// <summary>
    /// Faz 16 (B): Z raporu OCR sonucunu fişe uygular — GELİR belgesi olduğundan gider
    /// akışındaki analizler (gider gösterilebilirlik, kod motoru önerisi, plaka, KKEG)
    /// ÇALIŞTIRILMAZ. VendorName "Z Raporu {ÖKC seri} #{Z no}" olarak kurulur; kırılım
    /// ReceiptVatBreakdown'a yazılır, nakit/kart toplamları saklanır. SaveChanges ÇAĞIRANDA.
    /// </summary>
    private static void ApplyZReportExtraction(Receipt receipt, OcrReceiptResult ocr)
    {
        var serialPart = string.IsNullOrWhiteSpace(ocr.OkcSerialNumber) ? null : ocr.OkcSerialNumber.Trim();
        var zPart = string.IsNullOrWhiteSpace(ocr.ZNumber) ? null : ocr.ZNumber.Trim();
        receipt.VendorName = "Z Raporu"
                             + (serialPart is null ? string.Empty : $" {serialPart}")
                             + (zPart is null ? string.Empty : $" #{zPart}");
        receipt.VendorTaxId = null;
        receipt.DocumentNumber = zPart ?? (string.IsNullOrWhiteSpace(ocr.DocumentNumber) ? null : ocr.DocumentNumber.Trim());
        receipt.ReceiptDate = ocr.ReceiptDate.HasValue
            ? DateTime.SpecifyKind(ocr.ReceiptDate.Value, DateTimeKind.Utc)
            : null;
        receipt.TotalAmount = ocr.TotalAmount;
        receipt.VatRate = ocr.VatBreakdown.Select(b => b.Rate).Distinct().Count() == 1 ? ocr.VatBreakdown[0].Rate : null;
        receipt.VatAmount = ocr.VatAmount;
        receipt.NetAmount = ocr.NetAmount;
        receipt.CashTotal = ocr.CashTotal;
        receipt.CardTotal = ocr.CardTotal;
        ApplyVatBreakdown(receipt, ocr.VatBreakdown.Select(b => (b.Rate, b.Net, b.Vat)));
        // Preserve independently read header totals so a mismatch cannot disappear.
        receipt.NetAmount = ocr.NetAmount;
        receipt.VatAmount = ocr.VatAmount;
        receipt.OcrWarning = ocr.Warnings.Count > 0 ? string.Join(" ", ocr.Warnings.Distinct()) : null;

        // Gelir belgesi: gider gösterilebilirlik kapısına takılmaz; hesap önerisi 600 (satış
        // gelirleri) — export'taki asıl hesap "zrapor.gelir.hesap" parametresinden okunur.
        receipt.ExpenseEligible = true;
        receipt.SuggestedAccountCode = "600";
        receipt.Confidence = Math.Round(ocr.Confidence, 2);
    }

    /// <summary>
    /// Oran bazlı kırılımı fişe yazar (mevcut satırlar silinir, yenileri eklenir) ve toplam
    /// alanlarını kırılımla hizalar: NetAmount = Σnet, VatAmount = Σvat, VatRate = en yüksek
    /// (net+kdv) tutarlı satırın oranı. Kırılım boşsa fişteki mevcut değerlere DOKUNULMAZ
    /// (OCR kırılım okuyamadıysa tekil alanlar korunur). Çağıranın fişi VatBreakdowns
    /// Include'uyla yüklemiş olması gerekir.
    /// </summary>
    private static void ApplyVatBreakdown(Receipt receipt, IEnumerable<(decimal Rate, decimal Net, decimal Vat)> rows)
    {
        var list = rows
            .Where(r => r.Net != 0m || r.Vat != 0m)
            .GroupBy(r => r.Rate)
            .Select(g => (Rate: g.Key, Net: Math.Round(g.Sum(x => x.Net), 2), Vat: Math.Round(g.Sum(x => x.Vat), 2)))
            .OrderByDescending(r => r.Rate)
            .ToList();

        receipt.VatBreakdowns.Clear();
        if (list.Count == 0) return;

        foreach (var row in list)
        {
            receipt.VatBreakdowns.Add(new ReceiptVatBreakdown
            {
                TenantId = receipt.TenantId,
                ReceiptId = receipt.Id,
                VatRate = row.Rate,
                NetAmount = row.Net,
                VatAmount = row.Vat
            });
        }

        receipt.NetAmount = Math.Round(list.Sum(r => r.Net), 2);
        receipt.VatAmount = Math.Round(list.Sum(r => r.Vat), 2);
        receipt.VatRate = list.Count == 1 ? list[0].Rate : null;
    }

    /// <summary>
    /// Onay: SMMM düzeltmeleri uygulanır, gider gösterilebilirlik kapısı kontrol edilir,
    /// öğrenme (AccountMemory/VendorDefault/VendorCariMap — fatura onayıyla aynı upsert yolu)
    /// yazılır ve fiş Approved olur.
    /// </summary>
    public async Task<ReceiptOperationOutcome> ApproveAsync(Receipt receipt, ApproveReceiptRequest request, CancellationToken ct)
    {
        if (receipt.Status != ReceiptStatus.NeedsReview)
            return new ReceiptOperationOutcome { Success = false, ErrorMessage = "Yalnızca onay bekleyen fişler onaylanabilir." };

        // Düzeltmeler: yalnızca dolu gönderilen alanlar mevcut değeri ezer.
        if (!string.IsNullOrWhiteSpace(request.VendorName)) receipt.VendorName = request.VendorName.Trim();
        if (!string.IsNullOrWhiteSpace(request.VendorTaxId)) receipt.VendorTaxId = request.VendorTaxId.Trim();
        if (!string.IsNullOrWhiteSpace(request.DocumentNumber)) receipt.DocumentNumber = request.DocumentNumber.Trim();
        if (request.ReceiptDate.HasValue)
            receipt.ReceiptDate = DateTime.SpecifyKind(request.ReceiptDate.Value.Date, DateTimeKind.Utc);
        if (request.TotalAmount.HasValue) receipt.TotalAmount = request.TotalAmount;
        if (request.VatRate.HasValue) receipt.VatRate = request.VatRate;
        if (request.VatAmount.HasValue) receipt.VatAmount = request.VatAmount;
        if (request.NetAmount.HasValue) receipt.NetAmount = request.NetAmount;

        // Faz 16: iskonto + oran bazlı kırılım düzeltmesi. Kırılım gönderildiyse satırlar
        // birebir değiştirilir ve NetAmount/VatAmount/VatRate kırılım toplamlarından türetilir
        // (fişin ApproveAsync öncesi VatBreakdowns Include'uyla yüklenmiş olması gerekir).
        if (request.DiscountTotal.HasValue)
            receipt.DiscountTotal = request.DiscountTotal.Value > 0 ? Math.Round(request.DiscountTotal.Value, 2) : null;
        if (request.VatBreakdown is { Count: > 0 })
        {
            var candidateRows = request.VatBreakdown.Select(b => new ReceiptVatBreakdown
                { VatRate = b.VatRate, NetAmount = b.NetAmount, VatAmount = b.VatAmount }).ToList();
            var rowError = ReceiptVatMath.Validate(receipt.TotalAmount, candidateRows.Sum(b => b.NetAmount), candidateRows.Sum(b => b.VatAmount), candidateRows);
            if (rowError is not null) return new ReceiptOperationOutcome { Success = false, ErrorMessage = rowError };
            ApplyVatBreakdown(receipt, candidateRows.Select(b => (b.VatRate, b.NetAmount, b.VatAmount)));
        }

        if (receipt.ReceiptDate is null)
            return new ReceiptOperationOutcome { Success = false, ErrorMessage = "Belge tarihi boş olamaz." };
        if (receipt.TotalAmount is null or <= 0)
            return new ReceiptOperationOutcome { Success = false, ErrorMessage = "Fiş tutarı boş veya sıfır olamaz." };

        // Explicit single-rate corrections may fill missing amounts; unknown is never zero.
        if (receipt.VatBreakdowns.Count == 0 && receipt.VatRate.HasValue && ReceiptVatMath.SupportedRate(receipt.VatRate.Value))
        {
            receipt.NetAmount ??= ReceiptVatMath.Round(receipt.TotalAmount.Value / (1 + receipt.VatRate.Value / 100m));
            receipt.VatAmount ??= ReceiptVatMath.Round(receipt.TotalAmount.Value - receipt.NetAmount.Value);
            ApplyVatBreakdown(receipt, new[] { (receipt.VatRate.Value, receipt.NetAmount.Value, receipt.VatAmount.Value) });
        }
        var vatError = ReceiptVatMath.Validate(receipt.TotalAmount, receipt.NetAmount, receipt.VatAmount, receipt.VatBreakdowns);
        if (vatError is not null) return new ReceiptOperationOutcome { Success = false, ErrorMessage = vatError };

        // Gider gösterilebilirlik kapısı: reddedilen fiş ancak SMMM açıkça üstlenirse onaylanır.
        if (receipt.ExpenseEligible == false && !request.OverrideEligibility)
        {
            return new ReceiptOperationOutcome
            {
                Success = false,
                ErrorMessage =
                    $"Bu fiş gider gösterilemez: {receipt.IneligibleReason ?? "uygunluk kontrolünden geçmedi."} " +
                    "Yine de onaylamak için \"Yine de gider göster\" seçeneğini işaretleyin."
            };
        }
        if (receipt.ExpenseEligible == false && request.OverrideEligibility)
        {
            // SMMM sorumluluğu üstlendi; gerekçe denetim izi için IneligibleReason'da bırakılır.
            receipt.ExpenseEligible = true;
        }

        // Faz 8 (B3): KKEG düzeltmesi — null: dokunma; false: işareti temizle; true: işaretle.
        if (request.IsKkeg is not null)
        {
            if (request.IsKkeg == false)
            {
                receipt.IsKkeg = false;
                receipt.KkegReason = null;
                receipt.KkegAmount = null;
            }
            else
            {
                receipt.IsKkeg = true;
                if (request.KkegAmount.HasValue)
                {
                    var cap = receipt.NetAmount ?? request.KkegAmount.Value;
                    receipt.KkegAmount = Math.Round(Math.Min(request.KkegAmount.Value, cap), 2);
                }
                receipt.KkegAmount ??= receipt.NetAmount; // tutar yoksa tam KKEG varsayılır
                receipt.KkegReason = FirstNonEmpty(request.KkegReason, receipt.KkegReason)
                                     ?? "SMMM tarafından KKEG işaretlendi.";
            }
        }

        var finalCode = FirstNonEmpty(request.AccountCode, receipt.AccountCode, receipt.SuggestedAccountCode);
        if (finalCode is null)
            return new ReceiptOperationOutcome { Success = false, ErrorMessage = "Hesap kodu boş olamaz. Kodu girin veya öneriyi kullanın." };

        receipt.AccountCode = finalCode;
        if (!string.IsNullOrWhiteSpace(request.CariCode)) receipt.CariCode = request.CariCode.Trim();
        receipt.Confidence = 1.0; // SMMM onayı = tam güven.
        receipt.Status = ReceiptStatus.Approved;

        // ÖĞRENME: fatura onayıyla aynı upsert yolu (hafıza + satıcı varsayılanı + cari harita).
        // Z raporu GELİR belgesidir — "Z Raporu ..." unvanı satıcı hafızasını kirletmesin diye
        // öğrenme yazılmaz (gider fişlerinde davranış birebir eskisi gibidir).
        if (receipt.DocumentKind != ReceiptDocumentKind.ZRaporu)
            await _engine.LearnFromReceiptApprovalAsync(receipt, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Fiş onaylandı ve öğrenildi. ReceiptId: {Id}, Hesap: {Account}", receipt.Id, finalCode);
        return new ReceiptOperationOutcome { Success = true };
    }

    /// <summary>Red: gerekçe zorunludur; onaylanmış fiş reddedilemez (geri alma sonraki fazda).</summary>
    public async Task<ReceiptOperationOutcome> RejectAsync(Receipt receipt, string reason, CancellationToken ct)
    {
        if (receipt.Status == ReceiptStatus.Approved)
            return new ReceiptOperationOutcome { Success = false, ErrorMessage = "Onaylanmış fiş reddedilemez." };
        if (receipt.Status == ReceiptStatus.Rejected)
            return new ReceiptOperationOutcome { Success = false, ErrorMessage = "Fiş zaten reddedilmiş." };

        receipt.Status = ReceiptStatus.Rejected;
        receipt.RejectReason = reason.Trim();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Fiş reddedildi. ReceiptId: {Id}", receipt.Id);
        return new ReceiptOperationOutcome { Success = true };
    }

    /// <summary>Silme yardımcı: DB kaydıyla birlikte dosyayı da kaldırır (dosya hatası akışı bozmaz).</summary>
    public void TryDeleteStoredFile(string storedPath)
    {
        try
        {
            _storage.Delete(storedPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fiş dosyası silinemedi: {Path}", storedPath);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
