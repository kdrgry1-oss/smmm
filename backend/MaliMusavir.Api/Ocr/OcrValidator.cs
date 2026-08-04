using System.Globalization;

namespace MaliMusavir.Api.Ocr;

/// <summary>
/// Vision-LLM'in fişten çıkardığı HAM alanlar (LlmVisionOcrProvider'ın JSON şemasıyla birebir).
/// Model emin olmadığı alanı null bırakır ve o alanın güvenini düşürür — UYDURMAZ (prompt kuralı).
/// Çok oranlı KDV: VatBreakdown dizisi fişin KDV kırılım tablosunu satır satır taşır;
/// eski (tek oranlı) yanıtlarda dizi boş/null gelir — geriye uyumluluk OcrValidator'da sağlanır.
/// Z raporu okumasında (OcrPrompt.ZReport) OkcSerialNumber/ZNumber/ReportDate/TotalSales/
/// TotalVat/CashTotal/CardTotal alanları dolar; fiş alanları null kalır.
/// </summary>
public class LlmReceiptExtraction
{
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }
    public string? DocumentNumber { get; set; }
    /// <summary>ISO 8601 tarih ("2026-07-28"). Okunamadıysa null.</summary>
    public string? ReceiptDate { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal? VatRate { get; set; }
    public decimal? VatAmount { get; set; }
    public decimal? NetAmount { get; set; }
    /// <summary>Fişteki toplam iskonto/indirim (pozitif); yoksa null.</summary>
    public decimal? DiscountTotal { get; set; }
    /// <summary>Oran bazlı KDV kırılımı (rate/net/vat). Eski yanıtlarda null/boş olabilir.</summary>
    public List<LlmVatBreakdownRow>? VatBreakdown { get; set; }
    public string? Currency { get; set; }
    /// <summary>
    /// Faz 13: fişte basılı araç plakası (akaryakıt fişlerinde bulunur, ör. "34FPC630").
    /// Model görmüyorsa null bırakır — UYDURMA YOK (prompt kuralı).
    /// </summary>
    public string? PlateNumber { get; set; }
    public List<LlmReceiptItem>? Items { get; set; }
    /// <summary>"perakende_fis" | "fatura" | "yazar_kasa" | "diger".</summary>
    public string? DocumentType { get; set; }

    // ---------- Z raporu alanları (OcrPrompt.ZReport şeması) ----------
    /// <summary>Yazar kasa (ÖKC) sicil/seri numarası.</summary>
    public string? OkcSerialNumber { get; set; }
    /// <summary>Z raporu sayaç numarası.</summary>
    public string? ZNumber { get; set; }
    /// <summary>Z raporu tarihi (ISO 8601).</summary>
    public string? ReportDate { get; set; }
    /// <summary>Günlük KDV DAHİL toplam satış.</summary>
    public decimal? TotalSales { get; set; }
    /// <summary>Günlük toplam KDV.</summary>
    public decimal? TotalVat { get; set; }
    /// <summary>Nakit tahsilat toplamı (KDV dahil).</summary>
    public decimal? CashTotal { get; set; }
    /// <summary>Kart tahsilat toplamı (KDV dahil).</summary>
    public decimal? CardTotal { get; set; }

    /// <summary>Alan bazlı güvenler 0-1 (ör. {"vendorName":0.9, "totalAmount":0.95, ...}).</summary>
    public Dictionary<string, double>? Confidence { get; set; }
    /// <summary>Modelin kendi uyarıları (Türkçe).</summary>
    public List<string>? Warnings { get; set; }
}

/// <summary>Ham KDV kırılım satırı (model yanıtı: {"rate":20,"net":100.0,"vat":20.0}).</summary>
public class LlmVatBreakdownRow
{
    public decimal? Rate { get; set; }
    public decimal? Net { get; set; }
    public decimal? Vat { get; set; }
}

public class LlmReceiptItem
{
    public string? Name { get; set; }
    public decimal? Amount { get; set; }
}

