using System.Text.Json;
using MaliMusavir.Api.Beyanname;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>İndirilecek KDV kırılım satırı (191.01 = %1, 191.02 = %10, 191.03 = %20, 191.99 = diğer).</summary>
public class Kdv1BreakdownItem
{
    public string HesapKodu { get; set; } = string.Empty;
    public decimal KdvOrani { get; set; }
    /// <summary>KDV hariç matrah toplamı.</summary>
    public decimal Matrah { get; set; }
    public decimal Kdv { get; set; }
    /// <summary>Bu kırılıma giren belge (fatura satırı + fiş) sayısı.</summary>
    public int BelgeSayisi { get; set; }
}

/// <summary>
/// KDV1 taslak hesap özeti. Declaration.SummaryJson'a camelCase JSON olarak yazılır
/// (MockGibProvider tahakkuku "odenecekKdv" alanından okur).
/// </summary>
public class Kdv1Summary
{
    public string Period { get; set; } = string.Empty;
    /// <summary>Giden (satış) faturalarının KDV hariç matrah toplamı.</summary>
    public decimal HesaplananMatrah { get; set; }
    /// <summary>Hesaplanan KDV — giden faturalardan (satış yoksa 0).</summary>
    public decimal HesaplananKdv { get; set; }
    /// <summary>İndirilecek KDV toplamı (onaylı gelen fatura satırları + onaylı fişler).</summary>
    public decimal IndirilecekKdvToplam { get; set; }
    public List<Kdv1BreakdownItem> IndirimKirilimlari { get; set; } = new();
    /// <summary>Önceki dönem KDV1 beyanından devreden KDV (yoksa 0).</summary>
    public decimal OncekiDonemdenDevredenKdv { get; set; }
    /// <summary>Ödenecek KDV (hesaplanan &gt; indirim toplamı ise fark, değilse 0).</summary>
    public decimal OdenecekKdv { get; set; }
    /// <summary>Sonraki döneme devreden KDV (indirim fazlası).</summary>
    public decimal SonrakiDonemeDevredenKdv { get; set; }
    public int GidenFaturaSayisi { get; set; }
    public int GelenFaturaSayisi { get; set; }
    public int FisSayisi { get; set; }

    // ---------- Faz 8 ----------
    /// <summary>B3: KKEG'e isabet ettiği için indirilecek KDV'den DÜŞÜLEN tutar (kırılımlara yansıtılmıştır).</summary>
    public decimal KkegIndirilemeyenKdv { get; set; }
    /// <summary>B3: Dönemdeki KKEG matrah toplamı (bilgi).</summary>
    public decimal KkegToplam { get; set; }
    /// <summary>B2: Dönemdeki onaylı alış faturalarında tevkif edilen KDV toplamı (KDV2 ile beyan edilir; bilgi).</summary>
    public decimal TevkifatKdvToplam { get; set; }
}

/// <summary>Faz 8 — B2: KDV2 taslağı kod bazlı kırılım satırı.</summary>
public class Kdv2BreakdownItem
{
    /// <summary>GİB tevkifat kodu (ör. "601").</summary>
    public string Kod { get; set; } = string.Empty;
    public string Aciklama { get; set; } = string.Empty;
    /// <summary>Tevkifat oranı pay/payda (ör. "9/10").</summary>
    public string Oran { get; set; } = string.Empty;
    /// <summary>KDV hariç matrah toplamı.</summary>
    public decimal Matrah { get; set; }
    /// <summary>Sorumlu sıfatıyla beyan edilecek (tevkif edilen) KDV.</summary>
    public decimal TevkifatKdv { get; set; }
    public int BelgeSayisi { get; set; }
}

/// <summary>
/// Faz 8 — B2: KDV2 (sorumlu sıfatıyla) taslak özeti. Dönemdeki ONAYLI tevkifatlı ALIŞ
/// faturalarından hesaplanır. "OdenecekKdv" alan adı bilinçlidir: MockGibProvider tahakkuku
/// SummaryJson'daki "odenecekKdv" alanından okur (KDV1 ile aynı sözleşme).
/// </summary>
public class Kdv2Summary
{
    public string Period { get; set; } = string.Empty;
    /// <summary>Sorumlu sıfatıyla ödenecek tevkifat KDV toplamı.</summary>
    public decimal OdenecekKdv { get; set; }
    public List<Kdv2BreakdownItem> Kirilimlar { get; set; } = new();
    /// <summary>Tevkifatlı satır içeren onaylı alış faturası sayısı.</summary>
    public int FaturaSayisi { get; set; }
    /// <summary>Tevkifatlı satır sayısı.</summary>
    public int SatirSayisi { get; set; }
}

