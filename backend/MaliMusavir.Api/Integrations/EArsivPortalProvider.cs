using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Integrations;

/// <summary>
/// GİB e-Arşiv Portalı adaptörü — ENTEGRATÖR KULLANMAYAN mükellefler için ikinci ve
/// tercih edilen yol (bkz. <see cref="EArsivPortalClient"/> sınıf notu).
///
/// KAPSAM — ÖNEMLİ SINIR: Bu portal yalnızca e-ARŞİV belgelerini içerir.
///  ✔ Mükellefin adına kesilen e-Arşiv belgeleri  → GetIncomingInvoicesAsync
///  ✔ Mükellefin kendi kestiği e-Arşiv belgeleri  → GetOutgoingInvoicesAsync
///  ✘ Ticari e-FATURA (entegratör/GİB e-Fatura sistemi) BURADA GÖRÜNMEZ.
///  ✘ e-TEBLİGAT BURADA YOKTUR — o veri Dijital Vergi Dairesi'ndedir
///    (<see cref="GibPortalProvider"/> / gib-robot yolu).
/// Bu yüzden adaptör mevcut robot yolunun YERİNE GEÇMEZ, onu TAMAMLAR.
///
/// KİMLİK: Diğer kimlik-tabanlı sağlayıcılarla aynı kolonda (ClientIntegration.ApiKeyEncrypted)
/// AES ile şifreli JSON olarak durur: {"username":"...","password":"..."}.
/// Şifre düz metin saklanmaz ve LOGA ASLA yazılmaz.
///
/// KALEM DETAYI: Liste ucu tutar/kalem döndürmez; her belge için detay ucu (FATURA_GETIR)
/// çağrılır ve kalemler birebir eşlenir. Detay alınamayan belge için TEK ÖZET SATIR üretilir
/// (Nilvera/Uyumsoft/GibPortal fallback'iyle aynı yaklaşım).
/// </summary>
public class EArsivPortalProvider : IEInvoiceProvider
{
    /// <summary>Liste ucunun belge tipi filtresi — portalın kendi arayüzüyle aynı değer.</summary>
    private const string AllDocumentTypes = "5000/30000";

    /// <summary>
    /// Tek çekimde detayı alınacak azami belge sayısı. Aşılırsa kalanlar özet satırla
    /// eklenir ve kullanıcıya Türkçe uyarı döner (sessizce kırpma YAPILMAZ).
    /// </summary>
    private const int MaxDetailFetch = 300;

    private readonly EArsivPortalClient _client;
    private readonly ILogger<EArsivPortalProvider> _logger;

