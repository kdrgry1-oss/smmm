using System.Globalization;
using System.Text.RegularExpressions;
using MaliMusavir.Api.Contracts;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Controllers;

[ApiController]
[Route("api/clients")]
[Authorize]
public partial class ClientsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ClientsController(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    [GeneratedRegex(@"^\d{10}(\d)?$")]
    private static partial Regex TaxIdRegex();

    /// <summary>Mükellef listesi. q: Unvan/VKN araması. include=archived: arşivlenmişler de dahil.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ClientResponse>>> List(
        [FromQuery] string? q = null,
        [FromQuery] string? include = null)
    {
        var query = _db.Clients.AsNoTracking().AsQueryable();

        var includeArchived = string.Equals(include, "archived", StringComparison.OrdinalIgnoreCase);
        if (!includeArchived)
            query = query.Where(c => !c.IsArchived);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(c =>
                c.Title.ToLower().Contains(term) ||
                c.TaxId.Contains(term));
        }

        var clients = await query
            .OrderBy(c => c.Title)
            .ToListAsync();

        return Ok(clients.Select(ClientResponse.FromEntity).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ClientResponse>> Get(Guid id)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        return Ok(ClientResponse.FromEntity(client));
    }

    [HttpPost]
    public async Task<ActionResult<ClientResponse>> Create([FromBody] ClientCreateUpdateRequest request)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var taxId = request.TaxId.Trim();
        if (!TaxIdRegex().IsMatch(taxId))
            return BadRequest(new { message = "Vergi/TC kimlik numarası 10 veya 11 haneli olmalıdır." });

        // Plan kotası kontrolü (arşivlenmemiş mükellefler sayılır).
        var tenant = await _db.Tenants
            .Include(t => t.Plan)
            .FirstOrDefaultAsync(t => t.Id == _currentUser.TenantId.Value);

        if (tenant is null)
            return Forbid();

        var maxClients = tenant.Plan?.MaxClients ?? 10;
        var activeCount = await _db.Clients.CountAsync(c => !c.IsArchived);
        if (activeCount >= maxClients)
        {
            // Kota uyarısında kullanılan/limit bilgisi gösterilir (Faz 5 — ör. "10/10").
            return StatusCode(StatusCodes.Status403Forbidden,
                new
                {
                    message = $"{activeCount}/{maxClients} mükellef kullanımdasınız. " +
                              "Paketinizdeki mükellef sınırına ulaştınız. " +
                              "Yeni mükellef eklemek için Ayarlar → Abonelik'ten paketinizi yükseltin."
                });
        }

        // Aynı tenant'ta aynı VKN/TCKN kontrolü (query filter zaten tenant'a daraltır).
        var duplicate = await _db.Clients.AnyAsync(c => c.TaxId == taxId);
        if (duplicate)
            return Conflict(new { message = "Bu vergi numarasıyla kayıtlı mükellef zaten var." });

        var client = new Client
        {
            TenantId = _currentUser.TenantId.Value,
            Title = request.Title.Trim(),
            TaxId = taxId,
            TaxOffice = NullIfEmpty(request.TaxOffice),
            Email = NullIfEmpty(request.Email),
            Phone = NullIfEmpty(request.Phone),
            Address = NullIfEmpty(request.Address),
            EInvoiceEnabled = request.EInvoiceEnabled,
            EArchiveEnabled = request.EArchiveEnabled,
            IntegratorName = NullIfEmpty(request.IntegratorName),
            IsArchived = false
        };

        _db.Clients.Add(client);

        // Faz 2: kodlama ayarları mükellefle birlikte otomatik oluşturulur
        // (varsayılan gider hesabı 770.99, cari şeması 320.xxx, eşik 0.85).
        _db.ClientCodingSettings.Add(new ClientCodingSettings { ClientId = client.Id });

        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = client.Id }, ClientResponse.FromEntity(client));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ClientResponse>> Update(Guid id, [FromBody] ClientCreateUpdateRequest request)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var taxId = request.TaxId.Trim();
        if (!TaxIdRegex().IsMatch(taxId))
            return BadRequest(new { message = "Vergi/TC kimlik numarası 10 veya 11 haneli olmalıdır." });

        var duplicate = await _db.Clients.AnyAsync(c => c.TaxId == taxId && c.Id != id);
        if (duplicate)
            return Conflict(new { message = "Bu vergi numarasıyla kayıtlı mükellef zaten var." });

        client.Title = request.Title.Trim();
        client.TaxId = taxId;
        client.TaxOffice = NullIfEmpty(request.TaxOffice);
        client.Email = NullIfEmpty(request.Email);
        client.Phone = NullIfEmpty(request.Phone);
        client.Address = NullIfEmpty(request.Address);
        client.EInvoiceEnabled = request.EInvoiceEnabled;
        client.EArchiveEnabled = request.EArchiveEnabled;
        client.IntegratorName = NullIfEmpty(request.IntegratorName);

        await _db.SaveChangesAsync();
        return Ok(ClientResponse.FromEntity(client));
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<ActionResult<ClientResponse>> Archive(Guid id)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        client.IsArchived = true;
        await _db.SaveChangesAsync();
        return Ok(ClientResponse.FromEntity(client));
    }

    [HttpPost("{id:guid}/unarchive")]
    public async Task<ActionResult<ClientResponse>> Unarchive(Guid id)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        client.IsArchived = false;
        await _db.SaveChangesAsync();
        return Ok(ClientResponse.FromEntity(client));
    }

    // ---------- Faz 13: Mükellef araçları (plaka kayıtları) ----------

    /// <summary>Mükellefin kayıtlı araçları (plaka listesi).</summary>
    [HttpGet("{id:guid}/vehicles")]
    public async Task<ActionResult<List<ClientVehicleResponse>>> ListVehicles(Guid id)
    {
        var clientExists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == id);
        if (!clientExists)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var vehicles = await _db.ClientVehicles
            .AsNoTracking()
            .Where(v => v.ClientId == id)
            .OrderBy(v => v.Plate)
            .ToListAsync();

        return Ok(vehicles.Select(ClientVehicleResponse.FromEntity).ToList());
    }

    /// <summary>
    /// Araç ekler. Plaka sunucuda normalize edilir (büyük harf, boşluksuz);
    /// biçim geçersizse 400, aynı plaka zaten kayıtlıysa 409 döner.
    /// </summary>
    [HttpPost("{id:guid}/vehicles")]
    public async Task<ActionResult<ClientVehicleResponse>> CreateVehicle(
        Guid id, [FromBody] ClientVehicleCreateRequest request)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (client is null)
            return NotFound(new { message = "Mükellef bulunamadı." });

        var plate = PlateNormalizer.Normalize(request.Plate);
        if (!PlateNormalizer.IsValid(plate))
            return BadRequest(new { message = "Geçerli bir Türk plakası girin (ör. 34ABC123)." });

        var duplicate = await _db.ClientVehicles.AnyAsync(v => v.ClientId == id && v.Plate == plate);
        if (duplicate)
            return Conflict(new { message = "Bu plaka zaten kayıtlı." });

        var vehicle = new ClientVehicle
        {
            TenantId = client.TenantId,
            ClientId = client.Id,
            Plate = plate,
            IsPassengerCar = request.IsPassengerCar,
            Description = NullIfEmpty(request.Description)
        };

        _db.ClientVehicles.Add(vehicle);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(ListVehicles), new { id }, ClientVehicleResponse.FromEntity(vehicle));
    }

    /// <summary>Araç kaydını siler (mevcut fişlerdeki eşleşme sonuçları geriye dönük değişmez).</summary>
    [HttpDelete("{id:guid}/vehicles/{vehicleId:guid}")]
    public async Task<IActionResult> DeleteVehicle(Guid id, Guid vehicleId)
    {
        var vehicle = await _db.ClientVehicles
            .FirstOrDefaultAsync(v => v.Id == vehicleId && v.ClientId == id);
        if (vehicle is null)
            return NotFound(new { message = "Araç kaydı bulunamadı." });

        _db.ClientVehicles.Remove(vehicle);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------- Toplu içe aktarma: mükellef listesi + araç plakaları ----------

    /// <summary>Excel içe aktarma dosya sınırları (CodingController şablon kalıbıyla aynı).</summary>
    private const long MaxImportFileBytes = 2 * 1024 * 1024; // 2 MB
    private const int MaxImportRows = 1000;

    /// <summary>Türkçe büyük/küçük harf dönüşümleri (İ/i, I/ı) için sabit kültür.</summary>
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Metni en fazla verilen uzunlukta keser (kolon sınırları için).</summary>
    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Ortak .xlsx doğrulama + belleğe okuma (mükellef listesi ve araç plakası uçları):
    /// 2 MB / yalnızca .xlsx / en fazla 1000 veri satırı (+1 başlık).
    /// </summary>
    private static async Task<(List<List<string>>? Rows, ActionResult? Error)> ReadImportXlsxAsync(
        IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return (null, new BadRequestObjectResult(new { message = "Bir .xlsx dosyası seçin." }));
        if (file.Length > MaxImportFileBytes)
            return (null, new BadRequestObjectResult(new { message = "Dosya boyutu en fazla 2 MB olabilir." }));
        if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return (null, new BadRequestObjectResult(new { message = "Yalnızca .xlsx dosyaları içe aktarılabilir. Önce şablonu indirin." }));

        try
        {
            // ZipArchive seekable + senkron okuma ister; 2 MB sınırı belleğe almayı güvenli kılar.
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return (MinimalXlsxReader.ReadFirstSheet(buffer, MaxImportRows + 1), null); // +1 başlık satırı
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return (null, new BadRequestObjectResult(new { message = "Dosya okunamadı. Geçerli bir .xlsx dosyası yükleyin (şablonu kullanın)." }));
        }
    }

    /// <summary>
    /// MÜKELLEF LİSTESİ içe aktarma ŞABLONU (.xlsx): kolonlar Client alanlarına birebir karşılık
    /// gelir (Unvan, VKN/TCKN zorunlu; Vergi Dairesi, E-posta, Telefon, Adres opsiyonel).
    /// 2 örnek satır içerir (içe aktarmadan önce silinmeli). Mükellef seçimi GEREKTİRMEZ.
    /// </summary>
    [HttpGet("import-template")]
    public IActionResult DownloadImportTemplate()
    {
        var headers = new[] { "Unvan*", "VKN/TCKN*", "Vergi Dairesi", "E-posta", "Telefon", "Adres" };
        var rows = new List<IReadOnlyList<MinimalXlsxWriter.Cell>>
        {
            new[]
            {
                MinimalXlsxWriter.Cell.Text("ÖRNEK TİCARET LTD. ŞTİ."),
                MinimalXlsxWriter.Cell.Text("6120034142"),
                MinimalXlsxWriter.Cell.Text("Kadıköy VD"),
                MinimalXlsxWriter.Cell.Text("muhasebe@ornekticaret.com"),
                MinimalXlsxWriter.Cell.Text("0532 000 00 00"),
                MinimalXlsxWriter.Cell.Text("Örnek Mah. Deneme Sok. No:1 Kadıköy/İstanbul"),
            },
            new[]
            {
                MinimalXlsxWriter.Cell.Text("AHMET YILMAZ"),
                MinimalXlsxWriter.Cell.Text("12345678901"),
                MinimalXlsxWriter.Cell.Text("Çankaya VD"),
                MinimalXlsxWriter.Cell.Empty,
                MinimalXlsxWriter.Cell.Empty,
                MinimalXlsxWriter.Cell.Empty,
            },
        };

        var bytes = MinimalXlsxWriter.Build("Mukellef Listesi", headers, rows);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "mukellef_listesi_sablon.xlsx");
    }

    /// <summary>
    /// MÜKELLEF LİSTESİ içe aktarma: satır satır doğrulanır (Unvan zorunlu, VKN/TCKN 10-11 hane),
    /// VKN/TCKN üzerinden upsert edilir — VAR OLAN mükellefe DOKUNULMAZ (skipped sayılır), yeni
    /// olan plan kotası dahilinde eklenir (kodlama ayarları Create ile aynı şekilde açılır).
    /// Hatalı satır Türkçe nedenle raporlanır ve işlem kalan satırlarla DEVAM eder.
    /// Sınırlar: 2 MB, yalnızca .xlsx, en fazla 1000 satır. (TenantId, TaxId) unique index +
    /// global tenant filtresi izolasyonu sağlar.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<ClientImportResultResponse>> ImportClients(
        IFormFile? file, CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();
        var tenantId = _currentUser.TenantId.Value;

        var (rows, error) = await ReadImportXlsxAsync(file, ct);
        if (error is not null) return error;

        // Plan kotası (Create ile aynı kural): aktif mükellef sayısı sınırı aşamaz.
        var tenant = await _db.Tenants
            .Include(t => t.Plan)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return Forbid();
        var maxClients = tenant.Plan?.MaxClients ?? 10;
        var activeCount = await _db.Clients.CountAsync(c => !c.IsArchived, ct);

        // Mevcut mükellefler tek sorguda (arşivlenmişler dahil — VKN çakışması onları da kapsar).
        var existingByTaxId = (await _db.Clients.AsNoTracking().ToListAsync(ct))
            .GroupBy(c => c.TaxId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);

        var result = new ClientImportResultResponse();

        // 1. satır başlıktır; veri 2. satırdan başlar.
        for (var i = 1; i < rows!.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var rowNumber = i + 1;

            string CellAt(int col) => col < row.Count ? row[col].Trim() : string.Empty;

            var title = CellAt(0);
            var taxId = CellAt(1);
            var taxOffice = CellAt(2);
            var email = CellAt(3);
            var phone = CellAt(4);
            var address = CellAt(5);

            // Tamamen boş satır sessizce geçilir (Excel'in bıraktığı kuyruk satırları).
            if (title.Length == 0 && taxId.Length == 0 && taxOffice.Length == 0 &&
                email.Length == 0 && phone.Length == 0 && address.Length == 0)
            {
                continue;
            }

            // Satır doğrulamaları: hata kaydedilir, satır atlanır, işlem devam eder.
            if (title.Length == 0)
            {
                result.Errors.Add($"Satır {rowNumber}: Unvan zorunludur.");
                continue;
            }
            if (!TaxIdRegex().IsMatch(taxId))
            {
                result.Errors.Add($"Satır {rowNumber}: VKN/TCKN 10 veya 11 haneli rakam olmalıdır (\"{taxId}\").");
                continue;
            }
            if (email.Length > 0 && !email.Contains('@'))
            {
                result.Errors.Add($"Satır {rowNumber}: Geçerli bir e-posta adresi girin (\"{email}\").");
                continue;
            }

            // VAR OLAN mükellefe dokunulmaz (upsert'in "var olanı atla" yarısı).
            if (existingByTaxId.ContainsKey(taxId))
            {
                result.Skipped++;
                continue;
            }
            if (!seenInFile.Add(taxId))
            {
                result.Errors.Add($"Satır {rowNumber}: Bu VKN/TCKN dosyada daha önce geçti (\"{taxId}\").");
                continue;
            }

            // Plan kotası: sınıra ulaşıldıysa kalan yeni satırlar aktarılmaz (Türkçe tek hata).
            if (activeCount + result.Imported >= maxClients)
            {
                result.Errors.Add(
                    $"Satır {rowNumber}: Paketinizdeki mükellef sınırına ({maxClients}) ulaşıldı — kalan satırlar aktarılmadı. " +
                    "Ayarlar → Abonelik'ten paketinizi yükseltebilirsiniz.");
                break;
            }

            var client = new Client
            {
                TenantId = tenantId,
                Title = Truncate(title, 300),
                TaxId = taxId,
                TaxOffice = NullIfEmpty(Truncate(taxOffice, 150)),
                Email = NullIfEmpty(Truncate(email, 320)),
                Phone = NullIfEmpty(Truncate(phone, 30)),
                Address = NullIfEmpty(Truncate(address, 500)),
                IsArchived = false
            };
            _db.Clients.Add(client);

            // Create ucundaki kalıp: kodlama ayarları mükellefle birlikte otomatik açılır.
            _db.ClientCodingSettings.Add(new ClientCodingSettings { ClientId = client.Id });

            result.Imported++;
        }

        await _db.SaveChangesAsync(ct);

        var parts = new List<string> { $"{result.Imported} mükellef eklendi" };
        if (result.Skipped > 0) parts.Add($"{result.Skipped} satır atlandı (VKN zaten kayıtlı)");
        if (result.Errors.Count > 0) parts.Add($"{result.Errors.Count} satırda hata");
        result.Message = string.Join(", ", parts) + ".";

        return Ok(result);
    }

    /// <summary>
    /// ARAÇ PLAKASI içe aktarma ŞABLONU (.xlsx): "Mükellef VKN*" kolonu satırı mükellefe
    /// bağlar (mükellef seçimi gerektirmez), "Araç Tipi" BINEK/TICARI değerlerinden biridir.
    /// 2 örnek satır içerir (içe aktarmadan önce silinmeli).
    /// </summary>
    [HttpGet("/api/vehicles/import-template")]
    public IActionResult DownloadVehicleImportTemplate()
    {
        var headers = new[] { "Mükellef VKN*", "Plaka*", "Araç Tipi (BINEK/TICARI)*", "Açıklama (ops.)" };
        var rows = new List<IReadOnlyList<MinimalXlsxWriter.Cell>>
        {
            new[]
            {
                MinimalXlsxWriter.Cell.Text("6120034142"),
                MinimalXlsxWriter.Cell.Text("34ABC123"),
                MinimalXlsxWriter.Cell.Text("BINEK"),
                MinimalXlsxWriter.Cell.Text("Beyaz Passat"),
            },
            new[]
            {
                MinimalXlsxWriter.Cell.Text("6120034142"),
                MinimalXlsxWriter.Cell.Text("06 DEF 456"),
                MinimalXlsxWriter.Cell.Text("TICARI"),
                MinimalXlsxWriter.Cell.Empty,
            },
        };

        var bytes = MinimalXlsxWriter.Build("Arac Plakalari", headers, rows);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "arac_plakalari_sablon.xlsx");
    }

    /// <summary>
    /// ARAÇ PLAKASI içe aktarma: her satırda mükellef VKN'den bulunur (bulunamayan satır hata),
    /// plaka PlateNormalizer ile normalize edilip doğrulanır, tip BINEK → binek (KKEG %30),
    /// TICARI → ticari (tam gider) olarak eşlenir. Aynı mükellefte kayıtlı plaka atlanır
    /// (skipped). Hatalı satır Türkçe nedenle raporlanır ve işlem kalan satırlarla DEVAM eder.
    /// Sınırlar: 2 MB, yalnızca .xlsx, en fazla 1000 satır.
    /// </summary>
    [HttpPost("/api/vehicles/import")]
    public async Task<ActionResult<VehicleImportResultResponse>> ImportVehicles(
        IFormFile? file, CancellationToken ct = default)
    {
        if (_currentUser.TenantId is null)
            return Forbid();

        var (rows, error) = await ReadImportXlsxAsync(file, ct);
        if (error is not null) return error;

        // Mükellefler ve mevcut plakalar tek sorguda (satır başına sorgu yok) — mevcut import kalıbı.
        var clientsByTaxId = (await _db.Clients.AsNoTracking().ToListAsync(ct))
            .GroupBy(c => c.TaxId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var existingPlates = (await _db.ClientVehicles
                .AsNoTracking()
                .Select(v => new { v.ClientId, v.Plate })
                .ToListAsync(ct))
            .Select(v => (v.ClientId, v.Plate))
            .ToHashSet();

        var result = new VehicleImportResultResponse();

        // 1. satır başlıktır; veri 2. satırdan başlar.
        for (var i = 1; i < rows!.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var rowNumber = i + 1;

            string CellAt(int col) => col < row.Count ? row[col].Trim() : string.Empty;

            var taxId = CellAt(0);
            var plateRaw = CellAt(1);
            var typeRaw = CellAt(2);
            var description = CellAt(3);

            // Tamamen boş satır sessizce geçilir (Excel'in bıraktığı kuyruk satırları).
            if (taxId.Length == 0 && plateRaw.Length == 0 && typeRaw.Length == 0 && description.Length == 0)
                continue;

            if (!TaxIdRegex().IsMatch(taxId))
            {
                result.Errors.Add($"Satır {rowNumber}: Mükellef VKN 10 veya 11 haneli rakam olmalıdır (\"{taxId}\").");
                continue;
            }
            if (!clientsByTaxId.TryGetValue(taxId, out var client))
            {
                result.Errors.Add($"Satır {rowNumber}: \"{taxId}\" VKN'li mükellef bulunamadı. Önce mükellefi ekleyin.");
                continue;
            }

            // Mevcut normalize kalıbı: tekil araç ekleme ucuyla birebir aynı kurallar.
            var plate = PlateNormalizer.Normalize(plateRaw);
            if (!PlateNormalizer.IsValid(plate))
            {
                result.Errors.Add($"Satır {rowNumber}: Geçersiz plaka (\"{plateRaw}\"). Örnek biçim: 34ABC123.");
                continue;
            }

            // Araç tipi: BINEK → binek (KKEG %30), TICARI → ticari (tam gider); Türkçe İ toleranslı.
            var typeUpper = typeRaw.ToUpper(TurkishCulture).Replace("İ", "I");
            bool isPassengerCar;
            if (typeUpper == "BINEK") isPassengerCar = true;
            else if (typeUpper == "TICARI") isPassengerCar = false;
            else
            {
                result.Errors.Add($"Satır {rowNumber}: Araç tipi BINEK veya TICARI olmalıdır (\"{typeRaw}\").");
                continue;
            }

            if (!existingPlates.Add((client.Id, plate)))
            {
                result.Skipped++; // aynı mükellefte plaka zaten kayıtlı (dosya içi tekrar dahil)
                continue;
            }

            _db.ClientVehicles.Add(new ClientVehicle
            {
                TenantId = client.TenantId,
                ClientId = client.Id,
                Plate = plate,
                IsPassengerCar = isPassengerCar,
                Description = NullIfEmpty(Truncate(description, 200))
            });
            result.Imported++;
        }

        await _db.SaveChangesAsync(ct);

        var parts = new List<string> { $"{result.Imported} araç eklendi" };
        if (result.Skipped > 0) parts.Add($"{result.Skipped} satır atlandı (plaka zaten kayıtlı)");
        if (result.Errors.Count > 0) parts.Add($"{result.Errors.Count} satırda hata");
        result.Message = string.Join(", ", parts) + ".";

        return Ok(result);
    }
}
