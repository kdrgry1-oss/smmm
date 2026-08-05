namespace MaliMusavir.Api.Domain;

/// <summary>Hesap hareketi yönü. Alacak = para GİRDİ, Borc = para ÇIKTI (mükellef gözünden).</summary>
public enum BankDirection { Gelen = 0, Giden = 1 }

/// <summary>
/// Ekstre kaynağı. Bugün BİRİNCİL yol dosya içe aktarmadır (her banka çalışır, anlaşma
/// gerektirmez); banka kurumsal API'si sözleşmeyle geldiğinde ApiBanka devreye girer.
/// </summary>
public enum BankStatementSource
{
    /// <summary>Elle yüklenen MT940 / CAMT.053 / CSV / Excel ekstresi.</summary>
    DosyaIceAktarma = 0,
    /// <summary>Banka kurumsal API'si (IBankStatementProvider adaptörü).</summary>
    BankaApi = 1,
    /// <summary>Kullanıcının panelden elle girdiği hareket.</summary>
    ElleGiris = 2
}

/// <summary>
/// Mükellefin banka hesabı. Muhasebede 102.XX (Bankalar) alt hesabına karşılık gelir;
/// <see cref="AccountCode"/> bu eşlemeyi tutar ve hareketlerin karşı kaydında kullanılır.
/// </summary>
public class BankAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Banka adı (ör. "Ziraat Bankası"). Ekstreden okunur veya kullanıcı girer.</summary>
    public string BankName { get; set; } = string.Empty;
    /// <summary>IBAN — boşluksuz, büyük harf. (TenantId, Iban) benzersizdir.</summary>
    public string Iban { get; set; } = string.Empty;
    /// <summary>Banka şube/hesap numarası (IBAN yoksa eşleşme anahtarı).</summary>
    public string? AccountNumber { get; set; }
    public string CurrencyCode { get; set; } = "TRY";

    /// <summary>Muhasebe hesap kodu (ör. "102.01"). Hareketlerin banka ayağı bu koda yazılır.</summary>
    public string? AccountCode { get; set; }

    /// <summary>Ekstredeki son bakiye (bilgi amaçlı; mutabakatta kullanılır).</summary>
    public decimal? LastBalance { get; set; }
    public DateTime? LastBalanceAt { get; set; }

    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<BankTransaction> Transactions { get; set; } = new List<BankTransaction>();
}

/// <summary>
/// Tek banka hesap hareketi (dekont satırı). "Kimden ne gelmiş / kime ne gitmiş" sorusunun
/// cevabı <see cref="CounterpartyName"/> + <see cref="CounterpartyIban"/> alanlarındadır.
///
/// MÜKERRER KONTROLÜ: (TenantId, BankAccountId, DedupKey) benzersizdir. DedupKey banka
/// referansı varsa ondan, yoksa tarih+tutar+açıklama+bakiye özetinden üretilir — aynı ekstre
/// iki kez yüklenirse hareketler ÇİFTLENMEZ.
/// </summary>
public class BankTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Guid BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }

    /// <summary>Valör/işlem tarihi.</summary>
    public DateTime TransactionDate { get; set; }
    public BankDirection Direction { get; set; }
    /// <summary>Tutar — HER ZAMAN POZİTİF. Yön <see cref="Direction"/> alanındadır.</summary>
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "TRY";
    /// <summary>İşlem sonrası bakiye (banka veriyorsa).</summary>
    public decimal? BalanceAfter { get; set; }

    /// <summary>Karşı taraf unvanı/adı — "kimden geldi / kime gitti".</summary>
    public string? CounterpartyName { get; set; }
    /// <summary>Karşı taraf IBAN'ı (varsa).</summary>
    public string? CounterpartyIban { get; set; }
    /// <summary>Karşı taraf VKN/TCKN — ekstrede geçiyorsa cari eşleşmesinde kullanılır.</summary>
    public string? CounterpartyTaxId { get; set; }

    /// <summary>Dekont açıklaması (bankanın yazdığı ham metin).</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Banka işlem referansı / dekont no.</summary>
    public string? ReferenceNo { get; set; }
    /// <summary>Banka işlem tipi metni (EFT, HAVALE, POS, MASRAF, FAIZ vb.).</summary>
    public string? TransactionType { get; set; }

    // ---------- Kodlama (kod motoruyla aynı sözleşme) ----------
    /// <summary>Motorun önerdiği karşı hesap kodu (ör. "320.001", "770.01").</summary>
    public string? SuggestedAccountCode { get; set; }
    /// <summary>SMMM'nin onayladığı nihai karşı hesap kodu.</summary>
    public string? AccountCode { get; set; }
    /// <summary>Karşı tarafın cari hesap kodu (ör. "120.001" / "320.001").</summary>
    public string? CariCode { get; set; }
    /// <summary>Öneri güven skoru 0-1. Onaylananlarda 1.0.</summary>
    public double? Confidence { get; set; }
    public CodingStatus CodingStatus { get; set; } = CodingStatus.Uncoded;

    /// <summary>Hangi yolla geldi (dosya / API / elle).</summary>
    public BankStatementSource Source { get; set; } = BankStatementSource.DosyaIceAktarma;
    /// <summary>Bu hareketi getiren içe aktarma partisi (varsa).</summary>
    public Guid? ImportId { get; set; }

    /// <summary>Mükerrer kontrol anahtarı (bkz. sınıf notu).</summary>
    public string DedupKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Ekstre içe aktarma partisi — denetim izi. Hangi dosya, ne zaman, kaç satır okundu,