    public EArsivPortalProvider(EArsivPortalClient client, ILogger<EArsivPortalProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    public IntegrationProvider Provider => IntegrationProvider.EArsivPortal;

    // ---------- Kimlik paketleme ----------

    private sealed class EArsivCredentials
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }
    }

    /// <summary>Kullanıcı adı + şifreyi ApiKeyEncrypted alanında saklanacak JSON'a paketler.</summary>
    public static string PackCredentials(string username, string password) =>
        JsonSerializer.Serialize(new EArsivCredentials { Username = username, Password = password });

    /// <summary>Saklanan JSON'dan kullanıcı adı + şifreyi çözer. Geçersizse false.</summary>
    public static bool TryParseCredentials(string? json, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<EArsivCredentials>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.Username) ||
                string.IsNullOrWhiteSpace(parsed.Password))
            {
                return false;
            }

            username = parsed.Username.Trim();
            password = parsed.Password;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------- IEInvoiceProvider ----------

    /// <summary>Mükellefin ADINA KESİLEN e-Arşiv belgelerini çeker (gelen yön).</summary>
    public Task<ProviderResult<List<ProviderInvoice>>> GetIncomingInvoicesAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, CancellationToken ct) =>
        FetchAsync(apiKey, testEnv, from, to, incoming: true, ct);

    /// <summary>
    /// Mükellefin KENDİ KESTİĞİ e-Arşiv belgelerini çeker (giden yön). Arayüzün parçası
    /// değildir; giden fatura senkronu bu adaptöre özeldir (portal ikisini de verir).
    /// </summary>
    public Task<ProviderResult<List<ProviderInvoice>>> GetOutgoingInvoicesAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, CancellationToken ct) =>
        FetchAsync(apiKey, testEnv, from, to, incoming: false, ct);

    private async Task<ProviderResult<List<ProviderInvoice>>> FetchAsync(
        string apiKey, bool testEnv, DateTime from, DateTime to, bool incoming, CancellationToken ct)
    {
        if (!TryParseCredentials(apiKey, out var username, out var password))
        {
            return ProviderResult<List<ProviderInvoice>>.Fail(
                "GİB e-Arşiv Portalı kimlik bilgileri tanımlı değil. Mükellef kartından " +
                "kullanıcı kodu ve şifresini kaydedin.");
        }

        var loginResult = await _client.LoginAsync(username, password, testEnv, ct);
        if (!loginResult.Success || loginResult.Data is null)
            return ProviderResult<List<ProviderInvoice>>.Fail(loginResult.ErrorMessage!, loginResult.Retryable);

        var token = loginResult.Data;
        try
        {
            var cmd = incoming
                ? "EARSIV_PORTAL_ADIMA_KESILEN_BELGELERI_GETIR"
                : "EARSIV_PORTAL_TASLAKLARI_GETIR";
            var pageName = incoming ? "RG_ALICI_TASLAKLAR" : "RG_TASLAKLAR";

            var payload = new Dictionary<string, string>
            {
                ["baslangic"] = EArsivPortalClient.FormatDate(from.Date),
                ["bitis"] = EArsivPortalClient.FormatDate(to.Date),
                ["hangiTip"] = AllDocumentTypes,
            };

            var listResult = await _client.DispatchAsync(token, cmd, pageName, payload, testEnv, ct);
            if (!listResult.Success)
                return ProviderResult<List<ProviderInvoice>>.Fail(listResult.ErrorMessage!, listResult.Retryable);

            var rows = listResult.Data;
            if (rows.ValueKind != JsonValueKind.Array)
                return ProviderResult<List<ProviderInvoice>>.Ok(new List<ProviderInvoice>());

            var invoices = new List<ProviderInvoice>();
            var detailFetched = 0;
            var detailFailed = 0;

            foreach (var row in rows.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                var invoice = MapListRow(row, incoming);
                if (invoice is null) continue;

                // Liste ucu tutar/kalem vermez → belge detayı çekilir (sınırlı sayıda).
                if (detailFetched < MaxDetailFetch && !string.IsNullOrWhiteSpace(invoice.ExternalUuid))
                {
                    detailFetched++;
                    var enriched = await TryEnrichWithDetailAsync(token, invoice, testEnv, ct);
                    if (!enriched) detailFailed++;
                }

                if (invoice.Lines.Count == 0) AddSummaryLine(invoice);
                invoices.Add(invoice);
            }

            string? warning = null;
            if (invoices.Count > MaxDetailFetch)
            {
                warning = $"{invoices.Count} belgeden ilk {MaxDetailFetch} tanesinin kalem detayı alındı; " +
                          "kalanlar özet satır olarak eklendi. Tarih aralığını daraltarak tekrar çekebilirsiniz.";
            }
            else if (detailFailed > 0)
            {
                warning = $"{detailFailed} belgenin kalem detayı alınamadı; bu belgeler özet satırla eklendi.";
            }

            return ProviderResult<List<ProviderInvoice>>.Ok(invoices, warning);
        }
        finally
        {
            await _client.LogoutAsync(token, testEnv, ct);
        }
    }

    // ---------- Eşleme ----------

    /// <summary>
    /// Liste satırını ProviderInvoice'a eşler. Portal alan adlarını haber vermeden
    /// değiştirebildiği için her alan için birden çok aday isim denenir.
    /// </summary>
    private ProviderInvoice? MapListRow(JsonElement row, bool incoming)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;

        var ettn = ReadString(row, "ettn", "belgeUuid", "uuid");
        var number = ReadString(row, "belgeNumarasi", "belgeNo", "faturaNo");

        // Kimliksiz satır mükerrer kontrolüne giremez — atlanır.
        if (string.IsNullOrWhiteSpace(ettn) && string.IsNullOrWhiteSpace(number)) return null;

        var issueDate = EArsivPortalClient.ParseDate(
            ReadString(row, "belgeTarihi", "faturaTarihi", "tarih")) ?? DateTime.UtcNow.Date;

        // Gelen yönde KARŞI TARAF satıcıdır, giden yönde alıcıdır. Portal her iki listede de
        // karşı tarafı "alici*" alanlarıyla verebildiği için satıcı adayları önce denenir.
        var counterpartyTaxId = incoming
            ? ReadString(row, "saticiVknTckn", "saticiVkn", "vknTckn", "aliciVknTckn")
            : ReadString(row, "aliciVknTckn", "vknTckn");

        var counterpartyTitle = incoming
            ? ReadString(row, "saticiUnvanAdSoyad", "saticiUnvan", "unvan", "aliciUnvanAdSoyad")
            : ReadString(row, "aliciUnvanAdSoyad", "aliciUnvan", "unvan");

        return new ProviderInvoice
        {
            ExternalUuid = string.IsNullOrWhiteSpace(ettn) ? number! : ettn,
            InvoiceNumber = number ?? string.Empty,
            IssueDate = issueDate,
            SenderTaxId = counterpartyTaxId ?? string.Empty,
            SenderTitle = counterpartyTitle ?? string.Empty,
            CurrencyCode = "TRY",
        };
    }

    /// <summary>
    /// Belge detayını çekip tutarları ve kalemleri doldurur. Başarısızlıkta false döner —
    /// çekim DURMAZ, belge özet satırla eklenir.
    /// </summary>
    private async Task<bool> TryEnrichWithDetailAsync(
        string token, ProviderInvoice invoice, bool testEnv, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, string> { ["ettn"] = invoice.ExternalUuid };
            var result = await _client.DispatchAsync(
                token, "EARSIV_PORTAL_FATURA_GETIR", "RG_TASLAKLAR", payload, testEnv, ct);

            if (!result.Success || result.Data.ValueKind != JsonValueKind.Object) return false;

            var detail = result.Data;

            invoice.TaxExclusiveAmount = ReadAmount(detail, "matrah", "malHizmetToplamTutari", "toplamMatrah");
            invoice.TaxTotal = ReadAmount(detail, "hesaplananKdv", "toplamKdv", "kdvToplam");
            invoice.PayableAmount = ReadAmount(detail, "vergilerDahilToplamTutar", "odenecekTutar", "genelToplam");

            var currency = ReadString(detail, "paraBirimi", "doviz");
            if (!string.IsNullOrWhiteSpace(currency)) invoice.CurrencyCode = currency.Trim().ToUpperInvariant();

            if (detail.TryGetProperty("malHizmetTable", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var lineNumber = 1;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    invoice.Lines.Add(new ProviderInvoiceLine
                    {
                        LineNumber = lineNumber++,
                        ItemName = ReadString(item, "malHizmet", "aciklama") ?? "Belirtilmemiş",
                        Quantity = ReadAmount(item, "miktar"),
                        UnitCode = MapUnit(ReadString(item, "birim")),
                        UnitPrice = ReadAmount(item, "birimFiyat"),
                        LineAmount = ReadAmount(item, "malHizmetTutari", "tutar"),
                        VatRate = ReadAmount(item, "kdvOrani"),
                        VatAmount = ReadAmount(item, "kdvTutari"),
                    });
                }
            }

            // Toplam gelmediyse kalemlerden türet (portal bazı belgelerde toplamı boş bırakır).
            if (invoice.TaxExclusiveAmount == 0 && invoice.Lines.Count > 0)
                invoice.TaxExclusiveAmount = invoice.Lines.Sum(l => l.LineAmount);
            if (invoice.TaxTotal == 0 && invoice.Lines.Count > 0)
                invoice.TaxTotal = invoice.Lines.Sum(l => l.VatAmount);
            if (invoice.PayableAmount == 0)
                invoice.PayableAmount = invoice.TaxExclusiveAmount + invoice.TaxTotal;

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tek belgenin detay hatası tüm çekimi düşürmez.
            _logger.LogDebug(
                "e-Arşiv belge detayı alınamadı (özet satıra düşüldü). ETTN: {Ettn}, Hata: {Message}",
                invoice.ExternalUuid, ex.Message);
            return false;
        }
    }

    /// <summary>Detay alınamayan belge için tek özet satır üretir (tutarlar sıfır olabilir).</summary>
    private static void AddSummaryLine(ProviderInvoice invoice)
    {
        var vatRate = invoice.TaxExclusiveAmount > 0
            ? Math.Round(invoice.TaxTotal / invoice.TaxExclusiveAmount * 100m, 0)
            : 0m;

        invoice.Lines.Add(new ProviderInvoiceLine
        {
            LineNumber = 1,
            ItemName = "e-Arşiv belgesi (kalem detayı alınamadı)",
            Quantity = 1,
            UnitCode = "C62",
            UnitPrice = invoice.TaxExclusiveAmount,
            LineAmount = invoice.TaxExclusiveAmount,
            VatRate = vatRate,
            VatAmount = invoice.TaxTotal,
        });
    }

    /// <summary>Portalın Türkçe birim adını UN/ECE koduna çevirir; bilinmeyen birim → C62 (adet).</summary>
    private static string MapUnit(string? unit) => (unit?.Trim().ToLowerInvariant()) switch
    {
        "adet" or "ad" => "C62",
        "kg" or "kilogram" => "KGM",
        "gram" or "gr" => "GRM",
        "ton" => "TNE",
        "litre" or "lt" => "LTR",
        "metre" or "m" => "MTR",
        "metrekare" or "m2" => "MTK",
        "metreküp" or "m3" => "MTQ",
        "saat" => "HUR",
        "gün" => "DAY",
        "ay" => "MON",
        "paket" => "PK",
        "kutu" => "BX",
        _ => "C62",
    };

    // ---------- JSON okuma yardımcıları ----------

    /// <summary>Verilen aday isimlerden ilk dolu olanı string döner.</summary>
    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    /// <summary>
    /// Verilen aday isimlerden ilk dolu olanı decimal döner. Portal tutarları hem sayı hem
    /// "1.234,56" biçiminde metin olarak döndürebilir; ikisi de desteklenir.
    /// </summary>
    private static decimal ReadAmount(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String)
            {
                var parsed = EArsivPortalClient.ParseAmount(value.GetString());
                if (parsed != 0m) return parsed;
            }
        }
        return 0m;
    }
}
