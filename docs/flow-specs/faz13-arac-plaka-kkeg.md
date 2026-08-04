# Faz 13 — Araç Plakası + Akaryakıt KKEG Otomasyonu (Flow Spec)

## 1. Kural özeti (mali müşavirlik)

- **Binek** araçların akaryakıt giderinin **%70'i gider** yazılır, **%30'u KKEG** olur
  (GVK 40/1-5). İndirilebilir oran parametre merkezinden okunur:
  `binek.gider.kisit.oran` (varsayılan **70**; KKEG payı = 100 − oran).
  KKEG'e isabet eden KDV de indirilemez (`KkegAnalyzer.DisallowedVat` — mevcut hesap, değişmedi).
- **Ticari** araçların akaryakıt gideri **tam gider** yazılır (KKEG yok).
- Türk akaryakıt fişlerinde **araç plakası basılıdır** (ör. `34FPC630`, `06 ABC 123`).
  Mükellefin araçları sisteme plakayla kayıtlıdır; fişten okunan plaka kayıtlarla
  eşleştirilerek binek/ticari ayrımı OTOMATİK yapılır.
- Fişteki plaka kayıtlı araçlarla **eşleşmiyorsa** SMMM uyarılır (başka aracın/şahsın
  gideri olabilir) — KKEG kararı mevcut davranışta (HasCompanyVehicle bayrağı) kalır.

## 2. Veri modeli

### `ClientVehicle` (yeni tablo — `Domain/VehicleEntities.cs`)

| Alan | Tür | Not |
|---|---|---|
| Id, TenantId, ClientId | Guid | tenant query filter + Client FK (cascade) |
| Plate | string(12) | **NORMALİZE** saklanır: büyük harf, boşluksuz/tiresiz |
| IsPassengerCar | bool | binek mi — KKEG yalnız binekte |
| Description | string(200)? | ör. "Beyaz Passat" |
| CreatedAt | DateTime | |

- Unique index: **(TenantId, ClientId, Plate)** — aynı mükellefte aynı plaka bir kez.

### `Receipt` yeni kolonları (`Domain/ReceiptEntities.cs`)

- `PlateNumber` string(12)? — fişten okunan normalize plaka; okunamadıysa null.
- `PlateMatch` int — `PlateMatchStatus` enum: **Yok=0** (plaka okunmadı),
  **Eslesti=1**, **Eslesmedi=2**.
- `PlateWarning` string(300)? — plaka kaynaklı Türkçe uyarı; API'de `warnings: string[]`
  listesi olarak döner (fiş detay çekmecesinde gösterilir).

### Plaka normalizasyonu — tek kaynak: `PlateNormalizer` (`VehicleEntities.cs`)

- Boşluk / tire / nokta silinir; Türkçe **İ/ı → I** dahil büyük harfe çevrilir.
- Geçerlilik deseni: `^[0-9]{2}[A-Z]{1,3}[0-9]{2,5}$`, en fazla 12 karakter.
- Aynı normalizasyon üç yerde kullanılır: araç kaydı (ClientsController),
  OCR çıktısı (OcrValidator) ve fiş eşleştirmesi (ReceiptService) → tutarlı karşılaştırma.

## 3. API uçları

| Uç | Açıklama |
|---|---|
| `GET /api/clients/{id}/vehicles` | Plaka listesi (plakaya göre sıralı) |
| `POST /api/clients/{id}/vehicles` | Ekleme. Geçersiz biçim → **400** "Geçerli bir Türk plakası girin (ör. 34ABC123)."; mükerrer plaka → **409** "Bu plaka zaten kayıtlı." |
| `DELETE /api/clients/{id}/vehicles/{vehicleId}` | Silme (mevcut fişlerin eşleşme sonucu geriye dönük değişmez) |

`ReceiptResponse`'a eklenen alanlar: `plateNumber`, `plateMatch` ("Yok"/"Eslesti"/"Eslesmedi"),
`warnings` (string listesi).

## 4. OCR akışı

1. **Prompt** (`OcrPrompt.ReceiptExtraction`): şemaya `"plateNumber": string|null` alanı ve
   kural 11 eklendi — *"Akaryakıt fişlerinde araç plakası basılıdır; görüyorsan boşluksuz
   büyük harfle yaz, görmüyorsan null bırak — UYDURMA."* Tüm vision sağlayıcıları
   (Anthropic/OpenAI/Gemini) aynı prompt + şemayı paylaştığından tek noktadan geçerlidir.
2. **Parse** (`LlmReceiptExtraction.PlateNumber`) → **doğrulama** (`OcrValidator.Normalize`):
   plaka normalize edilir (boşluk/tire sil, upper, İ→I); desene uymuyorsa alan **sessizce
   null yapılır** — uyarı EKLENMEZ, güven düşürülmez (uydurma/yanlış okuma koruması;
   plaka çekirdek muhasebe alanı değildir).
