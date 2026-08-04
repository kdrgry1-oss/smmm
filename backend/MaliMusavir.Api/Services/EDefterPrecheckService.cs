using System.Text.RegularExpressions;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Tek e-Defter ön-kontrol satırı (E5).</summary>
public class EDefterCheckItem
{
    /// <summary>Makine anahtarı (ör. "fatura-onay").</summary>
    public string Key { get; set; } = string.Empty;
    public string Ad { get; set; } = string.Empty;
    /// <summary>Ok (yeşil) / Uyari (sarı) / Hata (kırmızı — berat yüklenirse iptal/yeniden riski).</summary>
    public PeriodCheckStatus Durum { get; set; }
    public string Detay { get; set; } = string.Empty;
    /// <summary>Türkçe çözüm önerisi (hangi ekranda ne yapılmalı).</summary>
    public string Oneri { get; set; } = string.Empty;
}

/// <summary>e-Defter ön-kontrol sonucu (E5).</summary>
public class EDefterPrecheckResult
{
    public Guid ClientId { get; set; }
    public string Period { get; set; } = string.Empty;
    public List<EDefterCheckItem> Items { get; set; } = new();
    /// <summary>En az bir Hata var mı? (berat yüklemeden önce çözülmesi önerilir)</summary>
    public bool HasError { get; set; }
    /// <summary>Dönem kaydının güncel durumu (kontrol sonrası).</summary>
    public EDefterPeriodStatus Status { get; set; }
    public DateTime CheckedAt { get; set; }
}

