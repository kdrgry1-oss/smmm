# Faz 9 — Luca Fiş Aktarım Şablonuna Birebir Export + Vision-LLM OCR

Bu faz iki kritik iyileştirmeyi kapsar:

1. **Görev 1:** Dışa aktarma çıktısı, kullanıcının verdiği GERÇEK Luca fiş aktarım şablonuna
   birebir uyan `.xlsx` dosyasına dönüştürüldü (harici NuGet paketi OLMADAN); aynı kolonlarla
   CSV ikinci format seçeneği olarak korundu. Zorunlu alan ön-kontrolü eklendi.
2. **Görev 2:** Fiş OCR'ına vision-LLM sağlayıcısı (`LlmVision`, Anthropic Messages API) ve
   çapraz doğrulama katmanı (`OcrValidator`) eklendi; `Receipt` varlığına `DocumentNumber`
   (Evrak No) alanı geldi. Mock varsayılan sağlayıcı olarak kaldı.

---

## 1. Luca Fiş Aktarım Şablonu

- **Sayfa adı:** `Fiş Aktarım Şablon` (birebir).
- **Dosya:** `GET /api/export/luca?clientId=&from=&to=&format=xlsx|csv` (varsayılan `xlsx`).
  - `xlsx`: `Infrastructure/MinimalXlsxWriter.cs` ile üretilir — yalnızca built-in
    `System.IO.Compression`; zip içinde `[Content_Types].xml`, `_rels/.rels`,
    `xl/workbook.xml`, `xl/_rels/workbook.xml.rels`, `xl/worksheets/sheet1.xml`,
    `xl/styles.xml`. `sharedStrings.xml` yerine inline string (`t="inlineStr"`) kullanılır.
  - `csv`: AYNI kolon sırası/başlıklar; `;` ayraçlı, UTF-8 BOM'lu (Türkçe Excel varsayılanı).
- **Tarihler:** metin olarak `dd.MM.yyyy` (Excel seri numarası riski yerine güvenli yol —
  Luca içe aktarma metin tarihi kabul eder).