3. `OcrReceiptResult.PlateNumber` sağlayıcı-bağımsız sonuca taşınır.
4. **MockOcrProvider**: akaryakıt profilleri (Shell, Opet) deterministik plaka üretir
   (`34FPC630`, `06ABC123`, ... — aynı dosya her zaman aynı plaka). Diğer profillerde null.

## 5. Fiş işleme akışı (`ReceiptService.UploadAndProcessAsync`)

Akaryakıt fişi tespiti: `KkegAnalyzer.IsFuelExpense(satıcı adı + OCR kalem özeti)` —
mevcut akaryakıt kelime seti (**akaryakıt / benzin / motorin / lpg**), tanım ikilenmez.

| Durum | PlateMatch | KKEG kararı | Uyarı |
|---|---|---|---|
| Plaka okundu + kayıtlı **binek** araçla eşleşti + akaryakıt fişi | Eslesti | **%30 KKEG otomatik** (`binek.gider.kisit.oran`), gerekçe: `"Binek araç ({Plaka}) akaryakıt gideri — %30 KKEG"`. **HasCompanyVehicle bayrağından BAĞIMSIZ** (plaka eşleşmesi daha güçlü kanıt). | — |
| Plaka okundu + kayıtlı **ticari** araçla eşleşti + akaryakıt fişi | Eslesti | Akaryakıt KKEG'i **uygulanmaz** (tam gider). Akaryakıt-dışı KKEG kuralları (ceza, temsil…) yine değerlendirilir. | — |
| Plaka okundu ama **eşleşmedi** | Eslesmedi | Mevcut davranış (HasCompanyVehicle kuralı). **Faz 15 eki:** karar `IsKkeg=false` kaldıysa `KkegAmount`+`KkegReason` yine de ÖNERİ olarak hesaplanıp yazılır (bkz. §11). | "Fişteki plaka ({Plaka}) mükellefin kayıtlı araçlarıyla eşleşmiyor — aracı sisteme ekleyin veya gideri kontrol edin." |
| Akaryakıt fişi ama plaka **okunamadı** | Yok | Mevcut davranış | "Akaryakıt fişinde plaka okunamadı — binek/ticari ayrımı için elle kontrol edin." |
| Akaryakıt-dışı fiş, plaka yok | Yok | Mevcut davranış | — (akış birebir eski) |

SMMM onay ekranındaki KKEG düzeltme mantığı (işaretle/temizle/tutar) değişmedi —
otomatik işaret her zaman elle düzeltilebilir.

## 6. Web arayüzü

- **Mükellefler**: satırda "Araçlar" butonu → modal: plaka listesi (plaka, **Binek/Ticari**
  rozeti, açıklama, sil) + ekleme formu (plaka, "Binek araç" onay kutusu, açıklama).
  UI'da da regex ön kontrol (`PLATE_REGEX` + `normalizePlate` — backend ikinci kez doğrular).
- **Fişler**: detay çekmecesinde **Plaka** alanı (değer + rozet: yeşil "Kayıtlı araç" /
  kırmızı "Kayıtlı değil" / gri "Okunamadı") ve `warnings` listesi sarı uyarı kutuları
  olarak gösterilir. Tabloda plaka okunan fişlerde küçük plaka rozeti (yeşil/kırmızı).

## 7. Uç durumlar

- **Plaka yok / akaryakıt-dışı fiş**: hiçbir yeni alan dolmaz, akış birebir eski davranıştır
  (geriye dönük uyumluluk temel kabul).
- **Uydurma koruması**: prompt "görmüyorsan null bırak — UYDURMA" der; yine de gelen
  değer desene uymuyorsa OcrValidator sessizce null'a çeker. Böylece halüsinasyon plaka
  asla eşleştirme/uyarı üretmez.
- **Ticari araç**: eşleşme ticari araca çıkarsa akaryakıt KKEG'i uygulanmaz — SMMM ayarında
  HasCompanyVehicle işaretli olsa bile (plaka kanıtı bayrağı ezer).
- **Eşleşmeyen plaka**: KKEG kararı değişmez, yalnız uyarı + kırmızı rozet; SMMM aracı
  ekledikten sonra YENİ yüklenen fişler otomatik eşleşir (mevcut fiş yeniden işlenmez).
- **Aynı plaka iki kez**: 409; farklı mükellefte aynı plaka serbesttir (index client bazlı).
- **%100 indirilebilir parametre** (`binek.gider.kisit.oran` ≥ 100): KKEG payı 0 → işaret konmaz.

## 8. Kabul kriterleri

1. `POST /api/clients/{id}/vehicles` "34 abc 123" girişini `34ABC123` olarak kaydeder;
   "PLAKA" → 400, mükerrer → 409 (Türkçe mesajlar).
2. Mock akaryakıt fişi (Shell/Opet profili) yüklendiğinde `plateNumber` dolu döner;
   plaka kayıtlı binek araçla eşleşiyorsa fiş `IsKkeg=true`,
   `KkegAmount = matrah × %30` ve `KkegReason = "Binek araç (…) akaryakıt gideri — %30 KKEG"`.
   Bu sonuç HasCompanyVehicle=false iken de üretilir.