/// <summary>
/// Faz 11 — E5: e-Defter berat ÖN-KONTROLÜ. Sahada berat iptal/yeniden sürecine yol açan
/// hataları (fişleştirilmemiş fatura, VKN/unvan eksikliği, süresi dolmuş mali mühür,
/// kapanış engelleri, dengesiz mizan, atlanmış önceki ay) berat YÜKLEMEDEN ÖNCE yakalar.
///
/// DÜRÜST KAPSAM NOTU: Gerçek e-Defter XML (XBRL-GL) üretimi, mali mühürle imzalama ve
/// GİB berat yükleme entegrasyonu bu fazda YOKTUR. SMMM beratı GİB e-Defter uygulamasında
/// yükler ve burada "Berat yüklendi" olarak elle işaretler; modül hazırlık listesi +
/// durum takibi sağlar. VKN-unvan eşleşmesi GİB kayıtlarıyla DOĞRULANAMAZ (servis yok) —
/// yalnızca platformdaki kayıt eksik/biçimsiz mi kontrol edilir.
/// </summary>
public class EDefterPrecheckService
{
    private static readonly Regex TaxIdRegex = new(@"^\d{10}(\d)?$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly PeriodCloseService _periodClose;
    private readonly TrialBalanceService _trialBalance;

    public EDefterPrecheckService(
        AppDbContext db, PeriodCloseService periodClose, TrialBalanceService trialBalance)
    {
        _db = db;
        _periodClose = periodClose;
        _trialBalance = trialBalance;
    }

    /// <summary>
    /// Kontrol setini çalıştırır ve EDefterPeriod kaydını upsert eder
    /// (Hata varsa Hata, yoksa KontrolTamam; BeratYuklendi durumu EZİLMEZ).
    /// </summary>
    public async Task<EDefterPrecheckResult> RunAsync(Guid clientId, string period, CancellationToken ct)
    {
        if (!DeclarationService.TryParsePeriod(period, out var periodStart))
            throw new ArgumentException("Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).", nameof(period));

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct)
            ?? throw new ArgumentException("Mükellef bulunamadı.", nameof(clientId));

        var periodEnd = periodStart.AddMonths(1);
        var items = new List<EDefterCheckItem>();

        // (a) Dönemdeki tüm gelen faturalar Approved mı? (fişleştirilmemiş kayıt beratta eksik kalır)
        var notApproved = await _db.EInvoices.CountAsync(i =>
            i.ClientId == clientId && i.Direction == InvoiceDirection.Incoming &&
            i.IssueDate >= periodStart && i.IssueDate < periodEnd &&
            i.CodingStatus != CodingStatus.Approved, ct);

        items.Add(new EDefterCheckItem
        {
            Key = "fatura-onay",
            Ad = "Fatura fişleştirme (onay)",
            Durum = notApproved > 0 ? PeriodCheckStatus.Hata : PeriodCheckStatus.Ok,
            Detay = notApproved > 0
                ? $"{notApproved} gelen fatura henüz onaylanmadı — bu kayıtlar deftere/berata girmez."
                : "Dönemdeki tüm gelen faturalar onaylanmış (fişleşmiş).",
            Oneri = notApproved > 0
                ? "Faturalar ve Onay Kuyruğu sayfalarından dönemi kodlayıp tüm faturaları onaylayın."
                : "İşlem gerekmez."
        });

        // (b) Mükellef VKN/unvan dolu ve biçimsel olarak tutarlı mı? (yanlış VKN-unvan = berat reddi)
        var titleOk = !string.IsNullOrWhiteSpace(client.Title) && client.Title.Trim().Length >= 3;
        var taxIdOk = TaxIdRegex.IsMatch(client.TaxId ?? string.Empty);
        items.Add(new EDefterCheckItem
        {
            Key = "vkn-unvan",
            Ad = "VKN / unvan kaydı",
            Durum = titleOk && taxIdOk ? PeriodCheckStatus.Uyari : PeriodCheckStatus.Hata,
            Detay = titleOk && taxIdOk
                ? $"Kayıt tam: {client.Title.Trim()} — VKN/TCKN {client.TaxId}. " +
                  "GİB sicil kaydıyla birebir eşleşme bu fazda otomatik doğrulanamıyor; berat yüklemeden önce " +
                  "unvanın vergi levhasındakiyle AYNI yazıldığını elle kontrol edin."
                : (!titleOk ? "Mükellef unvanı eksik veya çok kısa. " : string.Empty) +
                  (!taxIdOk ? "VKN/TCKN 10 veya 11 haneli değil." : string.Empty),
            Oneri = titleOk && taxIdOk
                ? "Unvanı vergi levhası / GİB sicil kaydıyla karşılaştırın (harf ve noktalama dahil)."
                : "Mükellefler sayfasından unvan ve VKN/TCKN bilgisini düzeltin."
        });

        // (c) Mali mühür / e-imza sertifikası var mı ve süresi yeterli mi? (E3 tablosundan)
        items.Add(await BuildCertificateCheckAsync(clientId, periodEnd, ct));

        // (d) Dönem kapanış kontrol listesi (B10) engel içeriyor mu?
        var checklist = await _periodClose.BuildChecklistAsync(clientId, period, ct);
        items.Add(new EDefterCheckItem
        {
            Key = "kapanis",
            Ad = "Dönem kapanış kontrolü",
            Durum = checklist.HasBlocker
                ? PeriodCheckStatus.Hata
                : checklist.Percent < 100 ? PeriodCheckStatus.Uyari : PeriodCheckStatus.Ok,
            Detay = checklist.HasBlocker
                ? $"Kapanış listesinde beyana/deftere engel durum var (hazırlık %{checklist.Percent})."
                : checklist.Percent < 100
                    ? $"Kapanış engeli yok; hazırlık %{checklist.Percent} (sarı satırlar kontrol edilmeli)."
                    : "Kapanış kontrol listesi tamamen temiz.",
            Oneri = checklist.HasBlocker || checklist.Percent < 100
                ? "Kapanış sayfasındaki kırmızı/sarı satırları çözüp kontrolü yeniden çalıştırın."
                : "İşlem gerekmez."
        });

        // (e) Mizan borç = alacak dengesi (E8 servisi — Luca fiş çekirdeğiyle aynı dağılım).
        var mizan = await _trialBalance.BuildAsync(clientId, period, ct);
        items.Add(new EDefterCheckItem
        {
            Key = "mizan-denge",
            Ad = "Mizan borç/alacak dengesi",
            Durum = mizan.Rows.Count == 0
                ? PeriodCheckStatus.Uyari
                : mizan.Denk ? PeriodCheckStatus.Ok : PeriodCheckStatus.Hata,
            Detay = mizan.Rows.Count == 0
                ? "Dönemde onaylı belge yok — mizan boş. Boş dönem beratı ('kıst dönem/boş defter') gerekiyorsa bilinçli ilerleyin."
                : mizan.Denk
                    ? $"Mizan denk (borç = alacak = {mizan.ToplamBorc:N2} TL)."
                    : $"Mizan DENGESİZ: fark {Math.Abs(mizan.Fark):N2} TL.",
            Oneri = mizan.Rows.Count == 0
                ? "Belgeler eksikse önce fatura senkronu ve fiş yüklemesini tamamlayın."
                : mizan.Denk
                    ? "İşlem gerekmez."
                    : "Mizan sayfasında dengesiz hesapları inceleyin; tutarı eksik/yanlış belgeleri düzeltin."
        });

        // (f) Önceki ay beratı yüklendi mi? (dönem atlanırsa GİB sıralı yüklemeyi reddeder)
        var previousPeriod = periodStart.AddMonths(-1).ToString("yyyy-MM");
        var previous = await _db.EDefterPeriods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ClientId == clientId && p.Period == previousPeriod, ct);

