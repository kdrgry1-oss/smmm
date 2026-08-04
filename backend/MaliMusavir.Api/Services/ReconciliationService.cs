using System.Security.Cryptography;
using System.Text;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Bulguya bağlı belge referansı (UI "faturaya git" linki için).</summary>
public class ReconciliationDocRef
{
    public Guid Id { get; set; }
    /// <summary>"Fatura" veya "Fiş".</summary>
    public string DocType { get; set; } = string.Empty;
    /// <summary>Ör. "MOCK2026000042 — Satıcı A.Ş.".</summary>
    public string Label { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Tek mutabakat bulgusu. Key deterministiktir: "Yoksay" DB kaydı bu anahtarla eşleşir.</summary>
public class ReconciliationFinding
{
    /// <summary>Deterministik bulgu anahtarı (SHA-256 hex kısaltması).</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>Mukerrer / Eksik / Tutarsiz.</summary>
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>Türkçe öneri metni (SMMM'ye ne yapması gerektiğini söyler).</summary>
    public string Suggestion { get; set; } = string.Empty;
    public List<ReconciliationDocRef> Documents { get; set; } = new();
}

/// <summary>Dönem analiz raporu (B5). Bulgular DB'de saklanmaz; her çağrıda yeniden hesaplanır.</summary>
public class ReconciliationReport
{
    public Guid ClientId { get; set; }
    public string Period { get; set; } = string.Empty;
    public List<ReconciliationFinding> Mukerrer { get; set; } = new();
    public List<ReconciliationFinding> Eksik { get; set; } = new();
    public List<ReconciliationFinding> Tutarsiz { get; set; } = new();
    /// <summary>Daha önce "Yoksay" denmiş ve bu analizde gizlenen bulgu sayısı.</summary>
    public int DismissedCount { get; set; }
    /// <summary>Dönemdeki toplam gelen fatura / fiş sayısı (bağlam bilgisi).</summary>
    public int InvoiceCount { get; set; }
    public int ReceiptCount { get; set; }
}

/// <summary>
/// Faz 8 — B5: Mükerrer / eksik fatura mutabakatı. BA/BS formları 25 Eylül 2024'te
/// kaldırıldığından bu bütünlük kontrolü kritik hale geldi (motor dokümanı §3 B5).
/// Analiz üç grup üretir:
///  (a) MÜKERRER: aynı (satıcı VKN, tutar, ±2 gün) fatura kümeleri; aynı fatura no + farklı UUID;
///      fişlerde aynı (satıcı VKN, tutar, tarih) çiftleri.
///  (b) EKSİK: entegratörden çekilen fatura sayısı ile kodlanan/onaylanan sayısı karşılaştırması +
///      Error/New statüsünde takılan faturalar. (Sınırlama: entegratör tarafındaki "gerçek" sayı
///      ancak senkron edilenler üzerinden bilinir — senkron edilmemiş fatura tespit edilemez.)
///  (c) TUTARSIZ: satır toplamı (matrah+KDV) ile fatura toplamı arasında 0,05 TL'den büyük fark.
/// "Yoksay" bulguları ReconciliationDismissal tablosuna yazılır (bellek değil DB) ve analizden gizlenir.
/// </summary>
public class ReconciliationService
{
    /// <summary>Tutar karşılaştırmalarında kuruş toleransı.</summary>
    public const decimal AmountTolerance = 0.05m;

    /// <summary>Mükerrer fatura adayı tarih penceresi (± gün).</summary>
    public const int DuplicateDateWindowDays = 2;

    private readonly AppDbContext _db;

    public ReconciliationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ReconciliationReport> AnalyzeAsync(Guid clientId, string period, CancellationToken ct)
    {
        if (!DeclarationService.TryParsePeriod(period, out var periodStart))
            throw new ArgumentException("Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).", nameof(period));

        var periodEnd = periodStart.AddMonths(1);

        var invoices = await _db.EInvoices.AsNoTracking()
            .Where(i => i.ClientId == clientId &&
                        i.Direction == InvoiceDirection.Incoming &&
                        i.IssueDate >= periodStart && i.IssueDate < periodEnd)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                i.ExternalUuid,
                i.IssueDate,
                i.SenderTaxId,
                i.SenderTitle,
                i.PayableAmount,
                i.Status,
                i.CodingStatus,
                LineTotal = i.Lines.Sum(l => (decimal?)(l.LineAmount + l.VatAmount)) ?? 0m,
                LineCount = i.Lines.Count
            })
            .ToListAsync(ct);

        var receipts = await _db.Receipts.AsNoTracking()
            .Where(r => r.ClientId == clientId &&
                        r.Status != ReceiptStatus.Rejected &&
                        r.ReceiptDate != null &&
                        r.ReceiptDate >= periodStart && r.ReceiptDate < periodEnd)
            .Select(r => new { r.Id, r.VendorTaxId, r.VendorName, r.ReceiptDate, r.TotalAmount, r.FileName })
            .ToListAsync(ct);

        var report = new ReconciliationReport
        {
            ClientId = clientId,
            Period = period,
            InvoiceCount = invoices.Count,
            ReceiptCount = receipts.Count
        };

        // ---------- (a) MÜKERRER — fatura: aynı (VKN, tutar), tarih ±2 gün kümeleri ----------
        foreach (var group in invoices
                     .Where(i => !string.IsNullOrWhiteSpace(i.SenderTaxId) && i.PayableAmount > 0)
                     .GroupBy(i => new { i.SenderTaxId, i.PayableAmount }))
        {
            var sorted = group.OrderBy(i => i.IssueDate).ThenBy(i => i.Id).ToList();
            var cluster = new List<(Guid Id, string No, DateTime Date, string Title, decimal Amount)>();

            void FlushCluster()
            {
                if (cluster.Count > 1)
                {
                    var finding = new ReconciliationFinding
                    {
                        Kind = "Mukerrer",
                        Title = $"Olası mükerrer fatura: {group.Key.SenderTaxId} — {group.Key.PayableAmount:N2} TL ({cluster.Count} belge)",
                        Suggestion =
                            "Aynı satıcıdan aynı tutarlı ve yakın tarihli birden fazla fatura bulundu. " +
                            "Belgeleri karşılaştırın; gerçekten mükerrerse fazlasını kayıtlara almayın, " +
                            "farklı işlemlerse \"Yoksay\" ile bu uyarıyı kapatın.",
                        Documents = cluster
                            .Select(c => new ReconciliationDocRef
                            {
                                Id = c.Id,
                                DocType = "Fatura",
                                Label = $"{c.No} — {c.Title}",
                                Date = c.Date,
                                Amount = c.Amount
                            })
                            .ToList()
                    };
                    finding.Key = BuildKey("mukerrer-fatura", cluster.Select(c => c.Id));
                    report.Mukerrer.Add(finding);
                }
                cluster.Clear();
            }

            foreach (var invoice in sorted)
            {
                if (cluster.Count > 0 && (invoice.IssueDate - cluster[^1].Date).TotalDays > DuplicateDateWindowDays)
                    FlushCluster();
                cluster.Add((invoice.Id, invoice.InvoiceNumber, invoice.IssueDate, invoice.SenderTitle, invoice.PayableAmount));
            }
            FlushCluster();
        }

        // ---------- (a) MÜKERRER — aynı fatura no, farklı UUID ----------
        foreach (var group in invoices
                     .Where(i => !string.IsNullOrWhiteSpace(i.InvoiceNumber))
                     .GroupBy(i => i.InvoiceNumber)
                     .Where(g => g.Select(i => i.ExternalUuid).Distinct().Count() > 1))
        {
            var docs = group.OrderBy(i => i.IssueDate).ToList();
            var finding = new ReconciliationFinding
            {
                Kind = "Mukerrer",
                Title = $"Aynı fatura numarası farklı UUID ile {docs.Count} kez kayıtlı: {group.Key}",
                Suggestion =
                    "Aynı fatura numarası entegratörden farklı UUID'lerle gelmiş. Genellikle iptal/yeniden " +
                    "kesim veya entegratör tekrarıdır — belgelerin içeriğini karşılaştırıp fazlasını ayıklayın.",
                Documents = docs
                    .Select(i => new ReconciliationDocRef
                    {
                        Id = i.Id,
                        DocType = "Fatura",
                        Label = $"{i.InvoiceNumber} — {i.SenderTitle}",
                        Date = i.IssueDate,
                        Amount = i.PayableAmount
                    })
                    .ToList()
            };
            finding.Key = BuildKey("mukerrer-no", docs.Select(i => i.Id));
            report.Mukerrer.Add(finding);
        }

        // ---------- (a) MÜKERRER — fiş: aynı (VKN, tutar, tarih) ----------
        foreach (var group in receipts
                     .Where(r => !string.IsNullOrWhiteSpace(r.VendorTaxId) && r.TotalAmount is > 0)
                     .GroupBy(r => new { r.VendorTaxId, r.TotalAmount, Date = r.ReceiptDate!.Value.Date })
                     .Where(g => g.Count() > 1))
        {
            var docs = group.OrderBy(r => r.Id).ToList();
            var finding = new ReconciliationFinding
            {
                Kind = "Mukerrer",
                Title = $"Olası mükerrer fiş: {group.Key.VendorTaxId} — {group.Key.TotalAmount:N2} TL, {group.Key.Date:dd.MM.yyyy} ({docs.Count} fiş)",
                Suggestion =
                    "Aynı satıcıdan aynı gün ve aynı tutarlı birden fazla fiş yüklenmiş. Görselleri " +
                    "karşılaştırın; aynı fiş iki kez çekilmişse birini reddedin/silin.",
                Documents = docs
                    .Select(r => new ReconciliationDocRef
                    {
                        Id = r.Id,
                        DocType = "Fiş",
                        Label = $"{r.FileName} — {r.VendorName ?? "?"}",
                        Date = r.ReceiptDate!.Value,
                        Amount = r.TotalAmount ?? 0m
                    })
                    .ToList()
            };
            finding.Key = BuildKey("mukerrer-fis", docs.Select(r => r.Id));
            report.Mukerrer.Add(finding);
        }

        // ---------- (b) EKSİK — kodlanmamış / onaysız / statüde takılan ----------
        var uncoded = invoices.Where(i => i.CodingStatus == CodingStatus.Uncoded).ToList();
        if (uncoded.Count > 0)
        {
            var finding = new ReconciliationFinding
            {
                Kind = "Eksik",
                Title = $"{uncoded.Count} fatura henüz kodlanmadı ({invoices.Count} faturadan)",
                Suggestion = "Entegratörden çekilen ama motordan geçmemiş faturalar var. Faturalar sayfasından \"Dönemi Kodla\" çalıştırın.",
                Documents = uncoded.Take(20)
                    .Select(i => ToInvoiceRef(i.Id, i.InvoiceNumber, i.SenderTitle, i.IssueDate, i.PayableAmount))
                    .ToList()
            };
            finding.Key = BuildKey("eksik-kodsuz", new[] { clientId }, period, uncoded.Count.ToString());
            report.Eksik.Add(finding);
        }

        var needsReview = invoices.Where(i => i.CodingStatus == CodingStatus.NeedsReview).ToList();
        if (needsReview.Count > 0)
        {
            var finding = new ReconciliationFinding
            {
                Kind = "Eksik",
                Title = $"{needsReview.Count} fatura onay kuyruğunda bekliyor",
                Suggestion = "Düşük güvenli kodlamalar onaylanmadan KDV taslağına girmez. Onay Kuyruğu sayfasından tek tek onaylayın.",
                Documents = needsReview.Take(20)
                    .Select(i => ToInvoiceRef(i.Id, i.InvoiceNumber, i.SenderTitle, i.IssueDate, i.PayableAmount))
                    .ToList()
            };
            finding.Key = BuildKey("eksik-onaysiz", new[] { clientId }, period, needsReview.Count.ToString());
            report.Eksik.Add(finding);
        }

        var stuck = invoices.Where(i => i.Status is EInvoiceStatus.Error or EInvoiceStatus.New).ToList();
        if (stuck.Count > 0)
        {
            var finding = new ReconciliationFinding
            {
                Kind = "Eksik",
                Title = $"{stuck.Count} fatura Yeni/Hata statüsünde takılı",
                Suggestion = "Bu faturalar işleme alınmamış görünüyor. Hata statüsündekileri açıp nedenini giderin; Yeni statüsündekileri kodlayıp onaylayın.",
                Documents = stuck.Take(20)
                    .Select(i => ToInvoiceRef(i.Id, i.InvoiceNumber, i.SenderTitle, i.IssueDate, i.PayableAmount))
                    .ToList()
            };
            finding.Key = BuildKey("eksik-statu", new[] { clientId }, period, stuck.Count.ToString());
            report.Eksik.Add(finding);
        }

        // ---------- (c) TUTARSIZ — satır toplamı ≠ fatura toplamı (0,05 TL tolerans) ----------
        foreach (var invoice in invoices.Where(i => i.LineCount > 0))
        {
            var difference = Math.Abs(invoice.LineTotal - invoice.PayableAmount);
            if (difference <= AmountTolerance) continue;

            var finding = new ReconciliationFinding
            {
                Kind = "Tutarsiz",
                Title = $"Satır toplamı fatura toplamından farklı: {invoice.InvoiceNumber} (fark {difference:N2} TL)",
                Suggestion =
                    $"Kalem toplamı {invoice.LineTotal:N2} TL, fatura toplamı {invoice.PayableAmount:N2} TL. " +
                    "İskonto/yuvarlama olabilir; ham UBL içeriğini kontrol edin. Fark bilinçliyse \"Yoksay\" ile kapatın.",
                Documents = new List<ReconciliationDocRef> { ToInvoiceRef(invoice.Id, invoice.InvoiceNumber, invoice.SenderTitle, invoice.IssueDate, invoice.PayableAmount) }
            };
            finding.Key = BuildKey("tutarsiz", new[] { invoice.Id });
            report.Tutarsiz.Add(finding);
        }

        // ---------- "Yoksay" filtrelemesi (DB — tenant filtresi DbContext'te) ----------
        var allKeys = report.Mukerrer.Concat(report.Eksik).Concat(report.Tutarsiz).Select(f => f.Key).ToList();
        if (allKeys.Count > 0)
        {
            var dismissed = await _db.ReconciliationDismissals
                .AsNoTracking()
                .Where(d => allKeys.Contains(d.FindingKey))
                .Select(d => d.FindingKey)
                .ToListAsync(ct);

            if (dismissed.Count > 0)
            {
                var dismissedSet = dismissed.ToHashSet(StringComparer.Ordinal);
                report.DismissedCount =
                    report.Mukerrer.RemoveAll(f => dismissedSet.Contains(f.Key)) +
                    report.Eksik.RemoveAll(f => dismissedSet.Contains(f.Key)) +
                    report.Tutarsiz.RemoveAll(f => dismissedSet.Contains(f.Key));
            }
        }

        return report;
    }

    private static ReconciliationDocRef ToInvoiceRef(Guid id, string number, string title, DateTime date, decimal amount) =>
        new()
        {
            Id = id,
            DocType = "Fatura",
            Label = $"{number} — {title}",
            Date = date,
            Amount = amount
        };

    /// <summary>
    /// Deterministik bulgu anahtarı: tür + sıralı kimlikler (+ ek parçalar) SHA-256 → ilk 32 hex.
    /// Aynı bulgu her analizde aynı anahtarı üretir; "Yoksay" kaydı böylece kalıcı eşleşir.
    /// </summary>
    private static string BuildKey(string kind, IEnumerable<Guid> ids, params string[] extra)
    {
        var payload = kind + "|" +
                      string.Join(",", ids.Select(i => i.ToString("N")).OrderBy(s => s, StringComparer.Ordinal)) +
                      (extra.Length > 0 ? "|" + string.Join("|", extra) : string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>"Yoksay": bulgu anahtarını tenant'a kaydeder (varsa sessizce geçer).</summary>
    public async Task DismissAsync(Guid tenantId, string findingKey, CancellationToken ct)
    {
        var key = findingKey.Trim().ToLowerInvariant();
        var exists = await _db.ReconciliationDismissals.AnyAsync(d => d.FindingKey == key, ct);
        if (exists) return;

        _db.ReconciliationDismissals.Add(new ReconciliationDismissal
        {
            TenantId = tenantId,
            FindingKey = key
        });
        await _db.SaveChangesAsync(ct);
    }
}
