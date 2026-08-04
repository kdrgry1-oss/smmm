# Flow Spec — Faz 7: E-Serisi Hızlı Kazanım Modülleri (E1–E4)

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu faz ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır. Kaynak: motor dokümanı §7
> (E1 belge portalı, E2 vergi takvimi, E3 sertifika takibi, E4 parametre merkezi).

---

## E1 — Mükellef Belge Portalı (en kritik kronik sorun)

### Özellik

SMMM her mükellef için bir **portal erişimi** üretir (link + 6 haneli PIN). Mükellef
**üyeliksiz**, bu linkten belge yükler; **Fiş** kategorisi doğrudan OCR/kod motoru hattına
düşer (Faz 3 fiş kuyruğu). SMMM ayrıca **eksik belge talebi** açar; mükellef portalda
kırmızı rozetle görür, yükleyince talep otomatik kapanır.

### Kaynak akış referansları

- Motor dokümanı §7 E1: mükellef belgeleri geç/eksik/dağınık (WhatsApp/e-posta) geliyor;
  portal tüm sistemi besleyen giriş kapısıdır.
- Faz 3 fiş hattı: `ReceiptService.UploadAndProcessAsync` (yükle → sakla → OCR → uygunluk →
  kod önerisi → NeedsReview). Portal Fiş yüklemesi AYNI hatta köprülenir.

### Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Kimlik | Üyeliksiz: `ClientPortalAccess` (mükellef başına TEK kayıt; Token unique, Guid "N") + 6 haneli PIN (BCrypt hash — düz metin ASLA saklanmaz) |
| PIN gösterimi | YALNIZCA üretim/yenileme yanıtında bir kez döner; GET ucu PIN dönmez |
| PIN yenileme | Link (token) SABİT kalır — mükellefe yeni link göndermek gerekmez; yalnızca PIN değişir + erişim yeniden aktifleşir |
| Portal oturumu | `POST /api/portal/{token}/login {pin}` → **2 saatlik** JWT; claim'ler: `portal=true`, `client_id`, `tenant_id` (rol claim'i YOK) |
| İzolasyon | Varsayılan authorization policy `portal=true` claim'ini REDDEDER → portal token'ı normal API'lere giremez; portal uçları yalnız `PortalOnly` policy ile açılır. `tenant_id` claim'i DbContext tenant filtresini besler |
| Kaba kuvvet | Bellek içi `PortalRateLimiter`: 5 yanlış PIN → 15 dk kilit (429); başarılı giriş sayacı sıfırlar |
| Bilgi sızıntısı | Login ÖNCESİ `GET {token}/info` yalnızca "geçerli/aktif" döner; **unvan login SONRASI** verilir (linki ele geçiren kimlik öğrenemez) |
| Fiş köprüsü | Kategori=Fis → `ReceiptService.UploadAndProcessAsync` (OCR hattı, SMMM onay kuyruğu); başarılıysa `PortalUpload.ProcessedReceiptId` dolar, dosya fişle PAYLAŞILIR (çift kopya yok) |
| OCR başarısız fiş | Belge KAYBEDİLMEZ: `portal/{tenantId}/` altına kaydedilir, SMMM elle inceler ("Fiş okunamadı; belge yine de iletildi") |
| Diğer kategoriler | Fatura / BankaEkstresi / Sozlesme / Diger yalnızca saklanır; uzantı listesi genişletilir (+xls/xlsx/csv/doc/docx), sınır 10 MB, en fazla 20 dosya |
| Talep eşleştirme | Yüklemede opsiyonel `documentRequestId`; ilk başarılı dosya talebi `Yuklendi` yapar, `FulfilledUploadId` yazılır |
| Talep iptali | Yalnızca `Bekliyor` talepler iptal edilebilir (Yuklendi → 400, denetim izi korunur) |
| Deaktive | `DELETE /api/clients/{id}/portal` → `IsActive=false` (kayıt silinmez; yeniden açılabilir) |

### Adım adım kullanıcı akışı

1. **SMMM** → sidebar **Belgeler** → mükellef seçer → "Portal Erişimi Oluştur".
   → **Beklenen:** link + tek seferlik PIN (amber kutu, "bir daha gösterilemez" uyarısı);
   "Linki Kopyala" panoya tam URL yazar. → **Hatalar:** mükellef yok → 404.
2. **Mükellef** → linki açar → jenerik "Belge Portalı" PIN ekranı (unvan YOK).
   → geçersiz/kapalı link → "Bağlantı geçerli değil" ekranı.
3. **Mükellef** PIN girer → unvan + bekleyen talepler (kırmızı rozet) + yükleme ekranı.
   → **Hatalar:** yanlış PIN → "PIN hatalı..." (401); 5. denemede → "15 dakika kilitlendi" (429);
   6 hane değilse UI + backend doğrulaması.