        items.Add(new EDefterCheckItem
        {
            Key = "onceki-donem",
            Ad = $"Önceki dönem beratı ({previousPeriod})",
            Durum = previous?.Status == EDefterPeriodStatus.BeratYuklendi
                ? PeriodCheckStatus.Ok
                : PeriodCheckStatus.Uyari,
            Detay = previous is null
                ? $"{previousPeriod} dönemi için platformda e-Defter kaydı yok. İlk kullanım ayıysa normaldir."
                : previous.Status == EDefterPeriodStatus.BeratYuklendi
                    ? $"{previousPeriod} beratı yüklendi olarak işaretli."
                    : $"{previousPeriod} dönemi \"{StatusLabel(previous.Status)}\" durumunda — beratı henüz yüklendi olarak işaretlenmedi.",
            Oneri = previous?.Status == EDefterPeriodStatus.BeratYuklendi
                ? "İşlem gerekmez."
                : "e-Defter beratları sırayla yüklenir; önce önceki ayın beratını yükleyip işaretleyin."
        });

        var hasError = items.Any(i => i.Durum == PeriodCheckStatus.Hata);
        var now = DateTime.UtcNow;

        // Dönem kaydını upsert et — BeratYuklendi durumu kontrolle EZİLMEZ (yüklenmiş berat geri alınmaz).
        var record = await _db.EDefterPeriods
            .FirstOrDefaultAsync(p => p.ClientId == clientId && p.Period == period, ct);
        if (record is null)
        {
            record = new EDefterPeriod { TenantId = client.TenantId, ClientId = clientId, Period = period };
            _db.EDefterPeriods.Add(record);
        }

        if (record.Status != EDefterPeriodStatus.BeratYuklendi)
            record.Status = hasError ? EDefterPeriodStatus.Hata : EDefterPeriodStatus.KontrolTamam;
        record.CheckedAt = now;
        await _db.SaveChangesAsync(ct);

