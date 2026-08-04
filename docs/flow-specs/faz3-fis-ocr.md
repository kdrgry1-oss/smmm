# Flow Spec — Faz 3: Fiş OCR → Gider Modülü

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu modül ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

## Özellik

Kağıt fiş / masraf belgesi fotoğrafı (JPG/PNG/PDF/HEIC) yüklenir; sistem dosyayı **webroot
dışında** saklar, OCR ile alanları çıkarır (satıcı, VKN, tarih, toplam, KDV oranı+tutarı,
matrah), **gider gösterilebilirlik** kontrolünü yapar ve Faz 2 kod motorunun **aynı kademe
mantığıyla** gider hesabı önerir. Fiş her zaman **insan onayına** düşer (NeedsReview);
onayda öğrenme fatura onayıyla **aynı upsert yoluna** (AccountMemory/VendorDefault/VendorCariMap)
yazılır ve onaylı fişler Luca CSV'sine **Kaynak=FİŞ** kolonuyla dahil edilir.

## Kaynak akış referansları

- Blueprint M3 (fiş yükleme + OCR + KDV/matrah tespit) ve §6.2 (fiş → gider → Luca satırı).
- Motor tasarımı B8: "Fiş OCR → Gider — aynı motora bağlanır."
- OCR sağlayıcısı adapter arkasında (blueprint §1.5 madde 5): `IReceiptOcrProvider` →
  bu fazda `MockOcrProvider`; gerçek Google Document AI / AWS Textract, sağlayıcı hesabı
  açıldığında TEK sınıfla eklenecek (fatura modülündeki `IEInvoiceProvider` deseninin aynısı).
- Gider gösterilebilirlik dayanağı: GVK md. 41 ve KVK md. 11 kapsamında alkollü içki/tütün
  giderlerinin indirim kısıtı (basit anahtar kelime kuralı; KKEG/B3 otomatiği sonraki fazda).
- e-Fatura/e-Arşiv XML'ine OCR uygulanmaz (blueprint M3 notu) — OCR yalnızca kağıt fiş içindir.

## Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Dosya deposu | `IFileStorage`/`LocalFileStorage`: `storage/receipts/{tenantId}/{guid}.{ext}` — webroot dışı, path traversal korumalı, kayıtlı ad sunucuda üretilir |
| Dosya sınırları | jpg/jpeg/png/pdf/heic; **10 MB üstü → "Dosya 10 MB'den büyük olamaz."** |
| Mock OCR | Dosya adı+boyutundan **deterministik** üretim; market/akaryakıt/restoran/kırtasiye/tekel profilleri; güven 0.55–0.98; 0–44 gün önce tarih (eski belge uyarısı test edilir) |
| Kod önerisi | `CodingEngine.SuggestForReceiptAsync` — K1a hafıza (imza=satıcı adı + KDV oranı) → K1b satıcı varsayılanı → K2 kural (satıcı adı + kalem özeti metni) → fallback varsayılan gider hesabı (0.30). Fatura akışına dokunulmadı |
| Güven | `Receipt.Confidence = min(OCR güveni, öneri güveni)`; onayda 1.0 |
| Onay politikası | Fişte **tam otomatik onay yok** — her fiş NeedsReview başlar; güven ≥ 0.85 ve gider uygunsa yalnızca **"Otomatik onaya hazır"** rozeti gösterilir |
| Uygunluk | alkol/bira/şarap/rakı/viski/tütün/sigara/tekel... → `ExpenseEligible=false` + "Alkol/tütün giderleri indirim konusu yapılamaz."; belge > 30 gün eski → ⚠ uyarı ama eligible kalır |
| Uygunsuz fişi onaylama | Ancak "Yine de gider göster" (OverrideEligibility) işaretlenirse; gerekçe denetim izi için saklanır |
| Öğrenme | Onayda `LearnFromReceiptApprovalAsync` — fatura onayıyla aynı AccountMemory/VendorDefault/VendorCariMap upsert'i |
| Cari | Fişte otomatik cari AÇILMAZ; VendorCariMap'te varsa önerilir. Luca fişinde cari yoksa alacak **100 (Kasa)** |
| Luca export | Onaylı fişler yevmiye/liste CSV'lerine dahil; yeni **Kaynak** kolonu: FATURA/FİŞ; fiş belge no `FIS-XXXXXXXX` |
| Red | Gerekçe **zorunlu** (`RejectReason` kolonu); onaylı fiş reddedilemez/silinemez (denetim izi) |