4. **Mükellef** kategori seçer (büyük butonlar), dosyaları sürükler/çeker, isteğe bağlı not +
   talep seçimi, "Yükle".
   → **Beklenen:** dosya bazlı sonuç listesi ("✓ Alındı"/"Hata" + Türkçe mesaj), bekleyen talepler
   tazelenir; Fiş kategorisi OCR hattına girer.
   → **Hatalar:** desteklenmeyen uzantı, >10 MB, >20 dosya, boş dosya — dosya bazlı Türkçe mesaj;
   oturum süresi dolduysa "PIN ile tekrar giriş yapın".
5. **SMMM** → Belgeler sayfası "Gelen Belgeler" tablosu (kategori filtreli): Fiş satırında
   "Fişlerde görüntüle" linki; her satırda "İndir". Talep listesi durum rozetli (Bekliyor sarı /
   Yüklendi yeşil / İptal gri).

### Uç durumlar

- Portal token'ı ile `/api/clients` gibi normal uca istek → **403** (varsayılan policy reddi).
- Erişim kapatıldıktan sonra login → 404; açık oturumun 2 saatlik JWT'si dolana kadar upload
  teknik olarak çalışır (sınırlama — aşağıda).
- Aynı talep için ikinci yükleme: talep zaten `Yuklendi` → eşleşme sessizce atlanır, dosyalar yine kaydedilir.
- Fiş dosyası Receipt ile aynı fiziksel dosyayı paylaşır: SMMM fişi silerse portal kaydının
  "İndir" ucu "dosya bulunamadı" döner (kayıt kalır — denetim izi).

### Kabul kriteri

PIN üretim→giriş→yükleme→SMMM görüntüleme uçtan uca hatasız; yanlış PIN kilidi çalışır;
portal token'ı normal API'lere 403 alır; Fiş yüklemesi fiş kuyruğunda görünür.

---

## E2 — Vergi Takvimi

### Tasarım kararları

| Karar | Uygulama |
|---|---|
| Standart set | `TaxCalendarService.StandardSet` const tablosu: KDV Beyannamesi → takip eden ayın **28**'i; Muhtasar → **26**'sı; SGK Bildirge → **26**'sı (mevzuat değişirse tek yerden güncellenir) |
| Mükerrer üretim | "Dönemi Oluştur" mevcut (mükellef, tür, dönem) kaydını ATLAR (unique index yerine servis kontrolü — aynı türden ikinci MANUEL kayıt eklenebilsin diye) |
| Beyanname senkronu | Aynı dönem KDV1 `Declaration.Status=Approved` → bekleyen "KDV Beyannamesi" yükümlülüğü otomatik `Tamamlandi` (idempotent `SyncWithDeclarationsAsync`; dönem üretiminde ve dönem filtreli listede çalışır) |
| Durum enum'ları | Spec gereği Türkçe üyeler: `Bekliyor/Tamamlandi`, `Otomatik/Manuel` (JSON'da string) |
| Sayaç | Kalan gün tebligat sayacı mantığıyla: geçti (koyu kırmızı) / bugün-≤3 gün (kırmızı) / ≤7 (sarı) / diğer (yeşil) |
| Pano sayaçları | Toplam/Bekleyen/Geciken — onlyPending/onlyOverdue filtresinden bağımsız (tebligat deseni) |

### Akış

1. **Takvim** sayfası → ay seçici (varsayılan önceki ay) → "Dönemi Oluştur" →
   "N yükümlülük oluşturuldu (M mükellef)[, K mevcut kayıt atlandı][, L KDV yükümlülüğü
   onaylı beyannameden otomatik tamamlandı]". Mükellef yoksa → 400 "önce mükellef ekleyin".
2. Tablo: mükellef, tür, dönem, son gün, kalan gün rozeti, kaynak, tamamla checkbox
   (işaretle→complete, kaldır→reopen). Geciken satırlar kırmızı zeminli.
3. "Manuel Ekle": mükellef + tür (öneri listesi: Geçici Vergi, Yıllık Gelir V. ...) + son gün.
4. Dashboard'da "Geciken Yükümlülük" kartı (geciken > 0 → kırmızı).

### Uç durumlar

Geçersiz dönem → 400; tamamlanmışı tekrar tamamla → 400; reopen sonrası KDV yükümlülüğü
dönem filtreli liste açılınca beyanname Approved ise senkron tekrar kapatır (bilinçli:
beyanname gerçekten onaylıysa yükümlülük yerine getirilmiştir).

---

## E3 — Sertifika Süre Takibi

| Karar | Uygulama |
|---|---|
| Model | `ClientCertificate`: ClientId **null = ofisin (SMMM) kendi sertifikası**; Type: EImza/MaliMuhur/Kep |
| Renk eşiği | <30 gün kırmızı, <90 gün sarı, ≥90 yeşil; süresi dolan koyu kırmızı "Süresi doldu" |
| API | `GET /api/certificates?expiringInDays=90` + POST/PUT/DELETE (basit CRUD) |
| Dashboard | 90 gün içinde biten sertifika varsa amber uyarı bandı (Sertifikalar sayfasına link) |

Akış: liste (bitişi en yakın önce) → "Sertifika Ekle" modalı (ait olduğu: Ofis/mükellef,
tür, sahip, bitiş, not) → düzenle/sil. Boş durum + yükleniyor iskeleti + hata/tekrar dene mevcut.