3. Aynı plaka ticari (IsPassengerCar=false) kayıtlıysa fişte KKEG işareti oluşmaz.
4. Kayıtsız plakalı akaryakıt fişinde `plateMatch="Eslesmedi"` ve warnings'te eşleşmeme
   uyarısı döner; KKEG sonucu, plakasız senaryoyla aynıdır.
5. Plakasız akaryakıt fişinde warnings'te "plaka okunamadı" bilgi uyarısı; market/restoran
   fişlerinde hiçbir plaka alanı/uyarısı oluşmaz (eski davranış birebir).
6. Fişler tablosunda plaka rozeti, detayda Plaka alanı + eşleşme rozeti + uyarılar görünür;
   Mükellefler → Araçlar modalından ekleme/silme çalışır.
7. Cari izolasyon: VendorCariMap/AccountMemory sorguları ClientId + tenant filtresiyle
   sınırlıdır; bir mükellefin cari kodu başka mükellefe önerilmez (bkz. §10).

## 9. Şema notu (EnsureCreated — dev DB sıfırlama)

Proje EF **migration kullanmaz**; şema `Database.EnsureCreatedAsync()` ile ilk açılışta
oluşturulur. `EnsureCreated` VAR OLAN veritabanına yeni tablo/kolon EKLEMEZ. Bu fazın
yeni şeması (`ClientVehicles` tablosu; `Receipts.PlateNumber`, `Receipts.PlateMatch`,
`Receipts.PlateWarning` kolonları) için geliştirme veritabanını sıfırlayın:

```bash
# docker-compose ile: postgres volume'unu silip yeniden ayağa kaldırın
docker compose down -v && docker compose up -d
```

Üretime geçişte EF migration'a (veya elle `ALTER TABLE` + `CREATE TABLE`) geçilmesi gerekir.

## 10. Cari hafıza izolasyon doğrulaması (kod incelemesi)

- `VendorCariMap` ve `AccountMemory` unique index'leri **(TenantId, ClientId, SenderTaxId, …)**
  anahtarlıdır (AppDbContext); tüm okuma/yazma sorguları `ClientId == …` filtresi içerir
  (CodingEngine: CodeInvoiceAsync, ApproveCodingAsync, ResolveCariCodeAsync,
  SuggestForReceiptAsync, LearnFromReceiptApprovalAsync). Tenant query filter ek katmandır.
- Fiş/fatura onayında öğrenme client-scoped yazılır; bir mükellefin cari kodu/hafızası
  başka mükellefe önerilemez. **Aykırılık bulunmadı, kod değişikliği gerekmedi.**

## 11. Faz 15 ek — KKEG otomatik hesaplama (UX)

SMMM, KKEG kutusunu işaretlediğinde tutar ve gerekçeyi ELLE hesaplamaz:

- **Oran ucu**: `GET /api/coding/parameters/kkeg` (`[Authorize]`, tüm oturumlu kullanıcılar) →
  `{ rate }`. Backend, `binek.gider.kisit.oran` (İNDİRİLEBİLİR %; varsayılan 70)
  parametresinden `rate = 100 − indirilebilir` türetir (varsayılan **30**). Fişler sayfası
  bu değeri açılışta BİR KEZ çeker; uç erişilemezse sessizce 30'a düşer (akış engellenmez).
- **Web fiş detayı (kutu işaretlenince, alan boşsa)**:
  - KKEG Tutarı = **matrah × oran** (matrah boşsa toplam tutar), 2 ondalığa yuvarlanır ve
    alana yazılır — SMMM isterse değiştirir.
  - Gerekçe: satıcı adı akaryakıt işareti (motorin/benzin/lpg/akaryakıt/petrol) taşıyorsa
    `"Binek araç ({plaka}) akaryakıt gideri — %30 KKEG"` (plaka okunmadıysa parantezsiz);
    değilse `"KKEG — %30 gider kısıtı"`.
  - **Kutu kaldırılınca** yalnızca hâlâ otomatik doldurulan değere eşit alanlar temizlenir;
    elle yazılmış/düzeltilmiş değere dokunulmaz.
- **Backend önerisi (eşleşmeyen plaka)**: akaryakıt fişinde plaka `Eslesmedi` ise ve mevcut
  kurallar KKEG işareti koymadıysa `ReceiptService.ApplyOcrExtractionAsync`,
  `KkegAnalyzer.AnalyzeFuelForPassengerVehicle` hesabını yapar ve `KkegAmount`+`KkegReason`
  alanlarını doldurur; **`IsKkeg=false` KALIR** (karar SMMM'nin — plaka kanıtı doğrulanmadı).
  Böylece onay ekranında kutu işaretlenince hazır değerler zaten doludur. Eşleşmeme uyarı
  metni ve eşleşen-binek dalı (`IsKkeg=true` otomatik) birebir korunur.