## Ön koşullar

1. SMMM oturum açmış (JWT); en az bir mükellef tanımlı.
2. (İsteğe bağlı) Faz 2 kuralları/hafızası dolu — öneri güveni yükselir.
3. Yükleme için mükellef seçilmiş olmalı (fiş mükellefe bağlıdır).

## Adım adım kullanıcı akışı

1. **Aksiyon:** Fişler sayfasında mükellef seçer, fiş dosyalarını sürükler ya da tıklayıp seçer.
   → **Beklenen:** `POST /api/clients/{id}/receipts` (multipart, çoklu dosya, ilerleme çubuğu);
   her dosya ayrı işlenir; yeşil özet bandı "3 fiş yüklendi ve işlendi, 1 dosya hata verdi." ve
   hatalı dosyalar için **dosya bazlı** kırmızı satırlar. Liste tazelenir; her fiş
   "Onay Bekliyor" rozetiyle görünür, güveni yüksek olanlarda "Otomatik onaya hazır" rozeti.
   → **Hatalar:** Mükellef seçilmemiş → "Fiş yüklemek için önce bir mükellef seçin.";
   10 MB üstü → "Dosya 10 MB'den büyük olamaz."; desteklenmeyen tür → "Desteklenmeyen dosya
   türü..."; boş dosya → "Dosya boş görünüyor..."; 20'den çok dosya → "Tek seferde en fazla 20
   dosya yükleyebilirsiniz."
2. **Aksiyon:** Fiş satırına tıklar; detay çekmecesi açılır.
   → **Beklenen:** Görsel önizleme (`GET /api/receipts/{id}/file` blob → object URL; PDF/HEIC
   "yeni sekmede aç"); OCR alanları düzenlenebilir input'larda önceden dolu; güven rozeti;
   öneri hesap kodu görünür ("Öneri: 770.99").
   → **Hatalar:** Dosya diskte yoksa → "Fiş dosyası bulunamadı. Dosya silinmiş olabilir.";
   önizleme hatası detay akışını bozmaz ("Önizleme yüklenemedi.").
