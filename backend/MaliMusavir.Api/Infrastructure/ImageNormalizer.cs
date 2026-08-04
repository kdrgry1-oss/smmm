using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Görsel normalizasyon sonucu (Faz 12): diske yazılacak akış + nihai uzantı + boyut bilgisi.
/// Passthrough durumunda Content, çağıranın verdiği orijinal akıştır ve Dispose ile KAPATILMAZ;
/// normalize edilmiş içerik ise sınıfın sahip olduğu bellek akışıdır (Dispose temizler).
/// </summary>
public sealed class ImageNormalizationResult : IDisposable
{
    private readonly bool _ownsContent;

    private ImageNormalizationResult(
        Stream content, string extension, bool normalized, bool heicUnsupported,
        long originalLength, bool ownsContent)
    {
        Content = content;
        Extension = extension;
        Normalized = normalized;
        HeicUnsupported = heicUnsupported;
        OriginalLength = originalLength;
        _ownsContent = ownsContent;
    }

    /// <summary>Diske yazılacak içerik (normalize edilmiş JPEG veya orijinal akış).</summary>
    public Stream Content { get; }

    /// <summary>Kayıtta kullanılacak uzantı (normalize edildiyse ".jpg", yoksa orijinal).</summary>
    public string Extension { get; }

    /// <summary>Görsel gerçekten küçültülüp JPEG'e çevrildi mi?</summary>
    public bool Normalized { get; }

    /// <summary>HEIC girişi: ImageSharp 3.x decode EDEMEZ, dosya olduğu gibi saklandı.</summary>
    public bool HeicUnsupported { get; }

    /// <summary>Orijinal bayt sayısı (loglama/kazanım ölçümü; passthrough kısayolunda 0 olabilir).</summary>
    public long OriginalLength { get; }

    public static ImageNormalizationResult Passthrough(
        Stream content, string extension, bool heicUnsupported = false, bool ownsContent = false)
        => new(content, extension, normalized: false, heicUnsupported, originalLength: 0, ownsContent);

    public static ImageNormalizationResult AsJpeg(Stream content, long originalLength)
        => new(content, ".jpg", normalized: true, heicUnsupported: false, originalLength, ownsContent: true);

    public void Dispose()
    {
        if (_ownsContent) Content.Dispose();
    }
}

/// <summary>
/// Fiş görseli normalizasyonu (Faz 12): DİSKE yazılacak kopyayı minimum boyuta indirir.
/// Faz 12 revizyonu ("önce OCR, sonra sıkıştırma"): normalizasyon artık OCR'dan SONRA,
/// yalnızca depolama kopyası için çalışır — OCR her zaman temp'teki ORİJİNAL dosyayı okur.
/// Amaç: disk kullanımını düşürmek; OCR girdi kalitesi bu hattan etkilenmez.
/// </summary>
public interface IImageNormalizer
{
    /// <summary>
    /// Girdi akışını normalize eder: EXIF yönü uygulanır, uzun kenar sınırlandırılır
    /// (küçük görsel BÜYÜTÜLMEZ), JPEG'e çevrilir, EXIF/metadata silinir (KVKK).
    /// Görsel olmayan dosya, HEIC ve Enabled=false durumunda passthrough döner —
    /// normalizasyon kayıt akışını ASLA engellemez.
    /// ImageProcessing:Enabled bayrağı YALNIZCA depolama sıkıştırmasını kontrol eder;
    /// false yapılsa da OCR orijinali zaten okumuştur (kalıcı kopya orijinal kalır).
    /// </summary>
    Task<ImageNormalizationResult> NormalizeAsync(string fileName, Stream content, CancellationToken ct);
}

/// <summary>
/// SixLabors.ImageSharp tabanlı normalizer. Yapılandırma (appsettings "ImageProcessing"):
/// Enabled (varsayılan true), MaxLongEdge (1600), JpegQuality (75).
/// HEIC istisnası: ImageSharp 3.x HEIC decode edemez → dosyaya dokunulmaz, sonuca
/// HeicUnsupported=true işareti konur (mobil katman image_picker ile zaten JPEG üretir).
/// </summary>
public class ImageSharpNormalizer : IImageNormalizer
{
    public const int DefaultMaxLongEdge = 1600;
    public const int DefaultJpegQuality = 75;

    /// <summary>Normalize edilebilen görsel uzantıları (HEIC ayrı ele alınır).</summary>
    private static readonly string[] DecodableExtensions = { ".jpg", ".jpeg", ".png" };

