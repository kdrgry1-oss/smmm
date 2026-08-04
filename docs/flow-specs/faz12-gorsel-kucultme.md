# Faz 12 — Fiş Görseli Küçültme Hattı (Resize/Compress)

Amaç: fiş fotoğraflarını OCR'ın güvenle okuyabileceği **MİNİMUM** boyuta indirerek
disk kullanımını ve vision-LLM OCR maliyetini yaklaşık **10 kat** düşürmek. Küçültme
üç katmanda, **aynı parametrelerle** yapılır; sunucu katmanı son güvencedir.

---

## 0. Önce OCR, sonra sıkıştırma (revizyon)

**Gerekçe (saha bulgusu):** müşteriler fişleri çoğunlukla WhatsApp'tan gönderir ve
WhatsApp görseli zaten agresif biçimde sıkıştırır. Eski sıra (yükle → sunucuda
sıkıştır → OCR sıkıştırılmış dosyadan) bu görsellerde **çift sıkıştırma** üretiyor
ve OCR okuma isabetini gözle görülür düşürüyordu. Yeni kural: **OCR her zaman
ORİJİNAL görüntüyü görür; disk her zaman sıkıştırılmış kopyayı tutar.**

Sunucu fiş hattının yeni sırası (`ReceiptService.UploadAndProcessAsync` — hem normal
yükleme hem portal **Fis** yolu, `PortalController` köprüsüyle):

```
yükleme (10 MB doğrulaması burada, DEĞİŞMEDİ)
   │
   ▼
storage/temp/{guid}{orijinal-uzantı}      ← orijinal, DOKUNULMAMIŞ (SaveReceiptTempAsync)
   │
   ▼
OCR (IReceiptOcrProvider.ExtractAsync)    ← girdi = temp'teki ORİJİNAL (kalite kaybı sıfır)
   │
   ▼  (başarılı YA DA başarısız — her iki durumda)
ImageNormalizer → storage/receipts/{tenantId}/{guid}.jpg   ← SIKIŞTIRILMIŞ kalıcı kopya
   │                                         (PromoteReceiptFromTempAsync; PDF/HEIC/decode
   │                                          edilemeyen türler passthrough — olduğu gibi)
   ▼
temp dosyası SİLİNİR (try/finally garantisi; silme hatası yutulur + loglanır)
```

Notlar:

- Temp adı **Guid**'dir → eşzamanlı yüklemelerde çakışma olmaz; uzantı korunur
  (OCR sağlayıcısı `media_type`'ı uzantıdan türetir).
- `Receipt.FileName`/`StoredPath` **kalıcı (sıkıştırılmış)** dosyayı gösterir — fiş
  listesi, önizleme endpoint'i ve Luca export hiçbir değişiklik hissetmez.
- Başarılı akışta info log: `"OCR orijinalden yapıldı ({OrigBytes} B), depoya
  sıkıştırılmış yazıldı ({NewBytes} B)"`.
- OCR okuyamazsa mevcut davranış korunur: kalıcı kopya da silinir, kullanıcıya
  dosya bazlı Türkçe hata döner (portalda belge portal deposuna yine kaydedilir).
- **`ImageProcessing:Enabled` semantiği değişti:** bayrak artık YALNIZCA diske
  yazılan kopyanın sıkıştırılmasını kontrol eder. `false` yapılsa da OCR orijinali
  okur; kalıcı kopya orijinal (sıkıştırmasız) saklanır. Önerilen değer: `true`.
- Resize gerektirmeyen küçük görselde q≥92 kuralı (çift sıkıştırma koruması) depolama
  kopyası için aynen korunur.

## 1. Parametreler ve gerekçeleri

| Parametre | Değer | Gerekçe |
|---|---|---|
| Uzun kenar (maks) | **1600 px** | Anthropic vision API 1568 px üstünü ZATEN kendisi küçültür — daha büyük göndermek yalnızca bant genişliği/disk israfıdır. 1600 px, tipik bir fişteki en küçük yazıyı (~6-8 pt termal baskı) hâlâ net okunur tutar; OCR doğruluğu üzerinde ölçülebilir kaybı yoktur. En-boy oranı korunur, **küçük görüntü büyütülmez**. |
| JPEG kalitesi | **75** | q75, metin kenarlarında görünür artefakt üretmeden dosya boyutunu q90'a göre ~%50-60 düşürür. Fiş = yüksek kontrastlı siyah metin; q75'te OCR alan çıkarımı etkilenmez. |
| Çıktı biçimi | **JPEG** | PNG/HEIC girişler de JPEG'e dönüştürülür (fişte şeffaflık/kayıpsızlık gereksizdir). **PDF'e DOKUNULMAZ** — olduğu gibi saklanır ve Anthropic'e `document` bloğu olarak gider. |

Kabaca kazanım: modern telefon fotoğrafı ~3-4 MB (4000×3000, q~90) →
1600 px uzun kenar + q75 ile **~200-300 KB**. Yani hem diskte hem base64 yükünde
(OCR isteği gövdesi) **~10-15x** küçülme; görüntü token maliyeti de 1568 px sınırına
zaten inen görüntüyle aynı kalır, ağ süresi ciddi kısalır.

## 2. Üç katman akışı

```
[MOBİL]  image_picker (native): maxWidth/Height 1600, imageQuality 75 → JPEG üretir
   │        (HEIC hiç oluşmaz; requestFullMetadata:false → EXIF/konum istenmez)
   ▼
[WEB]    lib/imageResize.ts (canvas): 1600 px / q0.75 JPEG'e çevirir; PDF/HEIC passthrough
   │        (hata olursa passthrough — yükleme ASLA engellenmez; kullanıcıya sessiz)
   ▼
[SUNUCU] Infrastructure/ImageNormalizer.cs (ImageSharp): SON GÜVENCE — kaynak ne
            gönderirse göndersin DEPOLAMA kopyasını normalize eder ve diske ÖYLE yazar
            (fiş hattında OCR'dan SONRA çalışır — bkz. §0).
```

### Katman 1 — Sunucu (`ImageSharpNormalizer`, güvence katmanı)

Fiş hattında `FileStorage.PromoteReceiptFromTempAsync` (OCR **sonrası**, bkz. §0),
portalda `SavePortalDocumentAsync` (yalnız **Fis** kategorisi) hattına bağlıdır. Adımlar:

1. Uzantıdan görsel tespiti (jpg/jpeg/png; HEIC ayrı, aşağıda).
2. **EXIF orientation uygulanır** (`AutoOrient`) — metadata silinmeden ÖNCE, yoksa fiş yan/ters kalır.
3. Uzun kenar > `MaxLongEdge` ise **Lanczos3** ile küçültme (metin keskinliği için en iyi filtre); küçük görsel büyütülmez.
4. **JPEG q75** olarak kaydedilir → kayıtlı uzantı **`.jpg`** olur; `Receipt.StoredPath`
   ve `Receipt.FileName` uzantısı buna eşitlenir (içerik türü/önizleme uzantıdan türetilir).
5. **EXIF/XMP/IPTC metadata SİLİNİR** (KVKK — aşağıda). ICC renk profili korunur.
6. Orijinal/yeni bayt sayısı ve nihai piksel boyutu loglanır (kazanım ölçümü).

Notlar:

- **10 MB girdi sınırı normalize ÖNCESİ uygulanmaya devam eder** (`ValidateReceiptFile`).
- `ImageProcessing:Enabled=false` → hat passthrough. Revizyon sonrası bu bayrak
  YALNIZCA depolama sıkıştırmasını kapatır (kalıcı kopya orijinal kalır); OCR her
  durumda temp'teki orijinali okur (bkz. §0).
- Decode edilemeyen/bozuk dosya passthrough kaydedilir — normalizasyon kayıt akışını
  ASLA engellemez; OCR yine dener, okunamazsa mevcut Türkçe hata akışı çalışır.
- `LlmVisionOcrProvider` uzantı tabanlı `media_type` tespitini korur: OCR girdisi artık
  temp'teki ORİJİNAL dosya olduğundan uzantı da orijinaldir (jpg → `image/jpeg`,
  png → `image/png`, pdf → `document` bloğu).

### Katman 2 — Web (`web/lib/imageResize.ts`)

- `createImageBitmap` + `OffscreenCanvas` varsa onlar, yoksa `<img>` + `<canvas>` fallback.
- Aynı parametreler: 1600 px / JPEG kalite 0.75; PNG şeffaflığı beyaz zemine düşer.
- Yalnızca tarayıcının decode edebildiği türler işlenir (`image/jpeg|png|webp`);
  PDF, HEIC ve ofis belgeleri passthrough.
- Sonuç orijinalden BÜYÜKSE orijinal korunur (zaten küçük dosyada kayıp yaşatmamak için).
- **Her hata passthrough'a düşer** — yükleme asla engellenmez. Kullanıcıya küçültme
  oranı hissettirilmez (sessiz); portal dosya listesinde yalnızca nihai boyut görünür.
- Entegrasyon: `panel/fisler/page.tsx` (`handleFiles`) ve `portal/[token]/page.tsx` (`addFiles`).

### Katman 3 — Mobil (en etkili yer — çekim anında)

`receipt_upload_screen.dart` içindeki `image_picker` çağrılarına (kamera + galeri):

```dart
maxWidth: 1600, maxHeight: 1600, imageQuality: 75, requestFullMetadata: false
```

- `image_picker` küçültmeyi **native** yapar; ek paket GEREKMEZ.
- Boyut/kalite parametresi verildiğinde iOS'ta çıktı **HEIC değil JPEG** olur —
  HEIC sorunu kaynağında çözülür (vision API HEIC kabul etmez).
- `requestFullMetadata: false`: iOS galeri seçiminde tam metadata (konum dahil) istenmez.

## 3. HEIC istisnası

