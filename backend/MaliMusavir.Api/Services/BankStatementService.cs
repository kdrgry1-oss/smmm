using System.Security.Cryptography;
using System.Text;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>Ekstre içe aktarma sonucu — kullanıcıya gösterilecek özet.</summary>
public class BankImportResult
{
    public bool Success { get; init; }
    public Guid? ImportId { get; init; }
    public Guid? BankAccountId { get; init; }
    public string? BankAccountLabel { get; init; }
    public string Format { get; init; } = string.Empty;
    public int Parsed { get; init; }
    public int Imported { get; init; }
    public int SkippedDuplicates { get; init; }
    public int AutoCoded { get; init; }
    public string? Warning { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Banka ekstresi içe aktarma + otomatik kodlama.
///
/// AKIŞ: dosya → <see cref="BankStatementParser"/> → hesap eşleştirme (IBAN) →
/// mükerrer eleme (DedupKey) → otomatik kodlama (<see cref="BankCounterpartyMap"/> hafızası)
/// → kaydet. SMMM onay ekranında düzelttiğinde <see cref="ApproveAsync"/> hafızayı günceller,
/// böylece aynı karşı taraf bir dahaki sefere otomatik kodlanır (kod motoruyla aynı mantık).
///
/// MÜKERRER KURALI: Aynı ekstre iki kez yüklenirse hareketler ÇİFTLENMEZ — DedupKey
/// (banka referansı varsa ondan, yoksa tarih+tutar+yön+açıklama+bakiye özetinden) benzersizdir.
/// Bu, muhasebede çift kayıt riskini ortadan kaldırır.
/// </summary>
public class BankStatementService
{
    /// <summary>Tek dosyada işlenecek azami hareket (bellek/işlem süresi koruması).</summary>
    private const int MaxTransactionsPerImport = 20000;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<BankStatementService> _logger;

    public BankStatementService(
        AppDbContext db, ICurrentUser currentUser, ILogger<BankStatementService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>Ekstre dosyasını okur, hareketleri ekler ve otomatik kodlar.</summary>
    public async Task<BankImportResult> ImportAsync(
        Guid clientId, Stream stream, string fileName, Guid? bankAccountId, CancellationToken ct)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
            return new BankImportResult { Success = false, ErrorMessage = "Mükellef bulunamadı." };

        ParsedStatement parsed;
        try
        {
            parsed = BankStatementParser.Parse(stream, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ekstre ayrıştırılamadı. Dosya: {FileName}", fileName);
            return new BankImportResult
            {
                Success = false,
                ErrorMessage = "Ekstre dosyası okunamadı. Desteklenen biçimler: MT940, CSV, Excel (.xlsx).",
            };
        }

        if (parsed.Lines.Count == 0)
        {
            return new BankImportResult
            {
                Success = false,
                Format = parsed.Format,
                ErrorMessage = parsed.Warning ?? "Ekstrede hareket bulunamadı.",
            };
        }

        // ---------- Banka hesabını bul / oluştur ----------
        var account = await ResolveAccountAsync(client, parsed, bankAccountId, fileName, ct);
        if (account is null)
        {
            return new BankImportResult
            {
                Success = false,
                Format = parsed.Format,
                ErrorMessage = "Ekstrenin hangi banka hesabına ait olduğu belirlenemedi. " +
                               "Hesabı panelden seçip tekrar yükleyin.",
            };
        }

        // ---------- Mevcut anahtarlar (mükerrer eleme) ----------
        var existingKeys = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.BankAccountId == account.Id)
            .Select(t => t.DedupKey)
            .ToListAsync(ct);
        var seen = existingKeys.ToHashSet(StringComparer.Ordinal);

        // ---------- Kodlama hafızası ----------
        var memory = await _db.BankCounterpartyMaps
            .AsNoTracking()
            .Where(m => m.ClientId == clientId)
            .ToListAsync(ct);

        var import = new BankStatementImport
        {
            TenantId = client.TenantId,
            ClientId = clientId,
            BankAccountId = account.Id,
            FileName = fileName,
            Format = parsed.Format,
            ParsedCount = parsed.Lines.Count,
            PeriodFrom = parsed.Lines.Min(l => l.TransactionDate),
            PeriodTo = parsed.Lines.Max(l => l.TransactionDate),
            Warning = parsed.Warning,
        };

        var added = new List<BankTransaction>();
        var duplicates = 0;
        var autoCoded = 0;

        foreach (var line in parsed.Lines.Take(MaxTransactionsPerImport))
        {
            ct.ThrowIfCancellationRequested();

            var key = BuildDedupKey(line);
            if (!seen.Add(key)) { duplicates++; continue; }

            var transaction = new BankTransaction
            {
                TenantId = client.TenantId,
                ClientId = clientId,
                BankAccountId = account.Id,
                TransactionDate = line.TransactionDate,
                Direction = line.Direction,
                Amount = line.Amount,
                CurrencyCode = account.CurrencyCode,
                BalanceAfter = line.BalanceAfter,
                CounterpartyName = line.CounterpartyName,
                CounterpartyIban = line.CounterpartyIban,
                CounterpartyTaxId = line.CounterpartyTaxId,
                Description = line.Description,
                ReferenceNo = line.ReferenceNo,
                TransactionType = line.TransactionType,
                Source = BankStatementSource.DosyaIceAktarma,
                ImportId = import.Id,
                DedupKey = key,
            };

            if (TryAutoCode(transaction, memory)) autoCoded++;

            added.Add(transaction);
        }

        import.ImportedCount = added.Count;
        import.SkippedDuplicates = duplicates;

        _db.BankStatementImports.Add(import);
        if (added.Count > 0) _db.BankTransactions.AddRange(added);

        // Bakiye bilgisi ekstreden geliyorsa hesapta güncellenir (mutabakat için).
        var lastLine = parsed.Lines.OrderBy(l => l.TransactionDate).LastOrDefault();
        var closing = parsed.ClosingBalance ?? lastLine?.BalanceAfter;
        if (closing.HasValue)
        {
            var tracked = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == account.Id, ct);
            if (tracked is not null)
            {
                tracked.LastBalance = closing;
                tracked.LastBalanceAt = import.PeriodTo;
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Banka ekstresi içe aktarıldı. ClientId: {ClientId}, Hesap: {Iban}, " +
            "Okunan: {Parsed}, Eklenen: {Imported}, Mükerrer: {Duplicates}",
            clientId, account.Iban, parsed.Lines.Count, added.Count, duplicates);

        var warning = parsed.Warning;
        if (parsed.Lines.Count > MaxTransactionsPerImport)
        {
            warning = $"Dosyada {parsed.Lines.Count} hareket var; ilk {MaxTransactionsPerImport} tanesi " +
                      "alındı. Kalanı için ekstreyi tarih aralığına bölerek yükleyin.";
        }

        return new BankImportResult
        {
            Success = true,
            ImportId = import.Id,
            BankAccountId = account.Id,
            BankAccountLabel = $"{account.BankName} — {FormatIban(account.Iban)}",
            Format = parsed.Format,
            Parsed = parsed.Lines.Count,
            Imported = added.Count,
            SkippedDuplicates = duplicates,
            AutoCoded = autoCoded,
            Warning = warning,
        };
    }

    /// <summary>
    /// Hareketin kodlamasını onaylar/düzeltir ve karşı taraf hafızasını günceller
    /// (bir sonraki aynı karşı taraf otomatik kodlanır).
    /// </summary>
    public async Task<bool> ApproveAsync(
        Guid transactionId, string? accountCode, string? cariCode, string? counterpartyName,
        CancellationToken ct)
    {
        var transaction = await _db.BankTransactions.FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction is null) return false;

        transaction.AccountCode = string.IsNullOrWhiteSpace(accountCode) ? null : accountCode.Trim();
        transaction.CariCode = string.IsNullOrWhiteSpace(cariCode) ? null : cariCode.Trim();
        if (!string.IsNullOrWhiteSpace(counterpartyName))
            transaction.CounterpartyName = counterpartyName.Trim();

        transaction.CodingStatus = CodingStatus.Approved;
        transaction.Confidence = 1.0;

        await LearnAsync(transaction, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Onaylanan kodlamayı karşı taraf hafızasına yazar (IBAN → VKN → unvan sırasıyla).</summary>
    private async Task LearnAsync(BankTransaction transaction, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transaction.AccountCode) &&
            string.IsNullOrWhiteSpace(transaction.CariCode))
        {
            return; // Öğrenilecek bir şey yok.
        }

        var nameKey = TextNormalizer.NormalizeCompanyName(transaction.CounterpartyName) ?? string.Empty;
        var iban = transaction.CounterpartyIban;
        var taxId = transaction.CounterpartyTaxId;

        var existing = await _db.BankCounterpartyMaps.FirstOrDefaultAsync(m =>
                m.ClientId == transaction.ClientId &&
                ((iban != null && m.CounterpartyIban == iban) ||
                 (iban == null && taxId != null && m.CounterpartyTaxId == taxId) ||
                 (iban == null && taxId == null && m.CounterpartyNameNormalized == nameKey)),
            ct);

        if (existing is null)
        {
            _db.BankCounterpartyMaps.Add(new BankCounterpartyMap
            {
                TenantId = transaction.TenantId,
                ClientId = transaction.ClientId,
                CounterpartyIban = iban,
                CounterpartyTaxId = taxId,
                CounterpartyNameNormalized = nameKey,
                AccountCode = transaction.AccountCode,
                CariCode = transaction.CariCode,
                DisplayName = transaction.CounterpartyName,
            });
            return;
        }

        existing.AccountCode = transaction.AccountCode ?? existing.AccountCode;
        existing.CariCode = transaction.CariCode ?? existing.CariCode;
        existing.DisplayName = transaction.CounterpartyName ?? existing.DisplayName;
        existing.UseCount++;
        existing.UpdatedAt = DateTime.UtcNow;

        // Eksik anahtarları zamanla doldur (ilk kayıtta IBAN yoksa sonra öğrenilir).
        existing.CounterpartyIban ??= iban;
        existing.CounterpartyTaxId ??= taxId;
        if (string.IsNullOrWhiteSpace(existing.CounterpartyNameNormalized))
            existing.CounterpartyNameNormalized = nameKey;
    }

    /// <summary>
    /// Hafızadan otomatik kodlama. Eşleşme sırası IBAN → VKN/TCKN → normalize unvan;
    /// güven skoru anahtarın güvenilirliğine ve kullanım sayısına göre verilir.
    /// </summary>
    private static bool TryAutoCode(BankTransaction transaction, List<BankCounterpartyMap> memory)
    {
        BankCounterpartyMap? match = null;
        double confidence = 0;

        if (!string.IsNullOrWhiteSpace(transaction.CounterpartyIban))
        {
            match = memory.FirstOrDefault(m => m.CounterpartyIban == transaction.CounterpartyIban);
            if (match is not null) confidence = 0.95;
        }

        if (match is null && !string.IsNullOrWhiteSpace(transaction.CounterpartyTaxId))
        {
            match = memory.FirstOrDefault(m => m.CounterpartyTaxId == transaction.CounterpartyTaxId);
            if (match is not null) confidence = 0.85;
        }

        if (match is null && !string.IsNullOrWhiteSpace(transaction.CounterpartyName))
        {
            var nameKey = TextNormalizer.NormalizeCompanyName(transaction.CounterpartyName);
            if (!string.IsNullOrWhiteSpace(nameKey))
            {
                match = memory.FirstOrDefault(m => m.CounterpartyNameNormalized == nameKey);
                if (match is not null) confidence = 0.70;
            }
        }

        if (match is null) return false;

        transaction.SuggestedAccountCode = match.AccountCode;
        transaction.CariCode = match.CariCode;
        transaction.Confidence = Math.Min(0.99, confidence + Math.Min(0.04, match.UseCount * 0.01));
        // Öneri her zaman SMMM onayına düşer — otomatik "Approved" YAPILMAZ.
        transaction.CodingStatus = CodingStatus.AutoCoded;

        if (string.IsNullOrWhiteSpace(transaction.CounterpartyName) && !string.IsNullOrWhiteSpace(match.DisplayName))
            transaction.CounterpartyName = match.DisplayName;

        return true;
    }

    /// <summary>Ekstredeki IBAN'a göre hesabı bulur; yoksa oluşturur.</summary>
    private async Task<BankAccount?> ResolveAccountAsync(
        Client client, ParsedStatement parsed, Guid? requestedAccountId, string fileName, CancellationToken ct)
    {
        if (requestedAccountId.HasValue && requestedAccountId.Value != Guid.Empty)
        {
            return await _db.BankAccounts
                .FirstOrDefaultAsync(a => a.Id == requestedAccountId.Value && a.ClientId == client.Id, ct);
        }

        var iban = parsed.Iban is null ? null : BankStatementParser.NormalizeIban(parsed.Iban);
        if (string.IsNullOrWhiteSpace(iban)) return null;

        var existing = await _db.BankAccounts
            .FirstOrDefaultAsync(a => a.ClientId == client.Id && a.Iban == iban, ct);
        if (existing is not null) return existing;

        var created = new BankAccount
        {
            TenantId = client.TenantId,
            ClientId = client.Id,
            Iban = iban,
            BankName = GuessBankName(iban, fileName),
            CurrencyCode = parsed.CurrencyCode ?? "TRY",
        };

        _db.BankAccounts.Add(created);
        return created;
    }

    /// <summary>
    /// IBAN'ın banka kodundan (5-9. haneler) banka adını tahmin eder. Bilinmeyen kodda
    /// dosya adı kullanılır; kullanıcı panelden düzeltebilir.
    /// </summary>
    private static string GuessBankName(string iban, string fileName)
    {
        if (iban.Length >= 9)
        {
            var bankCode = iban.Substring(4, 5);
            var known = bankCode switch
            {
                "00001" => "Türkiye Cumhuriyet Merkez Bankası",
                "00010" => "Ziraat Bankası",
                "00012" => "Halkbank",
                "00015" => "Vakıfbank",
                "00032" => "TEB",
                "00046" => "Akbank",
                "00062" => "Garanti BBVA",
                "00064" => "İş Bankası",
                "00067" => "Yapı Kredi",
                "00099" => "ING Bank",
                "00111" => "QNB Finansbank",
                "00123" => "HSBC",
                "00134" => "Denizbank",
                "00203" => "Albaraka Türk",
                "00205" => "Kuveyt Türk",
                "00206" => "Türkiye Finans",
                "00209" => "Ziraat Katılım",
                "00210" => "Vakıf Katılım",
                "00211" => "Emlak Katılım",
                _ => null,
            };
            if (known is not null) return known;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(name) ? "Banka" : name.Trim();
    }

    /// <summary>
    /// Mükerrer kontrol anahtarı. Banka referansı varsa o kullanılır (en güvenilir);
    /// yoksa tarih + yön + tutar + açıklama + bakiye özetinden deterministik hash üretilir.
    /// </summary>
    private static string BuildDedupKey(ParsedBankLine line)
    {
        var basis = !string.IsNullOrWhiteSpace(line.ReferenceNo)
            ? $"REF|{line.ReferenceNo}|{line.TransactionDate:yyyyMMdd}|{line.Amount:0.00}"
            : $"HASH|{line.TransactionDate:yyyyMMdd}|{(int)line.Direction}|{line.Amount:0.00}|" +
              $"{line.Description}|{line.BalanceAfter:0.00}";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(bytes)[..32];
    }

    /// <summary>IBAN'ı okunur biçimde gruplar (TR12 3456 7890 ...).</summary>
    public static string FormatIban(string iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < iban.Length; i += 4)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(iban.AsSpan(i, Math.Min(4, iban.Length - i)));
        }
        return builder.ToString();
    }
}
