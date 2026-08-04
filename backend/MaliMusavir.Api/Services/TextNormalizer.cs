using System.Globalization;
using System.Text;

namespace MaliMusavir.Api.Services;

/// <summary>
/// Katman 0 — Türkçe-duyarlı metin normalizasyonu (motor tasarımı §1 Katman 0).
/// Entegratörden gelen kalem açıklamaları kaynaktan bağımsız hale getirilir:
/// küçük harf (İ→i, I→ı doğru), noktalama/rakam temizliği, çoklu boşluk tekleme,
/// yaygın kısaltmaların açılması ve "kalem imzası" (ilk 6 anlamlı kelime) üretimi.
/// </summary>
public static class TextNormalizer
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// Yaygın fatura kısaltmaları sözlüğü (normalize edilmiş token → açılımı).
    /// Amaç: "fat", "fatr" gibi entegratör kısaltmalarının hafıza/kural eşleşmesini bozmaması.
    /// </summary>
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["fat"] = "fatura",
        ["fatr"] = "fatura",
        ["hizm"] = "hizmet",
        ["hzm"] = "hizmet",
        ["bed"] = "bedeli",
        ["bedl"] = "bedeli",
        ["elk"] = "elektrik",
        ["elek"] = "elektrik",
        ["tel"] = "telefon",
        ["akary"] = "akaryakıt",
        ["akr"] = "akaryakıt",
        ["kirt"] = "kırtasiye",
        ["kırt"] = "kırtasiye",
        ["nak"] = "nakliye",
        ["nakl"] = "nakliye",
        ["muh"] = "muhasebe",
        ["mush"] = "müşavirlik",
        ["dan"] = "danışmanlık",
        ["dnsm"] = "danışmanlık",
        ["malz"] = "malzeme",
        ["mlz"] = "malzeme",
        ["tem"] = "temizlik",
        ["tmz"] = "temizlik",
        ["abn"] = "abonelik",
        ["abone"] = "abonelik",
        ["yak"] = "yakıt",
        ["svc"] = "servis",
        ["srv"] = "servis",
    };

    /// <summary>
    /// İmza üretiminde atlanan, ayırt ediciliği olmayan dolgu kelimeleri.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "ve", "ile", "için", "icin", "adet", "ad", "kg", "lt", "litre", "tl", "try",
        "no", "nolu", "sayılı", "sayili", "ay", "ayı", "ayi", "dönem", "donem", "tarihli",
        "üzere", "uzere", "olan", "dahil", "hariç", "haric", "kdv",
    };

    /// <summary>
    /// Metni Türkçe-duyarlı normalize eder: küçük harf, rakam/noktalama → boşluk,
    /// çok boşluk tekleme, kısaltma açılımı. Boş/null girişte boş dize döner.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Türkçe-duyarlı küçük harf: "İSTANBUL" → "istanbul", "ISPARTA" → "ısparta".
        var lower = text.ToLower(Turkish);

        // Harf dışı her şey (rakam, noktalama, sembol) boşluğa çevrilir; harfler korunur.
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
            sb.Append(char.IsLetter(ch) ? ch : ' ');

        // Çoklu boşlukları tekle + kısaltmaları aç.
        var tokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Abbreviations.TryGetValue(t, out var full) ? full : t);

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Şirket türü ekleri ve dolgu kelimeleri — unvan normalizasyonunda atılır
    /// (Türkçe karakterler ASCII'ye sadeleştirildikten SONRA karşılaştırılır).
    /// "LİMİTED ŞİRKETİ", "LTD. ŞTİ.", "A.Ş." vb. yazım farkları böylece eşitlenir.
    /// </summary>
    private static readonly HashSet<string> CompanySuffixTokens = new(StringComparer.Ordinal)
    {
        "ANONIM", "SIRKETI", "SIRKET", "STI", "LTD", "LIMITED",
        "SANAYI", "SAN", "TICARET", "TIC", "AS", "VE",
    };

    /// <summary>
    /// Satıcı UNVANI normalizasyonu (VKN'siz eşleşme anahtarı — mizan/fiş listesi içe aktarma
    /// ile kod motorunun unvan-bazlı ikincil eşleşmesi AYNI fonksiyonu kullanır):
    /// Türkçe karakterler ASCII'ye sadeleştirilir (Ş→S, İ→I...), BÜYÜK harfe çevrilir,
    /// harf/rakam dışı her şey boşluk olur, şirket türü ekleri (ŞİRKETİ/ŞTİ/LTD/LİMİTED/
    /// SANAYİ/SAN/TİCARET/TİC/A.Ş/AŞ/VE) ve tek karakterlik artıklar atılır.
    /// Ör. "DİNÇ PETROL VE TİCARET LİMİTED ŞİRKETİ" → "DINC PETROL".
    /// </summary>
    public static string NormalizeCompanyName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var sb = new StringBuilder(title.Length);
        foreach (var raw in title)
        {
            // Önce Türkçe karakter sadeleştirme (büyük/küçük her iki biçim), sonra upper.
            var ch = raw switch
            {
                'ç' or 'Ç' => 'C',
                'ğ' or 'Ğ' => 'G',
                'ı' or 'İ' or 'i' => 'I',
                'ö' or 'Ö' => 'O',
                'ş' or 'Ş' => 'S',
                'ü' or 'Ü' => 'U',
                _ => char.ToUpperInvariant(raw)
            };
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        var tokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1 && !CompanySuffixTokens.Contains(t));

        var normalized = string.Join(' ', tokens);

        // Anlamlı kelime kalmadıysa (ör. yalnızca "A.Ş." gibi bir metin) boş döner — eşleşme yapılmaz.
        // DB kolonu sınırı (300) için güvenli kesme.
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    /// <summary>
    /// Kalem imzası: normalize metnin ilk 6 anlamlı kelimesi (tek harfli ve dolgu kelimeler atlanır).
    /// AccountMemory anahtarının parçasıdır; entegratörler arasında kararlı kalması hedeflenir.
    /// </summary>
    public static string BuildItemSignature(string? itemName)
    {
        var normalized = Normalize(itemName);
        if (normalized.Length == 0) return string.Empty;

        var meaningful = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Take(6);

        var signature = string.Join(' ', meaningful);

        // Anlamlı kelime kalmadıysa (ör. yalnızca rakam/dolgu içeren açıklama) ham normalize metne düş.
        if (signature.Length == 0) signature = normalized;

        // DB kolonu sınırı (200) için güvenli kesme.
        return signature.Length <= 200 ? signature : signature[..200];
    }
}
