using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Services;

/// <summary>Katman 3 (AI sınıflandırıcı) çıktısı: önerilen hesap + güven skoru.</summary>
public record ClassifierSuggestion(string AccountCode, double Confidence);

/// <summary>
/// Katman 3 — AI sınıflandırıcı genişleme noktası (motor tasarımı §1 Katman 3).
/// Bu fazda gerçek bir model YOK; CodingEngine kademesinde kural motorundan sonra,
/// garantili doldurma (fallback) öncesinde çağrılır. İleride LLM tabanlı sınıflandırıcı
/// bu arayüzü uygulayacak; öneri, mükellefin hesap planında (ChartAccount) var olan
/// bir hesapla sınırlandırılacak (halüsinasyon yok kısıtı).
/// </summary>
public interface IAccountClassifier
{
    /// <summary>Öneri üretemiyorsa null döner; motor bir sonraki katmana geçer.</summary>
    Task<ClassifierSuggestion?> ClassifyAsync(
        EInvoiceLine line,
        string normalizedItemName,
        EInvoice invoice,
        CancellationToken ct);
}

/// <summary>Faz 2 varsayılanı: hiçbir öneri üretmez; kademe garantili doldurmaya düşer.</summary>
public class NoopClassifier : IAccountClassifier
{
    public Task<ClassifierSuggestion?> ClassifyAsync(
        EInvoiceLine line,
        string normalizedItemName,
        EInvoice invoice,
        CancellationToken ct)
        => Task.FromResult<ClassifierSuggestion?>(null);
}
