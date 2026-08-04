# Faz 14 — Arka Plan OCR Kuyruğu, Aylık Kota Denetimi ve Sayfa İçi Luca Export

Bu faz üç özelliği kapsar:

- **A) Arka plan OCR kuyruğu:** fiş yükleme SENKRON akıştan ASENKRON kuyruk akışına geçti
  (Faz 11'in `OutboundJob` altyapısı yeniden kullanıldı — harici paket YOK).
- **B) Aylık OCR kota denetimi:** tenant planındaki `MonthlyOcrQuota`, yükleme anında denetlenir.
- **C) Sayfa içi Luca export:** Fişler/Faturalar sayfalarına tarih filtreli, kaynak kısıtlı
  XLSX indirme şeridi; export ucuna `source=all|fis|fatura` filtresi.

---

## A) Arka plan OCR kuyruğu

### Eski (senkron) akış — Faz 12/13

`POST /api/clients/{id}/receipts` her dosya için: doğrulama → temp'e orijinal → **OCR (istek
içinde beklenir)** → alan çıkarımı + plaka/KKEG → kalıcıya sıkıştırılmış kopya → temp sil →
`NeedsReview` fiş. 20 fişlik yükleme, vision-LLM sağlayıcısında dakikalarca sürebiliyordu ve
kullanıcı ekran başında bekliyordu.

### Yeni (asenkron) akış

`ReceiptService.UploadAndEnqueueAsync` (hem `ReceiptsController.Upload` hem `PortalController.Upload`
Fis kategorisi bu yolu kullanır):

1. Doğrulama: 10 MB sınırı, uzantı (jpg/jpeg/png/pdf/heic) ve **kota** (bkz. B). Doğrulama
   hataları eskisi gibi dosya bazlı Türkçe mesajla ANINDA döner.
2. Orijinal dosya `temp/` altına dokunulmadan yazılır (Faz 12 "önce OCR, sonra sıkıştırma"
   kuralı korunur — OCR kuyruğu her zaman orijinali okur).
3. `Receipt` kaydı açılır: `Status=Processing`, `FileName=orijinal ad`, `StoredPath=temp yolu`,
   OCR alanları boş.
4. `OutboundJob` kuyruğuna `Type="ReceiptOcr"`, `PayloadJson={"receiptId":"..."}` işi yazılır
   (aynı receiptId için bekleyen/çalışan iş varsa mükerrer iş AÇILMAZ — mevcut dedup).
5. Yanıt HEMEN döner: `"N fiş kuyruğa alındı — arka planda okunuyor."` + Processing durumlu
   fiş listesi.

### Kuyruk işleme (`ReceiptService.ProcessQueuedOcrAsync`)

`OutboundJobService.DispatchAsync`'e `"ReceiptOcr"` tipi eklendi (etiket: "Fiş OCR okuma").
İş, `OutboundJobWorker`'ın mevcut tenant scope deseninde çalışır (`WorkerCurrentUser.ActAsTenant`);
fiş yine de savunmacı olarak `IgnoreQueryFilters + TenantId == job.TenantId` ile yüklenir.

Adımlar:

1. Fiş `Processing` değilse iş başarı sayılır (arada elle işlenmiş). Fiş/mükellef silinmişse
   kalıcı hata (job `Basarisiz`, Türkçe mesaj).
2. Temp'teki ORİJİNALDEN OCR çalıştırılır.
3. Başarıda: görsel normalize edilip kalıcıya taşınır (`PromoteReceiptFromTempAsync` — uzun kenar
   ≤ 1600 px JPEG, uzantı değişirse `FileName` eşitlenir), temp silinir, alan çıkarımı + gider
   gösterilebilirlik + kod önerisi + plaka/KKEG analizi uygulanır, `Status=NeedsReview`.
   **Kod ikilemesi yok:** senkron akıştaki işleme mantığı ortak `ApplyOcrExtractionAsync`
   metoduna çıkarıldı; davranış (Faz 8 KKEG + Faz 13 plaka dahil) birebir aynıdır.