3. **Aksiyon:** Alkol/tütün fişinde onay dener.
   → **Beklenen:** Kırmızı uyarı bandı "Gider gösterilemez: Alkol/tütün giderleri indirim
   konusu yapılamaz."; Onayla → engellenir; "Yine de gider göster (sorumluluğu üstleniyorum)"
   işaretlenirse onay geçer (yasal kalkan: nihai karar SMMM'de).
4. **Aksiyon:** 30 günden eski tarihli fişi açar.
   → **Beklenen:** Sarı uyarı "⚠ Belge tarihi 30 günden eski..." — onay engellenmez.
5. **Aksiyon:** Alanları düzeltir (ör. hesap kodunu 770.30 yapar), **Onayla**'ya basar.
   → **Beklenen:** `POST /api/receipts/{id}/approve`; fiş Approved, güven %100; yeşil bant
   "Onaylandı — sistem bu eşlemeyi öğrendi ✓". Aynı satıcının SONRAKİ fişi yüklendiğinde
   öneri **hafızadan** (%95+) gelir.
   → **Hatalar:** Hesap kodu boş → "Hesap kodu boş olamaz."; biçimsiz kod → "Geçerli bir hesap
   kodu girin (ör. 770.01)."; tarih boş → "Belge tarihi boş olamaz."; tutar ≤ 0 → "Toplam tutar
   0'dan büyük olmalıdır."; onaylı fişi tekrar onaylama → "Yalnızca onay bekleyen fişler onaylanabilir."
6. **Aksiyon:** **Reddet** → gerekçe girer → onaylar.
   → **Beklenen:** `POST /api/receipts/{id}/reject`; fiş Rejected, gerekçe detayda görünür.
   → **Hatalar:** Gerekçe boş → "Red gerekçesi zorunludur."
7. **Aksiyon:** Dışa Aktar sayfasında dönem seçer.
   → **Beklenen:** Önizleme "N onaylanmış fatura (toplam X) ve M onaylanmış fiş (toplam Y)";
   CSV'de fiş satırları **Kaynak=FİŞ**, borç = gider hesabı + KDV (191.xx), alacak = cari veya
   **100 Kasa**; fiş dengesi borç=alacak.
   → **Hatalar:** Hiç onaylı belge yoksa → "Seçilen aralıkta onaylanmış fatura veya fiş yok."

## Uç durumlar

- **Kısmi başarı:** 5 dosyanın 2'si hatalıysa 3'ü işlenir; sonuç dosya bazlı raporlanır (all-or-nothing değil).
- **OCR başarısızlığı:** Dosya diske bırakılmaz (silinir), fiş kaydı açılmaz, Türkçe hata döner.
- **Path traversal:** `../` veya mutlak yol içeren StoredPath çözümlemesi reddedilir; kayıtlı ad zaten sunucu üretimidir (Guid).
- **Aynı dosya iki kez:** Bilinçli olarak İZİN VERİLİR (iki ayrı fiş kaydı) — kağıt fişte güvenilir bir tekillik anahtarı yok; mükerrer kontrolü (B5) sonraki fazda tutar+tarih+VKN benzerliğiyle eklenecek.
- **`<img src>` token taşıyamaz:** Önizleme Authorization'lı fetch + object URL ile yapılır; URL'ler kapanışta revoke edilir.
- **Silme:** Yalnızca Uploaded/NeedsReview; DB kaydı silinir, dosya da silinir (dosya hatası kaydı geri getirmez, loglanır).
- **Tenant izolasyonu:** Receipt global query filter'lı; başka tenant'ın fişi/dosyası 404.
- **Onayda eksik matrah:** Toplam+oran varsa matrah/KDV geriye hesaplanır; oran da yoksa matrah=toplam, KDV=0.

## Kabul kriterleri

1. Mock ile uçtan uca: çoklu yükleme → tüm fişlerde satıcı/tarih/tutar/KDV/matrah dolu, güven
   rozeti 0.55–0.98 bandında dağılır, hepsi NeedsReview.
2. Tekel fişinde onay engeli + override akışı çalışır; akaryakıt/market fişi normal onaylanır.
3. Bir satıcının fişi onaylandıktan sonra aynı satıcının yeni fişi %95+ güvenle hafızadan önerilir.
4. 10 MB üstü ve .docx gibi dosyalar dosya bazlı Türkçe hatayla reddedilir; diğer dosyalar işlenmeye devam eder.
5. Onaylı fişler Luca CSV'sinde Kaynak=FİŞ ile ve dengeli fiş olarak yer alır; fatura kolonları değişmedi (yalnız Kaynak eklendi).
6. Tüm ekran durumları (boş/yükleniyor/hata/ilerleme) ve Türkçe mesajlar eksiksiz.

## Test adımları

**Manuel (Mock ile):**
1. Mükellef seç → 4-5 farklı adlı görsel yükle → dosya bazlı sonuçları ve rozet dağılımını doğrula.
2. Adında "tekel" geçmese bile MockOcr bazı dosyaları tekel profiline düşürür (deterministik);
   böyle bir fişte onay engelini ve override'ı test et.
3. Bir fişi düzeltip onayla → aynı DOSYAYI farklı adla tekrar yükle (aynı satıcı profili
   çıkacak şekilde) → önerinin hafızadan geldiğini gör.
4. 11 MB'lik dosya + .txt dosyası + geçerli bir JPG'yi birlikte yükle → 2 dosya hatası, 1 başarı.
5. Reddet (boş gerekçeyle dene → hata; gerekçeyle → Rejected).
6. Dışa Aktar → önizlemede fiş sayısını gör → CSV'yi indir → FİŞ satırlarının borç=alacak dengesini doğrula.

**Otomatik (planlanan):** backend xUnit — LocalFileStorage (uzantı/boyut/traversal),
MockOcrProvider determinizmi, ReceiptService (uygunluk kuralı, onay kapısı, öğrenme çağrısı),
CodingEngine.SuggestForReceiptAsync kademe sırası; web Playwright — yükleme + onay/red akışı.

> **Not (geliştirme ortamı):** Şema `EnsureCreated` ile oluşturulduğundan Receipts tablosu ancak
> boş veritabanında oluşur. Mevcut geliştirme DB'sini sıfırlayın
> (`docker compose down -v && docker compose up -d`) veya migration'a geçin.