    private readonly IConfiguration _configuration;
    private readonly ILogger<ImageSharpNormalizer> _logger;

    public ImageSharpNormalizer(IConfiguration configuration, ILogger<ImageSharpNormalizer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ImageNormalizationResult> NormalizeAsync(string fileName, Stream content, CancellationToken ct)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var enabled = _configuration.GetValue<bool?>("ImageProcessing:Enabled") ?? true;

        // Kapalıysa (Enabled=false: yalnız DEPOLAMA sıkıştırması kapanır — OCR bu noktadan
        // önce orijinali zaten okudu) veya görsel değilse (ör. PDF) dosyaya dokunulmaz.
        if (!enabled || (!DecodableExtensions.Contains(extension) && extension != ".heic"))
            return ImageNormalizationResult.Passthrough(content, extension);

        // HEIC istisnası: decode edilemez, olduğu gibi saklanır (OCR sağlayıcısı zaten
        // HEIC için kullanıcıya Türkçe dönüştürme uyarısı verir).
        if (extension == ".heic")
        {
            _logger.LogWarning("HEIC görseli normalize edilemedi (ImageSharp desteklemez), olduğu gibi kaydediliyor: {File}", fileName);
            return ImageNormalizationResult.Passthrough(content, extension, heicUnsupported: true);
        }

        var maxLongEdge = _configuration.GetValue<int?>("ImageProcessing:MaxLongEdge") ?? DefaultMaxLongEdge;
        var jpegQuality = _configuration.GetValue<int?>("ImageProcessing:JpegQuality") ?? DefaultJpegQuality;

        // Girdi belleğe alınır — 10 MB üst sınırı normalize ÖNCESİ doğrulamadan geçmiştir,
        // bu yüzden bellek kullanımı sınırlıdır; decode başarısızsa aynı tampon geri yazılır.
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        try
        {
            using var image = await Image.LoadAsync(buffer, ct);

            // (b) EXIF orientation önce UYGULANIR (metadata silinmeden — yoksa fiş yan/ters kalır).
            image.Mutate(op => op.AutoOrient());

            // (c) Uzun kenar sınırı: yalnızca BÜYÜKSE küçültülür (küçük görsel büyütülmez).
            var resized = false;
            if (Math.Max(image.Width, image.Height) > maxLongEdge)
            {
                image.Mutate(op => op.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(maxLongEdge, maxLongEdge),
                    Sampler = KnownResamplers.Lanczos3 // metin keskinliği için en iyi küçültme filtresi
                }));
                resized = true;
            }

            // (e) KVKK: EXIF (GPS/konum, cihaz bilgisi), XMP ve IPTC metadata silinir.
            // ICC renk profili korunur (renk kayması OCR'ı olumsuz etkileyebilir).
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;

            // (d) JPEG çıktı (PNG girişler de JPEG olur — fişte şeffaflık kaybı önemsizdir).
            // ÇİFT SIKIŞTIRMA KORUMASI: görsel zaten küçükse (resize gerekmediyse) q75 ile
            // yeniden ezmek OCR'ı bozar (WhatsApp'tan gelen sıkıştırılmış fişlerde görüldü).
            // Küçültme YAPILMADIYSA yüksek kalite (min 92) kullanılır — EXIF/KVKK temizliği korunur.
            var effectiveQuality = resized ? jpegQuality : Math.Max(jpegQuality, 92);
            var output = new MemoryStream();
            await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = effectiveQuality }, ct);
            output.Position = 0;

            // (f) Kazanım loglanır: orijinal/yeni bayt + nihai piksel boyutu.
            _logger.LogInformation(
                "Fiş görseli normalize edildi. Dosya: {File}, {OriginalBytes} B → {NewBytes} B, {Width}x{Height} px",
                fileName, buffer.Length, output.Length, image.Width, image.Height);

            return ImageNormalizationResult.AsJpeg(output, buffer.Length);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            // Bozuk/uzantısı yanıltıcı dosya: normalizasyon akışı ASLA engellemez —
            // dosya olduğu gibi kaydedilir, OCR yine dener (okunamazsa mevcut hata akışı çalışır).
            _logger.LogWarning(ex, "Görsel decode edilemedi, olduğu gibi kaydediliyor: {File}", fileName);
            buffer.Position = 0;
            return ImageNormalizationResult.Passthrough(buffer, extension, ownsContent: true);
        }
    }
}
