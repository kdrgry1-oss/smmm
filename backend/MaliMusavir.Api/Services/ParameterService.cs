using System.Collections.Concurrent;
using System.Globalization;
using MaliMusavir.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Services;

/// <summary>
/// Parametre merkezi okuma servisi (Faz 7 — E4). Değerler süreç genelinde
/// static ConcurrentDictionary'de tutulur (IMemoryCache yerine bilinçli sade tercih);
/// ilk okuyan istek tabloyu tek sorguda yükler, admin güncellemesi Invalidate() ile
/// önbelleği boşaltır. Anahtar yoksa çağıranın verdiği varsayılan döner — böylece
/// parametre tablosu boş olsa bile davranış eski sabitlerle birebir aynı kalır.
/// </summary>
public class ParameterService
{
    // Süreç geneli önbellek: tüm scoped instance'lar aynı sözlüğü paylaşır.
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static volatile bool _loaded;
    private static readonly SemaphoreSlim LoadLock = new(1, 1);

    private readonly AppDbContext _db;

    public ParameterService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Önbelleği doldurur (ilk çağrıda tek sorgu; sonrası ücretsiz).</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;

        await LoadLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;

            // TaxParameter global tablodur (tenant filtresi yok) — doğrudan okunur.
            var parameters = await _db.TaxParameters.AsNoTracking().ToListAsync(ct);
            Cache.Clear();
            foreach (var parameter in parameters)
                Cache[parameter.Key] = parameter.Value;

            _loaded = true;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    /// <summary>Parametre değeri (string). Anahtar yoksa fallback döner. Önce EnsureLoadedAsync çağrılmalıdır.</summary>
    public string Get(string key, string fallback)
        => Cache.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>Sayısal parametre (nokta veya virgül ondalık kabul edilir). Çözülemezse fallback.</summary>
    public decimal GetDecimal(string key, decimal fallback)
    {
        if (!Cache.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;

        var normalized = raw.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    /// <summary>Önbelleği geçersiz kılar — admin parametre güncellemesinden sonra çağrılır.</summary>
    public static void Invalidate()
    {
        _loaded = false;
        Cache.Clear();
    }
}
