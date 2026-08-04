# Flow Spec — Faz 10: Uyumsoft Özel Entegratör Adaptörü

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu modül ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

## Özellik

Mükellefe entegratör olarak **Uyumsoft** bağlanabilir. Uyumsoft REST değil **SOAP 1.1** web
servisi sunar; kimlik API anahtarı değil **kullanıcı adı + şifredir**. Kimlik, mevcut
`ClientIntegration.ApiKeyEncrypted` kolonunda `{"username":"...","password":"...","endpoint":"..."?}`
JSON'u olarak **AES ile şifreli** saklanır (ayrı kolon açılmaz, migration gerekmez).
`endpoint` alanı opsiyoneldir: Uyumsoft'un "Web Servis URL" e-postasındaki adres girilir;
boşsa varsayılan uç kullanılır. Senkron akışı Faz 1 ile birebir aynıdır:
`EInvoiceSyncService` → `IEInvoiceProvider` → mükerrer kontrolü → gelen kutusu.

## Protokol kaynağı: ÇALIŞAN eski projeden birebir doğrulama (30.07.2026)

Adaptör, kullanıcının **canlıda fatura kesmiş, çalışan eski projesindeki Python istemcisinden**
doğrulanan protokole göre yazılmıştır. Önceki sürümdeki **WS-Security UsernameToken + Timestamp
varsayımı KALDIRILMIŞTIR** — o varsayım resmî doküman/WSDL'e dayanıyordu, ancak çalışan kodda
kimlik gövdededir ve uç farklıdır.

### Doğrulanan gerçek servis bilgileri (çalışan koddan)

| Konu | Doğrulanan bilgi |
|---|---|
| Varsayılan uç | `http://edonusumapi.uyum.com.tr/Services/BasicIntegration` — **DİKKAT: bu ortam HTTP'dir** ve hesaplar genellikle bu ortamda tanımlıdır. Adaptör varsayılan olarak `https://` dener; ilk denemede bağlantı/SSL hatası alırsa AYNI isteği bir kez `http://` ile yineler ve başarılı şemayı loglar. |
| Alternatif ortam | `efatura.uyumsoft.com.tr` **FARKLI bir ortamdır**; bazı hesaplar oradadır. Hangi ortamın kullanılacağı kimlikteki opsiyonel `endpoint` alanıyla seçilir (UI: "Servis Adresi"). |
| Test ucu | `https://efatura-test.uyumsoft.com.tr/services/BasicIntegration` (test kimliği: `Uyumsoft` / `Uyumsoft`) |
| Protokol | SOAP 1.1 (`Content-Type: text/xml; charset=utf-8` + `SOAPAction` başlığı), XML ad alanı `http://tempuri.org/` |
| SOAPAction | `"http://tempuri.org/IBasicIntegration/{Operasyon}"` — çalışan projede `SendInvoice`, `GetAlias`, `GetOutboxInvoiceView`, `GetOutboxInvoicePdf`, `TestConnection`, `CancelEArchiveInvoice` bu kalıpla çalışmıştır. |
| Kimlik doğrulama | **WS-Security YOK.** Gövdede İLK eleman olarak `<userInfo Username="..." Password="..."/>` (tempuri ad alanı) gönderilir. |
| Zarf yapısı | `Body` → `<{Operasyon} xmlns="http://tempuri.org/"><userInfo .../>{parametreler}</{Operasyon}>`. Parametreler basit camelCase elemanlardır (ör. `GetOutboxInvoiceView`: `<startDate>2024-01-01T00:00:00</startDate><endDate>...</endDate><pageIndex>1</pageIndex><pageSize>20</pageSize>`). |
| Yanıt zarfı | `{Operasyon}Result` üzerinde `IsSucceded` / `Message` **öznitelikleri**. `GetInboxInvoiceList` yanıtında alanlar **çocuk elemandır** (aşağıdaki canlı yanıt bölümü); View fallback'inde `InvoiceInfo` kayıtları alanları **öznitelik** olarak taşıyabilir (ör. `ETTN="..." InvoiceNumber="..."`) — ayrıştırıcı iki biçimi de tolere eder. |
| Sayfalama | `pageIndex` 1 tabanlıdır (çalışan projedeki örnek: `pageIndex=1`). |

