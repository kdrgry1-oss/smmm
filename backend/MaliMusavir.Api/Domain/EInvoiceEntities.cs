namespace MaliMusavir.Api.Domain;

/// <summary>
/// e-Belge entegratör sağlayıcısı. Mock: anahtar gerektirmeyen demo sağlayıcı.
/// Uyumsoft, DoganDonusum, Elogo ve Edm: SOAP tabanlı özel entegratörler — kimlik bilgisi
/// (kullanıcı adı + şifre + opsiyonel servis adresi) JSON olarak ApiKeyEncrypted
/// alanında şifreli saklanır (ayrı kolon YOK).
/// Parasut: REST (JSON:API) — kimlik JSON'u farklıdır:
/// {"clientId","clientSecret","username","password","companyId"} (aynı kolonda şifreli).
/// </summary>
public enum IntegrationProvider
{
    Mock = 0,
    Nilvera = 1,
    Uyumsoft = 2,
    DoganDonusum = 3,
    Elogo = 4,
    Edm = 5,
    Parasut = 6,
    /// <summary>
    /// Faz 15 — GİB Portal Robotu: entegratör kullanmayan mükellef. Gelen belgeler ve
    /// e-Tebligatlar, mükellefin Dijital Vergi Dairesi kimliğiyle iç ağdaki gib-robot
    /// servisi üzerinden otomatik çekilir.
    /// </summary>
    GibPortal = 7,
    /// <summary>
    /// GİB e-Arşiv Portalı (earsivportal.efatura.gov.tr) — entegratör kullanmayan mükellefler
    /// için TERCİH EDİLEN yol. GibPortal'dan farkı: ekran kazıma değil gerçek JSON API'dir ve
    /// girişinde doğrulama kodu (captcha) yoktur. Yalnız e-ARŞİV belgelerini kapsar; ticari
    /// e-Fatura ve e-Tebligat bu portalda YOKTUR (onlar için GibPortal yolu kullanılır).
    /// Fatura ÇEKME ve KESME işlemlerinin ikisini de destekler.
    /// </summary>
    EArsivPortal = 8
}

/// <summary>Fatura yönü.</summary>
public enum InvoiceDirection { Incoming = 0, Outgoing = 1 }

/// <summary>Faturanın platform içi yaşam döngüsü durumu.</summary>
public enum EInvoiceStatus { New = 0, Processing = 1, Processed = 2, Exported = 3, Error = 4 }

/// <summary>Mükellef ↔ entegratör bağlantısı. API anahtarı AES ile şifreli saklanır.</summary>
public class ClientIntegration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    public IntegrationProvider Provider { get; set; } = IntegrationProvider.Mock;
    /// <summary>
    /// AES ile şifrelenmiş entegratör kimliği (Mock için null olabilir). Düz metin ASLA saklanmaz.
    /// Nilvera: API anahtarının kendisi. Uyumsoft/Doğan/eLogo/EDM:
    /// {"username","password","endpoint"?} JSON'u; Paraşüt:
    /// {"clientId","clientSecret","username","password","companyId"} JSON'u;
    /// GibPortal (Faz 15): {"username","password","manualWarning"?,"manualWarningAt"?} JSON'u
    /// (ayrı kolon açılmaz; ilgili sağlayıcının PackCredentials/TryParseCredentials'ı kullanılır).
    /// </summary>
    public string? ApiKeyEncrypted { get; set; }
    public bool UseTestEnvironment { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncAt { get; set; }
}

