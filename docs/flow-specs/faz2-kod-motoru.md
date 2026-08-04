# Flow Spec — Faz 2: Otomatik Hesap/Cari Kod Motoru + Luca CSV Dışa Aktarma

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu modül ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

## Özellik

Ürünün kalbi: gelen e-fatura kalemlerine **boşluksuz, tam otomatik** hesap kodu ve cari kod
atanır (Katman 0-2 + garantili doldurma), düşük güvenli faturalar **onay kuyruğuna** düşer,
SMMM onayı **öğrenmeye** geri yazılır (Katman 1 hafıza + satıcı varsayılanı) ve onaylanmış
faturalar **Luca içe aktarmaya uygun CSV** (yevmiye fişi düzeni) olarak indirilir.

## Kaynak akış referansları

- Motor tasarımı: `MaliMusavir_Otomatik_Kod_Motoru_ve_Moduller.md` §1 (kademeli mimari),
  §2 (veri modeli), §6 (inşa sırası — bu faz: madde 1-2 + Katman 4 onay döngüsü).
- Blueprint §0 madde 3: Luca'nın resmi API'si yok → **garantili yol dosya formatı**
  (Excel/CSV içe aktarma). Bu fazda noktalı virgül ayraçlı, UTF-8 BOM'lu CSV üretilir
  (Türkçe Excel varsayılanı); Luca birebir şablon eşlemesi resmi şablon teyidi sonrası yapılacak.
- KDV hesap eşlemesi (motor tasarımı §1 "KDV kuralları motora gömülü"):
  %20→191.01, %10→191.02, %1→191.03, %0→KDV satırı yok (bu fazda sabit; ileride ayarlanabilir).

### Kademeli motor (uygulanan güven değerleri)

| Katman | Kaynak | Güven |
|---|---|---|
| K1a | AccountMemory tam eşleşme (satıcı VKN + kalem imzası + KDV oranı) | 0.95 + onay sayısına göre en çok 0.99 |
| K1b | VendorDefault (satıcı seviyesi varsayılan) | 0.80 |
| K2 | CodingRule (öncelik sırasıyla; regex veya ";" ayraçlı anahtar kelime) | 0.75 |
| K3 | IAccountClassifier — **bu fazda Noop** (genişleme noktası) | (modele göre) |
| Fallback | ClientCodingSettings.DefaultExpenseAccount (ör. 770.99) | 0.30 |

- Fatura durumu: tüm satırlar ≥ AutoApplyThreshold (varsayılan 0.85) → **AutoCoded**;
  değilse **NeedsReview** (onay kuyruğu). Onay sonrası → **Approved**.
- Cari: VendorCariMap'te varsa o kod; yoksa `CariPrefix + '.' + 3 haneli sayaç`
  (ör. "320.001") üretilir, sayaç artırılır ve haritaya yazılır.

## Ön koşullar

1. SMMM oturum açmış (JWT); Faz 1 akışı tamamlanmış: mükellef + entegrasyon + senkronlanmış faturalar.
2. Mükellefin kodlama ayarları mevcut (yeni mükellefte otomatik oluşur; eski mükelleflerde
   ilk erişimde varsayılanlarla oluşturulur: 770.99 / 320 / 1 / 0.85).
3. (İsteğe bağlı) SMMM, Kurallar sayfasından Katman 2 kuralları tanımlamış.

## Adım adım kullanıcı akışı

1. **Aksiyon:** Faturalar sayfasında mükellef + tarih aralığı seçer, **"Dönemi Kodla"** düğmesine basar.
   → **Beklenen:** `POST /api/einvoices/code-batch?clientId=&from=&to=` dönemdeki tüm
   Uncoded faturaları kodlar; yeşil sonuç bandı: "12 fatura otomatik kodlandı, 3 onay bekliyor.";
   tabloda "Kodlama" rozet kolonu güncellenir (Kodlanmadı/Otomatik/Onay Bekliyor/Onaylandı).
   → **Hatalar:** Mükellef seçilmemiş → "Toplu kodlama için bir mükellef seçin.";
   kodlanacak fatura yok → "Seçilen aralıkta kodlanacak fatura yok."; geçersiz aralık → 400 Türkçe mesaj.
