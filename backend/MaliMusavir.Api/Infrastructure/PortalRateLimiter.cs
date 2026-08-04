using System.Collections.Concurrent;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Portal PIN girişi için bellek içi basit kaba kuvvet koruması (Faz 7 — E1):
/// aynı token'a 5 yanlış PIN denemesi → 15 dakika kilit. Singleton kaydedilir;
/// süreç yeniden başlayınca sayaçlar sıfırlanır (tek instance MVP için yeterli —
/// çok instance dağıtımda Redis benzeri paylaşımlı sayaç gerekir, bkz. flow spec sınırlamaları).
/// </summary>
public class PortalRateLimiter
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public int FailedCount;
        public DateTime? LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Token şu anda kilitli mi? Kilitliyse kalan süre (dakika, yukarı yuvarlı) döner.</summary>
    public bool IsLocked(string token, out int remainingMinutes)
    {
        remainingMinutes = 0;
        if (!_entries.TryGetValue(token, out var entry) || entry.LockedUntil is null) return false;

        var remaining = entry.LockedUntil.Value - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            // Kilit süresi doldu: sayaç temizlenir, deneme hakkı yeniden açılır.
            _entries.TryRemove(token, out _);
            return false;
        }

        remainingMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
        return true;
    }

    /// <summary>Yanlış PIN kaydeder; 5. denemede 15 dk kilitler. Kilitlendiyse true döner.</summary>
    public bool RegisterFailure(string token)
    {
        var entry = _entries.GetOrAdd(token, _ => new Entry());
        lock (entry)
        {
            entry.FailedCount++;
            if (entry.FailedCount >= MaxAttempts)
            {
                entry.LockedUntil = DateTime.UtcNow.Add(LockDuration);
                entry.FailedCount = 0;
                return true;
            }
        }
        return false;
    }

    /// <summary>Başarılı giriş: sayaç sıfırlanır.</summary>
    public void RegisterSuccess(string token) => _entries.TryRemove(token, out _);
}
