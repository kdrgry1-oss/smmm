namespace MaliMusavir.Api.Domain;

/// <summary>Shared arithmetic checks for receipt approval and rate-based reports.</summary>
public static class ReceiptVatMath
{
    public const decimal Tolerance = 0.05m;
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    public static bool SupportedRate(decimal rate) => rate is 0m or 1m or 8m or 10m or 18m or 20m;
    public static List<ReceiptVatBreakdown> RowsOrLegacy(IEnumerable<ReceiptVatBreakdown> rows, decimal? rate, decimal? net, decimal? vat)
    {
        var result = rows.ToList();
        if (result.Count == 0 && rate.HasValue && net.HasValue && vat.HasValue)
            result.Add(new ReceiptVatBreakdown { VatRate = rate.Value, NetAmount = net.Value, VatAmount = vat.Value });
        return result;
    }
    public static string? Validate(decimal? total, decimal? net, decimal? vat, IEnumerable<ReceiptVatBreakdown> source)
    {
        var rows = source.ToList();
        if (rows.Count == 0) return "KDV oran kırılımı eksik. Okunamayan oran %0 sayılamaz; fişteki oran ve tutarları doğrulayın.";
        foreach (var row in rows)
        {
            if (!SupportedRate(row.VatRate)) return $"KDV oranı %{row.VatRate} desteklenmiyor; belgeyi kontrol edin.";
            if (row.NetAmount < 0 || row.VatAmount < 0) return "Matrah ve KDV negatif olamaz; iade/indirim belgesini ayrıca kontrol edin.";
            if ((row.VatRate == 0 && row.VatAmount != 0) || Math.Abs(Round(row.NetAmount * row.VatRate / 100m) - row.VatAmount) > Tolerance)
                return $"%{row.VatRate} KDV satırında matrah × oran ile KDV tutarı uyuşmuyor.";
        }
        var sumNet = Round(rows.Sum(r => r.NetAmount));
        var sumVat = Round(rows.Sum(r => r.VatAmount));
        if (!total.HasValue || Math.Abs(total.Value - sumNet - sumVat) > Tolerance)
            return "KDV kırılımındaki matrah + KDV, fişin genel toplamıyla uyuşmuyor. İskonto sonrası tutarları kontrol edin.";
        if (!net.HasValue || !vat.HasValue || Math.Abs(net.Value - sumNet) > Tolerance || Math.Abs(vat.Value - sumVat) > Tolerance)
            return "Fişin matrah/KDV toplamları oran kırılımıyla uyuşmuyor.";
        return null;
    }

    // Same proportional KKEG policy as the existing export; assign rounding remainder once.
    public static List<(decimal Rate, decimal Net, decimal Vat)> DeductibleRows(IEnumerable<ReceiptVatBreakdown> source, decimal disallowedVat)
    {
        var rows = source.GroupBy(r => r.VatRate).OrderByDescending(g => g.Key)
            .Select(g => (Rate:g.Key, Net:Round(g.Sum(r => r.NetAmount)), Vat:Round(g.Sum(r => r.VatAmount)))).ToList();
        var total = rows.Sum(r => r.Vat);
        if (total <= 0 || disallowedVat <= 0) return rows;
        var target = Round(Math.Max(0, total - disallowedVat));
        var scaled = rows.Select(r => (r.Rate, r.Net, Vat:Round(r.Vat * target / total))).ToList();
        var index = rows.FindIndex(r => r.Vat == rows.Max(x => x.Vat));
        var old = scaled[index];
        scaled[index] = (old.Rate, old.Net, old.Vat + target - scaled.Sum(r => r.Vat));
        return scaled;
    }
}