---

## E4 — Parametre Merkezi

| Karar | Uygulama |
|---|---|
| Tablo | `TaxParameter` — **TENANT'SIZ GLOBAL** (tenant filtresi yok); Key unique |
| Seed (2026) | KDV oranları (kdv.oran.standart=20 / indirimli=10 / dusuk=1); KDV hesap eşlemesi (kdv.hesap.20=191.01, .10=191.02, .1=191.03, .diger=191); binek.gider.kisit.oran=70; binek.kira.aylik.sinir=46000; binek.otv.kdv.gider.sinir=1200000; binek.amortisman.sinir=2600000. Seed yalnızca EKSİK anahtarları ekler (admin değişiklikleri ezilmez) |
| Okuma | `ParameterService`: static ConcurrentDictionary önbelleği (IMemoryCache yerine bilinçli sade tercih), ilk istekte tek sorgu, yazmada `Invalidate()` |
| Refactor 1 | `LucaExcelExporter` KDV hesap eşlemesi artık parametreden (`kdv.hesap.*`); metotlar `BuildJournalCsvAsync/BuildInvoiceListCsvAsync` oldu (çağıran `CodingController` güncellendi). Tablo boşsa eski sabit eşleme birebir geçerli — davranış AYNI |
| Refactor 2 | `CodingEngine` katman güven değerleri parametreden (`motor.guven.hafiza/satici/kural/varsayilan`), varsayılanlar eski sabitler (0.95/0.80/0.75/0.30) — davranış AYNI. (Not: CodingEngine'de KDV'ye özgü sabit yoktur; parametrelenebilir değerleri katman güvenleridir. Bu anahtarlar bilinçli olarak seed edilmedi — yoklukta varsayılan geçerli) |
| DeclarationService | KDV kırılım hesap etiketi (191.01=%1...) BİLİNÇLİ dokunulmadı: Luca eşlemesiyle tarihsel olarak farklı yönde; "davranış aynı kalsın" kuralı gereği Faz 8'de tek kaynağa bağlanacak |
| Yetki | `GET /api/parameters` tüm oturum açmış kullanıcılara açık (şeffaflık); POST/PUT/DELETE yalnız SuperAdmin. AdminController sınıf genelinde SuperAdmin zorunlu kıldığından CRUD ayrı `ParametersController`'da |
| UI | Admin sayfasında "Parametreler" tablosu + satır içi düzenleme + "Bu değerler tüm hesaplamaları etkiler" uyarısı |

---

## Sınırlamalar (dürüst notlar)

1. **Portal kilidi bellek içi:** çok instance dağıtımda sayaç paylaşılmaz (Redis gerekir);
   süreç yeniden başlayınca sayaç sıfırlanır. Tek instance MVP için yeterli.
2. **Deaktive edilen portalın açık oturumu:** 2 saatlik JWT'si dolana kadar yükleme yapabilir
   (token iptal listesi yok). Kısa ömür bilinçli risk sınırıdır.
3. **Hatırlatmalar (SMS/WhatsApp/push) bu fazda YOK** — E1/E2 tanımındaki otomatik hatırlatma
   bildirim altyapısı (ayrı sağlayıcı sözleşmesi) gerektirir; UI/veri modeli hazırdır.
4. **Parametre önbelleği süreç genelidir:** çok instance dağıtımda bir instance'ın Invalidate'i
   diğerini tazelemez (kısa TTL veya dağıtık cache Faz 8).
5. **Vergi takvimi günleri sabit tablodadır** (resmî tatil/uzatma takibi yok); GİB duyurusuyla
   uzayan süreler manuel yükümlülükle yönetilir.
6. **E2 unique index yok:** manuel kayıtlar serbest olduğundan mükerrerlik yalnızca OTOMATİK
   üretimde engellenir; SMMM aynı türde manuel mükerrer açabilir (bilinçli esneklik).
7. **Şema:** proje hâlâ `EnsureCreated` kullanır (Faz 0 kararı) — yeni tablolar (portal, takvim,
   sertifika, parametre) MEVCUT bir geliştirme veritabanına otomatik eklenmez; dev DB'yi sıfırlayın
   (`docker compose down -v && up -d`) veya Faz 8'de EF migration'a geçilecek.

## Test adımları (E2E — Playwright/xUnit hedefleri)

- E1: erişim üret → PIN bir kez görünür → login (yanlış PIN ×5 → 429) → fiş yükle →
  fiş kuyruğunda görünür → talep aç → portalda görünür → yükle → talep Yuklendi.
- E1 güvenlik: portal JWT ile `/api/clients` → 403; normal JWT ile `/api/portal/upload` → 403.
- E2: dönem oluştur → 3×N kayıt; tekrar oluştur → 0 eklendi; KDV1 Approved → liste açılınca
  KDV yükümlülüğü Tamamlandi.
- E3: <30 gün sertifika kırmızı rozet + dashboard uyarısı.
- E4: SuperAdmin kdv.hesap.20 değerini değiştir → Luca dışa aktarımında yeni hesap kodu;
  tenant kullanıcısı PUT → 403.
