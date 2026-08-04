using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Kontrol durumu: Ok (yeşil ✓) / Uyari (sarı ⚠ — dikkat) / Hata (kırmızı ✗ — beyana engel).</summary>
public enum PeriodCheckStatus { Ok = 0, Uyari = 1, Hata = 2 }

/// <summary>Tek kontrol satırı.</summary>
public class PeriodCheckItem
{
    /// <summary>Makine anahtarı (ör. "kodlanmamis-fatura").</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PeriodCheckStatus Status { get; set; }
    /// <summary>Türkçe detay (ör. "3 fatura kodlanmadı").</summary>
    public string Detail { get; set; } = string.Empty;
    /// <summary>İlgili panel sayfası yolu (ör. "/panel/faturalar").</summary>
    public string Link { get; set; } = string.Empty;
}

/// <summary>Dönem sonu kontrol listesi sonucu (B10).</summary>
public class PeriodCloseChecklist
{
    public Guid ClientId { get; set; }
    public string Period { get; set; } = string.Empty;
    /// <summary>"Beyana hazır" yüzdesi: Ok=1, Uyari=0.5, Hata=0 ağırlıklı ortalama.</summary>
    public int Percent { get; set; }
    /// <summary>En az bir Hata (beyana engel) var mı?</summary>
    public bool HasBlocker { get; set; }
    /// <summary>KDV taslağı kısayolu gösterilebilir mi (engel yok)?</summary>
    public bool ReadyForDeclaration { get; set; }
    public List<PeriodCheckItem> Items { get; set; } = new();
}

/// <summary>
/// Faz 8 — B10: Dönem sonu kontrol listesi. Beyanname öncesi tüm modüllerin (motor, fiş OCR,
/// mutabakat B5, beyanname, tevkifat B2, belge portalı E1, vergi takvimi E2) durumunu tek
/// listede toplar; her satır ilgili ekrana link verir. Skorlama: hata = beyana engel,
/// uyarı = dikkat → "Beyana hazır" yüzdesi (Ok=1, Uyari=0.5, Hata=0).
/// </summary>
public class PeriodCloseService
{
    private readonly AppDbContext _db;
    private readonly ReconciliationService _reconciliation;

    public PeriodCloseService(AppDbContext db, ReconciliationService reconciliation)
    {
        _db = db;
        _reconciliation = reconciliation;
    }

