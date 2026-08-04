# Faz 11 Flow Spec — E5 (e-Defter Ön-Kontrol), E6 (Gönderim Kuyruğu), E7 (Tahsilat), E8 (Mizan)

> Blueprint §0.5 ANA KURAL'a tabidir: Türkçe metinler, boş/yükleniyor/hata durumları,
> çift validasyon (UI + backend), mevcut davranış bozulmaz.

---

## E5 — e-Defter Berat Ön-Kontrolü

**Özellik:** Berat yüklemeden ÖNCE, sahada iptal/yeniden sürecine yol açan hataları yakalamak.

**Kaynak akış referansı:** GİB e-Defter uygulaması berat yükleme süreci (edefter.gov.tr
kılavuzları) — sık hata sebepleri: yanlış dönem/şirket kodu, VKN-unvan uyuşmazlığı,
süresi dolmuş mali mühür, dengesiz yevmiye/mizan, atlanmış dönem (beratlar SIRALI yüklenir).

**DÜRÜST KAPSAM NOTU:** Bu fazda **gerçek e-Defter XML (XBRL-GL) üretimi, mali mühürle
imzalama ve GİB berat yükleme entegrasyonu YOKTUR.** Modül; ön-kontrol seti + hazırlık
listesi + dönem durum takibidir. SMMM beratı GİB e-Defter uygulamasında yükler ve platformda
"Berat yüklendi" olarak **elle** işaretler. VKN-unvan eşleşmesi GİB sicil servisiyle
doğrulanamaz (servis yok) — yalnızca platform kaydının eksik/biçimsiz olup olmadığına bakılır
ve kullanıcıya "vergi levhasıyla elle karşılaştırın" uyarısı verilir (Uyari, asla Ok değil).

**Ön koşul:** Mükellef kayıtlı; dönem `yyyy-MM`.

**Adımlar:**
1. Aksiyon: SMMM, e-Defter sayfasında mükellef + dönem seçer → Beklenen: kontrol seti
   otomatik çalışır (GET `/api/edefter/precheck?clientId=&period=`), 6 kontrol kartı
   yeşil/sarı/kırmızı + öneri metniyle listelenir → Hatalar: geçersiz dönem 400 Türkçe mesaj;
   mükellef yoksa 400/404.
2. Kontrol seti (her satır: ad, durum Ok/Uyari/Hata, detay, öneri):
   - (a) **Fatura fişleştirme:** dönemdeki TÜM gelen faturalar Approved mı? Değilse **Hata**
     (onaysız kayıt deftere/berata girmez).
   - (b) **VKN/unvan:** unvan dolu (≥3 karakter) ve VKN/TCKN 10-11 hane mi? Değilse **Hata**;
     biçimsel tamsa **Uyari** + "GİB kaydıyla elle karşılaştırın" notu (dürüst sınır).
   - (c) **Sertifika (E3 tablosu):** mükellef mali mührü veya ofis e-imza/mali mührü kayıtlı mı,
     süresi dönem sonundan ileri mi? Kayıt yok → Uyari; süresi dolmuş/dönem sonundan önce → **Hata**;
     ≤30 gün → Uyari.
   - (d) **Kapanış (B10):** PeriodCloseService engel içeriyor mu? Engel → **Hata**; %100 değil → Uyari.
   - (e) **Mizan dengesi (E8):** borç=alacak (±0,01)? Dengesiz → **Hata**; dönem boş → Uyari
     ("boş defter beratı bilinçli olmalı").
   - (f) **Önceki ay beratı:** EDefterPeriod'da önceki dönem BeratYuklendi mi? Değilse Uyari
     (beratlar sıralı yüklenir; ilk kullanım ayında kayıt olmaması normaldir).
3. Aksiyon: Kontrol çalışınca EDefterPeriod upsert edilir → Hata varsa `Hata`, yoksa
   `KontrolTamam`; **BeratYuklendi durumu kontrolle EZİLMEZ**.
