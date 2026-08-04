# Flow Spec — Faz 1: Gelen e-Fatura Senkronizasyonu

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu modül ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

## Özellik

Mükellefe bir entegratör (Nilvera veya Mock/Demo) bağlanır; SMMM seçtiği tarih aralığı için
gelen e-faturaları senkronize eder; faturalar mükerrer kontrolüyle veritabanına yazılır ve
gelen kutusu ekranında listelenir; satıra tıklanınca başlık + kalem detayı görüntülenir.

## Kaynak akış referansları (Nilvera geliştirici dokümanı — 30.07.2026 tarihinde doğrulandı)

- Doküman indeksi: https://developer.nilvera.com/llms.txt
- Gelen faturalar genel: https://developer.nilvera.com/api/e-fatura-api/gelen-faturalar.md
- **Gelen Faturaları Listeler:** https://developer.nilvera.com/api/e-fatura-api/gelen-faturalar/gelen-faturalari-listeler.md
- **Detayları Getirir:** https://developer.nilvera.com/api/e-fatura-api/gelen-faturalar/detaylari-getirir.md
- Faturanın Xml'i Getirilir: https://developer.nilvera.com/api/e-fatura-api/gelen-faturalar/faturanin-xmli-getirilir.md
- Faturanın Pdf'i Getirilir: https://developer.nilvera.com/api/e-fatura-api/gelen-faturalar/faturanin-pdfi-getirilir.md

### Dokümandan doğrulanan gerçek API bilgileri

