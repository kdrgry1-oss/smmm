using System.Globalization;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Tebligat;

/// <summary>
/// GİB Portal e-Tebligat sağlayıcısı (Faz 15) — entegratörü/UETS bağlantısı olmayan
/// mükellefler için. gib-robot servisinin /tebligat ucunu çağırır.
///
/// Mükellefin GİB kimliği ClientIntegration.ApiKeyEncrypted içinde şifreli JSON olarak
/// durur (GibPortalProvider.PackCredentials ile aynı paket) — ŞEMA DEĞİŞİKLİĞİ YOKTUR.
/// Şifre LOGA ASLA yazılmaz.
///
/// YASAL SÜRE MODELİ: GİB portal listesi "son gün" bilgisi vermez. Süre, MockUetsProvider
/// ile AYNI modelden hesaplanır: tebliğ tarihini izleyen 5. İŞ GÜNÜ sonunda tebliğ edilmiş
/// sayılır + belge türüne göre ödeme 15 / itiraz 30 gün. Tür konu metninden çıkarılır;
/// çıkarılamazsa "Bilgi" (süresiz) kabul edilir — UYDURMA SÜRE YAZILMAZ.
///
/// GÜVENLİK KURALI: Robot captcha/SMS-OTP görürse ÇÖZMEYE ÇALIŞILMAZ; "Elle doğrulama
/// gerekli." önekli, tekrar denenmeyen (retryable:false) hata döner.
/// </summary>
public class GibPortalTebligatProvider : IETebligatProvider
{
    /// <summary>Sağlayıcı adı — ETebligatSyncService seçim yaparken bu adı kullanır.</summary>
    public const string ProviderName = "GibPortal";

    /// <summary>Varsayılan çekim aralığı (elle tetiklemede tarih verilmezse).</summary>
    public const int DefaultRangeDays = 90;

    private readonly AppDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly GibRobotClient _robot;
    private readonly ILogger<GibPortalTebligatProvider> _logger;

    public GibPortalTebligatProvider(
        AppDbContext db,
        ISecretProtector secretProtector,
        GibRobotClient robot,
        ILogger<GibPortalTebligatProvider> logger)
    {
        _db = db;
        _secretProtector = secretProtector;
        _robot = robot;
        _logger = logger;
    }

    public string Name => ProviderName;

    /// <summary>
    /// Çekim aralığı: ETebligatSyncService tek mükellef için tarih verirse burada
    /// ayarlanır (scoped servis; her istek/kuyruk işi kendi kopyasını alır).
    /// Null bırakılırsa son <see cref="DefaultRangeDays"/> gün taranır.
    /// </summary>
    public DateTime? RangeFrom { get; set; }
    public DateTime? RangeTo { get; set; }