/// <summary>
/// OCR doğrulama katmanı (Görev 2.2): vision-LLM çıktısını çapraz kontrollerden geçirip
/// sağlayıcı-bağımsız OcrReceiptResult'a normalize eder.
/// Kurallar:
///  - Matrah + KDV = Toplam tutarlılığı (±0,05 TL tolerans; tutmuyorsa uyarı + güven düşer).
///  - Belge tarihi gelecekte olamaz (gelecek tarih null'a çekilir + uyarı).
///  - VKN 10 / TCKN 11 hane kontrolü (uymayan değer null'a çekilir + uyarı).
///  - KDV oranı-tutarı çapraz kontrolü: vatAmount ≈ netAmount × oran/100 (±0,05 TL).
///  - KDV oranı yalnız 0/1/10/20 olabilir (Türkiye); farklıysa uyarı + güven düşer.
///  - Eksik matrah/KDV, toplam + oran biliniyorsa geriye doğru hesaplanır.
///  - ÇOK ORANLI KDV (yeni): model kırılım verdiyse doğrulama ORAN BAZLI yapılır —
///    her satırda |net×rate/100 − vat| ≤ 0,05 ve Σnet+Σvat ≈ toplam (±0,05); sapmada
///    Türkçe uyarı + güven düşer. Kırılım verilmediyse tek oranlı eski davranış birebir
///    korunur ve netAmount/vatAmount/vatRate'ten TEK satırlık kırılım türetilir.
/// Genel güven = alan bazlı güvenlerin MİNİMUMU; her uyarı skoru ayrıca %10 düşürür (taban 0.05).
/// </summary>
public class OcrValidator
{
    /// <summary>Tutar karşılaştırmalarında kuruş toleransı (TL).</summary>
    public const decimal AmountTolerance = 0.05m;

    /// <summary>Türkiye'de geçerli KDV oranları (%).</summary>
    private static readonly decimal[] ValidVatRates = { 0m, 1m, 10m, 20m };

    /// <summary>Her doğrulama uyarısının genel güvene çarpan etkisi.</summary>
    private const double WarningPenalty = 0.9;

    private const double MinimumConfidence = 0.05;

    public OcrReceiptResult Normalize(LlmReceiptExtraction raw, OcrDocumentKind kind = OcrDocumentKind.Fis)
        => kind == OcrDocumentKind.ZRaporu ? NormalizeZReport(raw) : NormalizeReceipt(raw);