| Konu | Doğrulanan bilgi |
|---|---|
| Base URL | Canlı: `https://api.nilvera.com` · Test: `https://apitest.nilvera.com` |
| API öneki | e-Fatura API `servers.url = /einvoice` → tam yol `.../einvoice/Purchase` |
| Kimlik | `Authorization: Bearer {API_KEY}` (portalden üretilen anahtar) |
| Listeleme ucu | `GET /einvoice/Purchase` |
| Query parametreleri | `Page`, `PageSize`, `Search`, `StartDate`, `EndDate` (ISO 8601 date-time), `SortColumn` (ör. `IssueDate`), `SortType` (`ASC`/`DESC`), `DateFilterType` (`IssueDate`/`EnvelopeDate`/`CreatedDate`), `IsRead`, `IsArchive`, `InvoiceProfile[]`, `InvoiceType[]`, `StatusCode[]`, `AnswerCode[]` |
| Response zarfı | `PurchaseInvoiceResponsePagination`: `Page`, `PageSize`, `TotalCount`, `TotalPages`, `Content[]` |
| Fatura alanları | `UUID`, `TaxNumber`, `SenderTaxNumber`, `SenderName`, `InvoiceNumber`, `InvoiceProfile`, `InvoiceType`, `CurrencyCode`, `IssueDate`, `PayableAmount`, `TaxExclusiveAmount`, `TaxTotalAmount`, `LineExtensionAmount`, `ExchangeRate`, `StatusCode` (`unknown/waiting/succeed/error`), `AnswerCode`, `CreatedDate`, `EnvelopeUUID`, `EnvelopeDate` |
| Detay ucu | `GET /einvoice/Purchase/{UUID}/Details` → yalnızca başlık düzeyi (`NumberOfItems`, `InvoiceAmount`, `TaxAmount`...). **Kalem satırları bu uçta yok.** |
| Kalem satırları | Satır detayı UBL XML'inden çıkarılır: `Faturanın Xml'i Getirilir` ucu (`GET /einvoice/Purchase/{UUID}/xml` — TODO-VERIFY: yol/casing canlı anahtar alınınca doğrulanacak). Faz 1'de Nilvera sağlayıcısı liste yanıtındaki toplamlarla tek özet satır üretir; UBL satır ayrıştırma canlı anahtar sonrası eklenecek. |
| Hata kodları | `400` geçersiz istek, `403` yetkisiz erişim, `404` kayıt yok, `422` iş kuralı; ayrıca genel `401` (geçersiz anahtar) ve `429` (istek sınırı) senaryoları ele alınır. Referans: https://developer.nilvera.com/hata-kodlari |

## Ön koşullar

1. SMMM oturum açmış (JWT) ve en az bir mükellef kayıtlı.
2. Mükellefe entegrasyon tanımlanmış: sağlayıcı `Mock` (anahtar gerekmez) veya `Nilvera`
   (portalden alınmış API anahtarı; test/canlı ortam seçimi).
3. API anahtarı veritabanında **AES ile şifreli** saklanır (`Security:SecretKey`, 32 karakter);
   hiçbir yerde düz metin görünmez, API yanıtlarında yalnızca son 4 hane maskelenmiş gösterilir.

## Adım adım kullanıcı akışı

1. **Aksiyon:** Mükellefler sayfasında satırdaki "Entegrasyon" düğmesine tıklar.
   → **Beklenen:** Modal açılır; mevcut entegrasyon varsa sağlayıcı, maskeli anahtar (`••••1234`),
   ortam bilgisi görünür; yoksa boş form.
   → **Hatalar:** Yükleme hatasında modal içinde Türkçe hata bandı.
2. **Aksiyon:** Sağlayıcı seçer (Mock (Demo) / Nilvera). Nilvera seçilirse API anahtarını yazar,
   "Test ortamı" kutusunu işaretler/bırakır; "Kaydet"e basar.
   → **Beklenen:** `POST /api/clients/{id}/integration` 200 döner; modal maskeli anahtarı gösterir;
   mükellef listesinde entegratör kolonu güncellenir.
   → **Hatalar:** Nilvera + boş anahtar → UI ve backend "Nilvera için API anahtarı zorunludur.";
   geçersiz sağlayıcı → 400.
3. **Aksiyon:** Sol menüden "Faturalar" sayfasına gider, mükellefi ve tarih aralığını seçer
   (varsayılan son 30 gün), "Senkronize Et"e basar.
   → **Beklenen:** Düğme spinner'a döner; `POST /api/clients/{id}/einvoices/sync?from=&to=` çalışır;
   bitince yeşil sonuç bandı: "X yeni fatura alındı, Y mükerrer atlandı." Liste yenilenir.
   → **Hatalar:** Entegrasyon yoksa 400 "Önce mükellefe bir entegratör bağlayın.";
   401 sağlayıcı hatası → "Entegratör API anahtarı geçersiz veya süresi dolmuş.";
   429 → "Entegratör istek sınırı aşıldı, birazdan tekrar denenecek."; 5xx/ağ → genel Türkçe mesaj.
4. **Aksiyon:** Gelen kutusunda arama yapar (fatura no / satıcı), durum filtresi uygular, sayfalar.
   → **Beklenen:** `GET /api/einvoices?clientId=&status=&q=&page=&pageSize=` sayfalı sonuç;
   tabloda tarih, no, satıcı, tutar, KDV, durum rozeti.
   → **Hatalar:** Ağ hatasında hata bandı + "Tekrar dene".
5. **Aksiyon:** Fatura satırına tıklar.
   → **Beklenen:** Detay çekmecesi açılır: başlık bilgileri (satıcı, VKN, tarih, tutarlar) +
   kalem tablosu (kalem, miktar, birim fiyat, KDV oranı/tutarı, hesap kodu — Faz 1'de "—").
   → **Hatalar:** Kayıt silinmişse "Fatura bulunamadı."; yüklenirken iskelet gösterilir.

## Uç durumlar

- **Geçersiz/expired anahtar (401):** Senkron durur, fatura yazılmaz, Türkçe mesaj; anahtar silinmez.
- **Kota/istek sınırı (429):** `Retryable=true`; kullanıcıya "birazdan tekrar denenecek" mesajı.
- **Boş dönem:** Sonuç bandı "0 yeni fatura"; liste boşsa "Bu aralıkta fatura yok. Senkronize etmeyi deneyin."
- **Mükerrer fatura:** `(TenantId, ExternalUuid)` unique index + upsert kontrolü; ikinci senkronda
  aynı faturalar "atlanan" sayacına yazılır, satır çoğaltılmaz.
- **Ağ hatası / zaman aşımı:** HttpClient 30 sn timeout; `Retryable=true`, genel Türkçe mesaj.
- **Geçersiz tarih aralığı:** `from > to` → 400 "Başlangıç tarihi bitiş tarihinden sonra olamaz.";
  aralık 366 günden uzun → 400.
- **Mock sağlayıcı:** Anahtar gerektirmez; tarih aralığına göre deterministik 8-10 sahte fatura üretir
  (aynı aralıkta tekrar senkron = hepsi mükerrer atlanır → mükerrer akışının testi).
- **Tenant izolasyonu:** Başka tenant'ın mükellefi/fatura Id'si ile istek → 404 (filtre gereği görünmez).

## Kabul kriterleri

1. Mock sağlayıcı ile uçtan uca akış API anahtarı olmadan tamamlanır (bağla → senkron → listele → detay).
2. Aynı aralık ikinci kez senkronlandığında 0 yeni / N mükerrer raporlanır; DB'de satır sayısı değişmez.
3. API anahtarı DB'de şifreli; GET yanıtında yalnızca son 4 hane döner.
4. Tüm hata mesajları Türkçe; boş/yükleniyor/hata durumları üç ekranda da (modal, gelen kutusu, detay) mevcuttur.
5. Entegrasyonu olmayan mükellefte senkron 400 + "Önce mükellefe bir entegratör bağlayın." döner.

## Test adımları

**Manuel (Mock ile):**
1. Mükellef oluştur → Entegrasyon → Mock (Demo) → Kaydet → maskeli görünüm doğrula.
2. Faturalar → mükellef seç → Senkronize Et → "8-10 yeni fatura alındı" bandını gör.
3. Aynı aralıkla tekrar senkron → "0 yeni, N mükerrer" doğrula.
4. Arama ("akaryakıt" satıcısı), durum filtresi, sayfalama, detay çekmecesi ve KDV kırılımını doğrula.
5. Entegrasyonu sil → senkron dene → 400 Türkçe mesaj doğrula.

**Otomatik (planlanan):** backend xUnit — SecretProtector şifrele/çöz, MockProvider determinizm,
EInvoiceSyncService mükerrer atlama, entegrasyonsuz senkron 400; web Playwright — happy path + boş durum.

> **Not (geliştirme ortamı):** Faz 0'da şema `EnsureCreated` ile oluşturulduğundan yeni tablolar
> ancak boş veritabanında oluşur. Mevcut geliştirme DB'sini sıfırlayın
> (`docker compose down -v && docker compose up -d`) veya migration'a geçin.