    public async Task<ProviderResult<List<ProviderTebligat>>> GetMessagesAsync(
        Client client, CancellationToken ct)
    {
        var integration = await _db.ClientIntegrations
            .AsNoTracking()
            .FirstOrDefaultAsync(ci => ci.ClientId == client.Id, ct);

        // Mükellefin KENDİ GİB kimliği var mı? (GibPortal entegrasyonu + kayıtlı kimlik)
        var hasOwnCredentials =
            integration is not null &&
            integration.Provider == IntegrationProvider.GibPortal &&
            !string.IsNullOrEmpty(integration.ApiKeyEncrypted);

        var to = (RangeTo ?? DateTime.UtcNow).Date;
        var from = (RangeFrom ?? to.AddDays(-DefaultRangeDays)).Date;
        if (from > to) (from, to) = (to, from);

        GibRobotClient.RobotResponse<GibRobotClient.RobotTebligat> response;

        if (hasOwnCredentials)
        {
            string username;
            string password;
            try
            {
                var plain = _secretProtector.Unprotect(integration!.ApiKeyEncrypted!);
                if (!GibPortalProvider.TryParseCredentials(plain, out username, out password))
                    return ProviderResult<List<ProviderTebligat>>.Fail(
                        "GİB Portalı kimlik bilgileri eksik. Kullanıcı kodu ve şifreyi yeniden kaydedin.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GİB portal kimliği çözülemedi. ClientId: {ClientId}", client.Id);
                return ProviderResult<List<ProviderTebligat>>.Fail(
                    "Kayıtlı GİB kimliği çözülemedi. Lütfen GİB erişimini yeniden kaydedin.");
            }

            response = await _robot.GetTebligatAsync(username, password, from, to, ct);
        }
        else
        {
            // Faz 18 — MÜŞAVİR TEK GİRİŞ: mükellefin kendi kimliği yoksa ama tenant'ta
            // müşavir GİB kimliği varsa, çekim müşavir oturumuyla yapılır
            // (sessionKey=advisor:<tenantId> + targetTaxId=<mükellef VKN>). Şifre gitmez.
            var hasAdvisor = await _db.TenantGibCredentials
                .AsNoTracking()
                .AnyAsync(c => c.TenantId == client.TenantId, ct);

            if (!hasAdvisor)
            {
                if (integration is not null && integration.Provider != IntegrationProvider.GibPortal)
                {
                    return ProviderResult<List<ProviderTebligat>>.Fail(
                        $"e-Tebligat yalnızca GİB Dijital Vergi Dairesi'nden çekilir; {integration.Provider} " +
                        "gibi e-belge entegratörlerinin e-Tebligat servisi yoktur. Bu mükellefin tebligatı için " +
                        "\"GİB Bağlantısı\" sayfasından müşavir girişi tanımlayın veya mükellefin kendi GİB " +
                        "kimliğini kaydedin.");
                }

                return ProviderResult<List<ProviderTebligat>>.Fail(
                    "Bu mükellef için GİB Portalı kimliği veya müşavir GİB oturumu tanımlı değil.");
            }

            response = await _robot.GetTebligatAsAdvisorAsync(
                GibRobotClient.AdvisorSessionKey(client.TenantId), client.TaxId, from, to, ct);
        }

        if (response.RequiresManual || response.NeedsCaptchaLogin)
        {
            // Faz 17: needsCaptchaLogin → kayıtlı GİB oturumu yok/geçersiz; kullanıcı
            // "GİB Oturumu Aç" ile doğrulama kodunu girmeli (kuyruk tekrar denemez).
            return ProviderResult<List<ProviderTebligat>>.Fail(
                GibPortalProvider.BuildManualVerificationMessage(response.Message), retryable: false);
        }

        if (!response.Ok)
        {
            return ProviderResult<List<ProviderTebligat>>.Fail(
                response.Message ?? "GİB portalından tebligatlar alınamadı. Lütfen daha sonra tekrar deneyin.",
                retryable: response.Retryable);
        }

        var messages = new List<ProviderTebligat>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in response.Items)
        {
            var externalId = item.ExternalId?.Trim();
            if (string.IsNullOrWhiteSpace(externalId)) continue;

            // Mükerrer anahtar mükellef bazında benzersizleştirilir (farklı mükelleflerde
            // aynı evrak numarası çakışmasın — (TenantId, ExternalId) tekil indeksi var).
            var key = $"GIBPORTAL-{client.TaxId}-{externalId}";
            if (!seen.Add(key)) continue;

            var deliveredAt = ParseDate(item.SentAt) ?? DateTime.UtcNow.Date;
            var readAt = ParseDate(item.ReadAt);
            var subject = string.IsNullOrWhiteSpace(item.Subject)
                ? "Konu belirtilmemiş tebligat"
                : item.Subject.Trim();

            var deadlineType = ClassifyDeadline(subject);
            var deadline = CalculateLegalDeadline(deliveredAt, deadlineType);

            messages.Add(new ProviderTebligat
            {
                ExternalId = key,
                Sender = string.IsNullOrWhiteSpace(item.TaxOffice)
                    ? "Gelir İdaresi Başkanlığı"
                    : $"Gelir İdaresi Başkanlığı — {item.TaxOffice.Trim()}",
                // Belge bağlantısı/metni varsa konuya iliştirilir (şema değişmeden görünür kalır).
                Subject = string.IsNullOrWhiteSpace(item.DocumentUrlOrText)
                    ? subject
                    : $"{subject} ({item.DocumentUrlOrText.Trim()})",
                DeliveredAt = deliveredAt,
                LegalDeadline = deadline,
                DeadlineType = deadlineType,
                IsRead = readAt.HasValue
            });
        }

        _logger.LogInformation(
            "GİB portal tebligat senkronu tamamlandı. ClientId: {ClientId}, Kayıt: {Count}, aralık: {From:d}-{To:d}",
            client.Id, messages.Count, from, to);

        return ProviderResult<List<ProviderTebligat>>.Ok(messages);
    }

    /// <summary>
    /// Konu metninden süre türünü çıkarır: "Ödeme" (ödeme emri / prim borcu),
    /// "İtiraz" (ihbarname / ceza kararı), aksi halde "Bilgi" (süresiz).
    /// TODO-VERIFY: GİB portalının gerçek konu metinleri canlıda doğrulanınca desenler genişletilecek.
    /// </summary>
    private static string ClassifyDeadline(string subject)
    {
        var s = subject.ToLower(new CultureInfo("tr-TR"));

        if (s.Contains("ödeme emri") || s.Contains("borç") || s.Contains("prim") ||
            s.Contains("tahsilat") || s.Contains("haciz"))
        {
            return "Ödeme";
        }

        if (s.Contains("ihbarname") || s.Contains("ceza") || s.Contains("takdir") ||
            s.Contains("tarhiyat") || s.Contains("vergi/ceza"))
        {
            return "İtiraz";
        }

        return "Bilgi";
    }

    /// <summary>
    /// Yasal son gün: tebliğ tarihini izleyen 5. iş günü sonunda tebliğ edilmiş sayılır;
    /// üzerine ödeme 15 / itiraz 30 gün eklenir. "Bilgi" için süre takibi yapılmaz (null).
    /// </summary>
    private static DateTime? CalculateLegalDeadline(DateTime deliveredAt, string deadlineType)
    {
        if (deadlineType == "Bilgi") return null;

        var deemedServedAt = AddBusinessDays(deliveredAt.Date, 5);
        var period = deadlineType == "Ödeme" ? 15 : 30;
        return deemedServedAt.AddDays(period);
    }

    /// <summary>İleriye doğru iş günü ekler (hafta sonları atlanır; resmi tatil takvimi sonraki fazda).</summary>
    private static DateTime AddBusinessDays(DateTime date, int businessDays)
    {
        var current = date;
        var added = 0;
        while (added < businessDays)
        {
            current = current.AddDays(1);
            if (current.DayOfWeek != DayOfWeek.Saturday && current.DayOfWeek != DayOfWeek.Sunday)
                added++;
        }
        return current;
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value.Date
            : null;
    }
}