4. **Geçici (retryable) OCR hatası:** iş `Bekliyor`'a döner, mevcut üstel geri çekilme
   (1/5/15/60/180 dk, 5 deneme) aynen devrede; temp dosya SİLİNMEZ, fiş `Processing` kalır.
5. **Kalıcı OCR hatası (okunamadı vb.):** görsel yine kalıcıya taşınır, fiş
   `Status=NeedsReview` + `OcrWarning="Fiş otomatik okunamadı: ... Alanları elle doldurup
   onaylayabilirsiniz."` olur, iş `Basarili` kapanır (koru-ve-uyar görevini tamamladı).
6. **Son denemede hâlâ geçici hata:** fiş yine koru-ve-uyar ile `NeedsReview`'a düşürülür
   ("İşleniyor"da sonsuza dek takılı fiş kalmaz) ama iş `Basarisiz` yazılır — altyapı hatası
   gönderim kuyruğu ekranında dürüstçe görünür.

`OcrWarning`, `ReceiptResponse.Warnings` listesinde (plaka uyarılarıyla birlikte) taşınır ve
fiş detay çekmecesinde sarı uyarı bandı olarak görünür.

### DAVRANIŞ DEĞİŞİKLİĞİ NOTLARI (bilinçli)

| Konu | Eski (senkron) | Yeni (kuyruk) |
|---|---|---|
| Yükleme yanıtı | OCR bitene dek bekler; "N fiş yüklendi ve işlendi" | Hemen döner; "N fiş kuyruğa alındı — arka planda okunuyor" |
| Okunamayan fiş | Dosya **silinir**, dosya bazlı anlık hata döner | Fiş **silinmez**: kalıcıya taşınır, `OcrWarning` + `NeedsReview` — SMMM alanları elle doldurur. Senkron akışta "sil ve bildir" mantıklıydı (kullanıcı ekran başında, hemen yeniden çekebilir); kuyruk akışında kullanıcı çoktan ayrılmıştır, sessizce silmek veri kaybı olurdu → koru-ve-uyar. |
| OCR hatasının görünürlüğü | HTTP yanıtında | Fiş uyarı bandında (+ altyapı hataları gönderim kuyruğu ekranında) |
| `Processing` durumu | Yalnızca kısa ara durum (kullanıcı hiç görmezdi) | Gerçek görünür durum: mavi "İşleniyor" rozeti + 5 sn'lik liste yoklaması |
| Portal Fis mesajı | "Fişiniz alındı ve otomatik okumaya gönderildi." (OCR aslında bitmişti) | "Fişiniz alındı — arka planda otomatik okunuyor." (artık gerçekten doğru) |

### Portal yolu

Portal Fis yüklemesi aynı kuyruk akışına geçti. `PortalUpload` kaydı fişle AYNI dosyayı paylaşır;
yükleme anında bu yol temp'tir, kuyruk işi dosyayı kalıcıya taşıyınca bağlı `PortalUpload`
kayıtlarının `StoredPath/FileName`'i de eşitlenir (`SyncLinkedPortalUploadsAsync`). Kuyruğa
alınamayan (doğrulama/kota) fiş dosyası KAYBEDİLMEZ — eskisi gibi portal deposuna kaydedilir
("Fiş otomatik okumaya alınamadı; belge yine de mali müşavirinize iletildi.").

### Web (Fişler sayfası)

- "İşleniyor" rozeti maviye çevrildi ve mini spinner eklendi (`StatusBadge`).
- Listede Processing fiş varken 5 sn'de bir SESSİZ yoklama (iskelet/hata bandı gösterilmez);
  Processing kalmayınca otomatik durur. Durum filtresine "İşleniyor" seçeneği eklendi.
