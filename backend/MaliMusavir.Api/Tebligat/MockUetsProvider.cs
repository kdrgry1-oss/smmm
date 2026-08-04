using System.Security.Cryptography;
using System.Text;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Integrations;

namespace MaliMusavir.Api.Tebligat;

/// <summary>
/// Demo/test UETS sağlayıcısı: mükellef başına 0-4 tebligat üretir (VKN'den deterministik —
/// aynı mükellef her senkronda aynı ExternalId'leri döndürür, mükerrer atlama test edilir).
/// Yasal süre modeli: tebliğ tarihini izleyen 5. İŞ GÜNÜ sonunda "tebliğ edilmiş sayılır"
/// (bkz. flow spec faz4-etebligat.md kaynak notu) + türe göre ödeme 15 / itiraz 30 gün.
/// Şablonlar sayaç testini besler: süresi ≤3 gün kalan, süresi geçmiş, sarı bantta ve
/// yeşil bantta örnekler; bazıları okunmuş bazıları okunmamış.
/// </summary>
public class MockUetsProvider : IETebligatProvider
{
    public string Name => "MockUets";

    /// <summary>
    /// Tebligat şablonları: (gönderen, konu, süre türü, bugüne göre kalan gün — null = süresiz bilgi yazısı).
    /// Kalan gün profili sayaç renk testlerini kapsar: 2 (kırmızı), -2 (süre doldu),
    /// 3/6 (sarı), 12/20 (yeşil), null (süresiz).
    /// </summary>
    private static readonly TebligatTemplate[] Templates =
    {
        new("Gelir İdaresi Başkanlığı — Kozyatağı Vergi Dairesi", "Vergi/ceza ihbarnamesi (2026/05 dönemi KDV)", "İtiraz", 2),
        new("Gelir İdaresi Başkanlığı — Kozyatağı Vergi Dairesi", "Ödeme emri (6183 sayılı Kanun md. 55)", "Ödeme", -2),
        new("Sosyal Güvenlik Kurumu", "Prim borcu tebligatı (2026/04 dönemi)", "Ödeme", 12),
        new("Ticaret Bakanlığı", "Sektörel denetim bilgi talebi yazısı", "Bilgi", null),
        new("Gelir İdaresi Başkanlığı — Kozyatağı Vergi Dairesi", "Defter ve belge ibraz talebi", "Bilgi", 6),
        new("Sosyal Güvenlik Kurumu", "İdari para cezası kararı", "İtiraz", 3),
        new("Gelir İdaresi Başkanlığı — Kozyatağı Vergi Dairesi", "Geçici vergi farkı bildirimi", "İtiraz", 20),
        new("Ticaret Bakanlığı", "İdari yaptırım kararı tebliği", "İtiraz", -6)
    };

    public Task<ProviderResult<List<ProviderTebligat>>> GetMessagesAsync(Client client, CancellationToken ct)
    {
        var seed = DeterministicSeed($"uets|{client.TaxId}");
        var count = seed % 5; // 0-4 tebligat (bazı mükelleflerde hiç tebligat yok — boş durum testi)
        var today = DateTime.UtcNow.Date;

        var messages = new List<ProviderTebligat>(count);
        for (var i = 0; i < count; i++)
        {
            var template = Templates[(seed / 7 + i * 3) % Templates.Length];

            DateTime deliveredAt;
            DateTime? deadline;

            if (template.RemainingDays.HasValue)
            {
                // Son gün bugüne göre sabitlenir; tebliğ tarihi yasal modelden geriye hesaplanır:
                // son gün = tebliğ sayılma (tebliği izleyen 5. iş günü) + süre (ödeme 15 / diğer 30 gün).
                deadline = today.AddDays(template.RemainingDays.Value);
                var periodDays = template.DeadlineType == "Ödeme" ? 15 : 30;
                var deemedServedAt = deadline.Value.AddDays(-periodDays);
                deliveredAt = SubtractBusinessDays(deemedServedAt, 5);
            }
            else
            {
                deadline = null;
                deliveredAt = today.AddDays(-((seed + i) % 12) - 1);
            }

            messages.Add(new ProviderTebligat
            {
                // Deterministik kimlik → aynı mükellef tekrar senkronlanınca mükerrer atlanır.
                ExternalId = $"UETS-{client.TaxId}-{i + 1:D3}",
                Sender = template.Sender,
                Subject = template.Subject,
                DeliveredAt = deliveredAt,
                LegalDeadline = deadline,
                DeadlineType = template.DeadlineType,
                IsRead = ((seed >> i) & 1) == 1 // bazıları okunmuş, bazıları okunmamış
            });
        }

        return Task.FromResult(ProviderResult<List<ProviderTebligat>>.Ok(messages));
    }

    /// <summary>Verilen tarihten geriye doğru iş günü sayar (hafta sonları atlanır; resmi tatil takvimi sonraki fazda).</summary>
    private static DateTime SubtractBusinessDays(DateTime date, int businessDays)
    {
        var current = date;
        var subtracted = 0;
        while (subtracted < businessDays)
        {
            current = current.AddDays(-1);
            if (current.DayOfWeek != DayOfWeek.Saturday && current.DayOfWeek != DayOfWeek.Sunday)
                subtracted++;
        }
        return current;
    }

    /// <summary>Verilen metinden her çalıştırmada aynı pozitif tohumu üretir (MD5 tabanlı).</summary>
    private static int DeterministicSeed(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var value = BitConverter.ToInt32(hash, 0);
        return value == int.MinValue ? 0 : Math.Abs(value);
    }

    private sealed record TebligatTemplate(string Sender, string Subject, string DeadlineType, int? RemainingDays);
}
