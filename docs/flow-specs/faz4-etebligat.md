# Flow Spec — Faz 4: e-Tebligat Tek Ekran

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu modül ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

## Özellik

Tüm mükelleflere gelen e-Tebligatlar **tek panoda** toplanır: gönderen kurum, konu, tebliğ
tarihi, **yasal süre sayacı** (renkli), okundu/okunmadı takibi, mükellef/okunmamış/acil
filtreleri ve "Senkronize Et". Sidebar'da okunmamış rozeti, dashboard'da "süresi yaklaşan
tebligat" uyarı kartı. Amaç: **gözden kaçan tebligat riskini bitirmek** (blueprint M6 —
asıl değer önerisi).

## Kaynak akış referansları

- Blueprint M6 (e-Tebligat tek ekran) ve §6.4 (e-Tebligat izleme akışı).
- Motor eki B11: süre sayaçlı tek ekran.
- **"Tebliğ edilmiş sayılma" kuralı (yasal dayanak):** 7201 sayılı Tebligat Kanunu md. 7/a ve
  213 sayılı VUK md. 107/A — elektronik tebligat, muhatabın elektronik adresine **ulaştığı
  tarihi izleyen beşinci günün sonunda** yapılmış/tebliğ edilmiş sayılır (UETS: uets.ptt.gov.tr,
  mevzuat: mevzuat.gov.tr). Bu uygulamada süre modeli **5 iş günü** olarak kurgulandı
  (hafta sonları atlanır) — **dürüst not:** kanun metni "beşinci gün" der ve yaygın yorum
  takvim günüdür; gerçek UETS entegrasyonunda sayaç, servisten dönen resmi tarihlerle
  teyit edilip gerekirse takvim gününe çekilecektir (adapter içinde tek noktadan değişir).
- Süre türleri (mock çeşitliliği): **Ödeme 15 gün** (ör. 6183 SK md. 55 ödeme emri),
  **İtiraz/dava 30 gün** (ör. vergi/ceza ihbarnamesi — İYUK md. 7 vergi mahkemesi süresi),
  **Bilgi** (süresiz veya kurum takdirli). Gerçek sürelerin belge türünden otomatik tespiti
  UETS meta verisiyle netleşecek.
- **Yasal not (blueprint M6):** SMMM'nin mükellef UETS kutusuna erişimi mükellefin açık
  yetkilendirmesini gerektirir; gerçek sağlayıcı bağlantısı PTT kurumsal başvurusu + mükellef
  onayı sonrası mükellef başına kurulacaktır.

## Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Adapter | `IETebligatProvider` → `MockUetsProvider`; gerçek PTT/UETS web servisi tek sınıfla eklenecek |
| Mock üretim | Mükellef VKN'sinden **deterministik** 0-4 tebligat; ExternalId `UETS-{vkn}-NNN` → tekrar senkronda mükerrer atlanır |
| Süre modeli | Son gün = tebliğ sayılma (tebliği izleyen 5. iş günü) + (Ödeme 15 / İtiraz-Bilgi 30 gün); mock son günü bugüne göre sabitler ve tebliğ tarihini geriye hesaplar (kalan 2 gün / süresi geçmiş / 3-7 sarı bant / >7 yeşil örnekleri garanti) |
| Mükerrer kontrol | Unique index (TenantId, ExternalId) + senkronda ön kontrol; mevcut kayıtların IsRead/ReadAt durumu EZİLMEZ |
| Acil tanımı | Kalan süre ≤ 3 gün (süresi geçmişler DAHİL — en kritik olanlar listeden düşmez) |
| Sayaç renkleri | >7 gün yeşil; 3-7 gün sarı; 1-2 gün kırmızı "⚠ N gün kaldı"; ≤0 koyu kırmızı "Süre doldu"; süresiz gri |
| Özet sayaçlar | Toplam/Okunmamış/Acil yalnız mükellef seçimine göre hesaplanır; onlyUnread/onlyUrgent sadece satırları daraltır |
| Sıralama | En yakın son gün üstte; süresiz bilgilendirmeler en sonda |
| Okundu | Platform içi takip (`IsRead/ReadAt`); UETS resmi okuma statüsünden bağımsız — mark-read idempotent |

## Ön koşullar