    public async Task<PeriodCloseChecklist> BuildChecklistAsync(Guid clientId, string period, CancellationToken ct)
    {
        if (!DeclarationService.TryParsePeriod(period, out var periodStart))
            throw new ArgumentException("Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).", nameof(period));

        var periodEnd = periodStart.AddMonths(1);
        var items = new List<PeriodCheckItem>();

        // 1) Kodlanmamış fatura var mı? (Hata — beyana engel: KDV taslağına hiç girmezler)
        var uncodedCount = await _db.EInvoices.CountAsync(i =>
            i.ClientId == clientId && i.Direction == InvoiceDirection.Incoming &&
            i.IssueDate >= periodStart && i.IssueDate < periodEnd &&
            i.CodingStatus == CodingStatus.Uncoded, ct);

        items.Add(new PeriodCheckItem
        {
            Key = "kodlanmamis-fatura",
            Name = "Kodlanmamış fatura",
            Status = uncodedCount > 0 ? PeriodCheckStatus.Hata : PeriodCheckStatus.Ok,
            Detail = uncodedCount > 0
                ? $"{uncodedCount} fatura henüz kodlanmadı — \"Dönemi Kodla\" çalıştırın."
                : "Dönemdeki tüm faturalar motordan geçti.",
            Link = "/panel/faturalar"
        });

        // 2) Onay kuyruğunda bekleyen var mı? (Uyarı — onaylanmadan KDV taslağına girmezler)
        var reviewCount = await _db.EInvoices.CountAsync(i =>
            i.ClientId == clientId && i.Direction == InvoiceDirection.Incoming &&
            i.IssueDate >= periodStart && i.IssueDate < periodEnd &&
            i.CodingStatus == CodingStatus.NeedsReview, ct);

        items.Add(new PeriodCheckItem
        {
            Key = "onay-kuyrugu",
            Name = "Onay kuyruğu",
            Status = reviewCount > 0 ? PeriodCheckStatus.Uyari : PeriodCheckStatus.Ok,
            Detail = reviewCount > 0
                ? $"{reviewCount} fatura onay bekliyor — onaylanmadan indirilecek KDV'ye girmez."
                : "Onay bekleyen fatura yok.",
            Link = "/panel/onay-kuyrugu"
        });

        // 3) Doğrulama bekleyen fiş var mı? (Uyarı)
        var receiptReviewCount = await _db.Receipts.CountAsync(r =>
            r.ClientId == clientId && r.Status == ReceiptStatus.NeedsReview &&
            r.ReceiptDate != null && r.ReceiptDate >= periodStart && r.ReceiptDate < periodEnd, ct);

        items.Add(new PeriodCheckItem
        {
            Key = "fis-dogrulama",
            Name = "Fiş doğrulama",
            Status = receiptReviewCount > 0 ? PeriodCheckStatus.Uyari : PeriodCheckStatus.Ok,
            Detail = receiptReviewCount > 0
                ? $"{receiptReviewCount} fiş doğrulama bekliyor."
                : "Doğrulama bekleyen fiş yok.",
            Link = "/panel/fisler"
        });

        // 4) Mutabakat bulguları (B5): mükerrer = Hata, eksik/tutarsız = Uyarı.
        var reconciliation = await _reconciliation.AnalyzeAsync(clientId, period, ct);
        var reconciliationStatus = reconciliation.Mukerrer.Count > 0
            ? PeriodCheckStatus.Hata
            : reconciliation.Eksik.Count + reconciliation.Tutarsiz.Count > 0
                ? PeriodCheckStatus.Uyari
                : PeriodCheckStatus.Ok;

        items.Add(new PeriodCheckItem
        {
            Key = "mutabakat",
            Name = "Mükerrer / eksik fatura mutabakatı",
            Status = reconciliationStatus,
            Detail = reconciliationStatus == PeriodCheckStatus.Ok
                ? "Mutabakat sorunu bulunamadı."
                : $"{reconciliation.Mukerrer.Count} mükerrer, {reconciliation.Eksik.Count} eksik, {reconciliation.Tutarsiz.Count} tutar uyuşmazlığı bulgusu var.",
            Link = "/panel/mutabakat"
        });

        // 5) KDV taslağı hazır mı? (Uyarı — kısayolla hazırlanabilir)
        var declaration = await _db.Declarations.AsNoTracking()
            .Where(d => d.ClientId == clientId && d.Type == DeclarationType.KDV1 && d.Period == period)
            .Select(d => new { d.Status })
            .FirstOrDefaultAsync(ct);

        var declarationStatus = declaration is null
            ? PeriodCheckStatus.Uyari
            : declaration.Status is DeclarationStatus.Sent or DeclarationStatus.Approved
                ? PeriodCheckStatus.Ok
                : PeriodCheckStatus.Uyari;

        items.Add(new PeriodCheckItem
        {
            Key = "kdv-taslagi",
            Name = "KDV1 taslağı",
            Status = declarationStatus,
            Detail = declaration is null
                ? "Bu dönem için KDV1 taslağı henüz hazırlanmadı."
                : declaration.Status switch
                {
                    DeclarationStatus.Approved => "KDV1 beyannamesi onaylandı (tahakkuk alındı).",
                    DeclarationStatus.Sent => "KDV1 beyannamesi GİB'e gönderildi; tahakkuk bekleniyor.",
                    DeclarationStatus.Error => "KDV1 gönderimi hata aldı — düzeltip yeniden gönderin.",
                    _ => "KDV1 taslağı hazır; kontrol edip GİB'e gönderin."
                },
            Link = "/panel/beyannameler"
        });

        // 6) Tevkifatlı satır onaysız mı? (B2 — Uyarı: onaylanmadan KDV2 taslağına girmezler)
        var unapprovedWithholding = await _db.EInvoiceLines.CountAsync(l =>
            l.EInvoice!.ClientId == clientId &&
            l.EInvoice.Direction == InvoiceDirection.Incoming &&
            l.EInvoice.IssueDate >= periodStart && l.EInvoice.IssueDate < periodEnd &&
            l.EInvoice.CodingStatus != CodingStatus.Approved &&
            l.WithholdingCode != null, ct);

        items.Add(new PeriodCheckItem
        {
            Key = "tevkifat-onay",
            Name = "Tevkifatlı satırlar (KDV2)",
            Status = unapprovedWithholding > 0 ? PeriodCheckStatus.Uyari : PeriodCheckStatus.Ok,
            Detail = unapprovedWithholding > 0
                ? $"{unapprovedWithholding} tevkifatlı satır onaysız — onaylanmadan KDV2 taslağına girmez."
                : "Onaysız tevkifatlı satır yok.",
            Link = "/panel/onay-kuyrugu"
        });

        // 7) Portalda işlenmemiş belge var mı? (E1 — Uyarı: fiş kategorisi OCR'a girmemiş yüklemeler)
        var unprocessedPortal = await _db.PortalUploads.CountAsync(u =>
            u.ClientId == clientId &&
            u.UploadedAt >= periodStart && u.UploadedAt < periodEnd &&
            u.Category == PortalUploadCategory.Fis && u.ProcessedReceiptId == null, ct);

        items.Add(new PeriodCheckItem
        {
            Key = "portal-belge",
            Name = "Portal belgeleri",
            Status = unprocessedPortal > 0 ? PeriodCheckStatus.Uyari : PeriodCheckStatus.Ok,
            Detail = unprocessedPortal > 0
                ? $"Portaldan gelen {unprocessedPortal} fiş belgesi OCR'a işlenememiş — elle kontrol edin."
                : "Portalda işlenmemiş fiş belgesi yok.",
            Link = "/panel/belgeler"
        });

        // 8) Vergi takviminde geciken yükümlülük var mı? (E2 — Hata)
        var today = DateTime.UtcNow.Date;
        var overdueObligations = await _db.TaxObligations.CountAsync(o =>
            o.ClientId == clientId && o.Period == period &&
            o.Status == ObligationStatus.Bekliyor && o.DueDate < today, ct);

        items.Add(new PeriodCheckItem
        {
            Key = "vergi-takvimi",
            Name = "Vergi takvimi",
            Status = overdueObligations > 0 ? PeriodCheckStatus.Hata : PeriodCheckStatus.Ok,
            Detail = overdueObligations > 0
                ? $"{overdueObligations} yükümlülüğün son günü geçti — gecikme faizi riski."
                : "Bu dönem için geciken yükümlülük yok.",
            Link = "/panel/takvim"
        });

        // Skor: Ok=1, Uyari=0.5, Hata=0 → yüzde.
        var score = items.Sum(i => i.Status switch
        {
            PeriodCheckStatus.Ok => 1.0,
            PeriodCheckStatus.Uyari => 0.5,
            _ => 0.0
        });
        var hasBlocker = items.Any(i => i.Status == PeriodCheckStatus.Hata);

        return new PeriodCloseChecklist
        {
            ClientId = clientId,
            Period = period,
            Percent = (int)Math.Round(100 * score / items.Count),
            HasBlocker = hasBlocker,
            ReadyForDeclaration = !hasBlocker,
            Items = items
        };
    }
}
