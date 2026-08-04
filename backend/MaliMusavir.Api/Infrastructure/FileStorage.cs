namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Belge/görsel dosya deposu soyutlaması (blueprint §4: S3 uyumlu depoya geçiş için adapter).
/// Bu fazda LocalFileStorage kullanılır; ileride S3 uyumlu implementasyon tek sınıfla eklenir.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Fiş dosyası ön doğrulaması. Sorun varsa kullanıcıya gösterilecek Türkçe hata döner, yoksa null.
    /// (Uzantı: jpg/jpeg/png/pdf/heic; boyut: en fazla 10 MB.)
    /// </summary>
    string? ValidateReceiptFile(string fileName, long length);

    /// <summary>
    /// Fiş dosyasını ORİJİNAL haliyle (sıkıştırmasız) "temp/" altına kaydeder; köke göre
    /// GÖRELİ yolu döner. Faz 12 revizyonu: OCR bu geçici orijinalden çalışır — görüntü
    /// kalitesi kaybı sıfırdır. Kayıtlı ad Guid'dir (eşzamanlı yüklemede çakışma olmaz);
    /// uzantı korunur (OCR sağlayıcısı media_type'ı uzantıdan türetir). Geçici dosyayı
    /// silmek ÇAĞIRANIN sorumluluğudur (ReceiptService try/finally ile garanti eder).
    /// </summary>
    Task<string> SaveReceiptTempAsync(string fileName, Stream content, CancellationToken ct);

    /// <summary>
    /// Geçici orijinali ImageNormalizer'dan geçirip "receipts/{tenantId}/" altına KALICI
    /// olarak yazar; köke göre GÖRELİ yolu döner. OCR bittikten SONRA çağrılır: normalize
    /// edilen görsel sıkıştırılmış (uzun kenar ≤ 1600 px, JPEG q75, EXIF/metadata silinmiş;
    /// resize gerekmeyen küçük görselde q≥92) ".jpg" olarak; PDF/HEIC ve decode edilemeyen
    /// türler passthrough (orijinal olduğu gibi) kaydedilir. ImageProcessing:Enabled=false
    /// ise kalıcı kopya da orijinaldir — bayrak YALNIZCA depolama sıkıştırmasını kontrol
    /// eder, OCR her durumda orijinali görmüştür. Geçici dosyayı SİLMEZ.
    /// </summary>
    Task<string> PromoteReceiptFromTempAsync(Guid tenantId, string tempStoredPath, CancellationToken ct);

    /// <summary>
    /// Portal belgesi ön doğrulaması (Faz 7 — E1). Fiş uzantılarına ek olarak ofis belgeleri
    /// (xls/xlsx/csv/doc/docx) kabul edilir; boyut sınırı aynıdır (10 MB). Sorun varsa Türkçe hata döner.
    /// </summary>
    string? ValidatePortalFile(string fileName, long length);

    /// <summary>
    /// Portal belgesini "portal/{tenantId}/" altına kaydeder; köke göre GÖRELİ yolu döner.
    /// normalizeImage=true (yalnız Fis kategorisi) ise görseller fişteki gibi normalize edilir.
    /// </summary>
    Task<string> SavePortalDocumentAsync(Guid tenantId, string fileName, Stream content, bool normalizeImage, CancellationToken ct);

    /// <summary>Kayıtlı dosya var mı? (Geçersiz/kök dışı yol için false.)</summary>
    bool Exists(string storedPath);

    /// <summary>Dosyayı okuma akışı olarak açar. Yol doğrulaması yapılır (path traversal koruması).</summary>
    Stream OpenRead(string storedPath);

    /// <summary>Dosyayı siler (yoksa sessizce geçer). Yol doğrulaması yapılır.</summary>
    void Delete(string storedPath);

    /// <summary>Kök dizine göre tam (mutlak) yol; kök dışına çıkan yollar reddedilir.</summary>
    string GetFullPath(string storedPath);

    /// <summary>Dosya adının uzantısından MIME türü (ör. image/jpeg, application/pdf).</summary>
    string GetContentType(string fileName);
}