1. SMMM oturum açmış (JWT); en az bir aktif (arşivlenmemiş) mükellef var.
2. Mock için ek yapılandırma gerekmez (gerçek UETS'te mükellef başına yetki kaydı gerekecek).

## Adım adım kullanıcı akışı

1. **Aksiyon:** Sidebar'dan **e-Tebligat**'a girer, **"Senkronize Et"**e basar (mükellef seçiliyse tek mükellef, değilse tümü).
   → **Beklenen:** `POST /api/etebligat/sync?clientId=` → yeşil bant "3 yeni tebligat alındı
   (5 mükellef tarandı)."; ikinci basışta "Yeni tebligat yok (5 mükellef tarandı)." (mükerrer atlandı).
   → **Hatalar:** Hiç mükellef yok → "Senkronize edilecek mükellef yok. Önce mükellef ekleyin.";
   geçersiz clientId → 400 "Mükellef bulunamadı."; sağlayıcı hatası (tek mükellef) → 502 + Türkçe mesaj.
2. **Aksiyon:** Panoyu inceler.
   → **Beklenen:** Üstte üç özet kart (Toplam / Okunmamış sarı / **Süresi Yaklaşan kırmızı** —
   acil > 0 ise kart kırmızı zeminli). Tabloda mükellef, gönderen, konu, tebliğ tarihi, süre
   türü + son gün, **renkli kalan süre sayacı**, okundu durumu. Okunmamış satırlar hafif sarı
   zeminli ve konu kalın.
3. **Aksiyon:** Filtreler: mükellef seçer / "Sadece okunmamış" / "Sadece acil" işaretler.
   → **Beklenen:** `GET /api/etebligat?clientId=&onlyUnread=&onlyUrgent=` satırlar daralır,
   özet kartlar mükellef kapsamında sabit kalır; eşleşme yoksa "Sonuç bulunamadı".
4. **Aksiyon:** Okunmamış satırda **"Okundu işaretle"**ye basar.
   → **Beklenen:** `POST /api/etebligat/{id}/mark-read` → satır "✓ Okundu"ya döner; sidebar
   rozeti bir sonraki sayfa geçişinde azalır. Tekrar çağrı durumu değiştirmez (idempotent).
   → **Hatalar:** Kayıt yok/başka tenant → 404 "Tebligat bulunamadı."
5. **Aksiyon:** Dashboard'a döner.
   → **Beklenen:** "Süresi Yaklaşan Tebligat" kartı — acil varsa kırmızı zemin + "⚠ N tebligatta
   kalan süre ≤ 3 gün — hemen bakın"; acil yoksa okunmamış sayısı; hiç yoksa
   "Tebligat yok — hepsi kontrol altında ✓". Sidebar'da e-Tebligat yanında kırmızı okunmamış rozeti.

## Uç durumlar

- **Mükerrer senkron:** Aynı ExternalId ikinci kez yazılmaz; DB unique index son savunma hattıdır.
- **Okundu durumu korunur:** Yeniden senkron, kullanıcının platformda okuduğu tebligatı okunmamışa çevirmez.
- **Süresi geçen tebligat:** "Süre doldu" koyu kırmızı rozetle listede KALIR ve acil sayacına dahildir (gözden kaçmasın).
- **Süresiz (Bilgi) tebligat:** Sayaç "Süresiz"; sıralamada en sonda; acil sayacına girmez.
- **Sayaç doğal akışı:** Mock son günü İLK senkron gününe göre yazar; sonraki günlerde kalan gün kendiliğinden azalır (deterministik ExternalId sayesinde tarih güncellenmez).
- **Tek mükellef hatası (toplu tarama):** Diğer mükelleflerin taranmasını durdurmaz; loglanır.
- **0 tebligatlı mükellef:** Mock bazı VKN'lerde 0 üretir — boş durum ve "Yeni tebligat yok" akışı test edilir.
- **Tenant izolasyonu:** ETebligatMessage global query filter'lı; rozet/sayaçlar yalnız kendi tenant'ını sayar.
- **500 satır sınırı:** Pano tek seferde en çok 500 kayıt listeler (sayaçlar tam sayıdır).

## Kabul kriterleri

1. Senkron sonrası panoda dört renk bandının tamamından örnek görülür (mock şablonları garanti eder):
   yeşil (>7), sarı (3-7), kırmızı "⚠ 2 gün kaldı", koyu kırmızı "Süre doldu".
2. İkinci senkron 0 yeni kayıt ekler; okundu işaretleri korunur.
3. Acil sayacı = kalan ≤3 gün + süresi geçmiş kayıtların toplamı; dashboard kartı ve pano kartı aynı sayıyı gösterir.
4. Okundu işaretleme anında yansır; sidebar rozeti okunmamış sayısını gösterir, 0'da gizlenir.
5. Filtre kombinasyonları (mükellef + okunmamış + acil) doğru kesişimi verir; özet kartlar filtreden etkilenmez.
6. Tüm ekranlarda boş/yükleniyor/hata durumları ve Türkçe mesajlar; boş durum metni
   "Tebligat yok — hepsi kontrol altında ✓".

## Test adımları

**Manuel (Mock ile):**
1. 3-4 mükellef ekle → "Senkronize Et" (tümü) → özet kartlar ve renk dağılımını doğrula.
2. Tekrar "Senkronize Et" → "Yeni tebligat yok" mesajı.
3. Bir tebligatı okundu işaretle → sayfalar arası gezin → sidebar rozetinin azaldığını gör.
4. "Sadece acil" + mükellef filtresi → yalnız o mükellefin ≤3 gün/süresi geçmiş kayıtları.
5. Dashboard'da kırmızı uyarı kartını ve fisler/tebligat sayaçlarını doğrula.

**Otomatik (planlanan):** backend xUnit — MockUetsProvider determinizm + iş günü hesabı,
ETebligatSyncService mükerrer/okundu koruması, acil eşik sorgusu; web Playwright —
senkron + filtre + okundu akışı + rozet.

> **Not (geliştirme ortamı):** Şema `EnsureCreated` ile oluşturulduğundan ETebligatMessages
> tablosu ancak boş veritabanında oluşur. Mevcut geliştirme DB'sini sıfırlayın
> (`docker compose down -v && docker compose up -d`) veya migration'a geçin.