- **Sayılar:** Borç/Alacak/Kur (xlsx'te) numerik hücre, ondalık NOKTA ile (OOXML kuralı);
  CSV'de Türkçe Excel uyumu için ondalık VİRGÜL.

### Kolon eşleme tablosu (A→N)

| Kolon | Başlık | Şablonda | Doldurma kuralı |
|---|---|---|---|
| A | Fiş No | Opsiyonel (gri) | **BOŞ** — Luca otomatik fiş numarası verir |
| B | Fiş Tarihi | Zorunlu (sarı) | Belge tarihi: fatura `IssueDate` / fiş `ReceiptDate`, `dd.MM.yyyy` |
| C | Fiş Açıklama | Zorunlu | `{SatıcıUnvan} - {BelgeNo}` (unvan yoksa yalnız belge no) |
| D | Hesap Kodu | Zorunlu | Satırın hesabı: gider hesabı / 191.xx KDV / 320-100 karşı hesap / 689 KKEG — **dengeli fiş mantığı AYNEN korunur** |
| E | Evrak No | Zorunlu | Fatura `InvoiceNumber` / fiş `Receipt.DocumentNumber` (yoksa `FIS-{Id ilk 8 hane}`) |
| F | Evrak Tarihi | Zorunlu | Belge tarihi `dd.MM.yyyy` (B ile aynı) |
| G | Detay Açıklama | Opsiyonel | Kalem adı; KKEG satırında `KKEG - {gerekçe} - {kalem}`; tevkifatlı kalemde `(Tevkifat {kod} {oran})` notu; KDV satırında `İndirilecek KDV %{oran}`; alacak satırında fiş açıklaması |
| H | Borç | Zorunlu kolon | Dengeli fiş borç tutarı; satırın boş tarafı **BOŞ hücre** (0 değil) |
| I | Alacak | Zorunlu kolon | Karşı hesap satırında borç toplamı (denge garantisi); diğer satırlarda BOŞ |
| J | Miktar | Opsiyonel | BOŞ |
| K | Belge Türü | Zorunlu | Parametre: `luca.belge.turu.fatura`="Fatura" / `luca.belge.turu.fis`="Fiş" |
| L | Para Birimi | Zorunlu | Parametre `luca.para.birimi` (varsayılan "TRY") |
| M | Kur | Zorunlu | `1` (TRY) |
| N | Döviz Tutar | Zorunlu (sarı) | TRY'de **BOŞ**; `luca.doviz.tutar.sifir=true` yapılırsa `0` yazılır |

### Dengeli fiş üretimi (değişmedi, yalnız kolon düzeni yeni)

Her belge tek dengeli fiş olur:

- Borç satırları: her kalem kendi (onaylı) hesabına; KKEG işaretli satır İKİYE bölünür —
  indirilebilir kısım kendi hesabına, KKEG kısmı (matrah payı + indirilemeyen KDV)
  `kkeg.hesap`'a (689).
- KDV satırları: oran bazında gruplanır; hesap eşlemesi parametre merkezinden
  (`kdv.hesap.20`→191.01, `kdv.hesap.10`→191.02, `kdv.hesap.1`→191.03, diğer→191).
- Alacak satırı: cari kod (satırlardan ilk dolu `CariCode`, fatura fallback 320; fişte cari
  yoksa 100 Kasa); tutar = borç toplamı (kuruş farkında bile fiş dengeli kalır).

### Zorunlu alan ön-kontrolü (export engeli)

`LucaExcelExporter.BuildTemplateAsync` satırları üretirken Türkçe hata listesi toplar:

- Hesap kodu atanmamış satırı olan belge,
- Belge tarihi olmayan fiş,
- Evrak numarası üretilemeyen belge (fatura no boş),
- Borç toplamı 0 olan belge (tutar okunamadı).

Liste doluysa `GET /api/export/luca` **400** döner (`message` + `errors[]`, Türkçe).
Aynı liste `GET /api/export/luca/preview` yanıtındaki `missingFields` alanında döner;
web *Dışa Aktar* sayfası listeyi sarı uyarı kutusunda gösterir ve indirme düğmesini kilitler.

### Parametreler (DbSeeder — mevcut değerler ASLA ezilmez)

| Anahtar | Varsayılan | Açıklama |
|---|---|---|
| `luca.belge.turu.fatura` | `Fatura` | K kolonu — e-fatura satırları |
| `luca.belge.turu.fis` | `Fiş` | K kolonu — fiş satırları |
| `luca.para.birimi` | `TRY` | L kolonu |
| `luca.doviz.tutar.sifir` | `false` | `true` → N kolonuna 0 yazılır, `false` → boş |

---

## 2. Vision-LLM OCR akışı

```
Yükleme → IFileStorage (webroot dışı) → IReceiptOcrProvider.ExtractAsync
   ├─ MockOcrProvider   (varsayılan; deterministik demo, anahtar gerekmez)
   └─ LlmVisionOcrProvider
        1) Dosya → base64 (jpg/png/webp/gif "image", pdf "document"; HEIC desteklenmez → Türkçe hata)
        2) POST https://api.anthropic.com/v1/messages
           - header: x-api-key + anthropic-version: 2023-06-01
           - model: Ocr:Anthropic:Model (varsayılan "claude-sonnet-4-5"), max_tokens: 2000
           - Türkçe fiş uzmanı prompt: SADECE geçerli JSON, şema sabit
        3) Yanıt JSON → LlmReceiptExtraction (kod bloğu işaretleri temizlenir)
        4) OcrValidator.Normalize → OcrReceiptResult
→ ReceiptService: alanlar + DocumentNumber Receipt'e yazılır → KKEG/uygunluk/kod önerisi
→ NeedsReview (fişte TAM otomatik onay YOK) → SMMM onay formu (Evrak No düzenlenebilir)
```

### Prompt kuralları (LlmVisionOcrProvider)

- SADECE geçerli JSON; şema: `vendorName, vendorTaxId, documentNumber, receiptDate (ISO),
  totalAmount, vatRate, vatAmount, netAmount, currency, items[{name, amount}],
  documentType ("perakende_fis"|"fatura"|"yazar_kasa"|"diger"),
  confidence {alan: 0-1}, warnings[]`.
- Türkçe sayı biçimi (1.234,56 → 1234.56), tarih GG.AA.YYYY → ISO.
- KDV oranı yalnız 0/1/10/20 (eski %8/%18 görülürse warnings'e not).
- VKN 10 / TCKN 11 hane; uymuyorsa null.
- Fiş no / Z no / belge no → `documentNumber`.
- Emin olunmayan alan null + düşük confidence — **UYDURMA YOK**.

### Doğrulama kuralları (Ocr/OcrValidator.cs)

| Kural | Aksiyon |
|---|---|
| matrah + KDV = toplam (±0,05 TL) | Tutmazsa Türkçe uyarı + güven cezası |
| Belge tarihi gelecekte olamaz | Tarih null'a çekilir + uyarı + ceza |
| VKN/TCKN hane kontrolü (10/11) | Uymayan değer null + uyarı + ceza |
| KDV oranı ∈ {0, 1, 10, 20} | Değilse uyarı + ceza |
| vatAmount ≈ netAmount × oran/100 (±0,05) | Tutmazsa uyarı + ceza |
| Eksik matrah/KDV | toplam + oran biliniyorsa geriye hesaplanır |

**Genel güven** = alan bazlı güvenlerin **MİNİMUMU** × 0,9^(uyarı sayısı), taban 0,05.
Bu değer `ReceiptService`'te kod önerisi güveniyle tekrar `min()` alınır (mevcut davranış).

### Yapılandırma

```json
"Ocr": {
  "Provider": "Mock",              // "Mock" | "LlmVision" (başka değer → açılışta Türkçe hata)
  "Anthropic": {
    "ApiKey": "",                  // boşken LlmVision seçilirse çağrıda açıklayıcı Türkçe hata
    "Model": "claude-sonnet-4-5",
    "MaxTokens": 2000
  }
}
```

EnvVar override .NET config ile hazır: `Ocr__Provider`, `Ocr__Anthropic__ApiKey`.
HttpClient: adlandırılmış `anthropic-ocr`, 90 sn zaman aşımı (blueprint §0.5).

### Receipt.DocumentNumber

- `Domain/ReceiptEntities.cs`: `string? DocumentNumber` (max 60, AppDbContext).
- OCR doldurur (Mock deterministik `Z####` üretir); onay formunda düzenlenebilir
  (web Fişler sayfası "Evrak No (fiş/Z no)" alanı; `ApproveReceiptRequest.DocumentNumber`).
- Luca şablonunda Evrak No (E) kolonuna gider; boşsa `FIS-{Id ilk 8 hane}` üretilir.
- **Mobil modele DOKUNULMADI** — mobil alan eşitlemesi sonraki tur.
- Not: proje migration kullanmıyor (`EnsureCreated`); mevcut veritabanında yeni kolon için
  DB'yi yeniden oluşturmak (veya elle `ALTER TABLE`) gerekir.

---

## 3. TODO-VERIFY listesi

1. **Luca belge türü kabul listesi:** K kolonunda "Fatura"/"Fiş" metinleri varsayıldı.
   Luca farklı kod bekliyorsa süper admin `luca.belge.turu.fatura` / `luca.belge.turu.fis`
   parametrelerini panelden günceller. (`LucaExcelExporter`, `DbSeeder`)
2. **Döviz Tutar (N):** şablonda sarı/zorunlu görünse de TRY satırında boş kabul edildiği
   varsayıldı; Luca 0 istiyorsa `luca.doviz.tutar.sifir=true` yapılır. (`DbSeeder`)
3. **Tarih biçimi:** `dd.MM.yyyy` metin tarih Luca içe aktarmada kabul varsayımı — gerçek
   içe aktarma denemesiyle doğrulanmalı (gerekirse Excel tarih hücresine geçilebilir).
4. Tevkifat notu yalnızca Detay Açıklama'ya bilgi olarak yazılır; Luca tarafında tevkifatlı
   KDV-2 kaydının ayrı fiş isteyip istemediği muhasebeci ile doğrulanmalı.
5. Faz 8'den devreden: tevkifat kodu oranları ve KKEG sınırları GİB güncel tebliğlerinden
   doğrulanmalı (bkz. `faz8-kdv-kapanis.md`).