## CANLI YANIT doğrulaması (31.07.2026 — GetInboxInvoiceList, HTTP 200)

`http://edonusumapi.uyum.com.tr/Services/BasicIntegration` ucuna canlı kimlikle atılan
`GetInboxInvoiceList` isteği **çalıştı** (kimlik + zarf doğrulandı). Birebir gözlenen yapı:

```xml
<GetInboxInvoiceListResponse xmlns="http://tempuri.org/">
  <GetInboxInvoiceListResult IsSucceded="true">
    <Value PageIndex="1" PageSize="5" TotalCount="1044" TotalPages="209">
      <Items>
        <InvoiceId>COS2024000000984</InvoiceId>
        <DocumentId>42E01485-B92D-44F0-8CBC-0468AC6A5DAE</DocumentId>
        <Type>BaseInvoice</Type>
        <TargetTcknVkn>5820459419</TargetTcknVkn>
        <TargetTitle>KOZMOPLEKS MAĞAZACILIK A.Ş. </TargetTitle>
        <Status>Approved</Status>
        <CreateDateUtc>2024-07-16T17:18:39.567</CreateDateUtc>
        <ExecutionDate>2024-07-16T12:52:11</ExecutionDate>
        <PayableAmount>830.0000</PayableAmount>
        <TaxTotal>138.3300</TaxTotal>
        <TaxExclusiveAmount>691.67</TaxExclusiveAmount>
        <DocumentCurrencyCode>TRY</DocumentCurrencyCode>
        <Vat20>138.33</Vat20>
        <Vat20TaxableAmount>691.67</Vat20TaxableAmount>
        ...
      </Items>
      <Items>...</Items>
    </Value>
  </GetInboxInvoiceListResult>
</GetInboxInvoiceListResponse>
```

Canlı yanıttan çıkan kritik bulgular ve adaptöre uygulanan düzeltmeler:

1. **Alanlar öznitelik DEĞİL çocuk elemandır** (tempuri default ns altında) — `GetField`
   zaten öznitelik → eleman sırasıyla toleranslı okuduğundan eleman biçimi doğrulandı.