/// <summary>Beyanname işlemi sonucu (Türkçe mesajlı; NotFound controller'da 404'e eşlenir).</summary>
public class DeclarationResult
{
    public bool Success { get; init; }
    public bool NotFound { get; init; }
    public string? Message { get; init; }
    public string? ErrorMessage { get; init; }
    public Declaration? Declaration { get; init; }
    /// <summary>
    /// Faz 11 (E6): Hata GEÇİCİ mi (sağlayıcı/ağ hatası — kuyruğa alınıp yeniden denenebilir)?
    /// Durum hataları ("zaten gönderilmiş" vb.) retryable DEĞİLDİR.
    /// </summary>
    public bool Retryable { get; init; }

    public static DeclarationResult Ok(Declaration declaration, string? message = null) =>
        new() { Success = true, Declaration = declaration, Message = message };

    public static DeclarationResult Fail(string errorMessage, bool notFound = false, bool retryable = false) =>
        new() { Success = false, ErrorMessage = errorMessage, NotFound = notFound, Retryable = retryable };
}

/// <summary>
/// Beyanname otomasyonu (blueprint M8/B9). Bu fazda KDV1 taslak hesaplaması:
/// - Hesaplanan KDV: dönem içindeki GİDEN faturalardan (satış senkronu henüz yoksa 0).
/// - İndirilecek KDV: dönem içindeki ONAYLI gelen fatura satırları + ONAYLI fişlerin KDV toplamı,
///   oran kırılımıyla (191.01 %1 / 191.02 %10 / 191.03 %20 / 191.99 diğer).
/// - Önceki dönemden devreden: bir önceki dönemin KDV1 kaydındaki "sonraki döneme devreden" alanı
///   (önceki kayıt yoksa 0; taslak/onaylı ayrımı yapılmaz — sınırlama flow spec'te not edildi).
/// - Sonuç: ödenecek KDV veya sonraki döneme devreden.
/// Gönderim/tahakkuk IEBeyannameProvider adapter'ı üzerindendir (MockGib → gerçek GİB tek sınıfla).
/// </summary>
public class DeclarationService
{
    private static readonly JsonSerializerOptions SummaryJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly IEBeyannameProvider _provider;
    private readonly ILogger<DeclarationService> _logger;

    public DeclarationService(AppDbContext db, IEBeyannameProvider provider, ILogger<DeclarationService> logger)
    {
        _db = db;
        _provider = provider;
        _logger = logger;
    }