        return new EDefterPrecheckResult
        {
            ClientId = clientId,
            Period = period,
            Items = items,
            HasError = hasError,
            Status = record.Status,
            CheckedAt = now
        };
    }

    /// <summary>
    /// Sertifika kontrolü (c): önce mükellefin mali mührü, yoksa ofisin (SMMM) e-imza/mali mührü.
    /// Kural: hiç kayıt yok → Uyari; süresi dolmuş veya dönem sonundan önce bitiyor → Hata;
    /// bitişe ≤ 30 gün → Uyari; aksi halde Ok.
    /// </summary>
    private async Task<EDefterCheckItem> BuildCertificateCheckAsync(
        Guid clientId, DateTime periodEnd, CancellationToken ct)
    {
        var certificates = await _db.ClientCertificates.AsNoTracking()
            .Where(c => (c.ClientId == clientId && c.Type == CertificateType.MaliMuhur) ||
                        (c.ClientId == null && (c.Type == CertificateType.MaliMuhur || c.Type == CertificateType.EImza)))
            .OrderByDescending(c => c.ExpiresAt)
            .ToListAsync(ct);

        if (certificates.Count == 0)
        {
            return new EDefterCheckItem
            {
                Key = "sertifika",
                Ad = "Mali mühür / e-imza sertifikası",
                Durum = PeriodCheckStatus.Uyari,
                Detay = "Ne mükellefin mali mührü ne de ofisin e-imza/mali mühür kaydı bulundu — süre takibi yapılamıyor.",
                Oneri = "Sertifikalar sayfasından imzada kullanılacak sertifikayı bitiş tarihiyle kaydedin."
            };
        }

        // En geç biten sertifika esas alınır (imza için herhangi biri yeterlidir).
        var best = certificates[0];
        var owner = best.ClientId is null ? "ofis sertifikası" : "mükellef mali mührü";
        var today = DateTime.UtcNow.Date;
        var daysLeft = (int)Math.Ceiling((best.ExpiresAt.Date - today).TotalDays);

        if (best.ExpiresAt.Date <= today || best.ExpiresAt.Date <= periodEnd.Date)
        {
            return new EDefterCheckItem
            {
                Key = "sertifika",
                Ad = "Mali mühür / e-imza sertifikası",
                Durum = PeriodCheckStatus.Hata,
                Detay = best.ExpiresAt.Date <= today
                    ? $"Kayıtlı {owner} ({best.Owner}) SÜRESİ DOLMUŞ ({best.ExpiresAt:dd.MM.yyyy}) — berat imzalanamaz."
                    : $"Kayıtlı {owner} ({best.Owner}) dönem sonundan önce bitiyor ({best.ExpiresAt:dd.MM.yyyy}).",
                Oneri = "Sertifikayı yeniletin (KamuSM/ESHS başvurusu) ve yeni bitiş tarihini Sertifikalar sayfasına işleyin."
            };
        }

        if (daysLeft <= 30)
        {
            return new EDefterCheckItem
            {
                Key = "sertifika",
                Ad = "Mali mühür / e-imza sertifikası",
                Durum = PeriodCheckStatus.Uyari,
                Detay = $"Kayıtlı {owner} ({best.Owner}) {daysLeft} gün içinde bitiyor ({best.ExpiresAt:dd.MM.yyyy}).",
                Oneri = "Berat yükleme takvimini öne alın veya sertifika yenileme başvurusunu şimdiden yapın."
            };
        }

        return new EDefterCheckItem
        {
            Key = "sertifika",
            Ad = "Mali mühür / e-imza sertifikası",
            Durum = PeriodCheckStatus.Ok,
            Detay = $"Kayıtlı {owner} ({best.Owner}) geçerli — bitiş {best.ExpiresAt:dd.MM.yyyy} ({daysLeft} gün).",
            Oneri = "İşlem gerekmez."
        };
    }

    public static string StatusLabel(EDefterPeriodStatus status) => status switch
    {
        EDefterPeriodStatus.Hazirlaniyor => "Hazırlanıyor",
        EDefterPeriodStatus.KontrolTamam => "Kontrol tamam",
        EDefterPeriodStatus.BeratYuklendi => "Berat yüklendi",
        _ => "Hata"
    };
}