/// <summary>
/// Yerel disk deposu: dosyalar webroot DIŞINDA "storage/" kökü altında tutulur
/// (Storage:Root yapılandırması; varsayılan: içerik kökü altında "storage").
/// Kayıtlı dosya adı sunucuda üretilir (Guid) — kullanıcı girdisi asla yol olarak kullanılmaz.
/// </summary>
public class LocalFileStorage : IFileStorage
{
    /// <summary>İzin verilen fiş dosya uzantıları (küçük harf, noktalı).</summary>
    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".heic" };

    /// <summary>Portal belgeleri (E1): fiş uzantıları + ofis belgeleri (banka ekstresi, sözleşme).</summary>
    private static readonly string[] AllowedPortalExtensions =
        { ".jpg", ".jpeg", ".png", ".pdf", ".heic", ".xls", ".xlsx", ".csv", ".doc", ".docx" };

    public const long MaxReceiptFileSize = 10 * 1024 * 1024; // 10 MB

    private readonly string _root;
    private readonly IImageNormalizer _normalizer;

    public LocalFileStorage(IConfiguration configuration, IHostEnvironment environment, IImageNormalizer normalizer)
    {
        _normalizer = normalizer;
        var configured = configuration["Storage:Root"];
        _root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(environment.ContentRootPath, "storage")
                : Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(environment.ContentRootPath, configured));
    }

    public string? ValidateReceiptFile(string fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "Dosya adı boş olamaz.";

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            return "Desteklenmeyen dosya türü. Yalnızca JPG, PNG, PDF veya HEIC yükleyebilirsiniz.";

        if (length <= 0)
            return "Dosya boş görünüyor. Lütfen geçerli bir fiş görseli yükleyin.";

        if (length > MaxReceiptFileSize)
            return "Dosya 10 MB'den büyük olamaz.";

        return null;
    }

    public async Task<string> SaveReceiptTempAsync(string fileName, Stream content, CancellationToken ct)
    {
        // Faz 12 revizyonu ("önce OCR, sonra sıkıştırma"): orijinal içerik DOKUNULMADAN
        // geçici konuma yazılır — OCR maksimum okuma isabeti için bu dosyadan çalışır.
        // Guid ad eşzamanlı yüklemelerde çakışmayı önler; uzantı korunur (media_type tespiti).
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var tempPath = Path.Combine("temp", $"{Guid.NewGuid():N}{extension}");

        var fullPath = GetFullPath(tempPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(target, ct);

        // DB'ye yazılmaz ama tutarlılık için aynı kural: '/' ayraçlı görece yol.
        return tempPath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public async Task<string> PromoteReceiptFromTempAsync(Guid tenantId, string tempStoredPath, CancellationToken ct)
    {
        // OCR bitti → görsel şimdi normalize edilip (sıkıştırılmış) kalıcı konuma yazılır.
        // PDF/HEIC ve decode edilemeyen dosyalar ile ImageProcessing:Enabled=false durumunda
        // normalizer passthrough döner: orijinal olduğu gibi kalıcıya kopyalanır.
        var tempFullPath = GetFullPath(tempStoredPath);

        await using var source = new FileStream(tempFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var normalized = await _normalizer.NormalizeAsync(Path.GetFileName(tempStoredPath), source, ct);

        // Kayıtlı ad sunucuda üretilir; kullanıcı dosya adı yalnızca DB'de (Receipt.FileName) saklanır.
        // Uzantı normalizasyon sonucundan gelir (normalize edilen görsel ".jpg").
        var storedPath = Path.Combine("receipts", tenantId.ToString(), $"{Guid.NewGuid():N}{normalized.Extension}");

        var fullPath = GetFullPath(storedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await normalized.Content.CopyToAsync(target, ct);

        // DB'de her zaman '/' ayraçlı görece yol saklanır (platformdan bağımsız).
        return storedPath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public string? ValidatePortalFile(string fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "Dosya adı boş olamaz.";

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedPortalExtensions.Contains(extension))
            return "Desteklenmeyen dosya türü. JPG, PNG, PDF, HEIC, Excel, CSV veya Word yükleyebilirsiniz.";

        if (length <= 0)
            return "Dosya boş görünüyor. Lütfen geçerli bir belge yükleyin.";

        if (length > MaxReceiptFileSize)
            return "Dosya 10 MB'den büyük olamaz.";

        return null;
    }

    public async Task<string> SavePortalDocumentAsync(Guid tenantId, string fileName, Stream content, bool normalizeImage, CancellationToken ct)
    {
        // Faz 12: yalnızca Fis kategorisi görselleri normalize edilir (normalizeImage=true);
        // ofis belgeleri ve diğer kategoriler olduğu gibi saklanır.
        using var normalized = normalizeImage
            ? await _normalizer.NormalizeAsync(fileName, content, ct)
            : ImageNormalizationResult.Passthrough(content, Path.GetExtension(fileName).ToLowerInvariant());

        // Fişteki desenle aynı: kayıtlı ad sunucuda üretilir, kullanıcı adı yalnızca DB'de kalır.
        var storedPath = Path.Combine("portal", tenantId.ToString(), $"{Guid.NewGuid():N}{normalized.Extension}");

        var fullPath = GetFullPath(storedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await normalized.Content.CopyToAsync(target, ct);

        return storedPath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public bool Exists(string storedPath)
    {
        try
        {
            return File.Exists(GetFullPath(storedPath));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public Stream OpenRead(string storedPath)
        => new FileStream(GetFullPath(storedPath), FileMode.Open, FileAccess.Read, FileShare.Read);

    public void Delete(string storedPath)
    {
        var fullPath = GetFullPath(storedPath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    /// <summary>
    /// Path traversal koruması: birleşik yol normalize edilir ve kökün ALTINDA kalmak zorundadır.
    /// ("../", mutlak yol, sembolik kaçışlar reddedilir.)
    /// </summary>
    public string GetFullPath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || Path.IsPathRooted(storedPath))
            throw new InvalidOperationException("Geçersiz dosya yolu.");

        var fullPath = Path.GetFullPath(Path.Combine(_root, storedPath));
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Geçersiz dosya yolu.");

        return fullPath;
    }

    public string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".pdf" => "application/pdf",
        ".heic" => "image/heic",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".csv" => "text/csv",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream"
    };
}
