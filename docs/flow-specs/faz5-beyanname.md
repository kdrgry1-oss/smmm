# Flow Spec — Faz 5B: GİB e-Beyanname (KDV Taslağı → Gönderim → Tahakkuk)

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu modül ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

## Özellik

Motor verisinden (onaylı gelen faturalar + onaylı fişler) **KDV1 taslağı** hesaplanır,
GİB'e web servisle gönderilir, **tahakkuk/onay/hata** tek panodan takip edilir — çok
mükellefli, dönem bazlı (blueprint M8 / motor eki B9: "hazırla → gönder → tahakkuk al").

## Kaynak akış referansları

- Blueprint M8 + B9: GİB'in e-Beyanname sistemi web servis + token ile gönderimi destekler;
  SMMM'nin kendi e-Beyanname kullanıcı kodu/şifresi ve "yeni beyanname sistemi token"ı ile
  çalışır (Luca'ya bağımlı değil). Paket, BDP şemasına birebir uymalıdır.
- KDV1 hesap mantığı (beyanname yapısı): Hesaplanan KDV (satış/giden faturalar) −
  [İndirilecek KDV (alış belgeleri) + Önceki dönemden devreden] → Ödenecek KDV **veya**
  Sonraki döneme devreden KDV.
- İndirilecek KDV kırılımı: 191 hesap alt kırılımı — **191.01 (%1), 191.02 (%10), 191.03 (%20)**,
  diğer oranlar 191.99 (Tekdüzen Hesap Planı yaygın uygulaması).
- **Dürüst not:** Gerçek BDP paket üretimi (XML şeması) ve GİB uçları, SMMM kimliği + token
  iletilince Flow Discovery ile doğrulanıp `GibProvider` adapter'ına eklenecek; bu fazda taslak
  hesap + mock gönderim/tahakkuk uçtan uca çalışır.

## Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Adapter | `IEBeyannameProvider` → `MockGibProvider`; gerçek GİB web servisi tek sınıfla eklenecek |
| Mock gönderim | Paket no `GIB-{unixSaniye}-{rastgele}` (gönderim anı pakete gömülü) |
| Mock durum | İlk 30 sn "işleniyor" (Sent kalır); sonra paket no'dan DETERMİNİSTİK %10 şema hatası (Error), aksi halde Onaylandı + tahakkuk (özetteki `odenecekKdv`; devredende 0) |
| Retry | Yeniden gönderim YENİ paket no üretir → hata alan beyanname tekrar gönderilince genellikle geçer (test edilebilir retry) |
| Mükerrer | Unique (TenantId, ClientId, Type, Period); "hazırla" mevcut Draft/Ready/Error kaydı YENİDEN HESAPLAR; Sent/Approved yeniden hesaplanamaz (400) |
| Hesaplanan KDV | Yalnızca GİDEN (satış) faturalarından; satış senkronu yoksa 0 + UI bilgi notu |
| İndirilecek KDV | Yalnızca ONAYLI belgeler: gelen fatura satırları (CodingStatus=Approved) + fişler (Status=Approved), oran kırılımıyla |
| Devreden | Önceki dönemin KDV1 kaydındaki "sonraki döneme devreden" (kayıt yoksa 0). **Sınırlama:** önceki kayıt taslak bile olsa kaynak alınır (onay şartı aranmaz) |
| Özet saklama | `Declaration.SummaryJson` (camelCase JSON, tek kolon) — detay/işlem yanıtlarında tip güvenli `kdvSummary` olarak döner |
| Diğer türler | KDV2/Muhtasar/Damga → 400 "Bu beyanname türü yakında eklenecek..." |
| Durumlar | Draft → Sent → Approved / Error (Error → yeniden gönder); Ready durumu ileride "kontrol edildi" işareti için rezerve |

## Ön koşullar

1. SMMM oturum açmış; en az bir mükellef var.
2. Dönem içinde onaylı fatura/fiş varsa kırılım dolu gelir (yoksa taslak 0'larla hazırlanır — boş durum notuyla).
3. Mock için entegrasyon ayarı GEREKMEZ.

## Adım adım kullanıcı akışı

1. **Aksiyon:** Sidebar → **Beyannameler**; mükellef + dönem (ay seçici, varsayılan önceki ay) seçer,
   "KDV Taslağı Hazırla"ya basar.
   → **Beklenen:** `POST /api/declarations/prepare` → yeşil bant "KDV1 taslağı hazırlandı (N onaylı
   fatura + M onaylı fiş işlendi)."; detay kartında kırılım tablosu (191.01/02/03, matrah/KDV/belge),
   beyan özeti (hesaplanan / indirilecek / devreden / **ödenecek** kırmızı veya **devreden** yeşil).
   → **Hatalar:** KDV1 dışı tür → 400 "Bu beyanname türü yakında eklenecek."; geçersiz dönem →
   400 "Dönem biçimi geçersiz. Örnek: 2026-06."; mükellef yok → 404.
2. **Aksiyon:** Aynı dönem için tekrar "Hazırla" basar (ör. yeni fatura onayladıktan sonra).
   → **Beklenen:** Mevcut taslak YENİDEN hesaplanır: "Mevcut taslak yeniden hesaplandı (...)".
3. **Aksiyon:** Detay kartında **"GİB'e Gönder"**.
   → **Beklenen:** `POST /api/declarations/{id}/send` → durum "Gönderildi" (sarı rozet), paket no
   görünür, sarı bant "~30 saniye sonra Durumu Sorgula".
   → **Hatalar:** Zaten gönderilmiş → 400; taslak özeti boş → 400 "Önce Taslağı Hazırla...".
4. **Aksiyon:** 30 sn içinde **"Durumu Sorgula"**.
   → **Beklenen:** "GİB beyannameyi hâlâ işliyor..." — durum Gönderildi kalır.
5. **Aksiyon:** 30 sn sonra tekrar **"Durumu Sorgula"**.
   → **Beklenen:** ~%90: durum **Onaylandı** (yeşil), tahakkuk tutarı + onay zamanı; devreden
   dönemde "ödenecek KDV çıkmadı". ~%10: durum **Hata** (kırmızı) + GİB şema hatası açıklaması +
   **"Yeniden Gönder"** butonu.
6. **Aksiyon:** Hata durumunda **"Yeniden Gönder"**.
   → **Beklenen:** Yeni paket no ile tekrar Gönderildi; sorguda genellikle Onaylanır (retry akışı).
7. **Aksiyon:** Alt panoda listeyi inceler; mükellef/dönem filtreler.
   → **Beklenen:** Tüm mükelleflerin dönem beyannameleri tek tabloda: mükellef, tür, dönem,
   durum rozeti (+kısa hata metni), paket no, tahakkuk, hazırlanma tarihi, "Görüntüle";
   eşleşme yoksa "Sonuç bulunamadı"; hiç kayıt yoksa yönlendirici boş durum.

## Uç durumlar

- **Boş dönem:** Onaylı belge yoksa taslak 0'larla oluşur; kırılım bölümünde "önce belgeleri
  onaylayın" yönlendirmesi gösterilir.
- **Onaylanmış beyanname:** Yeniden hazırlanamaz/gönderilemez (400, Türkçe).
- **Sorgu sırası:** Gönderilmemiş beyannamede "Durumu Sorgula" → 400 "Yalnızca gönderilmiş...".
- **Devir zinciri:** Dönem N'in devri, N+1 taslağına otomatik taşınır; N kaydı yoksa 0.
- **Tenant izolasyonu:** Tüm sorgular query filter'lı; başka tenant'ın beyannamesi 404.
- **Sağlayıcı hatası:** ProviderResult.Fail → Türkçe mesaj, durum DEĞİŞMEZ (yeniden denenebilir).

## Kabul kriteri

Uçtan uca: onaylı belgelerle taslak hazırla → kırılım toplamları belge toplamlarıyla birebir →
gönder → 30 sn sonra Onaylandı + tahakkuk = ödenecek KDV; hata senaryosunda Error + açıklama +
yeniden gönderim başarısı; devir zinciri iki ardışık dönemde doğru taşınır.

## Test adımları

- Backend: DeclarationService birim testleri (kırılım, devir, ödenecek/devreden, durum geçişleri) — xUnit (sonraki faz).
- Manuel: yukarıdaki 7 adım + uç durumlar; Playwright E2E sonraki fazda eklenecek.