- Detay çekmecesinde Processing fiş için bilgi bandı; alanlar bu durumda düzenlenemez
  (düzenleme yalnızca `NeedsReview`'da — mevcut kural).

---

## B) Aylık OCR kota denetimi

- Kaynak: `Tenant.Plan.MonthlyOcrQuota` (Baslangic 200 / Profesyonel 1000 / Ofis 5000).
- Sayaç: **bu takvim ayında (UTC) oluşturulmuş `Receipt` sayısı — tenant geneli, mükelleften
  bağımsız.** Statüsü ne olursa olsun sayılır (silinen fiş sayacı düşürür; kabul edilen sınırlama).
- Yükleme anında `ReceiptService.GetQuotaGateAsync` istek başına BİR kez yüklenir
  (`ReceiptQuotaGate`); her kabul edilen dosya bellekte sayacı artırır:
  - Kota istek başında tamamen doluysa → **403**:
    `"Aylık fiş kotanız doldu ({kullanılan}/{kota}). Paketinizi yükseltin veya gelecek ayı bekleyin."`
  - Kalan kota < dosya sayısı → sığanlar kabul, kalanlar aynı mesajla dosya bazlı red.
  - Plan yoksa (eski kayıt) veya kota ≤ 0 / `int.MaxValue` → denetim atlanır (`unlimited`).
- Portalda 403 dönülmez; kota aşan fişler dosya bazlı reddedilir ve belge portal deposuna düşer.
- Yeni uç: `GET /api/receipts/quota` → `{used, quota, unlimited}` (SuperAdmin gibi tenant'sız
  kimlikte `unlimited=true`).
- Fişler sayfası başlığında küçük gösterge: **"Bu ay: 12/200 fiş"** (dolunca kırmızı; sınırsız
  planda gizli). Gösterge süstür — asıl denetim her zaman backend'dedir.

---

## C) Sayfa içi Luca export

### Backend

- `GET /api/export/luca` ve `GET /api/export/luca/preview` uçlarına `source=all|fis|fatura`
  parametresi eklendi (varsayılan `all` — mevcut davranış BİREBİR korunur).
  - `fis`: yalnız `Approved` fişler (fatura sorgusu hiç çalışmaz).
  - `fatura`: yalnız `Approved` faturalar (fiş sorgusu hiç çalışmaz).
  - Geçersiz değer → 400 `"Geçersiz kaynak filtresi. 'all', 'fis' veya 'fatura' seçin."`
- Uygulama noktası: `CodingController.LoadApprovedDocumentsAsync` (kaynağa göre boş liste).
  `LucaExcelExporter`'a **dokunulmadı** — boş liste alan taraf şablona satır üretmez; şablon
  kolonları, dengeli fiş mantığı ve zorunlu alan ön-kontrolü değişmedi. (Spec'te "exporter'a
  parametre" denmişti; aynı sonucu exporter'ı değiştirmeden controller'da filtreleyerek almak
  tercih edildi — test edilmiş exporter davranışı garanti birebir kalır.)
- Boş sonuç mesajları kaynağa göre: "Seçilen aralıkta onaylanmış fiş yok." /
  "... onaylanmış fatura yok." / mevcut genel mesaj.
- Dosya adı: kaynak filtresinde `-fisler` / `-faturalar` eki
  (`luca-fis-aktarim-{slug}-fisler-{from}-{to}.xlsx`); `all`'da ad birebir eski.

### Web

- **Ortak bileşen** `web/components/LucaExportStrip.tsx`: kompakt şerit — tarih aralığı
  (varsayılan: içinde bulunulan ayın 1'i → bugün) + "Luca XLSX İndir (yalnız fişler/faturalar)"
  butonu; indirme/hata/başarı bantları. Mükellef seçilmeden basılırsa Türkçe uyarı; aralıkta
  onaylanmış belge yoksa sunucunun Türkçe hatası bantta gösterilir.
- Fişler sayfası: şerit `source="fis"` ile; Faturalar sayfası: `source="fatura"` ile.
- Dışa Aktar sayfası: "Kaynak" açılır menüsü (Tümü (fatura + fiş) / Yalnız fişler / Yalnız
  faturalar), varsayılan Tümü; önizleme ve indirme seçilen kaynağı kullanır.

---

## Dosya değişiklikleri

Backend (`backend/MaliMusavir.Api/`):
- `Domain/ReceiptEntities.cs` — `Receipt.OcrWarning` (yeni kolon), durum doc'ları.
- `Domain/OutboundJobEntities.cs` — "ReceiptOcr" tipi doc'u.
- `Services/ReceiptService.cs` — `UploadAndEnqueueAsync`, `ProcessQueuedOcrAsync`,
  `ApplyOcrExtractionAsync` (ortak işleme), `GetQuotaGateAsync`, `ReceiptQuotaGate`,
  `SyncLinkedPortalUploadsAsync`; senkron `UploadAndProcessAsync` kaldırıldı.
- `Services/OutboundJobService.cs` — `TypeReceiptOcr` + dispatcher case + payload sınıfı.
- `Controllers/ReceiptsController.cs` — asenkron upload + 403 kota + `GET /api/receipts/quota`.
- `Controllers/PortalController.cs` — Fis kategorisi kuyruk akışı + kota kapısı.
- `Controllers/CodingController.cs` — `source` parametresi (export + preview), kaynak bazlı
  mesaj/dosya adı, `NormalizeSource`.
- `Contracts/Dtos.cs` — `ReceiptQuotaResponse`; `Warnings` artık `OcrWarning`'i de içerir.

Web (`web/`):
- `lib/api.ts` — `LucaExportSource`, `source` parametreleri, `ReceiptQuota` + `getReceiptQuota`.
- `components/LucaExportStrip.tsx` — YENİ ortak şerit bileşeni.
- `app/panel/fisler/page.tsx` — İşleniyor rozeti/spinner, 5 sn sessiz yoklama, kota göstergesi,
  export şeridi, "İşleniyor" durum filtresi, asenkron metinler.
- `app/panel/faturalar/page.tsx` — export şeridi (source=fatura).
- `app/panel/disa-aktar/page.tsx` — kaynak açılır menüsü.

---

## Sınırlamalar / bilinçli kabuller

1. **DB şeması:** proje `EnsureCreated` kullanır (migration yok) — mevcut dolu veritabanına
   `Receipts.OcrWarning` kolonu otomatik eklenmez; taze DB'de sorunsuzdur. Mevcut ortamda
   `ALTER TABLE "Receipts" ADD COLUMN "OcrWarning" text NULL;` yeterlidir.
2. **Kota sayacı** silinen/reddedilen fişleri geri iade eder (ay içi oluşturulan kayıt sayısı
   üzerinden sayıldığı için silme sayacı düşürür). Kötüye kullanım senaryosu (yükle-sil döngüsü)
   bu fazda kabul edildi; kalıcı sayaç istenirse ayrı tablo gerekir.
3. **Processing fiş silinemez** (mevcut silme kuralı Uploaded/NeedsReview ile sınırlı) —
   kuyruktaki iş bitince fiş `NeedsReview`'a düşer ve silinebilir hale gelir.
4. **Portal eşzamanlılık:** `PortalUpload` kaydı fiş kuyruğa alındıktan hemen sonra kaydedilir;
   worker 15 sn'lik tarama aralığında çalıştığından pratikte yarış yoktur, ancak teoride iş
   PortalUpload kaydından önce biterse yol eşitlemesi o turda atlanır (dosya temp'te kalır).
5. **Tek instance varsayımı** (Faz 11'den miras): worker satır kilidi kullanmaz; yatay
   ölçeklemede `SKIP LOCKED` gerekir.
6. **Kuyruk gecikmesi:** worker 15 sn'de bir tarar → küçük yüklemelerde fiş, senkron akışa göre
   en fazla ~15-20 sn geç `NeedsReview`'a düşer; karşılığında yükleme yanıtı anlıktır.
7. `source` filtresi `LucaExcelExporter` yerine controller'daki belge yüklemesinde uygulanır
   (yukarıda gerekçelendirildi); şablon üretimi tüm kaynak kombinasyonlarında aynı kod yolundadır.