/// kaçı mükerrer diye atlandı. Kullanıcı "neden 12 değil 9 hareket geldi" sorusunun
/// cevabını buradan görür.
/// </summary>
public class BankStatementImport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public Guid? BankAccountId { get; set; }

    public string FileName { get; set; } = string.Empty;
    /// <summary>Algılanan biçim: "MT940" / "CAMT053" / "CSV" / "XLSX".</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Dosyada okunan toplam hareket sayısı.</summary>
    public int ParsedCount { get; set; }
    /// <summary>Yeni eklenen hareket sayısı.</summary>
    public int ImportedCount { get; set; }
    /// <summary>Mükerrer olduğu için atlanan hareket sayısı.</summary>
    public int SkippedDuplicates { get; set; }

    /// <summary>Ekstrenin kapsadığı ilk/son tarih (bilgi amaçlı).</summary>
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }

    /// <summary>Kullanıcıya gösterilecek Türkçe uyarı (kısmi okuma, tanınmayan satır vb.).</summary>
    public string? Warning { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Karşı taraf → cari/hesap kodu hafızası. Kod motorunun VendorCariMap'iyle aynı mantık:
/// SMMM bir kez "bu IBAN'dan gelen para 120.005 Ahmet Ltd."dir dediğinde, sonraki
/// hareketler otomatik kodlanır.
///
/// EŞLEŞME SIRASI: IBAN → VKN/TCKN → normalize unvan. IBAN en güvenilir anahtardır.
/// </summary>
public class BankCounterpartyMap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Karşı taraf IBAN'ı (boşluksuz, büyük harf). Birincil eşleşme anahtarı.</summary>
    public string? CounterpartyIban { get; set; }
    /// <summary>Karşı taraf VKN/TCKN. İkincil anahtar.</summary>
    public string? CounterpartyTaxId { get; set; }
    /// <summary>Normalize edilmiş karşı taraf unvanı. Üçüncül anahtar (TextNormalizer ile).</summary>
    public string CounterpartyNameNormalized { get; set; } = string.Empty;

    /// <summary>Öğrenilen cari hesap kodu.</summary>
    public string? CariCode { get; set; }
    /// <summary>Öğrenilen karşı hesap kodu (gelir/gider/kredi vb.).</summary>
    public string? AccountCode { get; set; }
    /// <summary>Kullanıcıya gösterilen karşı taraf adı (son onaydaki hali).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Kaç kez kullanıldı (güven skoruna girer).</summary>
    public int UseCount { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
