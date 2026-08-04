# Flow Spec — Faz 8: KDV / Kapanış Hızlandırma Paketi (B2, B3, B5, B10)

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Kaynak: motor dokümanı §3
> (B2 tevkifat, B3 KKEG, B5 mutabakat, B10 dönem sonu kontrol) + §1 "KDV kuralları motora gömülü".
> Bu faz ancak bu dokümandaki tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

---

## B2 — Tevkifatlı Fatura (KDV-2) Otomatik Tespit + Beyan Hazırlığı

### Özellik

Motor, GELEN (alış) fatura kalemlerini global **WithholdingRule** tablosundaki anahtar
kelimelerle eşleştirir; eşleşen satıra **tevkifat kodu + oran (pay/payda)** önerir ve
**tevkif edilen KDV**'yi hesaplar (`WithholdingVatAmount = VatAmount × pay/payda`).
SMMM onay çekmecesinde kodu/oranı düzeltebilir veya temizleyebilir. **KDV2 taslağı**
dönemdeki ONAYLI tevkifatlı alış faturalarından kod bazlı kırılımla hazırlanır ve
mevcut beyanname akışıyla (hazırla → gönder → tahakkuk) GİB'e gider.

### Kaynak akış referansları

- KDV Genel Uygulama Tebliği — kısmi tevkifat bölümü (35 Seri No'lu tebliğ sonrası oranlar).
- e-Fatura UBL tevkifat kod listesi (601, 602, ...).
- Mevcut beyanname akışı: `DeclarationService` (Faz 5) — KDV2 aynı yaşam döngüsünü kullanır.

### Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Kural tablosu | `WithholdingRule` GLOBAL (tenant'sız; TaxParameter deseni): Code unique, Fraction "4/10", KeywordPattern ";" ayraçlı |
| Tespit kapsamı | Yalnızca `Direction=Incoming`; giden faturada tevkifat alanları hep temiz |
| Yeniden kodlama | Eşleşme yoksa alanlar TEMİZLENİR (tutarlı recompute); onaylı fatura zaten yeniden kodlanamaz |
| Güven/akış | Tespit yalnızca alan yazar; kodlama güven/durum mantığına DOKUNMAZ (düşük güveni onay kuyruğu zaten yakalar) |
| Onayda düzeltme | `ApproveLineRequest.WithholdingCode`: null=dokunma, ""=temizle, dolu=uygula; oran verilirse tevkif KDV yeniden hesaplanır; tutar açıkça gönderildiyse o esas |
| KDV2 taslağı | Yalnızca ONAYLI faturaların tevkifatlı satırları; kod+oran kırılımı; `OdenecekKdv` alan adı MockGib tahakkuk sözleşmesiyle aynı ("odenecekKdv") |
| KDV1 bilgisi | KDV1 özetine `TevkifatKdvToplam` bilgi satırı eklendi (KDV1 hesabını DEĞİŞTİRMEZ) |

### Adım adım kullanıcı akışı

1. SMMM faturayı kodlar ("Kodla" veya "Dönemi Kodla") → tevkifat eşleşen satırda mor
   **"Tevkifat 601 (4/10)"** rozeti + otomatik açılan Tevkifat/KKEG paneli.
2. Onay çekmecesinde kodu açılır menüden değiştirir (oran otomatik dolar), oranı elle
   düzeltebilir, "Tevkifat yok" seçerek temizleyebilir → "Onayla".
   → **Hatalar:** kod 3 hane değil / oran "9/10" biçiminde değil → satır bazlı Türkçe mesaj (UI + backend).
3. Beyannameler sayfası → Tür: **KDV-2 (tevkifat)** → "KDV Taslağı Hazırla".
   → **Beklenen:** kod bazlı kırılım tablosu + "sorumlu sıfatıyla ödenecek KDV" toplamı.
   → **Uç durum:** dönemde tevkifatlı onaylı fatura yoksa özet 0 + yönlendirme metni.
4. "GİB'e Gönder" → "Durumu Sorgula" → tahakkuk (mevcut Faz 5 akışı, MockGib).

### Kabul kriteri

Tevkifat anahtar kelimeli kalem kodlanınca rozet + hesaplanmış tevkif KDV görünür; onayda
düzeltme kalıcıdır; KDV2 taslağı yalnızca onaylı satırları toplar; tevkifatsız faturada
davranış Faz 7 ile birebir aynıdır.

---

## B3 — KKEG / Binek Oto Gider Kısıtı

### Özellik

`KkegAnalyzer` kural seti (kodlama motoru + fiş OCR hattına gömülü):

- **(a) Binek oto işaretleri** ("binek", "otopark", "otoyol", "hgs", "ogs"...; **akaryakıt
  YALNIZCA** mükellef ayarında "Binek araç var" işaretliyse) → matrahın **%30'u KKEG**
  (parametre `binek.gider.kisit.oran` = indirilebilir %70) ve **KDV'sinin aynı oranı indirilemez**.
- **(b) Araç kiralama** ("araç kira", "rent a car"...) → aylık tutar `binek.kira.aylik.sinir`
  (46.000 TL, 2026) üstündeyse **aşan kısım KKEG**; sınır altı kiralama işaretlenmez.
- **(c) Ceza / gecikme zammı-faizi** → **tam KKEG** ("Cezalar gider yazılamaz");
  temsil-ağırlama anahtar kelimeleri de tam KKEG ön işareti alır (SMMM düzeltebilir).

### Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Veri modeli | Kalem bazlı: `EInvoiceLine`/`Receipt` → `IsKkeg` + `KkegReason` + `KkegAmount` (KDV hariç matrah payı) |
| Mükellef ayarı | `ClientCodingSettings.HasCompanyVehicle` (Mükellefler → Düzenle modalında "Binek araç var (KKEG)") |
| İndirilemeyen KDV | Tek formül (`KkegAnalyzer.DisallowedVat`): satır KDV'si × (KKEG tutarı / matrah); tam KKEG'de KDV'nin tamamı — Luca export ve KDV1 aynı formülü kullanır |
| Luca yevmiye | KKEG satırı ayrı borç satırı: hesap `kkeg.hesap` parametresi (varsayılan **689**), tutar = KKEG matrahı + indirilemeyen KDV (aynı satırda), açıklamada gerekçe; 191 satırı indirilemeyen KDV kadar azalır; fiş dengesi korunur |
| KDV1 taslağı | KKEG'e isabet eden KDV kırılım satırlarından düşülür; özet `KkegIndirilemeyenKdv` + `KkegToplam` alanlarını gösterir |
| Onayda düzeltme | Fatura satırı + fiş onayında `IsKkeg` null=dokunma / false=temizle / true=işaretle (tutar + gerekçe düzenlenebilir; tutar matrahı aşamaz) |
| Davranış koruması | KKEG işareti olmayan belgede motor, export ve KDV1 çıktısı Faz 7 ile birebir aynı |

### Adım adım kullanıcı akışı

1. Mükellefler → Düzenle → "Binek araç var (KKEG)" işaretle (akaryakıt kuralı açılır).
2. Fatura kodla → KKEG eşleşen satırda turuncu **KKEG** rozeti + panelde tutar/gerekçe.
3. Onay çekmecesinde tutarı/gerekçeyi düzelt ya da işareti kaldır → "Onayla".
   → **Hatalar:** KKEG tutarı boş/0/matrah üstü → Türkçe mesaj (UI + backend).
4. Fiş doğrulamada aynı alanlar (turuncu KKEG kutusu); onayda öğrenme akışı değişmez.
5. Dışa Aktar → Luca yevmiye CSV'de `689` satırı (KKEG + indirilemeyen KDV, gerekçeli).
6. Beyannameler → KDV1 → özet "KKEG nedeniyle indirilemeyen KDV −X TL (düşüldü)".

### Kabul kriteri

"Otopark" kalemi %30 KKEG bölünür; 46.000 TL üstü araç kirasında aşan kısım KKEG;
"gecikme zammı" tam KKEG; akaryakıt yalnızca HasCompanyVehicle=true iken işaretlenir;
Luca fişi dengeli kalır; KDV1 indirimi tam olarak indirilemeyen KDV kadar azalır.

---

## B5 — Mükerrer / Eksik Fatura Mutabakatı

### Özellik

Dönem bazlı bütünlük analizi (BA/BS formları 25.09.2024'te kalktığı için kritik):
**(a) Mükerrer:** aynı (satıcı VKN, tutar, ±2 gün) fatura kümeleri; aynı fatura no + farklı
UUID; fişlerde aynı (VKN, tutar, gün). **(b) Eksik:** kodlanmamış / onay kuyruğunda /
Yeni-Hata statüsünde takılan faturalar. **(c) Tutarsız:** satır toplamı ≠ fatura toplamı
(kuruş toleransı **0,05 TL**).

### Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Bulgular | DB'de saklanmaz; her "Analiz Et" yeniden hesaplar (veri değiştikçe bulgu kendiliğinden güncellenir) |
| "Yoksay" | `ReconciliationDismissal` (TenantId + FindingKey unique, tenant filtreli) — bellek değil DB; bulgu anahtarı deterministik SHA-256 (tür + sıralı belge Id'leri) |
| Uçlar | `GET /api/reconciliation?clientId&period`, `POST /api/reconciliation/dismiss {findingKey}` |
| UI | `/panel/mutabakat`: kırmızı mükerrer / sarı eksik / turuncu tutarsız kartlar; her belgede "Faturaya git" (çekmece deep-link `?fatura=`) / "Fişlere git"; boş durum "Sorun bulunamadı ✓" |
| Kapsam | Yalnızca GELEN faturalar + Rejected olmayan fişler |

### Uç durumlar

- Dönem biçimi hatalı / mükellef seçilmemiş → 400 Türkçe mesaj (UI çift doğrular).
- "Yoksay" sonrası bulgu yerel listeden anında düşer; yeni analizde de gizli kalır.
- Aynı bulgu ikinci kez yok sayılırsa sessizce geçilir (unique index + kontrol).
- Senkron edilmemiş fatura tespit EDİLEMEZ (sınırlama — aşağıda).

### Kabul kriteri

Aynı satıcı+tutar+yakın tarihli iki fatura mükerrer kartı üretir; kodlanmamış fatura
eksik kartı üretir; satır toplamı 0,05 TL'den fazla sapan fatura tutarsız kartı üretir;
"Yoksay" kalıcıdır; temiz dönemde "Sorun bulunamadı ✓" görünür.

---

## B10 — Dönem Sonu Kontrol Listesi

### Özellik

"Beyana hazır mıyım?" tek ekran: mükellef + dönem için 8 kontrol → her satır
{ad, durum: Ok/Uyari/Hata, Türkçe detay, ilgili sayfa linki}. Skor: **hata = engel,
uyarı = dikkat** → yüzde (Ok=1, Uyari=0.5, Hata=0). Engel yoksa **"KDV Taslağı Hazırla"**
kısayolu doğrudan taslağı üretir.

### Kontroller

| # | Kontrol | Durum kuralı | Link |
|---|---|---|---|
| 1 | Kodlanmamış fatura | >0 → **Hata** | /panel/faturalar |
| 2 | Onay kuyruğu (NeedsReview fatura) | >0 → Uyarı | /panel/onay-kuyrugu |
| 3 | Doğrulama bekleyen fiş | >0 → Uyarı | /panel/fisler |
| 4 | Mutabakat bulguları (B5 çağrılır) | mükerrer>0 → **Hata**; eksik/tutarsız → Uyarı | /panel/mutabakat |
| 5 | KDV1 taslağı | yok/taslak/hata → Uyarı; gönderildi/onaylandı → Ok | /panel/beyannameler |
| 6 | Onaysız tevkifatlı satır (B2) | >0 → Uyarı | /panel/onay-kuyrugu |
| 7 | Portalda işlenmemiş fiş belgesi (E1) | >0 → Uyarı | /panel/belgeler |
| 8 | Takvimde geciken yükümlülük (E2) | >0 → **Hata** | /panel/takvim |

### UI

`/panel/kapanis`: mükellef+dönem seçimi (otomatik yüklenir), üstte SVG yüzde halkası
(kırmızı=engel var, yeşil ≥%85, amber arası), kontrol kartları (yeşil ✓ / sarı ⚠ /
kırmızı ✗ + "Sayfaya git"), engel yoksa "KDV Taslağı Hazırla" düğmesi (başarıda liste
tazelenir). Dashboard'a "Dönem Kapanışı" kısayol kartı eklendi.

### Kabul kriteri

Temiz dönemde %100 + yeşil halka + kısayol aktif; kodlanmamış fatura varken halka kırmızı,
kısayol gizli; her satırın linki doğru sayfayı açar.

---

## TODO-VERIFY listesi (yayın öncesi doğrulanacak)

1. **Tevkifat oranları ve kod eşlemeleri** (`DbSeeder.SeedWithholdingRulesAsync`):
   601=4/10, 602=9/10, 603=7/10, 604=5/10, 605=5/10 görece emin; **606 (işgücü 9/10),
   612 (temizlik 9/10), 613 (çevre-bahçe 9/10), 615 (baskı-basım 7/10), 625 (karayolu yük
   2/10)** GİB güncel KDV Genel Uygulama Tebliği + e-Fatura UBL kod listesinden doğrulanmalı.
2. **Kiralamada %30 kısıt** (`KkegAnalyzer` kural b): sınır altı kiraya %30 genel gider
   kısıtının uygulanmadığı varsayıldı — güncel mevzuattan doğrula.
3. **Temsil-ağırlama tam KKEG** (`KkegAnalyzer` kural c): işle ilgili temsil-ağırlama
   kısmen indirilebilir; "tam KKEG" ihtiyatlı ön işarettir — muhasebe politikası netleştirilmeli.
4. **KDV2 şeması**: gerçek GİB BDP paketinde tevkifat kırılım alan adları/formatı birebir
   doğrulanmalı (şu an MockGib "odenecekKdv" sözleşmesi kullanılıyor).
5. **2026 binek sınırları** parametre merkezinde (46.000 / 1.200.000 / 2.600.000) — yıl
   güncellemelerinde süper admin panelinden revize edilmeli.

## Sınırlamalar

- Şema migration yok (`EnsureCreated`): MEVCUT veritabanına yeni kolonlar otomatik eklenmez;
  geliştirme DB'si sıfırlanmalı ya da kolonlar elle eklenmelidir (önceki fazlarla aynı sınırlama).
- B5 "eksik" kontrolü yalnızca SENKRON EDİLMİŞ faturalar üzerinden çalışır; entegratörde olup
  hiç çekilmemiş fatura tespit edilemez (entegratör sayım ucu ileride eklenecek).
- KDV2'ye yalnızca ONAYLI faturaların tevkifatlı satırları girer; onaysızlar B10/6 uyarısıyla gösterilir.
- Tevkifat tespiti anahtar kelime tabanlıdır (UBL `WithholdingTaxTotal` alanı mock veride yok);
  gerçek entegratör UBL'sinde hazır tevkifat alanı gelirse Katman 0'da doğrudan okunacak.
- Binek oto ÖTV+KDV gider sınırı (1.200.000) ve amortisman sınırı (2.600.000) parametre olarak
  seed edildi ancak otomatik kural HENÜZ bağlanmadı (alım/amortisman verisi Faz 8 kapsamı dışında).
- `HasCompanyVehicle` yalnızca mükellef DÜZENLEME modalında değiştirilebilir (yeni mükellefte varsayılan: hayır).