2. **Aksiyon:** Fatura satırına tıklar; detay çekmecesi açılır.
   → **Beklenen:** Kalem tablosunda düzenlenebilir **hesap kodu** input'u (öneri önceden dolu),
   **cari kod** alanı ve **güven yüzdesi rozeti** (yeşil ≥ %85, sarı ≥ %60, altı kırmızı).
   Kodlanmamış faturada "Kodla" düğmesi tek faturayı motordan geçirir
   (`POST /api/einvoices/{id}/code`).
   → **Hatalar:** Onaylanmış fatura yeniden kodlanamaz → 400 "Onaylanmış fatura yeniden kodlanamaz.";
   kalemsiz fatura → 400 "Faturada kodlanacak kalem satırı yok."
3. **Aksiyon:** Gerekliyse kodları düzeltir, **"Onayla"** düğmesine basar.
   → **Beklenen:** `POST /api/einvoices/{id}/approve` (gövde: satır düzeltmeleri) →
   satırlar AccountCode olarak kesinleşir (güven %100), **öğrenme** çalışır:
   AccountMemory upsert (ApproveCount++), VendorDefault = satıcının en çok onaylanan hesabı,
   cari düzeltmesi VendorCariMap'e işlenir; fatura **Approved**; yeşil bant:
   **"Onaylandı — sistem bu eşlemeyi öğrendi ✓"**.
   → **Hatalar:** Boş hesap kodu → UI "N. satır için hesap kodu boş olamaz.";
   biçimsiz kod → UI + backend "Geçerli bir hesap kodu girin (ör. 770.01).";
   faturaya ait olmayan satır Id → 400 "Düzeltme satırı bu faturaya ait değil."
4. **Aksiyon:** Sol menüden **Onay Kuyruğu** sayfasına gider.
   → **Beklenen:** `GET /api/coding/review-queue?clientId=` NeedsReview faturaları listeler;
   mükellef seçici ile daraltılır; "Faturaya Git" → `/panel/faturalar?fatura=<id>` detay
   çekmecesi otomatik açılır (hızlı onay akışı).
   → **Hatalar:** Ağ hatasında hata bandı + "Tekrar dene"; boş durumda "Onay bekleyen fatura yok 🎉".
5. **Aksiyon:** **Kurallar** sayfasında desen ("akaryakıt;benzin;motorin" ya da regex),
   hesap kodu, öncelik ve kapsam (mükellef / tüm mükellefler) girip "Kural Ekle"ye basar.
   → **Beklenen:** `POST /api/coding/rules` kaydeder; tabloda görünür; sonraki kodlamalarda
   K2'de öncelik sırasıyla uygulanır. Silme `DELETE /api/coding/rules/{id}` + onay penceresi.
   → **Hatalar:** ";" içermeyen ve derlenemeyen desen → 400 **"Geçersiz desen."** (UI'da da
   aynı ön kontrol); hesap kodu biçimsiz → "Geçerli bir hesap kodu girin (ör. 770.01).";
   öncelik aralık dışı → "Öncelik 0 ile 1000 arasında olmalıdır."
6. **Aksiyon:** **Dışa Aktar** sayfasında mükellef + tarih aralığı + düzen
   (yevmiye fişi / düz liste) seçer.
   → **Beklenen:** Önizleme bandı `GET /api/export/luca/preview` ile "N onaylanmış fatura
   (toplam X TL)" gösterir; **"Luca CSV İndir"** → `GET /api/export/luca?clientId=&from=&to=&type=`
   Content-Disposition ile `luca-fis-{mükellef}-{tarih}.csv` indirir (blob).
   → **Hatalar:** Onaylanmış fatura yoksa 400 **"Seçilen aralıkta onaylanmış fatura yok."**;
   mükellef seçilmemişse UI engeller.

## Uç durumlar