    /// <summary>SummaryJson'ı tip güvenli KDV1 özetine çevirir (bozuksa null).</summary>
    public static Kdv1Summary? ParseSummary(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Kdv1Summary>(summaryJson, SummaryJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>SummaryJson'ı tip güvenli KDV2 özetine çevirir (bozuksa null) — Faz 8 (B2).</summary>
    public static Kdv2Summary? ParseKdv2Summary(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Kdv2Summary>(summaryJson, SummaryJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// KDV1 taslağı hazırlar. Aynı (mükellef, tür, dönem) için kayıt varsa mevcut taslağı
    /// YENİDEN HESAPLAR (Draft/Ready/Error); gönderilmiş/onaylanmış beyanname yeniden hesaplanamaz.
    /// </summary>
    public async Task<DeclarationResult> PrepareAsync(
        Guid tenantId, Guid clientId, DeclarationType type, string period, CancellationToken ct)
    {
        if (type is not (DeclarationType.KDV1 or DeclarationType.KDV2))
        {
            return DeclarationResult.Fail(
                "Bu beyanname türü yakında eklenecek. Şimdilik KDV1 ve KDV2 taslağı hazırlanabilir.");
        }

        if (!TryParsePeriod(period, out var periodStart))
            return DeclarationResult.Fail("Dönem biçimi geçersiz. Örnek: 2026-06 (yyyy-AA).");

        var periodEnd = periodStart.AddMonths(1);

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return DeclarationResult.Fail("Mükellef bulunamadı.", notFound: true);

        var declaration = await _db.Declarations
            .FirstOrDefaultAsync(d => d.ClientId == clientId && d.Type == type && d.Period == period, ct);

        if (declaration is not null && declaration.Status is DeclarationStatus.Sent)
            return DeclarationResult.Fail("Bu dönemin beyannamesi GİB'e gönderilmiş durumda. Önce durumunu sorgulayın.");

        if (declaration is not null && declaration.Status is DeclarationStatus.Approved)
            return DeclarationResult.Fail("Bu dönemin beyannamesi onaylanmış; yeniden hesaplanamaz.");

        // Faz 8 (B2): tür bazlı özet hesabı — KDV1 (indirilecek KDV) veya KDV2 (sorumlu sıfatıyla tevkifat).
        string summaryJson;
        string doneDetail;
        if (type == DeclarationType.KDV1)
        {
            var summary = await ComputeKdv1SummaryAsync(clientId, period, periodStart, periodEnd, ct);
            summaryJson = JsonSerializer.Serialize(summary, SummaryJsonOptions);
            doneDetail = $"({summary.GelenFaturaSayisi} onaylı fatura + {summary.FisSayisi} onaylı fiş işlendi)";
        }
        else
        {
            var summary = await ComputeKdv2SummaryAsync(clientId, period, periodStart, periodEnd, ct);
            summaryJson = JsonSerializer.Serialize(summary, SummaryJsonOptions);
            doneDetail = summary.SatirSayisi == 0
                ? "(dönemde tevkifatlı ONAYLI alış faturası bulunamadı — özet 0 olarak hazırlandı)"
                : $"({summary.FaturaSayisi} tevkifatlı fatura, {summary.SatirSayisi} satır işlendi)";
        }

        var isNew = declaration is null;
        if (declaration is null)
        {
            declaration = new Declaration
            {
                TenantId = client.TenantId,
                ClientId = clientId,
                Type = type,
                Period = period
            };
            _db.Declarations.Add(declaration);
        }

        declaration.Status = DeclarationStatus.Draft;
        declaration.SummaryJson = summaryJson;
        declaration.PreparedAt = DateTime.UtcNow;
        declaration.PackageRef = null;
        declaration.SentAt = null;
        declaration.ApprovedAt = null;
        declaration.TahakkukAmount = null;
        declaration.ErrorMessage = null;

        await _db.SaveChangesAsync(ct);

        var typeLabel = type == DeclarationType.KDV1 ? "KDV1" : "KDV2";
        var message = isNew
            ? $"{typeLabel} taslağı hazırlandı {doneDetail}."
            : $"Mevcut {typeLabel} taslağı yeniden hesaplandı {doneDetail}.";

        return DeclarationResult.Ok(declaration, message);
    }

    /// <summary>Beyannameyi GİB'e gönderir (Draft/Ready/Error → Sent). Error'da yeniden deneme budur.</summary>
    public async Task<DeclarationResult> SendAsync(Guid id, CancellationToken ct)
    {
        var declaration = await _db.Declarations.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (declaration is null)
            return DeclarationResult.Fail("Beyanname bulunamadı.", notFound: true);

        if (declaration.Status == DeclarationStatus.Sent)
            return DeclarationResult.Fail("Beyanname zaten gönderilmiş. \"Durumu Sorgula\" ile tahakkuku kontrol edin.");

        if (declaration.Status == DeclarationStatus.Approved)
            return DeclarationResult.Fail("Beyanname zaten onaylanmış; yeniden gönderilemez.");

        if (string.IsNullOrWhiteSpace(declaration.SummaryJson))
            return DeclarationResult.Fail("Beyanname taslağı boş. Önce \"Taslağı Hazırla\" ile hesaplama yapın.");

        var sendResult = await _provider.SendDeclarationAsync(declaration, ct);
        if (!sendResult.Success || sendResult.Data is null)
        {
            // Faz 11 (E6): sağlayıcı gönderim hatası GEÇİCİDİR (GİB/ağ çökmesi) — kuyruğa alınabilir.
            return DeclarationResult.Fail(
                sendResult.ErrorMessage ?? "Beyanname gönderilemedi. Lütfen tekrar deneyin.",
                retryable: true);
        }

        var wasError = declaration.Status == DeclarationStatus.Error;
        declaration.Status = DeclarationStatus.Sent;
        declaration.PackageRef = sendResult.Data.PackageRef;
        declaration.SentAt = DateTime.UtcNow;
        declaration.ErrorMessage = null;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Beyanname gönderildi. Id: {Id}, Paket: {PackageRef}, YenidenDeneme: {Retry}",
            declaration.Id, declaration.PackageRef, wasError);

        return DeclarationResult.Ok(declaration,
            $"Beyanname GİB'e gönderildi. Paket no: {declaration.PackageRef}. " +
            "Yaklaşık 30 saniye sonra \"Durumu Sorgula\" ile tahakkuku kontrol edin.");
    }

    /// <summary>Sağlayıcıdan durum/tahakkuk çeker: Onaylandı → Approved, ret → Error (yeniden gönderilebilir).</summary>
    public async Task<DeclarationResult> RefreshStatusAsync(Guid id, CancellationToken ct)
    {
        var declaration = await _db.Declarations.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (declaration is null)
            return DeclarationResult.Fail("Beyanname bulunamadı.", notFound: true);

        if (declaration.Status == DeclarationStatus.Approved)
            return DeclarationResult.Ok(declaration, "Beyanname zaten onaylanmış.");

        if (declaration.Status != DeclarationStatus.Sent)
            return DeclarationResult.Fail("Yalnızca gönderilmiş beyannamelerin durumu sorgulanabilir. Önce \"GİB'e Gönder\"i kullanın.");

        var statusResult = await _provider.GetStatusAsync(declaration, ct);
        if (!statusResult.Success || statusResult.Data is null)
        {
            return DeclarationResult.Fail(
                statusResult.ErrorMessage ?? "Durum sorgulanamadı. Lütfen tekrar deneyin.");
        }

        switch (statusResult.Data.State)
        {
            case GibDeclarationState.Pending:
                return DeclarationResult.Ok(declaration,
                    "GİB beyannameyi hâlâ işliyor. Lütfen yaklaşık 30 saniye sonra tekrar sorgulayın.");

            case GibDeclarationState.Approved:
                declaration.Status = DeclarationStatus.Approved;
                declaration.ApprovedAt = DateTime.UtcNow;
                declaration.TahakkukAmount = statusResult.Data.TahakkukAmount ?? 0m;
                declaration.ErrorMessage = null;
                await _db.SaveChangesAsync(ct);
                return DeclarationResult.Ok(declaration,
                    declaration.TahakkukAmount > 0
                        ? $"Beyanname onaylandı. Tahakkuk tutarı: {declaration.TahakkukAmount:N2} TL."
                        : "Beyanname onaylandı. Ödenecek KDV çıkmadı (devreden dönem).");

            case GibDeclarationState.Error:
            default:
                declaration.Status = DeclarationStatus.Error;
                declaration.ErrorMessage = statusResult.Data.ErrorMessage ?? "GİB beyannameyi reddetti.";
                await _db.SaveChangesAsync(ct);
                return DeclarationResult.Ok(declaration,
                    $"GİB beyannameyi reddetti: {declaration.ErrorMessage} Düzeltip \"Yeniden Gönder\" ile tekrar deneyebilirsiniz.");
        }
    }

    private async Task<Kdv1Summary> ComputeKdv1SummaryAsync(
        Guid clientId, string period, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        // Hesaplanan KDV: dönem içindeki GİDEN (satış) faturaları. Satış senkronu henüz
        // yapılmadıysa 0 kalır — özet ekranında bilgi notu gösterilir.
        var outgoing = await _db.EInvoices.AsNoTracking()
            .Where(i => i.ClientId == clientId &&
                        i.Direction == InvoiceDirection.Outgoing &&
                        i.IssueDate >= periodStart && i.IssueDate < periodEnd)
            .Select(i => new { i.TaxExclusiveAmount, i.TaxTotal })
            .ToListAsync(ct);

        // İndirilecek KDV: yalnızca ONAYLI (CodingStatus.Approved) gelen faturaların satırları.
        // Faz 8: KKEG ve tevkifat alanları da çekilir (işaret yoksa hepsi null/false → eski davranış).
        var incomingLines = await _db.EInvoiceLines.AsNoTracking()
            .Where(l => l.EInvoice!.ClientId == clientId &&
                        l.EInvoice.Direction == InvoiceDirection.Incoming &&
                        l.EInvoice.CodingStatus == CodingStatus.Approved &&
                        l.EInvoice.IssueDate >= periodStart && l.EInvoice.IssueDate < periodEnd)
            .Select(l => new { l.EInvoiceId, l.VatRate, l.VatAmount, l.LineAmount, l.IsKkeg, l.KkegAmount, l.WithholdingVatAmount })
            .ToListAsync(ct);

        // + ONAYLI fişler (fiş OCR modülü) — matrah/KDV alanları boşsa 0 sayılır.
        var receipts = await _db.Receipts.AsNoTracking()
            .Where(r => r.ClientId == clientId &&
                        r.Status == ReceiptStatus.Approved &&
                        r.ReceiptDate != null &&
                        r.ReceiptDate >= periodStart && r.ReceiptDate < periodEnd)
            .Select(r => new { r.VatRate, r.VatAmount, r.NetAmount, r.IsKkeg, r.KkegAmount })
            .ToListAsync(ct);

        // Oran kırılımı: %1 → 191.01, %10 → 191.02, %20 → 191.03, diğer → 191.99.
        var buckets = new Dictionary<decimal, Kdv1BreakdownItem>();

        void AddToBucket(decimal rate, decimal matrah, decimal kdv)
        {
            if (!buckets.TryGetValue(rate, out var bucket))
            {
                bucket = new Kdv1BreakdownItem { KdvOrani = rate, HesapKodu = MapVatAccount(rate) };
                buckets[rate] = bucket;
            }
            bucket.Matrah += matrah;
            bucket.Kdv += kdv;
            bucket.BelgeSayisi++;
        }

        // Faz 8 (B3): KKEG'e isabet eden KDV indirilemez → satırın kırılıma giren KDV'sinden düşülür.
        // (B2): tevkif edilen KDV bilgisi toplanır (KDV2 beyanına gider; KDV1'de bilgi satırı).
        var kkegDisallowedVat = 0m;
        var kkegTotal = 0m;
        var withheldVatTotal = 0m;

        foreach (var line in incomingLines)
        {
            var disallowed = KkegAnalyzer.DisallowedVat(line.IsKkeg, line.KkegAmount, line.LineAmount, line.VatAmount);
            kkegDisallowedVat += disallowed;
            if (line.IsKkeg && line.KkegAmount is > 0)
                kkegTotal += Math.Min(line.KkegAmount.Value, line.LineAmount);
            withheldVatTotal += line.WithholdingVatAmount ?? 0m;

            AddToBucket(line.VatRate, line.LineAmount, line.VatAmount - disallowed);
        }

        foreach (var receipt in receipts)
        {
            var net = receipt.NetAmount ?? 0m;
            var vat = receipt.VatAmount ?? 0m;
            var disallowed = KkegAnalyzer.DisallowedVat(receipt.IsKkeg, receipt.KkegAmount, net, vat);
            kkegDisallowedVat += disallowed;
            if (receipt.IsKkeg && receipt.KkegAmount is > 0)
                kkegTotal += net > 0 ? Math.Min(receipt.KkegAmount.Value, net) : receipt.KkegAmount.Value;

            AddToBucket(receipt.VatRate ?? 0m, net, vat - disallowed);
        }

        var breakdown = buckets.Values
            .OrderBy(b => b.KdvOrani)
            .Select(b =>
            {
                b.Matrah = Math.Round(b.Matrah, 2);
                b.Kdv = Math.Round(b.Kdv, 2);
                return b;
            })
            .ToList();

        var hesaplananMatrah = Math.Round(outgoing.Sum(o => o.TaxExclusiveAmount), 2);
        var hesaplananKdv = Math.Round(outgoing.Sum(o => o.TaxTotal), 2);
        var indirilecekToplam = Math.Round(breakdown.Sum(b => b.Kdv), 2);

        // Önceki dönemden devreden: bir önceki dönemin KDV1 kaydındaki devir alanı (yoksa 0).
        var previousPeriod = periodStart.AddMonths(-1).ToString("yyyy-MM");
        var previousJson = await _db.Declarations.AsNoTracking()
            .Where(d => d.ClientId == clientId && d.Type == DeclarationType.KDV1 && d.Period == previousPeriod)
            .Select(d => d.SummaryJson)
            .FirstOrDefaultAsync(ct);
        var oncekiDevreden = ParseSummary(previousJson)?.SonrakiDonemeDevredenKdv ?? 0m;

        var toplamIndirim = indirilecekToplam + oncekiDevreden;
        var odenecek = hesaplananKdv > toplamIndirim ? Math.Round(hesaplananKdv - toplamIndirim, 2) : 0m;
        var sonrakiDevreden = hesaplananKdv >= toplamIndirim ? 0m : Math.Round(toplamIndirim - hesaplananKdv, 2);

        return new Kdv1Summary
        {
            Period = period,
            HesaplananMatrah = hesaplananMatrah,
            HesaplananKdv = hesaplananKdv,
            IndirilecekKdvToplam = indirilecekToplam,
            IndirimKirilimlari = breakdown,
            OncekiDonemdenDevredenKdv = oncekiDevreden,
            OdenecekKdv = odenecek,
            SonrakiDonemeDevredenKdv = sonrakiDevreden,
            GidenFaturaSayisi = outgoing.Count,
            GelenFaturaSayisi = incomingLines.Select(l => l.EInvoiceId).Distinct().Count(),
            FisSayisi = receipts.Count,
            KkegIndirilemeyenKdv = Math.Round(kkegDisallowedVat, 2),
            KkegToplam = Math.Round(kkegTotal, 2),
            TevkifatKdvToplam = Math.Round(withheldVatTotal, 2)
        };
    }

    /// <summary>
    /// Faz 8 (B2): KDV2 (sorumlu sıfatıyla) taslağı — dönemdeki ONAYLI tevkifatlı ALIŞ
    /// faturalarının satırlarından kod bazlı kırılım. Onaysız faturalar dahil edilmez
    /// (dönem sonu kontrol listesi B10 bu satırları ayrıca uyarır).
    /// </summary>
    private async Task<Kdv2Summary> ComputeKdv2SummaryAsync(
        Guid clientId, string period, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        var lines = await _db.EInvoiceLines.AsNoTracking()
            .Where(l => l.EInvoice!.ClientId == clientId &&
                        l.EInvoice.Direction == InvoiceDirection.Incoming &&
                        l.EInvoice.CodingStatus == CodingStatus.Approved &&
                        l.EInvoice.IssueDate >= periodStart && l.EInvoice.IssueDate < periodEnd &&
                        l.WithholdingCode != null && l.WithholdingVatAmount != null && l.WithholdingVatAmount > 0)
            .Select(l => new { l.EInvoiceId, l.WithholdingCode, l.WithholdingRate, l.LineAmount, WithholdingVat = l.WithholdingVatAmount!.Value })
            .ToListAsync(ct);

        // Kod açıklamaları global tevkifat tablosundan (yoksa kod olduğu gibi gösterilir).
        var descriptions = await _db.WithholdingRules.AsNoTracking()
            .ToDictionaryAsync(r => r.Code, r => r.Description, ct);

        var kirilimlar = lines
            .GroupBy(l => new { l.WithholdingCode, l.WithholdingRate })
            .Select(g => new Kdv2BreakdownItem
            {
                Kod = g.Key.WithholdingCode!,
                Aciklama = descriptions.TryGetValue(g.Key.WithholdingCode!, out var d) ? d : "Tevkifat kodu " + g.Key.WithholdingCode,
                Oran = g.Key.WithholdingRate ?? "—",
                Matrah = Math.Round(g.Sum(l => l.LineAmount), 2),
                TevkifatKdv = Math.Round(g.Sum(l => l.WithholdingVat), 2),
                BelgeSayisi = g.Select(l => l.EInvoiceId).Distinct().Count()
            })
            .OrderBy(k => k.Kod, StringComparer.Ordinal)
            .ToList();

        return new Kdv2Summary
        {
            Period = period,
            OdenecekKdv = Math.Round(kirilimlar.Sum(k => k.TevkifatKdv), 2),
            Kirilimlar = kirilimlar,
            FaturaSayisi = lines.Select(l => l.EInvoiceId).Distinct().Count(),
            SatirSayisi = lines.Count
        };
    }

    private static string MapVatAccount(decimal rate) => rate switch
    {
        1m => "191.01",
        10m => "191.02",
        20m => "191.03",
        _ => "191.99"
    };

    /// <summary>"yyyy-MM" dönemini ayın ilk günü (UTC) olarak çözer.</summary>
    public static bool TryParsePeriod(string period, out DateTime periodStart)
    {
        periodStart = default;
        if (string.IsNullOrWhiteSpace(period)) return false;

        var parts = period.Split('-');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var year) ||
            !int.TryParse(parts[1], out var month) ||
            year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            return false;
        }

        periodStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }
}