    private OcrReceiptResult NormalizeReceipt(LlmReceiptExtraction raw)
    {
        var warnings = new List<string>(raw.Warnings ?? new List<string>());
        var penalties = 0;

        // ---------- Tarih: gelecekte olamaz ----------
        var receiptDate = ParseDate(raw.ReceiptDate, warnings, ref penalties);

        // ---------- VKN/TCKN: 10 veya 11 hane ----------
        var taxId = raw.VendorTaxId?.Trim();
        if (!string.IsNullOrEmpty(taxId))
        {
            var digits = new string(taxId.Where(char.IsDigit).ToArray());
            if (digits.Length is 10 or 11)
            {
                taxId = digits;
            }
            else
            {
                warnings.Add($"VKN/TCKN hane sayısı geçersiz (\"{taxId}\") — alan boş bırakıldı.");
                taxId = null;
                penalties++;
            }
        }

        // ---------- Faz 13: Plaka — normalize + biçim kontrolü (SESSİZ düşürme) ----------
        // Boşluk/tire silinir, TR karakter (İ/ı → I) dahil büyük harfe çevrilir; normalize değer
        // Türk plaka desenine uymuyorsa alan null yapılır. Uyarı EKLENMEZ ve güven DÜŞÜRÜLMEZ:
        // plaka çekirdek muhasebe alanı değildir, modelin yanlış okuması fiş akışını cezalandırmamalıdır.
        string? plateNumber = null;
        if (!string.IsNullOrWhiteSpace(raw.PlateNumber))
        {
            var normalizedPlate = Domain.PlateNormalizer.Normalize(raw.PlateNumber);
            if (Domain.PlateNormalizer.IsValid(normalizedPlate))
                plateNumber = normalizedPlate;
        }

        // ---------- Tutar alanları + geriye hesaplama ----------
        var total = raw.TotalAmount;
        var net = raw.NetAmount;
        var vatAmount = raw.VatAmount;
        var vatRate = raw.VatRate;

        // ---------- Çok oranlı KDV kırılımı (yeni) ----------
        // Model kırılım verdiyse (dizi dolu) tutar alanları KIRILIM TOPLAMLARINDAN türetilir ve
        // doğrulama oran bazlı yapılır. Kırılım yoksa eski tek oranlı akış birebir çalışır.
        var breakdown = SanitizeBreakdown(raw.VatBreakdown);
        var hasBreakdown = breakdown.Count > 0;

        if (hasBreakdown)
        {
            var sumNet = Math.Round(breakdown.Sum(b => b.Net), 2);
            var sumVat = Math.Round(breakdown.Sum(b => b.Vat), 2);

            // Oran bazlı çapraz kontroller.
            ValidateBreakdown(breakdown, warnings, ref penalties);

            // Kırılım tek gerçek kaynak: toplam alanlar kırılımla hizalanır. Modelin ayrıca
            // verdiği netAmount/vatAmount kırılımdan sapıyorsa uyarı düşülür (ceza bir kez).
            if (net.HasValue && Math.Abs(net.Value - sumNet) > AmountTolerance)
                warnings.Add($"Matrah alanı ({Tl(net.Value)}) KDV kırılımı toplamından ({Tl(sumNet)}) farklı — kırılım esas alındı.");
            if (vatAmount.HasValue && Math.Abs(vatAmount.Value - sumVat) > AmountTolerance)
                warnings.Add($"KDV alanı ({Tl(vatAmount.Value)}) KDV kırılımı toplamından ({Tl(sumVat)}) farklı — kırılım esas alındı.");

            net = sumNet;
            vatAmount = sumVat;
            total ??= Math.Round(sumNet + sumVat, 2);

            // Geriye uyumlu tekil oran: en yüksek (net+kdv) tutarlı kırılımın oranı.
            vatRate = breakdown.OrderByDescending(b => b.Net + b.Vat).First().Rate;
        }
        else
        {
            // ---------- Tek oranlı eski davranış (birebir korunur) ----------
            if (vatRate.HasValue && !ValidVatRates.Contains(vatRate.Value))
            {
                warnings.Add($"KDV oranı %{vatRate.Value.ToString("0.##", CultureInfo.InvariantCulture)} Türkiye'de geçerli oranlardan (0/1/10/20) değil.");
                penalties++;
            }

            // Eksik matrah/KDV tamamlanır: toplam + oran biliniyorsa geriye doğru hesap.
            if (total.HasValue && vatRate.HasValue && (!net.HasValue || !vatAmount.HasValue))
            {
                var computedNet = Math.Round(total.Value / (1 + vatRate.Value / 100m), 2);
                net ??= computedNet;
                vatAmount ??= Math.Round(total.Value - computedNet, 2);
            }
            else if (net.HasValue && vatAmount.HasValue && !total.HasValue)
            {
                total = Math.Round(net.Value + vatAmount.Value, 2);
            }

            // ---------- KDV oranı-tutarı çapraz kontrolü: vatAmount ≈ net × oran/100 ----------
            if (net.HasValue && vatRate is > 0 && vatAmount.HasValue)
            {
                var expectedVat = Math.Round(net.Value * vatRate.Value / 100m, 2);
                if (Math.Abs(expectedVat - vatAmount.Value) > AmountTolerance)
                {
                    warnings.Add(
                        $"KDV çapraz kontrolü tutmadı: matrah × %{vatRate.Value:0.##} = {Tl(expectedVat)} beklenirken {Tl(vatAmount.Value)} okundu.");
                    penalties++;
                }
            }

            // Geriye uyumluluk: tek oranlı okumadan TEK satırlık kırılım türetilir
            // (export ve onay ekranı hep kırılımdan okuyabilsin diye).
            if (net is > 0 || vatAmount is > 0)
            {
                breakdown.Add(new OcrVatBreakdownRow
                {
                    Rate = vatRate ?? 0m,
                    Net = net ?? 0m,
                    Vat = vatAmount ?? 0m
                });
            }
        }

        // ---------- Matrah + KDV = Toplam (±0,05) ----------
        if (total.HasValue && net.HasValue && vatAmount.HasValue)
        {
            var diff = Math.Abs(total.Value - (net.Value + vatAmount.Value));
            if (diff > AmountTolerance)
            {
                warnings.Add(hasBreakdown
                    ? $"KDV kırılımı toplamla uyuşmuyor: Σmatrah ({Tl(net.Value)}) + ΣKDV ({Tl(vatAmount.Value)}) ≠ toplam ({Tl(total.Value)}), fark {Tl(diff)}."
                    : $"Tutar tutarsızlığı: matrah ({Tl(net.Value)}) + KDV ({Tl(vatAmount.Value)}) ≠ toplam ({Tl(total.Value)}), fark {Tl(diff)}.");
                penalties++;
            }
        }

        // ---------- İskonto: negatif değer mutlak değere çevrilir (model "-25,00" yazabilir) ----------
        decimal? discount = raw.DiscountTotal.HasValue ? Math.Abs(raw.DiscountTotal.Value) : null;
        if (discount is 0m) discount = null;

        var confidence = ComputeConfidence(raw.Confidence, penalties);

        var itemsSummary = string.Join(" ",
            (raw.Items ?? new List<LlmReceiptItem>())
            .Select(i => i.Name?.Trim())
            .Where(n => !string.IsNullOrEmpty(n)));

        return new OcrReceiptResult
        {
            VendorName = raw.VendorName?.Trim() ?? string.Empty,
            VendorTaxId = taxId ?? string.Empty,
            DocumentNumber = string.IsNullOrWhiteSpace(raw.DocumentNumber) ? null : raw.DocumentNumber.Trim(),
            ReceiptDate = receiptDate,
            TotalAmount = total ?? 0m,
            VatRate = vatRate ?? 0m,
            VatAmount = vatAmount ?? 0m,
            NetAmount = net ?? 0m,
            VatBreakdown = breakdown,
            DiscountTotal = discount,
            PlateNumber = plateNumber,
            Confidence = Math.Round(confidence, 2),
            ItemsSummary = itemsSummary,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Z raporu normalizasyonu: alan eşlemesi (reportDate→tarih, totalSales→toplam,
    /// totalVat→KDV) + AYNI oran bazlı kırılım doğrulaması. Nakit+kart kırılımı varsa
    /// toplamla çapraz kontrol edilir (±0,05).
    /// </summary>
    private OcrReceiptResult NormalizeZReport(LlmReceiptExtraction raw)
    {
        var warnings = new List<string>(raw.Warnings ?? new List<string>());
        var penalties = 0;

        var reportDate = ParseDate(raw.ReportDate ?? raw.ReceiptDate, warnings, ref penalties);

        var total = raw.TotalSales ?? raw.TotalAmount;
        var totalVat = raw.TotalVat ?? raw.VatAmount;

        var breakdown = SanitizeBreakdown(raw.VatBreakdown);
        decimal? net = null;
        decimal? vat = totalVat;
        decimal vatRate = 0m;

        if (breakdown.Count > 0)
        {
            ValidateBreakdown(breakdown, warnings, ref penalties);

            var sumNet = Math.Round(breakdown.Sum(b => b.Net), 2);
            var sumVat = Math.Round(breakdown.Sum(b => b.Vat), 2);

            if (totalVat.HasValue && Math.Abs(totalVat.Value - sumVat) > AmountTolerance)
            {
                warnings.Add($"Z raporu toplam KDV ({Tl(totalVat.Value)}) kırılım toplamından ({Tl(sumVat)}) farklı — kırılım esas alındı.");
                penalties++;
            }

            net = sumNet;
            vat = sumVat;
            total ??= Math.Round(sumNet + sumVat, 2);
            vatRate = breakdown.OrderByDescending(b => b.Net + b.Vat).First().Rate;
        }
        else if (total.HasValue && totalVat.HasValue)
        {
            net = Math.Round(total.Value - totalVat.Value, 2);
            warnings.Add("Z raporunda KDV oran kırılımı okunamadı — tek satırlık kırılım toplamlardan türetildi, oranları kontrol edin.");
            penalties++;
            breakdown.Add(new OcrVatBreakdownRow { Rate = 0m, Net = net.Value, Vat = totalVat.Value });
        }

        // Σnet+Σvat ≈ toplam satış (±0,05).
        if (total.HasValue && net.HasValue && vat.HasValue)
        {
            var diff = Math.Abs(total.Value - (net.Value + vat.Value));
            if (diff > AmountTolerance)
            {
                warnings.Add(
                    $"KDV kırılımı toplamla uyuşmuyor: Σmatrah ({Tl(net.Value)}) + ΣKDV ({Tl(vat.Value)}) ≠ toplam satış ({Tl(total.Value)}), fark {Tl(diff)}.");
                penalties++;
            }
        }

        // Nakit + kart ≈ toplam satış (yalnız ikisi de okunduysa kontrol edilir).
        if (total.HasValue && raw.CashTotal.HasValue && raw.CardTotal.HasValue)
        {
            var paid = Math.Round(raw.CashTotal.Value + raw.CardTotal.Value, 2);
            if (Math.Abs(paid - total.Value) > AmountTolerance)
            {
                warnings.Add(
                    $"Nakit ({Tl(raw.CashTotal.Value)}) + kart ({Tl(raw.CardTotal.Value)}) toplamı günlük satışla ({Tl(total.Value)}) uyuşmuyor.");
                penalties++;
            }
        }

        var confidence = ComputeConfidence(raw.Confidence, penalties);

        return new OcrReceiptResult
        {
            VendorName = string.Empty, // ReceiptService "Z Raporu {ÖKC seri} #{Z no}" olarak kurar
            VendorTaxId = string.Empty,
            DocumentNumber = string.IsNullOrWhiteSpace(raw.ZNumber) ? null : raw.ZNumber.Trim(),
            ReceiptDate = reportDate,
            TotalAmount = total ?? 0m,
            VatRate = vatRate,
            VatAmount = vat ?? 0m,
            NetAmount = net ?? 0m,
            VatBreakdown = breakdown,
            OkcSerialNumber = string.IsNullOrWhiteSpace(raw.OkcSerialNumber) ? null : raw.OkcSerialNumber.Trim(),
            ZNumber = string.IsNullOrWhiteSpace(raw.ZNumber) ? null : raw.ZNumber.Trim(),
            CashTotal = raw.CashTotal,
            CardTotal = raw.CardTotal,
            Confidence = Math.Round(confidence, 2),
            ItemsSummary = string.Empty,
            Warnings = warnings
        };
    }

    /// <summary>Ham kırılımı temizler: rate'i null/negatif ya da net+vat'ı boş satırlar elenir.</summary>
    private static List<OcrVatBreakdownRow> SanitizeBreakdown(List<LlmVatBreakdownRow>? rawRows)
    {
        var rows = new List<OcrVatBreakdownRow>();
        foreach (var row in rawRows ?? new List<LlmVatBreakdownRow>())
        {
            if (row is null) continue;
            var net = row.Net ?? 0m;
            var vat = row.Vat ?? 0m;
            if (row.Rate is null || row.Rate < 0) continue;
            if (net == 0m && vat == 0m) continue;
            rows.Add(new OcrVatBreakdownRow
            {
                Rate = row.Rate.Value,
                Net = Math.Round(net, 2),
                Vat = Math.Round(vat, 2)
            });
        }

        // Aynı oran birden fazla satırda geldiyse toplanarak birleştirilir (oran başına tek satır).
        return rows
            .GroupBy(r => r.Rate)
            .Select(g => new OcrVatBreakdownRow
            {
                Rate = g.Key,
                Net = Math.Round(g.Sum(x => x.Net), 2),
                Vat = Math.Round(g.Sum(x => x.Vat), 2)
            })
            .OrderByDescending(r => r.Rate)
            .ToList();
    }

    /// <summary>
    /// Oran bazlı doğrulama: her satırda oran 0/1/10/20 kontrolü ve
    /// |net×rate/100 − vat| ≤ 0,05 çapraz kontrolü (sapmada Türkçe uyarı + ceza).
    /// </summary>
    private static void ValidateBreakdown(
        List<OcrVatBreakdownRow> breakdown, List<string> warnings, ref int penalties)
    {
        foreach (var row in breakdown)
        {
            if (!ValidVatRates.Contains(row.Rate))
            {
                warnings.Add($"KDV kırılımında %{row.Rate.ToString("0.##", CultureInfo.InvariantCulture)} oranı Türkiye'de geçerli oranlardan (0/1/10/20) değil.");
                penalties++;
            }

            if (row.Rate <= 0) continue;
            var expectedVat = Math.Round(row.Net * row.Rate / 100m, 2);
            if (Math.Abs(expectedVat - row.Vat) > AmountTolerance)
            {
                warnings.Add(
                    $"KDV kırılımı satırı tutarsız: %{row.Rate:0.##} matrah {Tl(row.Net)} × oran = {Tl(expectedVat)} beklenirken {Tl(row.Vat)} okundu.");
                penalties++;
            }
        }

    }

    private static DateTime? ParseDate(string? rawDate, List<string> warnings, ref int penalties)
    {
        if (string.IsNullOrWhiteSpace(rawDate)) return null;

        if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            if (parsed.Date > DateTime.UtcNow.Date)
            {
                warnings.Add($"Belge tarihi gelecekte ({parsed:dd.MM.yyyy}) — tarih boş bırakıldı, elle doğrulayın.");
                penalties++;
                return null;
            }

            return parsed.Date;
        }

        warnings.Add($"Belge tarihi çözümlenemedi: \"{rawDate}\".");
        penalties++;
        return null;
    }

    /// <summary>Genel güven: alan bazlı güvenlerin MİNİMUMU × uyarı cezaları (taban 0.05).</summary>
    private static double ComputeConfidence(Dictionary<string, double>? fieldConfidence, int penalties)
    {
        var confidence = fieldConfidence is { Count: > 0 }
            ? fieldConfidence.Values.Where(v => v is >= 0 and <= 1).DefaultIfEmpty(0.5).Min()
            : 0.5; // model alan güveni bildirmediyse temkinli orta değer

        confidence *= Math.Pow(WarningPenalty, penalties);
        return Math.Clamp(confidence, MinimumConfidence, 1.0);
    }

    private static string Tl(decimal value) => value.ToString("0.00", CultureInfo.GetCultureInfo("tr-TR")) + " TL";
}