- **Boşluksuz doldurma:** Hiçbir katman eşleşmese bile fallback (varsayılan gider hesabı,
  güven 0.30) atanır → fatura NeedsReview olur; hiçbir satır kodsuz kalmaz.
- **Onaylanmış faturayı yeniden kodlama:** 400 + "Onaylanmış fatura yeniden kodlanamaz."
  (onaylı kodlar ezilmez; geri alma sonraki fazda).
- **Aynı satıcı, aynı toplu kodlamada iki fatura:** İlk fatura cari kodu üretir ve haritayı
  hemen kaydeder; ikinci fatura aynı kodu haritadan okur (çift kod üretilmez).
- **Hafıza çakışması:** SMMM aynı kalemi farklı hesapla onaylarsa hafıza yeni koda döner,
  sayaç 1'den başlar (son onay kazanır).
- **Geçersiz regex'li eski kural:** Çalışma anında regex derlenemezse düz içerme kontrolüne
  düşülür; regex zaman aşımı (250 ms) eşleşmedi sayılır — kodlama hiç kesilmez.
- **KDV %0 satır:** Fişe KDV satırı yazılmaz; bilinmeyen oran (ör. tarihi %8) 191 ana hesabına yazılır.
- **Kuruş farkı:** Fişte alacak = borç toplamı yazılır (satır toplamı ile fatura başlığı
  arasında yuvarlama farkı olsa bile fiş dengeli kalır).
- **Türkçe Excel uyumu:** CSV noktalı virgül ayraçlı + UTF-8 BOM'lu; tutarlar virgül
  ondalıklı ("1234,56"), tarihler gg.AA.yyyy. Alan içinde ";" varsa çift tırnak kaçışı.
- **Tenant izolasyonu:** Tüm yeni tablolarda (hafıza, kural, cari harita, ayarlar) global
  query filter; başka tenant'ın kaydı 404 gibi görünmezdir.
- **Batch sınırı:** Tek toplu kodlama isteği en çok 500 fatura işler (zaman aşımı koruması).

## Kabul kriterleri

1. Mock verisiyle uçtan uca: senkron → "Dönemi Kodla" → tüm faturalarda kodlama rozeti dolu,
   hiçbir satır önerisiz değil (fallback dahil).
2. Bir faturayı düzeltip onayla → aynı satıcıdan yeni fatura kodlandığında düzeltilen kod
   **hafızadan** (%95+) gelir; onay kuyruğu ikinci turda kısalır.
3. Kural ekle ("akaryakıt;benzin" → 770.01) → hafızasız yeni kalem K2'den 0.75 güvenle kodlanır.
4. Onaysız dönemde CSV indirme 400 "Seçilen aralıkta onaylanmış fatura yok." döner;
   onay sonrası indirilen fiş CSV'sinde her fatura için borç toplamı = alacak (dengeli fiş)
   ve KDV satırları oran eşlemesine uygundur.
5. Tüm ekranlarda boş/yükleniyor/hata durumları ve Türkçe mesajlar; hesap kodu doğrulaması
   hem UI hem backend'de aynı desenle çalışır.
6. Yeni mükellef oluşturulduğunda ClientCodingSettings otomatik oluşur (770.99 / 320 / 0.85).

## Test adımları

**Manuel (Mock ile):**
1. Mükellef + Mock entegrasyon + senkron (Faz 1 akışı) → Faturalar'da "Kodlanmadı" rozetlerini gör.
2. "Dönemi Kodla" → sonuç bandı ve rozet dağılımını doğrula (kural/hafıza yokken çoğu
   satır fallback 0.30 → NeedsReview beklenir).
3. Kurallar → "akaryakıt;benzin;motorin" → 770.01, öncelik 10, tüm mükellefler → kaydet;
   geçersiz regex "([a-z" ile 400 "Geçersiz desen." doğrula.
4. Onay Kuyruğu → "Faturaya Git" → çekmece otomatik açılır → kodları düzelt → "Onayla" →
   "Onaylandı — sistem bu eşlemeyi öğrendi ✓" bandını gör.
