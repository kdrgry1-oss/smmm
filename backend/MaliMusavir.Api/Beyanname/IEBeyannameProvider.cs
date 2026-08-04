using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Beyanname;

/// <summary>Gönderim sonucu: GİB paket referans numarası.</summary>
public class GibSendResult
{
    public string PackageRef { get; init; } = string.Empty;
}

/// <summary>Sağlayıcı tarafındaki beyanname işleme durumu.</summary>
public enum GibDeclarationState
{
    /// <summary>GİB hâlâ işliyor — daha sonra tekrar sorgulanmalı.</summary>
    Pending = 0,
    /// <summary>Onaylandı; tahakkuk kesildi.</summary>
    Approved = 1,
    /// <summary>Reddedildi (ör. şema hatası) — düzeltilip yeniden gönderilebilir.</summary>
    Error = 2
}

/// <summary>Durum sorgusu sonucu (tahakkuk tutarı yalnızca Approved'da dolu).</summary>
public class GibStatusResult
{
    public GibDeclarationState State { get; init; }
    /// <summary>Tahakkuk tutarı (Onaylandı; ödenecek KDV çıkmadıysa 0).</summary>
    public decimal? TahakkukAmount { get; init; }
    /// <summary>Hata durumunda kullanıcıya gösterilebilir Türkçe açıklama.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// GİB e-Beyanname web servisi adaptör arayüzü (blueprint M8/B9). Gerçek servis, SMMM'nin
/// e-Beyanname kullanıcı kodu/şifresi + GİB yeni beyanname sistemi token'ı ile çalışır —
/// kullanıcı kimlik bilgilerini iletince bu arayüzü uygulayan tek sınıf (GibProvider)
/// eklenecek; MockGibProvider başvuru beklenmeden uçtan uca akışı çalıştırır.
/// </summary>
public interface IEBeyannameProvider
{
    /// <summary>Sağlayıcının görünen adı (ör. "MockGib", "GibEBeyanname").</summary>
    string Name { get; }

    /// <summary>Hazırlanan beyanname paketini (BDP uyumlu) GİB'e gönderir, paket no döner.</summary>
    Task<ProviderResult<GibSendResult>> SendDeclarationAsync(Declaration declaration, CancellationToken ct);

    /// <summary>
    /// Paket durumunu / tahakkuku sorgular. Gerçek serviste yalnızca PackageRef kullanılır;
    /// mock, tahakkuk tutarını hesaplayabilmek için beyanname kaydının tamamını alır.
    /// </summary>
    Task<ProviderResult<GibStatusResult>> GetStatusAsync(Declaration declaration, CancellationToken ct);
}