4. Aksiyon: SMMM beratı GİB portalında yükledikten sonra "Berat yüklendi olarak işaretle"
   (+ isteğe bağlı not, ör. GİB onay no) → POST `/api/edefter/{clientId}/{period}/mark-uploaded`
   → Beklenen: durum `BeratYuklendi`, MarkedUploadedAt yazılır → Hatalar: zaten işaretliyse 400.
   Kırmızı kontrol varken işaretleme ENGELLENMEZ (SMMM sorumluluğu) ama UI uyarı gösterir.
5. Dönem geçmişi tablosu: GET `/api/edefter?clientId=` (durum rozeti, kontrol/işaret zamanları, not).

**Uç durumlar:** ilk ay (önceki kayıt yok → Uyari), boş dönem (mizan boş → Uyari), mükellef
silinmesi (cascade), aynı döneme mükerrer kontrol (upsert — ikinci kayıt oluşmaz, unique index).

**Kabul kriteri:** Onaysız faturası olan dönemde kırmızı "Fatura fişleştirme" kartı çıkar;
tüm belgeler onaylanıp sertifika kayıtlıyken durum `KontrolTamam` olur; işaretleme sonrası
`BeratYuklendi` kontrol tekrarında korunur.

---

## E6 — Dayanıklılık / Gönderim Kuyruğu

**Özellik:** GİB/SGK/entegratör çöktüğünde SMMM'yi gece ekran başında tutmamak — gönderimler
kuyruğa alınır, sistem müsait olduğunda otomatik denenir.

**Ön koşul:** —

**Adımlar:**
1. Aksiyon: Beyanname detayında "Kuyruğa Al" → POST `/api/declarations/{id}/send?queue=true`
   → Beklenen: OutboundJob (Type=BeyannameGonder) oluşturulur, mesaj: "Gönderim kuyruğa
   alındı — sistem müsait olduğunda otomatik gönderilecek." → Hatalar: zaten gönderilmiş →
   400; taslak boş → 400.
2. Aksiyon: Normal "GİB'e Gönder" GEÇİCİ hata alırsa (sağlayıcı/ağ — `DeclarationResult.Retryable`)
   → Beklenen: mevcut 400 hata yanıtı KORUNUR, iş otomatik kuyruğa düşer ve mesaja
   "kuyruğa alındı, otomatik denenecek" eklenir. Durum hataları ("zaten gönderilmiş") kuyruğa
   düşmez.
3. Worker (OutboundJobWorker, .NET BackgroundService): 15 sn'de bir `Bekliyor + NextAttemptAt ≤ şimdi`
   işleri tarar; her işi işin tenant'ı adına (WorkerCurrentUser) yürütür. Başarısızlıkta üstel
   geri çekilme: **1, 5, 15, 60, 180 dk**; 5. denemeden sonra `Basarisiz`. Kalıcı hatalar
   (kayıt yok, bilinmeyen tip) beklemeden `Basarisiz`. Kuyrukta beklerken beyanname elden
   gönderildiyse iş `Basarili` sayılır (no-op).
4. Dispatcher: BeyannameGonder → DeclarationService.SendAsync; TebligatSync →
   ETebligatSyncService.SyncAsync; FaturaSync → EInvoiceSyncService.SyncIncomingAsync.