5. Aynı satıcının başka faturasını tek tek "Kodla" → güven rozeti yeşil (%95+) ve kaynak
   hafıza olmalı; cari kod her iki faturada aynı olmalı.
6. Dışa Aktar → önizleme sayısını doğrula → CSV indir → Excel'de aç: Türkçe karakterler,
   virgül ondalık, borç=alacak dengesi.

**Otomatik (planlanan):** backend xUnit — TextNormalizer (Türkçe küçük harf, imza, kısaltma),
CodingEngine kademe sırası + fallback + cari sayaç + onay/öğrenme, RuleMatches (regex/anahtar
kelime/geçersiz desen), LucaExcelExporter denge + BOM + kaçış; web Playwright — batch kodlama
+ onay akışı + boş durumlar.

> **Not (geliştirme ortamı):** Şema `EnsureCreated` ile oluşturulduğundan Faz 2 tabloları ve
> yeni kolonlar (EInvoice.CodingStatus, EInvoiceLine.Confidence/CariCode) ancak boş
> veritabanında oluşur. Mevcut geliştirme DB'sini sıfırlayın
> (`docker compose down -v && docker compose up -d`) veya migration'a geçin.

## Ek: Listede kod görünürlüğü + toplu onay (sonradan eklendi)

**Kod görünürlüğü.** `EInvoiceListItemResponse`'a iki alan eklendi:

- `AccountCodeSummary`: satırların etkili kodlarının (`AccountCode ?? SuggestedAccountCode`)
  DISTINCT birleşimi — en fazla 3 kod virgülle, fazlası `" +N"` (ör. `770.01, 153.01 +2`);
  hiç kod yoksa null.
- `CariCode`: ilk satırın cari kodu.

Liste (`GET /api/einvoices`) ve onay kuyruğu sorguları satırları `Include` ile TEK sorguda
çeker (N+1 yok). Web'de "Hesap Kodu" ve "Cari" kolonları mono yazıyla gösterilir;
`codingStatus` **Approved** ise kod kesin (normal), diğer durumlarda öneri görünümü
(soluk/italik + "öneri" etiketi) kullanılır.

**Toplu onay / toplu kodlama uçları.**

- `POST /api/einvoices/bulk-approve` — gövde `{ids: Guid[]}` (en çok 500, tekrarlar ayıklanır).
  Her fatura için: tenant'a ait değilse → failed; `Approved` ise → skipped;
  `Uncoded` ise önce `CodeInvoiceAsync`, sonra `ApproveCodingAsync` **düzeltmesiz** çağrılır
  (önerilen kodlar kesinleşir, hafıza/satıcı varsayılanı ÖĞRENİR). Tek faturanın hatası
  diğerlerini durdurmaz (try/catch + log → failed). Dönüş:
  `{approved, skipped, failed, message}` Türkçe özetle
  (ör. "42 fatura onaylandı, 3 atlandı (zaten onaylı), 2 hata.").
- `POST /api/einvoices/bulk-code` — aynı gövde; seçilen faturalar yalnız motordan geçirilir
  (onaylanmaz), `Approved` olanlar atlanır. Dönüş:
  `{autoCoded, needsReview, skipped, failed, message}`.

**Web.** Faturalar tablosunda en solda seçim kutuları (başlıktaki kutu yalnız görünen sayfayı
seçer); seçim varken yapışkan çubuk: "N fatura seçili — [Toplu Onayla] [Toplu Kodla] [Vazgeç]".
Toplu Onayla onay penceresi gösterir ("N fatura önerilen kodlarla onaylanacak. Motor bu
eşlemeleri öğrenecek. Devam edilsin mi?"), sonuç bandı backend mesajını basar ve liste
yenilenir; sayfa/filtre değişiminde seçim sıfırlanır. Onay Kuyruğu sayfasında da aynı seçim +
"Toplu Onayla" akışı ve "Önerilen Kod" kolonu vardır (ortak bileşen:
`web/components/AccountCodeText.tsx`).