2. **TERS EŞLEME DÜZELTİLDİ:** `InvoiceId` = **GİB fatura numarası** (ör. `COS2024000000984`),
   `DocumentId` = **UUID/GUID (ETTN)**. Önceki kod tam tersini yapıyordu. Yeni eşleme:
   `ExternalUuid := ETTN → DocumentId`, `InvoiceNumber := InvoiceNumber → InvoiceId`
   (ilk adlar View fallback'i içindir).
3. **`Value` öznitelikleri:** `PageIndex`/`PageSize`/`TotalCount`/`TotalPages` ✓ (sayfalama
   `TotalPages`'ten okunur).
4. **KDV kırılımı satır üretimi:** yanıt `Vat1/Vat8/Vat10/Vat18/Vat20` +
   `VatXTaxableAmount` elemanlarını döndürür → tek özet satır yerine **sıfır olmayan her
   oran için bir `ProviderInvoiceLine`** üretilir (`ItemName: "%X KDV'li mal/hizmet toplamı"`,
   `LineAmount = VatXTaxableAmount`, `VatRate = X`, `VatAmount = VatX`). Kırılım tamamen
   boşsa eski tek-özet-satır davranışına düşülür. Kod motoru satır bazlı çalıştığı için bu
   ciddi kalite artışıdır.
5. **Zarf sadeleştirildi:** canlı denemede query **yalnız `PageIndex`/`PageSize`
   öznitelikleriyle** çalıştı (tarih/sıralama elemanları yokken). Query artık yalnız
   `PageIndex`/`PageSize`/`OnlyNewestInvoices` öznitelikleri + `ExecutionStartDate`/
   `ExecutionEndDate` elemanlarını içerir (tarih filtresi işlevsel); `xsi:nil`'li elemanlar
   (`CreateStartDate`/`CreateEndDate`/`Status`/`IsArchived`) ve `SortColumn`/`SortMode`
   **kaldırıldı** — giden zarfta `xsi` bildirimi de yoktur.
6. **Sayfalar arası mükerrer koruması:** `TotalCount` 1.044 / `TotalPages` 209 olabildiği
   ve sunucu sıralaması garanti edilmediği için sağlayıcı içinde `HashSet` ile aynı
   `ExternalUuid` ikinci kez gelirse atlanır (sayfa içi + sayfalar arası);
   `EInvoiceSyncService` zaten aynı-run içinde eklenen uuid'leri bellek içi sette tutar
   (`existingSet.Add`) ve DB `(TenantId, ExternalUuid)` unique index'i son savunmadır.
7. **`TargetTitle` sondaki boşlukla dönebilir** (`"... A.Ş. "`) — değerler trim'lenir.
8. **MaxPages sınırı bilgilendirmesi:** sınır 50 sayfa × 100 = 5.000 faturada kalır; sınıra
   takılınca senkron mesajına **"İlk 5.000 fatura alındı; daha eski kayıtlar için tarih
   aralığını daraltın."** notu eklenir (`ProviderResult.Warning` → `SyncResult.Warning` →
   `SyncResultResponse.Message`).

### Gelen fatura listesi: iki operasyonlu strateji

1. **Önce `GetInboxInvoiceList`** denenir — **canlıda 31.07.2026'da ÇALIŞTIĞI doğrulandı**
   (sadeleştirilmiş query: `PageIndex`/`PageSize`/`OnlyNewestInvoices` öznitelikleri +
   `ExecutionStartDate`/`ExecutionEndDate` elemanları; `xsi:nil`'li elemanlar ve Sort
   elemanları kaldırıldı). SOAPAction: `http://tempuri.org/IBasicIntegration/GetInboxInvoiceList`.
2. **Fault "operasyon tanınmadı" derse** (`ActionNotSupported` / `"was not recognized"` /
   `ContractFilter` benzeri) aynı uca **`GetInboxInvoiceView`** ile düşülür: basit parametreler
   `<startDate>yyyy-MM-ddTHH:mm:ss</startDate><endDate>...</endDate><pageIndex>N</pageIndex><pageSize>100</pageSize>`.
   Kayıtlar (List: `Items` çocuk elemanları; View: `InvoiceInfo` öznitelikleri) **toleranslı**
   ayrıştırılır — her ad önce öznitelikte, sonra alt elemanda aranır:
   - `ETTN` veya `DocumentId` → UUID (yoksa kayıt atlanır) — **DİKKAT: `InvoiceId` UUID değildir**
   - `InvoiceNumber` veya `InvoiceId` → fatura numarası
   - `TargetTcknVkn` veya `SenderTcknVkn` → karşı taraf VKN
   - `TargetTitle` veya `SenderTitle` → karşı taraf unvanı (trim'li)
   - `PayableAmount` / `TaxTotal` / `TaxExclusiveAmount` → tutarlar (**eksik sayısal alanlar 0**)
   - `ExecutionDate` veya `CreateDateUtc` → fatura tarihi (yoksa bugün)
   - `Vat1/Vat8/Vat10/Vat18/Vat20` + `VatXTaxableAmount` → oran bazlı kalem satırları
3. Hangi operasyonun kullanıldığı ve etkin uç **loglanır**; fault metni ve `IsSucceded=false`
   `Message` değeri her durumda **TAM olarak** log'a yazılır (gerçek hata metinleri eşleme
   iyileştirmesi için gereklidir).

Gerçek kalem (satır) detayı liste yanıtında dönmez; ancak **KDV kırılımı döner** →
sıfır olmayan her `VatX` için bir satır üretilir (`"%X KDV'li mal/hizmet toplamı"`),
kırılım boşsa başlık toplamlarından tek özet satıra düşülür (Nilvera ile aynı yaklaşım);
gerçek kalemler ileride `GetInboxInvoiceData`'nın UBL XML'inden çıkarılacaktır.

### TODO-VERIFY listesi (canlı kimlikle test edilecek)

1. ~~`GetInboxInvoiceList`'in edonusumapi ortamında tanınıp tanınmadığı~~ — **DOĞRULANDI
   (31.07.2026): tanınıyor ve HTTP 200 dönüyor** (yukarıdaki canlı yanıt bölümü);
   `GetInboxInvoiceView` fallback'i yine de korunur.
2. **`GetInboxInvoiceView` yanıtındaki gerçek alan adları** — ayrıştırma öznitelik + eleman
   biçimlerini ve alternatif adları (`ETTN/DocumentId`, `Target*/Sender*`) tolere eder.
3. **Gerçek kalem (satır) detayı** — `GetInboxInvoiceData` UBL XML ayrıştırması eklenecek;
   şimdilik KDV kırılımından oran bazlı satırlar (kırılım boşsa tek özet satır).
4. **`IsSucceded=false` durumundaki `Message` metinleri** — kimlik hatası tespiti anahtar
   kelimeyle yapılıyor (`kullanıcı/şifre/password/unauthorized...`); tüm mesajlar tam
   loglandığından gerçek metinler görülünce eşleme netleştirilecek.

## Ön koşullar

1. SMMM oturum açmış (JWT) ve en az bir mükellef kayıtlı.
2. Mükellefe entegrasyon tanımlanmış: sağlayıcı `Uyumsoft` + web servis kullanıcı adı + şifre
   (+ opsiyonel servis adresi, test/canlı ortam seçimi). Kimlik
   `{"username","password","endpoint"?}` JSON'u olarak AES ile şifreli saklanır; API yanıtında
   kullanıcı adının ilk 2 + son 2 karakteri maskeli döner (`uy••••um`), şifre hiçbir zaman
   dönmez. **Servis adresi gizli değildir, maskesiz döner** (`IntegrationResponse.Endpoint`).
3. Harici NuGet paketi YOKTUR: SOAP zarfı `HttpClient` ile ham POST edilir, yanıt
   `XDocument` ile ayrıştırılır.

## Adım adım kullanıcı akışı

1. **Aksiyon:** Mükellefler sayfasında "Entegrasyon" düğmesine tıklar, sağlayıcı olarak
   "Uyumsoft" seçer.
   → **Beklenen:** "Kullanıcı Adı" + "Şifre" alanlarının yanında opsiyonel **"Servis Adresi"**
   alanı görünür (placeholder: `https://edonusumapi.uyum.com.tr/Services/BasicIntegration`;
   yardım metni: "Uyumsoft'tan gelen 'Web Servis URL' e-postasındaki adres. Boş bırakılırsa
   varsayılan kullanılır."); "Test ortamını kullan" kutusu görünür.
   → **Hatalar:** Boş kullanıcı adı/şifre ile Kaydet → "Uyumsoft için kullanıcı adı ve şifre
   zorunludur."; yalnızca biri girilirse "Uyumsoft için kullanıcı adı ve şifre birlikte
   girilmelidir."; geçersiz servis adresi (http/https URL değilse) → 400
   **"Geçerli bir servis adresi girin (https://... )."** (UI ve backend çift validasyon).
2. **Aksiyon:** Kimliği (ve varsa servis adresini) girip "Kaydet"e basar.
   → **Beklenen:** `POST /api/clients/{id}/integration` 200/201; kimlik + endpoint JSON'a
   paketlenip şifrelenir; modalda "Kayıtlı sağlayıcı: Uyumsoft · Kullanıcı adı: uy••••um"
   görünür, Servis Adresi alanı kayıtlı değerle dolu gelir.
   → **Hatalar:** Kayıtlı kimlik varken yalnızca test ortamı veya servis adresi
   değiştirilebilir (kimlik alanları boş bırakılabilir; endpoint değişirse kayıtlı kimlik
   çözülüp yeni endpoint ile yeniden paketlenir); sağlayıcı değişiminde (Nilvera↔Uyumsoft)
   eski sır korunmaz, yeni kimlik zorunludur.
3. **Aksiyon:** Faturalar sayfasında tarih aralığı seçip "Senkronize Et"e basar.
   → **Beklenen:** `EInvoiceSyncService` kimliği çözer → `UyumsoftProvider` ucu çözer
   (kimlikteki endpoint > test ortamı > varsayılan edonusumapi) → `GetInboxInvoiceList`
   zarfını sayfa sayfa POST eder (PageSize=100, en fazla 50 sayfa); https kurulamazsa http'ye
   bir kez düşer; operasyon tanınmazsa `GetInboxInvoiceView`'e düşer → faturalar
   `(TenantId, ExternalUuid)` mükerrer kontrolüyle yazılır → "X yeni fatura alındı, Y mükerrer
   atlandı."
   → **Hatalar:** aşağıdaki hata eşleme tablosu.
4. **Aksiyon:** Gelen kutusunda faturayı açar.
   → **Beklenen:** Başlık bilgileri (satıcı unvanı, VKN, tarih, tutarlar) + KDV kırılımından
   oran bazlı kalem satırları (ör. "%20 KDV'li mal/hizmet toplamı"); kırılım dönmediyse tek
   özet satır ("Fatura toplamı (kalem detayı UBL XML'den alınacak)").

## Hata eşleme (UyumsoftProvider)

| Durum | Davranış |
|---|---|
| Kimlik JSON'u çözülemedi / eksik | "Uyumsoft kimlik bilgileri tanımlı değil. Lütfen entegrasyonu kullanıcı adı ve şifreyle yeniden kaydedin." (retryable değil) |
| Fault/mesaj "yetkiniz yok" içeriyor | **"Uyumsoft bu hesaba web servis erişim izni vermedi..."** — kimlik doğru, Uyumsoft tarafında web servis yetkisi/IP izni tanımlatılmalı (Uyumsoft mesajı ekli). |
| HTTP 401/403 veya kimlik içerikli fault/mesaj | **"Uyumsoft kullanıcı adı veya şifresi hatalı."** (retryable değil) |
| Fault "operasyon tanınmadı" (`ActionNotSupported`/`was not recognized`/`ContractFilter`) | Hata DEĞİL → `GetInboxInvoiceView` fallback'i aynı sayfadan devam eder (loglanır). |
| Diğer SOAP Fault | Fault metni TAM loglanır; kullanıcıya "Uyumsoft servisi hata bildirdi: {faultstring}"; faultcode `Server/Receiver` ise retryable |
| `IsSucceded="false"` | `Message` TAM loglanır; kullanıcıya "Uyumsoft servisi hata bildirdi: {Message}" |
| Zaman aşımı (30 sn) | "Uyumsoft servisi yanıt vermedi (zaman aşımı)..." — **retryable** |
| HTTP 408/429 | "Uyumsoft servisi şu anda meşgul..." — **retryable** |
| HTTP 5xx | "Uyumsoft tarafında geçici bir sorun oluştu..." — **retryable** |
| Ağ hatası (http fallback dahil başarısız) | "Uyumsoft servisine bağlanılamadı. Servis adresini ve internet bağlantınızı kontrol edip tekrar deneyin." — **retryable** |
| Bozuk XML yanıt | "Uyumsoft'tan beklenmeyen bir yanıt alındı..." — retryable |

## Uç durumlar

- **Boş dönem:** boş sayfa → 0 fatura, hata değil; UI "0 yeni fatura alındı."
- **Mükerrer:** aynı ETTN ikinci senkronda atlanır (Faz 1 altyapısı). Ek olarak sağlayıcı
  içinde sayfa içi + **sayfalar arası** tekrar `HashSet` ile atlanır (sunucu sıralaması
  garanti değil; canlıda TotalPages 209 görüldü).
- **MaxPages sınırına takılma (50 sayfa × 100):** senkron başarılıdır; mesaja "İlk 5.000
  fatura alındı; daha eski kayıtlar için tarih aralığını daraltın." notu eklenir.
- **UUID'siz kayıt (`ETTN`/`InvoiceId` boş):** yok sayılır.
- **`ExecutionDate` yok/nil:** `CreateDateUtc` kullanılır; o da yoksa bugünün tarihi.
- **Eksik sayısal alanlar:** 0 kabul edilir (View fallback'inde alan adları değişebilir).
- **`TotalPages` dönmedi (View):** dolu ama eksik sayfa (`< 100` kayıt) son sayfa sayılır;
  boş sayfa döngüyü her durumda sonlandırır; en fazla 50 sayfa × 100 fatura.
- **https → http düşüşü:** yalnızca ilk istekte ve URL https ise bir kez denenir; başarılı
  şema loglanır ve sonraki sayfalarda kullanılır.
- **Mock ile çalışmaya devam:** Uyumsoft kimliği alınana kadar Mock sağlayıcı uçtan uca
  akışı çalıştırır; adaptör derlenir ve DI'da kayıtlıdır ama seçilmedikçe çağrılmaz.

## Dokunulan dosyalar

- `backend/MaliMusavir.Api/Domain/EInvoiceEntities.cs` — enum'a `Uyumsoft = 2`
- `backend/MaliMusavir.Api/Integrations/UyumsoftProvider.cs` — BasicIntegration SOAP adaptörü:
  gövdede `userInfo` kimliği (WS-Security YOK), `IBasicIntegration` SOAPAction, endpoint çözümü
  + http şema fallback'i, `GetInboxInvoiceList` → `GetInboxInvoiceView` operasyon fallback'i,
  toleranslı öznitelik/eleman ayrıştırma; `PackCredentials(username, password, endpoint?)` /
  `TryParseCredentials(..., out endpoint)` kimlik paketleme yardımcıları
- `backend/MaliMusavir.Api/Contracts/Dtos.cs` — `IntegrationSaveRequest.Username/Password/Endpoint`,
  `IntegrationResponse.MaskedUsername/Endpoint`
- `backend/MaliMusavir.Api/Controllers/IntegrationsController.cs` — Uyumsoft doğrulaması
  (endpoint URL kontrolü dahil) + JSON paketleme + maskeleme (+ yalnız-endpoint güncellemesi)
- `backend/MaliMusavir.Api/Program.cs` — `uyumsoft` HttpClient (30 sn) + DI kaydı
- `backend/MaliMusavir.Api/Integrations/IEInvoiceProvider.cs` — `ProviderResult.Warning`
  (başarılı sonuca bilgi notu; MaxPages kırpılma mesajı için)
- `backend/MaliMusavir.Api/Services/EInvoiceSyncService.cs` — `SyncResult.Warning` taşınır
- `backend/MaliMusavir.Api/Controllers/EInvoicesController.cs` — başarı mesajına `Warning` eklenir
- `web/lib/api.ts` — `"Uyumsoft"` tipi, `username/password/endpoint`, `maskedUsername/endpoint`
- `web/app/panel/mukellefler/page.tsx` — entegrasyon modalında Uyumsoft kimlik alanları +
  opsiyonel "Servis Adresi" alanı