5. Aksiyon: Gönderim Kuyruğu sayfası → GET `/api/jobs?status=` (tip, durum rozeti, deneme
   sayısı, sonraki deneme, son hata; 15 sn'de bir otomatik tazelenir); "Şimdi Dene"
   (POST `/api/jobs/{id}/retry-now` — başarısızda deneme serisi sıfırlanır), "İptal"
   (POST `/api/jobs/{id}/cancel` — yalnız Bekliyor/Basarisiz). Boş durum: **"Kuyruk boş ✓"**.
   Sidebar "Gönderim Kuyruğu" bekleyen sayısı rozetini taşır.

**Uç durumlar:** aynı tip+payload için ikinci kuyruk isteği mevcut işi döner (mükerrer
koruması); uygulama kapanırken çalışan iş Bekliyor'a geri düşer (deneme hakkı yanmaz);
worker turu hatası döngüyü öldürmez.

**Sınırlamalar (dürüst):** TEK API instance varsayımı — iş seçimi satır kilidi kullanmaz
(yatay ölçeklemede SKIP LOCKED / advisory lock gerekir; OutboundJobWorker'da yorum notu).
SGK gönderimi diye bir iş tipi henüz yok (SGK entegrasyonu yok).

**Kabul kriteri:** Mock GİB %10 şema hatası senaryosunda kuyruğa alınan gönderim, worker
tarafından denenip sonuç durumuna yazılır; iptal edilen iş bir daha denenmez.

---

## E7 — SMMM Ücret / Tahsilat Takibi

**Özellik:** Meslek mensubunun kendi hizmet ücretini mükelleflerinden tahsil etmesini takip etmek.

**DÜRÜST KAPSAM NOTU:** Mükellefe e-Arşiv hizmet faturası kesimi ve otomatik hatırlatma
(SMS/e-posta) bu fazda yoktur; takip elle "Ödendi" işaretiyle yürür.

**Ön koşul:** Mükellef kayıtlı.

**Adımlar:**
1. Aksiyon: Tahsilat sayfası → "Ücret Tanımla" modalı (Mükellefler sayfasındaki "Ücret"
   linkinden de ulaşılır): mükellef + aylık ücret + başlangıç ayı + not → PUT
   `/api/clients/{id}/fee` (upsert; mükellef başına TEK tanım) → Hatalar: tutar ≤ 0 → Türkçe
   validasyon (UI + backend); mükellef yok → 404. GET aynı uçtan; tanım yoksa 404 → UI boş
   form (hata sayılmaz). DELETE tanımı siler, mevcut tahakkuklar KORUNUR.
2. Aksiyon: "Dönemi Oluştur" → POST `/api/fees/generate {period}` → Beklenen: ücret tanımlı
   tüm aktif mükelleflere o ay için FeeInvoice üretilir (mükerrersiz — unique index
   (TenantId, ClientId, Period); DueDate = ayın son günü; StartDate dönemden sonraysa
   üretilmez); ayrıca TÜM dönemlerde vadesi geçmiş Bekliyor → Gecikti çekilir. Mesaj:
   "N tahakkuk oluşturuldu, M mevcut kayıt atlandı...".
3. Aksiyon: Liste → GET `/api/fees?period=&status=` → özet kartlar (Beklenen / Tahsil Edilen /
   Geciken kırmızı; status filtresinden bağımsız) + tablo (mükellef, dönem, tutar, vade,
   durum rozeti). Liste açılışında da vade taraması çalışır (durum daima günceldir).
4. Aksiyon: "Ödendi" → POST `/api/fees/{id}/mark-paid {paidAmount?}` (tutar boşsa tahakkuk
   tutarı) → Hatalar: zaten ödendi → 400; tutar ≤ 0 → 400. "Geri Al" → POST
   `/api/fees/{id}/reopen` → durum vadeye göre Bekliyor/Gecikti'ye döner.
5. Panel (dashboard): geciken tahsilat varsa kırmızı uyarı kartı (adet + toplam ₺, Tahsilat linki).

**Uç durumlar:** ücret tanımsız tenant'ta "Dönemi Oluştur" → "Ücret tanımı yapılmış mükellef
yok" mesajı; boş durum ekranı "Ücret tanımı yapılmadı — mükelleflere aylık ücret tanımlayın";
arşivli mükelleflere tahakkuk üretilmez; 0/negatif ücrete tahakkuk üretilmez.

**Kabul kriteri:** Aynı döneme iki kez "Dönemi Oluştur" ikinci tahakkuk üretmez; vadesi
geçen Bekliyor kaydı listede kırmızı "Gecikti" görünür; ödendi → geri al → durum doğru döner.

---

## E8 — Mizan & Uyum Kontrolü

**Özellik:** Dönemdeki ONAYLI fatura + fiş satırlarından hesap bazlı mizan + beyanname uyum kontrolleri.

**Ortak çekirdek (kod tekrarı YOK):** Dengeli fiş satırları `LucaExcelExporter.BuildTemplateAsync`
ile üretilir (Luca dışa aktarmayla AYNI kurallar: KKEG bölünmesi + indirilemeyen KDV, oran
bazlı 191 gruplaması parametre merkezli hesap eşlemesiyle, alacak = cari 320.xx / kasa 100,
alacak = borç toplamı denge garantisi). TrialBalanceService bu satırları yalnızca hesap koduna
göre TOPLAR — davranış Luca dosyasıyla birebir aynıdır.

**Adımlar:**
1. Aksiyon: Mizan sayfasında mükellef + dönem seç → GET `/api/trial-balance?clientId=&period=`
   → Beklenen: kolonlar Hesap Kodu, Hesap Adı (ChartAccount'tan; yoksa bilinen tekdüzen
   adlar), Borç, Alacak, Borç Bakiye, Alacak Bakiye + TOPLAM satırı; denge rozeti
   **"Denk ✓"** yeşil / **"Dengesiz ✗"** kırmızı + fark (tolerans ±0,01 — kuruş yuvarlama
   farkları gerçek dengesizlik olarak yakalanır).
2. Uyum kontrolleri (uyarı bantları):
   - Mizan **191*** borç toplamı ≈ KDV1 taslağındaki **indirilecek KDV** (±0,05 TL).
   - Mizan **KKEG hesabı** (parametre `kkeg.hesap`, varsayılan 689) toplamı = KDV1 özetindeki
     **KKEG matrah + indirilemeyen KDV** toplamı (mizan KKEG satırı iki bileşeni birlikte taşır).
   - KDV1 taslağı yoksa: karşılaştırma yapılamadı Uyarısı + "taslağı hazırlayın" önerisi.
3. Aksiyon: "Excel İndir" → GET `/api/trial-balance?...&format=xlsx` → MinimalXlsxWriter ile
   "Mizan" sayfalı .xlsx (TOPLAM satırı dahil) → Hatalar: boş dönem → 400 "onaylanmış belge yok".
4. Hesap kodu atanmamış satırlar `(kodsuz)` kovasında sarı vurguyla toplanır; şablon üretim
   uyarıları (eksik alanlı belgeler) bilgi bandında listelenir.

**Uç durumlar:** boş dönem (boş durum ekranı — "onaylı belge yok"), KDV1 taslağı yok (Uyari),
KKEG'siz dönem ("kontrol temiz" Ok), alt kırılım hesapları (191.01 → "İndirilecek KDV (191.01)").

**Sınırlamalar (dürüst):** Mizan yalnızca platformdaki onaylı alış faturaları + fişlerden
üretilir — satış faturaları (600'ler), banka/kasa hareketleri ve açılış fişleri platformda
olmadığından GERÇEK genel mizanın alt kümesidir ("alış tarafı mizanı"). Denge, exporter'ın
alacak=borç garantisi sayesinde yapısaldır; dengesizlik ancak kuruş yuvarlama farklarında
görülür ve raporlanır.

**Kabul kriteri:** Aynı dönem için mizan 191 toplamı, Luca export dosyasındaki 191 satırları
toplamına eşittir; KDV1 taslağı hazırlanmış dönemde 191 uyum kontrolü Ok verir.

---

## Ortak teslimat listesi (Faz 11)

- Domain: `EDefterEntities.cs`, `OutboundJobEntities.cs`, `FeeEntities.cs`
- Servisler: `EDefterPrecheckService`, `OutboundJobService`, `FeeService`, `TrialBalanceService`
- Altyapı: `OutboundJobWorker` (HostedService), `WorkerCurrentUser` (arka plan tenant kimliği)
- Controller: `EDefterController`, `JobsController`, `FeesController`, `TrialBalanceController`;
  `DeclarationsController.Send` `?queue=true` + otomatik kuyruk
- AppDbContext: 4 yeni DbSet + tenant filtreleri + unique index'ler
- Web: `edefter`, `gonderim-kuyrugu`, `tahsilat`, `mizan` sayfaları; sidebar linkleri +
  bekleyen gönderim rozeti; dashboard geciken tahsilat kartı; beyanname "Kuyruğa Al" butonu;
  mükellefler "Ücret" linki
