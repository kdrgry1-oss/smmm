using System.Text.RegularExpressions;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Tek satırın kodlama sonucu: hangi katman hangi güvenle kod atadı.</summary>
public class LineCodingInfo
{
    public Guid LineId { get; init; }
    /// <summary>Kaynağın Türkçe etiketi: "Hafıza", "Satıcı varsayılanı", "Kural", "AI", "Varsayılan gider".</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>Fatura kodlama sonucu özeti.</summary>
public class CodingOutcome
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public CodingStatus CodingStatus { get; init; }
    public List<LineCodingInfo> Lines { get; init; } = new();
}

/// <summary>Onay (öğrenme) sonucu.</summary>
public class ApproveOutcome
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Onayda tek satır düzeltmesi (Faz 8'de tevkifat + KKEG alanlarıyla genişletildi).
/// null = alan gönderilmedi, mevcut değer korunur. WithholdingCode = "" (boş dize) tevkifatı temizler;
/// IsKkeg = false KKEG işaretini temizler.
/// </summary>
public sealed record ApproveLineCorrection(
    string? AccountCode,
    string? CariCode,
    string? WithholdingCode = null,
    string? WithholdingRate = null,
    decimal? WithholdingVatAmount = null,
    bool? IsKkeg = null,
    string? KkegReason = null,
    decimal? KkegAmount = null);

/// <summary>Fiş kod önerisi sonucu (Faz 3): hangi katman hangi güvenle kod atadı.</summary>
public class ReceiptSuggestion
{
    public string AccountCode { get; init; } = string.Empty;
    public double Confidence { get; init; }
    /// <summary>Kaynağın Türkçe etiketi (fatura kademesindekiyle aynı sözlük).</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>VendorCariMap'te kayıtlıysa satıcının cari kodu; yoksa null (fişte otomatik cari açılmaz).</summary>
    public string? CariCode { get; init; }
}

/// <summary>
/// Otomatik Hesap/Cari Kod Motoru — kademeli mimari (motor tasarımı §1).
/// Her kalem sırayla katmanlardan geçer; ilk yeterli güveni veren katman kodu atar:
///   K1a AccountMemory tam eşleşme (0.95+) → K1b VendorDefault (0.80) →
///   K2 CodingRule (0.75) → K3 IAccountClassifier (bu fazda Noop) →
///   fallback ClientCodingSettings.DefaultExpenseAccount (0.30).
/// Boşluksuz doldurma ilkesi: motor HER ZAMAN bir kod üretir; düşük güvenli
/// satırlar faturayı onay kuyruğuna (NeedsReview) düşürür.
/// Öğrenme, ApproveCodingAsync içindedir: her onay AccountMemory + VendorDefault'a geri yazılır.
/// </summary>
public class CodingEngine
{
    // Katman güven VARSAYILANLARI. Faz 7 (E4) itibarıyla asıl kaynak parametre merkezidir
    // ("motor.guven.*" anahtarları); tablo boşsa bu sabitler geçerli kalır — davranış birebir aynı.
    private const double MemoryBaseConfidenceDefault = 0.95;
    private const double VendorDefaultConfidenceDefault = 0.80;
    /// <summary>VKN eşleşmesi yokken NORMALİZE UNVAN eşleşmesinin güveni (mizan/fiş listesi içe aktarım kanalı).</summary>
    private const double VendorNameMatchConfidenceDefault = 0.85;
    private const double RuleConfidenceDefault = 0.75;
    private const double FallbackConfidenceDefault = 0.30;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly AppDbContext _db;
    private readonly IAccountClassifier _classifier;
    private readonly ParameterService _parameters;
    private readonly KkegAnalyzer _kkegAnalyzer;
    private readonly ILogger<CodingEngine> _logger;

    public CodingEngine(
        AppDbContext db,
        IAccountClassifier classifier,
        ParameterService parameters,
        KkegAnalyzer kkegAnalyzer,
        ILogger<CodingEngine> logger)
    {
        _db = db;
        _classifier = classifier;
        _parameters = parameters;
        _kkegAnalyzer = kkegAnalyzer;
        _logger = logger;
    }

    // Parametre merkezi okumaları (EnsureLoadedAsync sonrası ücretsizdir).
    private double MemoryBaseConfidence => (double)_parameters.GetDecimal("motor.guven.hafiza", (decimal)MemoryBaseConfidenceDefault);
    private double VendorDefaultConfidence => (double)_parameters.GetDecimal("motor.guven.satici", (decimal)VendorDefaultConfidenceDefault);
    private double VendorNameMatchConfidence => (double)_parameters.GetDecimal("motor.guven.unvan", (decimal)VendorNameMatchConfidenceDefault);
    private double RuleConfidence => (double)_parameters.GetDecimal("motor.guven.kural", (decimal)RuleConfidenceDefault);
    private double FallbackConfidence => (double)_parameters.GetDecimal("motor.guven.varsayilan", (decimal)FallbackConfidenceDefault);

    /// <summary>
    /// Mükellefin kodlama ayarlarını getirir; yoksa varsayılanlarla oluşturur.
    /// (Faz 2 öncesi açılmış mükellefler için güvenlik ağı.)
    /// </summary>
    public async Task<ClientCodingSettings> GetOrCreateSettingsAsync(Guid clientId, CancellationToken ct)
    {
        var settings = await _db.ClientCodingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
        if (settings is null)
        {
            settings = new ClientCodingSettings { ClientId = clientId };
            _db.ClientCodingSettings.Add(settings);
            await _db.SaveChangesAsync(ct);
        }
        return settings;
    }

    /// <summary>
    /// Faturayı kademeli motorla kodlar: her satıra SuggestedAccountCode + Confidence + CariCode yazar,
    /// fatura CodingStatus'unu belirler ve kaydeder. Onaylanmış fatura yeniden kodlanmaz.
    /// Fatura, satırlarıyla birlikte İZLENEN (tracked) bir entity olmalıdır.
    /// </summary>
    public async Task<CodingOutcome> CodeInvoiceAsync(EInvoice invoice, CancellationToken ct)
    {
        if (invoice.CodingStatus == CodingStatus.Approved)
        {
            return new CodingOutcome
            {
                Success = false,
                ErrorMessage = "Onaylanmış fatura yeniden kodlanamaz.",
                CodingStatus = invoice.CodingStatus
            };
        }

        if (invoice.Lines.Count == 0)
        {
            return new CodingOutcome
            {
                Success = false,
                ErrorMessage = "Faturada kodlanacak kalem satırı yok.",
                CodingStatus = invoice.CodingStatus
            };
        }

        var settings = await GetOrCreateSettingsAsync(invoice.ClientId, ct);
        await _parameters.EnsureLoadedAsync(ct); // Faz 7 (E4): katman güven değerleri parametreden

        // Katman verileri tek seferde çekilir (satır başına sorgu yok).
        var memories = await _db.AccountMemories
            .AsNoTracking()
            .Where(m => m.ClientId == invoice.ClientId && m.SenderTaxId == invoice.SenderTaxId)
            .ToListAsync(ct);

        var vendorDefault = await _db.VendorDefaults
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.ClientId == invoice.ClientId && v.SenderTaxId == invoice.SenderTaxId, ct);

        // İkincil eşleşme: VKN'ye kayıtlı varsayılan yoksa NORMALİZE UNVAN denenir
        // (geçmiş fiş listesi içe aktarımı VKN'siz, unvan anahtarlı öğrenme yazar).
        var vendorDefaultByName = false;
        if (vendorDefault is null)
        {
            var senderNameKey = TextNormalizer.NormalizeCompanyName(invoice.SenderTitle);
            if (senderNameKey.Length > 0)
            {
                vendorDefault = await _db.VendorDefaults
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.ClientId == invoice.ClientId && v.VendorNameNormalized == senderNameKey, ct);
                vendorDefaultByName = vendorDefault is not null;
            }
        }

        var rules = await _db.CodingRules
            .AsNoTracking()
            .Where(r => r.IsActive && (r.ClientId == null || r.ClientId == invoice.ClientId))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);

        // Cari kod: haritada varsa kullan; yoksa şemaya göre yeni kod üret ve haritaya yaz.
        var cariCode = await ResolveCariCodeAsync(invoice, settings, ct);

        // Faz 8 (B2): tevkifat kuralları — global tablo, yalnızca GELEN (alış) faturalarda uygulanır.
        var withholdingRules = invoice.Direction == InvoiceDirection.Incoming
            ? await _db.WithholdingRules.AsNoTracking().OrderBy(r => r.Code).ToListAsync(ct)
            : new List<WithholdingRule>();

        var lineInfos = new List<LineCodingInfo>();

        foreach (var line in invoice.Lines.OrderBy(l => l.LineNumber))
        {
            var normalized = TextNormalizer.Normalize(line.ItemName);
            var signature = TextNormalizer.BuildItemSignature(line.ItemName);

            string suggested;
            double confidence;
            string source;

            // K1a — öğrenen hafıza: (satıcı VKN, kalem imzası, KDV oranı) tam eşleşme.
            var memory = memories.FirstOrDefault(m => m.ItemSignature == signature && m.VatRate == line.VatRate);
            if (memory is not null)
            {
                suggested = memory.AccountCode;
                // Onay sayısı arttıkça güven 0.95'ten 0.99'a yaklaşır.
                confidence = Math.Min(0.99, MemoryBaseConfidence + 0.005 * Math.Min(memory.ApproveCount, 8));
                source = "Hafıza";
            }
            // K1b — satıcı seviyesi varsayılan (VKN ya da normalize unvan eşleşmesi).
            else if (vendorDefault is not null)
            {
                suggested = vendorDefault.AccountCode;
                confidence = vendorDefaultByName ? VendorNameMatchConfidence : VendorDefaultConfidence;
                source = vendorDefaultByName ? "Satıcı unvan eşleşmesi" : "Satıcı varsayılanı";
            }
            else
            {
                // K2 — kural motoru: öncelik sırasıyla regex veya anahtar kelime eşleşmesi.
                var rule = rules.FirstOrDefault(r => RuleMatches(r, normalized));
                if (rule is not null)
                {
                    suggested = rule.AccountCode;
                    confidence = RuleConfidence;
                    source = "Kural";
                }
                else
                {
                    // K3 — AI sınıflandırıcı genişleme noktası (bu fazda Noop → null).
                    var aiSuggestion = await _classifier.ClassifyAsync(line, normalized, invoice, ct);
                    if (aiSuggestion is not null)
                    {
                        suggested = aiSuggestion.AccountCode;
                        confidence = aiSuggestion.Confidence;
                        source = "AI";
                    }
                    else
                    {
                        // Garantili doldurma: hiçbir eşleşme yoksa varsayılan gider hesabı, düşük güvenle.
                        suggested = settings.DefaultExpenseAccount;
                        confidence = FallbackConfidence;
                        source = "Varsayılan gider";
                    }
                }
            }

            line.SuggestedAccountCode = suggested;
            line.Confidence = confidence;
            line.CariCode = cariCode;

            // Faz 8 (B2): tevkifat tespiti — kalem metni kural anahtar kelimeleriyle eşleşirse
            // kod/oran önerilir ve tevkif edilen KDV hesaplanır. Eşleşme yoksa alanlar temizlenir
            // (yeniden kodlamada tutarlılık); tevkifatsız faturada davranış birebir eskisi gibidir.
            var withholdingRule = withholdingRules.FirstOrDefault(r => WithholdingMatches(r, normalized));
            if (withholdingRule is not null && TryParseFraction(withholdingRule.Fraction, out var num, out var den))
            {
                line.WithholdingCode = withholdingRule.Code;
                line.WithholdingRate = withholdingRule.Fraction;
                line.WithholdingVatAmount = Math.Round(line.VatAmount * num / den, 2);
            }
            else
            {
                line.WithholdingCode = null;
                line.WithholdingRate = null;
                line.WithholdingVatAmount = null;
            }

            // Faz 8 (B3): KKEG analizi — satıcı + kalem metni üzerinden. İşaret yoksa alanlar
            // temizlenir; KKEG'siz faturada akış değişmez (güven/durum mantığına dokunulmaz).
            var kkeg = invoice.Direction == InvoiceDirection.Incoming
                ? _kkegAnalyzer.Analyze($"{invoice.SenderTitle} {line.ItemName}", line.LineAmount, settings.HasCompanyVehicle)
                : null;
            if (kkeg is not null)
            {
                line.IsKkeg = true;
                line.KkegReason = kkeg.Reason;
                line.KkegAmount = kkeg.Amount;
            }
            else
            {
                line.IsKkeg = false;
                line.KkegReason = null;
                line.KkegAmount = null;
            }

            lineInfos.Add(new LineCodingInfo { LineId = line.Id, Source = source });
        }

        // Katman 4: tüm satırlar eşik üstündeyse otomatik uygulanır; değilse onay kuyruğuna düşer.
        invoice.CodingStatus = invoice.Lines.All(l => (l.Confidence ?? 0) >= settings.AutoApplyThreshold)
            ? CodingStatus.AutoCoded
            : CodingStatus.NeedsReview;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Fatura kodlandı. EInvoiceId: {Id}, Durum: {Status}, Satır: {Count}",
            invoice.Id, invoice.CodingStatus, invoice.Lines.Count);

        return new CodingOutcome
        {
            Success = true,
            CodingStatus = invoice.CodingStatus,
            Lines = lineInfos
        };
    }

    /// <summary>
    /// Onay + öğrenme (Katman 4 → Katman 1 geri besleme): SMMM düzeltmeleri uygulanır,
    /// her onaylı satır AccountMemory'ye upsert edilir (ApproveCount++), satıcının en çok
    /// onaylanan hesabı VendorDefault'a yazılır ve fatura Approved olur.
    /// corrections: satır Id → düzeltmeler (hesap/cari + Faz 8 tevkifat/KKEG); boş olabilir (öneri aynen onaylanır).
    /// </summary>
    public async Task<ApproveOutcome> ApproveCodingAsync(
        EInvoice invoice,
        IReadOnlyDictionary<Guid, ApproveLineCorrection> corrections,
        CancellationToken ct)
    {
        if (invoice.Lines.Count == 0)
            return new ApproveOutcome { Success = false, ErrorMessage = "Faturada onaylanacak kalem satırı yok." };

        // Bilinmeyen satır Id'si ile düzeltme gönderilmesin (çift doğrulama).
        var lineIds = invoice.Lines.Select(l => l.Id).ToHashSet();
        if (corrections.Keys.Any(id => !lineIds.Contains(id)))
            return new ApproveOutcome { Success = false, ErrorMessage = "Düzeltme satırı bu faturaya ait değil." };

        var memories = await _db.AccountMemories
            .Where(m => m.ClientId == invoice.ClientId && m.SenderTaxId == invoice.SenderTaxId)
            .ToListAsync(ct);

        string? approvedCariCode = null;
        var now = DateTime.UtcNow;

        foreach (var line in invoice.Lines.OrderBy(l => l.LineNumber))
        {
            corrections.TryGetValue(line.Id, out var correction);

            var finalCode = FirstNonEmpty(correction?.AccountCode, line.AccountCode, line.SuggestedAccountCode);
            if (finalCode is null)
            {
                return new ApproveOutcome
                {
                    Success = false,
                    ErrorMessage = $"{line.LineNumber}. satır için hesap kodu boş olamaz. Önce faturayı kodlayın veya kodu girin."
                };
            }

            line.AccountCode = finalCode;
            line.Confidence = 1.0; // SMMM onayı = tam güven.

            var cari = FirstNonEmpty(correction?.CariCode, line.CariCode);
            if (cari is not null)
            {
                line.CariCode = cari;
                approvedCariCode ??= cari;
            }

            // Faz 8 (B2): tevkifat düzeltmesi. null = dokunma; "" = tevkifatı temizle;
            // dolu kod = kodu uygula (oran verilmişse tevkif KDV yeniden hesaplanır, tutar
            // açıkça gönderildiyse o esas alınır).
            if (correction?.WithholdingCode is not null)
            {
                var code = correction.WithholdingCode.Trim();
                if (code.Length == 0)
                {
                    line.WithholdingCode = null;
                    line.WithholdingRate = null;
                    line.WithholdingVatAmount = null;
                }
                else
                {
                    line.WithholdingCode = code;
                    var rate = FirstNonEmpty(correction.WithholdingRate, line.WithholdingRate);
                    line.WithholdingRate = rate;

                    if (correction.WithholdingVatAmount.HasValue)
                        line.WithholdingVatAmount = Math.Round(correction.WithholdingVatAmount.Value, 2);
                    else if (rate is not null && TryParseFraction(rate, out var num, out var den))
                        line.WithholdingVatAmount = Math.Round(line.VatAmount * num / den, 2);
                }
            }

            // Faz 8 (B3): KKEG düzeltmesi. null = dokunma; false = işareti temizle;
            // true = işaretle (tutar/gerekçe gönderildiyse onlar esas alınır).
            if (correction?.IsKkeg is not null)
            {
                if (correction.IsKkeg == false)
                {
                    line.IsKkeg = false;
                    line.KkegReason = null;
                    line.KkegAmount = null;
                }
                else
                {
                    line.IsKkeg = true;
                    if (correction.KkegAmount.HasValue)
                        line.KkegAmount = Math.Round(Math.Min(correction.KkegAmount.Value, line.LineAmount), 2);
                    line.KkegAmount ??= Math.Round(line.LineAmount, 2); // tutar hiç yoksa tam KKEG varsayılır
                    var reason = FirstNonEmpty(correction.KkegReason, line.KkegReason);
                    line.KkegReason = reason ?? "SMMM tarafından KKEG işaretlendi.";
                }
            }

            // ÖĞRENME: (satıcı, imza, KDV) → onaylı kod hafızaya yazılır.
            var signature = TextNormalizer.BuildItemSignature(line.ItemName);
            if (signature.Length == 0) continue;

            var memory = memories.FirstOrDefault(m => m.ItemSignature == signature && m.VatRate == line.VatRate);
            if (memory is null)
            {
                memory = new AccountMemory
                {
                    TenantId = invoice.TenantId,
                    ClientId = invoice.ClientId,
                    SenderTaxId = invoice.SenderTaxId,
                    ItemSignature = signature,
                    VatRate = line.VatRate,
                    AccountCode = finalCode,
                    ApproveCount = 1,
                    LastUsedAt = now
                };
                _db.AccountMemories.Add(memory);
                memories.Add(memory);
            }
            else if (memory.AccountCode == finalCode)
            {
                memory.ApproveCount++;
                memory.LastUsedAt = now;
            }
            else
            {
                // SMMM farklı bir kodu onayladı → hafıza yeni koda döner, sayaç sıfırdan başlar.
                memory.AccountCode = finalCode;
                memory.ApproveCount = 1;
                memory.LastUsedAt = now;
            }
        }

        // Satıcı varsayılanı: bu satıcı için en çok onaylanan hesap (K1b'nin kaynağı).
        var top = memories
            .GroupBy(m => m.AccountCode)
            .Select(g => new { AccountCode = g.Key, Count = g.Sum(m => m.ApproveCount) })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        if (top is not null)
        {
            var vendorDefault = await _db.VendorDefaults
                .FirstOrDefaultAsync(v => v.ClientId == invoice.ClientId && v.SenderTaxId == invoice.SenderTaxId, ct);

            // Fiş listesi içe aktarımından gelen VKN'siz (unvan anahtarlı) kayıt varsa yenisi
            // açılmaz; mevcut kayda VKN geri doldurulur (mükerrer satıcı varsayılanı oluşmaz).
            if (vendorDefault is null)
            {
                var approveNameKey = TextNormalizer.NormalizeCompanyName(invoice.SenderTitle);
                if (approveNameKey.Length > 0)
                {
                    vendorDefault = await _db.VendorDefaults
                        .FirstOrDefaultAsync(v => v.ClientId == invoice.ClientId
                                                  && v.SenderTaxId == string.Empty
                                                  && v.VendorNameNormalized == approveNameKey, ct);
                    if (vendorDefault is not null && !string.IsNullOrWhiteSpace(invoice.SenderTaxId))
                        vendorDefault.SenderTaxId = invoice.SenderTaxId;
                }
            }

            if (vendorDefault is null)
            {
                _db.VendorDefaults.Add(new VendorDefault
                {
                    TenantId = invoice.TenantId,
                    ClientId = invoice.ClientId,
                    SenderTaxId = invoice.SenderTaxId,
                    VendorNameNormalized = TextNormalizer.NormalizeCompanyName(invoice.SenderTitle),
                    AccountCode = top.AccountCode,
                    ApproveCount = top.Count
                });
            }
            else
            {
                vendorDefault.AccountCode = top.AccountCode;
                vendorDefault.ApproveCount = top.Count;
                // Eski kayıtlarda unvan anahtarı boş olabilir — onayda geri doldurulur.
                if (vendorDefault.VendorNameNormalized.Length == 0)
                    vendorDefault.VendorNameNormalized = TextNormalizer.NormalizeCompanyName(invoice.SenderTitle);
            }
        }

        // Cari düzeltmesi haritaya da işlenir (bir sonraki faturada aynı kod kullanılır).
        if (approvedCariCode is not null)
        {
            var map = await _db.VendorCariMaps
                .FirstOrDefaultAsync(m => m.ClientId == invoice.ClientId && m.SenderTaxId == invoice.SenderTaxId, ct);

            if (map is null)
            {
                _db.VendorCariMaps.Add(new VendorCariMap
                {
                    TenantId = invoice.TenantId,
                    ClientId = invoice.ClientId,
                    SenderTaxId = invoice.SenderTaxId,
                    SenderTitle = invoice.SenderTitle,
                    VendorNameNormalized = TextNormalizer.NormalizeCompanyName(invoice.SenderTitle),
                    CariCode = approvedCariCode
                });
            }
            else if (map.CariCode != approvedCariCode)
            {
                map.CariCode = approvedCariCode;
            }
        }

        // Onayda kullanılan hesap + cari kodları mükellef hesap planına (ChartAccount) da
        // işlenir — autocomplete (api/coding/accounts) veri kaynağı kendiliğinden dolar.
        var chartCodes = invoice.Lines
            .Select(l => l.AccountCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => (Code: c!.Trim(), Name: (string?)null, Type: AccountType.Diger))
            .ToList();
        if (approvedCariCode is not null)
            chartCodes.Add((approvedCariCode, invoice.SenderTitle, AccountType.Cari));
        await EnsureChartAccountsAsync(invoice.TenantId, invoice.ClientId, chartCodes, ct);

        invoice.CodingStatus = CodingStatus.Approved;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Fatura kodlaması onaylandı ve öğrenildi. EInvoiceId: {Id}, Satır: {Count}",
            invoice.Id, invoice.Lines.Count);

        return new ApproveOutcome { Success = true };
    }

    /// <summary>
    /// Kullanılan kodları mükellef hesap planına (ChartAccount) upsert eder: yoksa ekler,
    /// varsa dokunmaz. SaveChanges ÇAĞIRMAZ — çağıran akış kendi kayıt anında yazar.
    /// Aynı change-tracker turunda eklenen (henüz kaydedilmemiş) kayıtlar da sayılır;
    /// (TenantId, ClientId, Code) unique index'i mükerrer eklemeye karşı korunur.
    /// </summary>
    private async Task EnsureChartAccountsAsync(
        Guid tenantId,
        Guid clientId,
        IReadOnlyCollection<(string Code, string? Name, AccountType Type)> codes,
        CancellationToken ct)
    {
        if (codes.Count == 0) return;

        // Aynı kod birden fazla satırda geçebilir; adı dolu olan varyant tercih edilir.
        var distinct = codes
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Name)).First())
            .ToList();

        var codeList = distinct.Select(c => c.Code).ToList();
        var existing = await _db.ChartAccounts
            .Where(a => a.ClientId == clientId && codeList.Contains(a.Code))
            .Select(a => a.Code)
            .ToListAsync(ct);

        var known = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var local in _db.ChartAccounts.Local)
        {
            if (local.ClientId == clientId) known.Add(local.Code);
        }

        foreach (var (code, name, type) in distinct)
        {
            if (known.Contains(code)) continue;

            var trimmedName = name?.Trim() ?? string.Empty;
            _db.ChartAccounts.Add(new ChartAccount
            {
                TenantId = tenantId,
                ClientId = clientId,
                Code = code,
                Name = trimmedName.Length <= 200 ? trimmedName : trimmedName[..200],
                Type = type
            });
            known.Add(code);
        }
    }

    /// <summary>
    /// Cari kod çözümü: önce VendorCariMap'te VKN eşleşmesi, yoksa NORMALİZE UNVAN eşleşmesi
    /// (mizan içe aktarımı VKN'siz, unvan anahtarlı kayıt yazar — bulunursa VKN geri doldurulur);
    /// hiçbiri yoksa CariPrefix + '.' + 3 haneli sıra numarası üretir, sayacı artırır ve haritaya yazar.
    /// </summary>
    private async Task<string> ResolveCariCodeAsync(EInvoice invoice, ClientCodingSettings settings, CancellationToken ct)
    {
        var map = await _db.VendorCariMaps
            .FirstOrDefaultAsync(m => m.ClientId == invoice.ClientId && m.SenderTaxId == invoice.SenderTaxId, ct);

        if (map is not null) return map.CariCode;

        var nameKey = TextNormalizer.NormalizeCompanyName(invoice.SenderTitle);
        if (nameKey.Length > 0)
        {
            map = await _db.VendorCariMaps
                .FirstOrDefaultAsync(m => m.ClientId == invoice.ClientId && m.VendorNameNormalized == nameKey, ct);

            if (map is not null)
            {
                // İçe aktarılan kayıtta VKN yoktur; ilk faturada geri doldurulur → sonraki
                // aramalar hızlı VKN yolundan eşleşir (unique index VKN + unvan birleşimidir).
                if (map.SenderTaxId.Length == 0 && !string.IsNullOrWhiteSpace(invoice.SenderTaxId))
                {
                    map.SenderTaxId = invoice.SenderTaxId;
                    await _db.SaveChangesAsync(ct);
                }
                return map.CariCode;
            }
        }

        var code = $"{settings.CariPrefix}.{settings.CariNextNumber:D3}";
        settings.CariNextNumber++;

        _db.VendorCariMaps.Add(new VendorCariMap
        {
            TenantId = invoice.TenantId,
            ClientId = invoice.ClientId,
            SenderTaxId = invoice.SenderTaxId,
            SenderTitle = invoice.SenderTitle,
            VendorNameNormalized = nameKey,
            CariCode = code
        });

        // Sayaç + harita hemen kaydedilir; aynı toplu kodlamada ikinci fatura aynı satıcıyı haritadan bulur.
        await _db.SaveChangesAsync(ct);
        return code;
    }

    // ---------- Faz 3: Fiş (receipt) overload'ları — mevcut fatura akışına DOKUNMAZ ----------

    /// <summary>
    /// Fiş için hesap kodu önerisi (Faz 3). Fatura kademesiyle AYNI katman mantığını kullanır:
    /// K1a hafıza (satıcı imzası + KDV oranı) → K1b satıcı varsayılanı → K2 kural → fallback.
    /// Fişte kalem satırı olmadığından hafıza imzası SATICI ADINDAN üretilir (öneri ve öğrenme
    /// aynı imzayı kullanır → tutarlı eşleşme); kural eşleşmesi ise satıcı adı + OCR kalem
    /// özetinin (ruleText) normalize metni üzerinde çalışır.
    /// </summary>
    public async Task<ReceiptSuggestion> SuggestForReceiptAsync(
        Guid clientId,
        string? vendorTaxId,
        string? vendorName,
        string ruleText,
        decimal? vatRate,
        CancellationToken ct)
    {
        var settings = await GetOrCreateSettingsAsync(clientId, ct);
        await _parameters.EnsureLoadedAsync(ct); // Faz 7 (E4): katman güven değerleri parametreden
        var signature = TextNormalizer.BuildItemSignature(vendorName);
        var normalized = TextNormalizer.Normalize(ruleText);

        string? cariCode = null;

        if (!string.IsNullOrWhiteSpace(vendorTaxId))
        {
            cariCode = await _db.VendorCariMaps
                .AsNoTracking()
                .Where(m => m.ClientId == clientId && m.SenderTaxId == vendorTaxId)
                .Select(m => m.CariCode)
                .FirstOrDefaultAsync(ct);

            var memories = await _db.AccountMemories
                .AsNoTracking()
                .Where(m => m.ClientId == clientId && m.SenderTaxId == vendorTaxId)
                .ToListAsync(ct);

            // K1a — hafıza: önce (imza + KDV oranı) tam eşleşme; oran okunamadıysa imza yeterli.
            var memory = signature.Length == 0
                ? null
                : memories.FirstOrDefault(m => m.ItemSignature == signature && vatRate.HasValue && m.VatRate == vatRate.Value)
                  ?? memories.FirstOrDefault(m => m.ItemSignature == signature);

            if (memory is not null)
            {
                return new ReceiptSuggestion
                {
                    AccountCode = memory.AccountCode,
                    Confidence = Math.Min(0.99, MemoryBaseConfidence + 0.005 * Math.Min(memory.ApproveCount, 8)),
                    Source = "Hafıza",
                    CariCode = cariCode
                };
            }

            // K1b — satıcı seviyesi varsayılan.
            var vendorDefault = await _db.VendorDefaults
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.ClientId == clientId && v.SenderTaxId == vendorTaxId, ct);

            if (vendorDefault is not null)
            {
                return new ReceiptSuggestion
                {
                    AccountCode = vendorDefault.AccountCode,
                    Confidence = VendorDefaultConfidence,
                    Source = "Satıcı varsayılanı",
                    CariCode = cariCode
                };
            }
        }

        // İkincil eşleşme (fatura akışıyla aynı kural): VKN eşleşmesi yoksa NORMALİZE UNVAN
        // denenir — mizan/fiş listesi içe aktarımı VKN'siz, unvan anahtarlı kayıt yazar.
        var vendorNameKey = TextNormalizer.NormalizeCompanyName(vendorName);
        if (vendorNameKey.Length > 0)
        {
            cariCode ??= await _db.VendorCariMaps
                .AsNoTracking()
                .Where(m => m.ClientId == clientId && m.VendorNameNormalized == vendorNameKey)
                .Select(m => m.CariCode)
                .FirstOrDefaultAsync(ct);

            var vendorDefaultByName = await _db.VendorDefaults
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.ClientId == clientId && v.VendorNameNormalized == vendorNameKey, ct);

            if (vendorDefaultByName is not null)
            {
                return new ReceiptSuggestion
                {
                    AccountCode = vendorDefaultByName.AccountCode,
                    Confidence = VendorNameMatchConfidence,
                    Source = "Satıcı unvan eşleşmesi",
                    CariCode = cariCode
                };
            }
        }

        // K2 — kural motoru (fatura akışıyla aynı RuleMatches).
        var rules = await _db.CodingRules
            .AsNoTracking()
            .Where(r => r.IsActive && (r.ClientId == null || r.ClientId == clientId))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);

        var rule = rules.FirstOrDefault(r => RuleMatches(r, normalized));
        if (rule is not null)
        {
            return new ReceiptSuggestion
            {
                AccountCode = rule.AccountCode,
                Confidence = RuleConfidence,
                Source = "Kural",
                CariCode = cariCode
            };
        }

        // Garantili doldurma: hiçbir katman eşleşmezse varsayılan gider hesabı, düşük güvenle.
        return new ReceiptSuggestion
        {
            AccountCode = settings.DefaultExpenseAccount,
            Confidence = FallbackConfidence,
            Source = "Varsayılan gider",
            CariCode = cariCode
        };
    }

    /// <summary>
    /// Fiş onayı öğrenmesi (Faz 3): fatura onayıyla AYNI upsert yolu —
    /// AccountMemory (satıcı imzası + KDV oranı → onaylı kod, ApproveCount++),
    /// VendorDefault (satıcının en çok onaylanan hesabı) ve cari düzeltmesi VendorCariMap'e yazılır.
    /// SaveChanges ÇAĞIRMAZ; çağıran servis fiş durumuyla birlikte tek seferde kaydeder.
    /// </summary>
    public async Task LearnFromReceiptApprovalAsync(Receipt receipt, CancellationToken ct)
    {
        // Hesap planı öğrenmesi (autocomplete kaynağı): satıcı VKN'siz fişte de çalışır;
        // ekleme SaveChanges çağırmaz (fatura akışının aksine burada kayıt çağırana aittir).
        var chartCodes = new List<(string Code, string? Name, AccountType Type)>();
        if (!string.IsNullOrWhiteSpace(receipt.AccountCode))
            chartCodes.Add((receipt.AccountCode!.Trim(), null, AccountType.Diger));
        if (!string.IsNullOrWhiteSpace(receipt.CariCode))
            chartCodes.Add((receipt.CariCode!.Trim(), receipt.VendorName, AccountType.Cari));
        await EnsureChartAccountsAsync(receipt.TenantId, receipt.ClientId, chartCodes, ct);

        if (string.IsNullOrWhiteSpace(receipt.VendorTaxId) || string.IsNullOrWhiteSpace(receipt.AccountCode))
            return;

        var signature = TextNormalizer.BuildItemSignature(receipt.VendorName);
        if (signature.Length == 0) return;

        var vendorTaxId = receipt.VendorTaxId!;
        var finalCode = receipt.AccountCode!;
        var vatRate = receipt.VatRate ?? 0m;
        var now = DateTime.UtcNow;

        var memories = await _db.AccountMemories
            .Where(m => m.ClientId == receipt.ClientId && m.SenderTaxId == vendorTaxId)
            .ToListAsync(ct);

        var memory = memories.FirstOrDefault(m => m.ItemSignature == signature && m.VatRate == vatRate);
        if (memory is null)
        {
            memory = new AccountMemory
            {
                TenantId = receipt.TenantId,
                ClientId = receipt.ClientId,
                SenderTaxId = vendorTaxId,
                ItemSignature = signature,
                VatRate = vatRate,
                AccountCode = finalCode,
                ApproveCount = 1,
                LastUsedAt = now
            };
            _db.AccountMemories.Add(memory);
            memories.Add(memory);
        }
        else if (memory.AccountCode == finalCode)
        {
            memory.ApproveCount++;
            memory.LastUsedAt = now;
        }
        else
        {
            // SMMM farklı bir kodu onayladı → hafıza yeni koda döner, sayaç sıfırdan başlar.
            memory.AccountCode = finalCode;
            memory.ApproveCount = 1;
            memory.LastUsedAt = now;
        }

        // Satıcı varsayılanı: bu satıcı için en çok onaylanan hesap (fatura onayıyla aynı kural).
        var top = memories
            .GroupBy(m => m.AccountCode)
            .Select(g => new { AccountCode = g.Key, Count = g.Sum(m => m.ApproveCount) })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        if (top is not null)
        {
            var vendorDefault = await _db.VendorDefaults
                .FirstOrDefaultAsync(v => v.ClientId == receipt.ClientId && v.SenderTaxId == vendorTaxId, ct);

            if (vendorDefault is null)
            {
                _db.VendorDefaults.Add(new VendorDefault
                {
                    TenantId = receipt.TenantId,
                    ClientId = receipt.ClientId,
                    SenderTaxId = vendorTaxId,
                    VendorNameNormalized = TextNormalizer.NormalizeCompanyName(receipt.VendorName),
                    AccountCode = top.AccountCode,
                    ApproveCount = top.Count
                });
            }
            else
            {
                vendorDefault.AccountCode = top.AccountCode;
                vendorDefault.ApproveCount = top.Count;
            }
        }

        // Cari düzeltmesi haritaya işlenir (sonraki fiş/faturada aynı kod kullanılır).
        if (!string.IsNullOrWhiteSpace(receipt.CariCode))
        {
            var map = await _db.VendorCariMaps
                .FirstOrDefaultAsync(m => m.ClientId == receipt.ClientId && m.SenderTaxId == vendorTaxId, ct);

            if (map is null)
            {
                _db.VendorCariMaps.Add(new VendorCariMap
                {
                    TenantId = receipt.TenantId,
                    ClientId = receipt.ClientId,
                    SenderTaxId = vendorTaxId,
                    SenderTitle = receipt.VendorName ?? string.Empty,
                    VendorNameNormalized = TextNormalizer.NormalizeCompanyName(receipt.VendorName),
                    CariCode = receipt.CariCode!
                });
            }
            else if (map.CariCode != receipt.CariCode)
            {
                map.CariCode = receipt.CariCode!;
            }
        }
    }

    /// <summary>
    /// Kural eşleşmesi: Pattern ";" içeriyorsa anahtar kelime listesi (herhangi biri normalize
    /// metinde geçerse eşleşir); içermiyorsa regex denenir, regex geçersizse düz içerme kontrolüne düşülür.
    /// </summary>
    public static bool RuleMatches(CodingRule rule, string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern) || normalizedText.Length == 0) return false;

        var pattern = rule.Pattern.Trim();

        if (pattern.Contains(';'))
        {
            return pattern
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => TextNormalizer.Normalize(k))
                .Any(k => k.Length > 0 && normalizedText.Contains(k, StringComparison.Ordinal));
        }

        try
        {
            return Regex.IsMatch(normalizedText, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            // Geçersiz regex (API doğrulamasını atlayan eski kayıt vb.) → düz içerme.
            return normalizedText.Contains(TextNormalizer.Normalize(pattern), StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Faz 8 (B2): tevkifat kuralı eşleşmesi — ";" ayraçlı anahtar kelimeler, normalize metinde aranır
    /// (CodingRule anahtar kelime moduyla aynı desen).
    /// </summary>
    public static bool WithholdingMatches(WithholdingRule rule, string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(rule.KeywordPattern) || normalizedText.Length == 0) return false;

        return rule.KeywordPattern
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => TextNormalizer.Normalize(k))
            .Any(k => k.Length > 0 && normalizedText.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>"9/10" biçimindeki tevkifat oranını çözer; geçersizse false.</summary>
    public static bool TryParseFraction(string? fraction, out decimal numerator, out decimal denominator)
    {
        numerator = 0m;
        denominator = 1m;
        if (string.IsNullOrWhiteSpace(fraction)) return false;

        var parts = fraction.Trim().Split('/');
        if (parts.Length != 2 ||
            !decimal.TryParse(parts[0].Trim(), out numerator) ||
            !decimal.TryParse(parts[1].Trim(), out denominator) ||
            numerator <= 0 || denominator <= 0 || numerator > denominator)
        {
            return false;
        }

        return true;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