/// <summary>e-Fatura başlığı (gelen/giden). Ham UBL XML dosya yolu ile referanslanır.</summary>
public class EInvoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }
    public InvoiceDirection Direction { get; set; } = InvoiceDirection.Incoming;
    /// <summary>Entegratördeki fatura UUID/ETTN'i. (TenantId, ExternalUuid) benzersizdir.</summary>
    public string ExternalUuid { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public string SenderTaxId { get; set; } = string.Empty;
    public string SenderTitle { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "TRY";
    /// <summary>KDV hariç matrah toplamı.</summary>
    public decimal TaxExclusiveAmount { get; set; }
    /// <summary>Toplam vergi (KDV) tutarı.</summary>
    public decimal TaxTotal { get; set; }
    /// <summary>Ödenecek toplam tutar.</summary>
    public decimal PayableAmount { get; set; }
    public EInvoiceStatus Status { get; set; } = EInvoiceStatus.New;
    /// <summary>Otomatik kod motorunun kodlama durumu (Faz 2 — bkz. CodingEntities.cs).</summary>
    public CodingStatus CodingStatus { get; set; } = CodingStatus.Uncoded;
    /// <summary>Ham UBL XML'in object store/disk yolu (Faz 1'de null olabilir).</summary>
    public string? RawXmlPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EInvoiceLine> Lines { get; set; } = new List<EInvoiceLine>();
}

/// <summary>e-Fatura kalem satırı.</summary>
public class EInvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EInvoiceId { get; set; }
    public EInvoice? EInvoice { get; set; }
    public int LineNumber { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    /// <summary>UN/ECE birim kodu (ör. C62=adet, LTR=litre, KGM=kg, MON=ay).</summary>
    public string UnitCode { get; set; } = "C62";
    public decimal UnitPrice { get; set; }
    /// <summary>KDV hariç satır tutarı.</summary>
    public decimal LineAmount { get; set; }
    /// <summary>KDV oranı, yüzde (ör. 1, 10, 20).</summary>
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
    /// <summary>Otomatik kod motorunun önerdiği hesap kodu (Faz 1'de boş).</summary>
    public string? SuggestedAccountCode { get; set; }
    /// <summary>SMMM'nin onayladığı nihai hesap kodu (Faz 1'de boş).</summary>
    public string? AccountCode { get; set; }
    /// <summary>Motor önerisinin güven skoru 0-1 (Faz 2). Onaylanan satırlarda 1.0.</summary>
    public double? Confidence { get; set; }
    /// <summary>Satıcının cari hesap kodu (ör. "320.001"; VendorCariMap üzerinden atanır).</summary>
    public string? CariCode { get; set; }

    // ---------- Faz 8 — B2: Tevkifat (KDV-2) ----------
    /// <summary>GİB kısmi tevkifat kodu (ör. "601", "606"); motor önerir, SMMM onayda düzeltebilir.</summary>
    public string? WithholdingCode { get; set; }
    /// <summary>Tevkifat oranı pay/payda biçiminde (ör. "9/10").</summary>
    public string? WithholdingRate { get; set; }
    /// <summary>Tevkif edilen (alıcının sorumlu sıfatıyla beyan edeceği) KDV tutarı = VatAmount × pay/payda.</summary>
    public decimal? WithholdingVatAmount { get; set; }

    // ---------- Faz 8 — B3: KKEG / gider kısıtı ----------
    /// <summary>Satırda kanunen kabul edilmeyen gider (KKEG) kısmı var mı (KkegAnalyzer işaretler).</summary>
    public bool IsKkeg { get; set; }
    /// <summary>KKEG gerekçesi (Türkçe; ör. "Binek oto gideri: %30 kısmı KKEG").</summary>
    public string? KkegReason { get; set; }
    /// <summary>KKEG tutarı — satır matrahının (LineAmount) indirilemeyen kısmı, KDV hariç.</summary>
    public decimal? KkegAmount { get; set; }
}

/// <summary>
/// Faz 8 — B2: GİB kısmi KDV tevkifat kodu kuralı. TENANT'SIZ GLOBAL tablodur
/// (parametre merkezi gibi süper admin yönetir, motor herkes için okur).
/// KeywordPattern: ";" ile ayrılmış anahtar kelimeler — kalem/satıcı metninde
/// herhangi biri geçerse motor bu kodu ve oranı önerir (SMMM onayda düzeltir).
/// </summary>
public class WithholdingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>GİB tevkifat kodu (ör. "601"). Benzersizdir.</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>Türkçe açıklama (ör. "Yapım işleri...").</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Tevkifat oranı pay/payda (ör. "4/10").</summary>
    public string Fraction { get; set; } = string.Empty;
    /// <summary>";" ayraçlı anahtar kelime listesi (normalize metinde aranır).</summary>
    public string KeywordPattern { get; set; } = string.Empty;
}

/// <summary>
/// Faz 8 — B5: Mutabakat bulgusu "Yoksay" kaydı. Bulgular DB'de saklanmaz (her analiz
/// yeniden hesaplanır); yok sayılanlar bu tabloda FindingKey (deterministik hash) ile tutulur
/// ve sonraki analizlerde gizlenir. Tenant filtrelidir.
/// </summary>
public class ReconciliationDismissal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Bulgunun deterministik anahtarı (tür + ilgili belge kimlikleri hash'i).</summary>
    public string FindingKey { get; set; } = string.Empty;
    public DateTime DismissedAt { get; set; } = DateTime.UtcNow;
}
