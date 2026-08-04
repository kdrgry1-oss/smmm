namespace MaliMusavir.Api.Domain;

/// <summary>
/// Mevzuat/parametre merkezi kaydı (E4). TENANT'SIZ GLOBAL tablodur — süper admin yönetir,
/// tüm hesaplamalar (Luca KDV hesap eşlemesi, kod motoru güven eşikleri, binek kısıtları...)
/// buradan okur. Key noktalı ad alanı kullanır (ör. "kdv.hesap.20", "binek.kira.aylik.sinir").
/// Değer her zaman string saklanır; okuyan taraf ParameterService ile tip güvenli çözer.
/// </summary>
public class TaxParameter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Benzersiz parametre anahtarı (unique index).</summary>
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// <summary>Türkçe açıklama — admin ekranında gösterilir.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Değerin yürürlük başlangıcı (bilgi amaçlı; ör. 2026-01-01).</summary>
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
