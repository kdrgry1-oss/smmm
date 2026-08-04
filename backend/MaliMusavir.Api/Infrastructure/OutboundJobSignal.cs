namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Faz 16 (A2 — OCR hızı): kuyruk işçisini UYANDIRMA sinyali. Yeni iş kuyruğa alındığında
/// (EnqueueAsync / RetryNowAsync) Notify çağrılır; OutboundJobWorker tarama aralığı beklemesini
/// bu sinyalle keser ve işi HEMEN alır — fiş yükleme → OCR başlangıcı arasındaki 15 sn'ye
/// varan bekleme ortadan kalkar.
///
/// SemaphoreSlim(0, 1) kullanılır: birden fazla Notify tek "uyan" sinyaline katlanır
/// (SemaphoreFullException yutulur — sinyal zaten beklemede, kayıp yok, yarış yok).
/// Singleton olarak kaydedilir; hem HTTP scope'ları hem worker aynı örneği paylaşır.
/// </summary>
public class OutboundJobSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Yeni iş var — worker beklemedeyse hemen uyanır (çoklu çağrı tek sinyale katlanır).</summary>
    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Sinyal zaten set — worker ilk uyanışında tüm bekleyen işleri zaten tarar.
        }
    }

    /// <summary>
    /// Sinyal gelene ya da timeout dolana kadar bekler. true = sinyalle uyandı, false = süre doldu
    /// (her iki durumda da worker olağan taramasını yapar; dönüş değeri yalnız teşhis içindir).
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
        => _signal.WaitAsync(timeout, ct);
}