ImageSharp 3.x HEIC **decode edemez** (patent/codec). Sunucuya yine de HEIC ulaşırsa
(eski istemci, doğrudan API çağrısı): dosyaya **dokunulmaz**, olduğu gibi saklanır ve
normalizasyon sonucuna `HeicUnsupported=true` işareti konup uyarı loglanır. OCR
sağlayıcısı HEIC'te zaten kullanıcıya Türkçe "JPG'ye dönüştürün" hatası verir.
Pratikte mobil katman artık HEIC üretmediği ve web katmanı da JPEG'e çevirdiği için
bu yol istisnadır; kalıcı çözüm istenirse ileride `libheif` tabanlı bir decoder eklenir.

## 4. KVKK / EXIF temizliği

Telefon fotoğrafları EXIF içinde **GPS konumu**, cihaz modeli ve çekim zamanı taşır —
mükellefin/personelin konum verisidir ve fiş işleme amacı için gereksizdir (veri
minimizasyonu). Sunucu normalizasyonu EXIF/XMP/IPTC profillerini **siler** (orientation
bilgisi silinmeden önce piksellere uygulanır). Mobilde `requestFullMetadata: false` ve
web'de canvas yeniden kodlaması da metadata'yı zaten taşımaz. ICC renk profili OCR
doğruluğu için korunur (konum/kişisel veri içermez).

## 5. Yapılandırma

`appsettings.json`:

```json
"ImageProcessing": {
  "Enabled": true,
  "MaxLongEdge": 1600,
  "JpegQuality": 75
}
```

EnvVar override .NET config ile hazırdır: `ImageProcessing__Enabled`,
`ImageProcessing__MaxLongEdge`, `ImageProcessing__JpegQuality`.
Web/mobil değerleri sabittir (`imageResize.ts` ve `receipt_upload_screen.dart`);
sunucu değeri değiştirilirse istemci sabitleri de güncellenmelidir.

## 6. Bağımlılık ve lisans

- **SixLabors.ImageSharp 3.1.x** (yalnızca bu paket eklendi). Lisans: **Six Labors
  Split License** — yıllık geliri 1M USD altındaki şirketler için ücretsizdir
  (Apache 2.0 koşullarıyla); üstündeyse ticari lisans gerekir. README'de not vardır.
  Güvenlik yamaları için 3.1.x'in en güncel patch sürümünde kalınmalıdır.

## 7. Sınırlamalar / bilinçli kararlar

- Var olan (eski) kayıtlar geriye dönük küçültülmez — hat yalnızca yeni yüklemelere uygulanır.
- PDF içindeki görüntüler sıkıştırılmaz (PDF'e dokunmama kuralı).
- Portalda Fis DIŞI kategorilerdeki görseller sunucuda normalize edilmez (belge
  aslının korunması tercih edildi); web katmanı yine de küçültür.
- HEIC sunucuda dönüştürülmez (yukarıdaki istisna).

## 8. Kademeli OCR (ucuz model → güçlü model fallback)

**Gerekçe (saha bulgusu):** müşteriler fişleri çoğunlukla WhatsApp'tan gönderir ve
WhatsApp görselleri agresif biçimde yeniden sıkıştırır. Bu görsellerde ucuz model
(`claude-haiku-4-5`) "okuyamadım" dönebiliyorken sonnet sınıfı model aynı görseli
okuyabiliyor. Ürün gereksinimi: WhatsApp fişleri OKUNMALI — bu yüzden tek modele
bağlı kalınmaz.

**Akış** (`LlmVisionOcrProvider`):

1. Görsel önce **birincil (ucuz) model** ile okunur (`Ocr:Anthropic:Model`).
2. Sonuç şu koşullardan birini sağlıyorsa AYNI görsel **fallback model** ile
   (`Ocr:Anthropic:FallbackModel`, varsayılan `claude-sonnet-4-5`) bir kez daha okunur:
   - ana alanlar boş (satıcı adı boş VE toplam 0),
   - genel güven < `Ocr:Anthropic:FallbackThreshold` (varsayılan **0.5**),
   - uyarılarda "okunamadı / düşük kalite / bulanık" sinyali (boş ya da
     çözümlenemeyen model yanıtı da bu sınıfa girer).
3. İki sonuçtan **iyisi** seçilir (ana alanları dolduran ya da güveni yüksek olan);
   fallback devreye girdiyse sonuca kullanıcı görünür `"Güçlü modelle yeniden okundu."`
   uyarısı eklenir ve info log'a iki denemenin güvenleri yazılır.

**Maliyet mantığı:** fişlerin büyük çoğunluğu haiku ile tek çağrıda okunur (ucuz);
yalnızca zayıf/başarısız okumalarda ikinci (pahalı) çağrı yapılır. Çift maliyet
korumaları: fallback **en fazla bir kez** denenir; `FallbackModel` boşsa ya da
birincil modelle AYNIYSA ikinci çağrı hiç yapılmaz; retryable hatalarda (429/5xx/
ağ/zaman aşımı) fallback tetiklenmez — güçlü model bunları çözmez. Deploy tarafında
kademe `ANTHROPIC_MODEL` (varsayılan haiku) + `ANTHROPIC_FALLBACK_MODEL`
(varsayılan sonnet) env değişkenleriyle yönetilir. OpenAI sağlayıcısında kademe
yoktur (istenirse ayrıca eklenir).
