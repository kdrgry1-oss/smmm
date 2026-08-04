using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// Entegratörden okunan normalize fatura satırı (sağlayıcı-bağımsız DTO).
/// </summary>
public class ProviderInvoiceLine
{
    public int LineNumber { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string UnitCode { get; set; } = "C62";
    public decimal UnitPrice { get; set; }
    public decimal LineAmount { get; set; }
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
}

/// <summary>
/// Entegratörden okunan normalize gelen fatura (sağlayıcı-bağımsız DTO).
/// </summary>
public class ProviderInvoice
{
    public string ExternalUuid { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public string SenderTaxId { get; set; } = string.Empty;
    public string SenderTitle { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "TRY";
    public decimal TaxExclusiveAmount { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal PayableAmount { get; set; }
    public List<ProviderInvoiceLine> Lines { get; set; } = new();
}

/// <summary>
/// Sağlayıcı çağrısı sonucu. ErrorMessage her zaman kullanıcıya gösterilebilir Türkçe metindir.
/// Retryable=true ise (429, 5xx, ağ hatası) işlem daha sonra tekrar denenebilir.
/// </summary>
public class ProviderResult<T>
{
    public bool Success { get; private init; }
    public T? Data { get; private init; }
    public string? ErrorMessage { get; private init; }
    public bool Retryable { get; private init; }

    /// <summary>
    /// Başarılı sonuca eklenebilen, kullanıcıya gösterilebilir Türkçe bilgi notu
    /// (ör. sayfa sınırına takılınca "İlk 5.000 fatura alındı; ... aralığı daraltın.").
    /// </summary>
    public string? Warning { get; private init; }

    public static ProviderResult<T> Ok(T data, string? warning = null) =>
        new() { Success = true, Data = data, Warning = warning };

    public static ProviderResult<T> Fail(string errorMessage, bool retryable = false) =>
        new() { Success = false, ErrorMessage = errorMessage, Retryable = retryable };
}

/// <summary>
/// e-Belge entegratör adaptör arayüzü (blueprint §1.2: entegratör-bağımsız adapter pattern).
/// Yeni entegratör eklemek = bu arayüzü uygulayan tek sınıf yazmak.
/// </summary>
public interface IEInvoiceProvider
{
    /// <summary>Bu adaptörün karşıladığı sağlayıcı.</summary>
    IntegrationProvider Provider { get; }

    /// <summary>Verilen tarih aralığındaki gelen faturaları satırlarıyla birlikte çeker.</summary>
    Task<ProviderResult<List<ProviderInvoice>>> GetIncomingInvoicesAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, CancellationToken ct);
}
