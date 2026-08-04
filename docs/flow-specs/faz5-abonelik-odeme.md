# Flow Spec — Faz 5A: Abonelik / Ödeme

> Blueprint §0.5 ANA KURAL şablonuna göre hazırlanmıştır. Bu modül ancak bu dokümandaki
> tüm adımlar ve uç durumlar hatasız geçince "bitti" sayılır.

## Özellik

SMMM ofisi kayıt olduğunda **14 günlük deneme** otomatik başlar; Ayarlar → Abonelik'ten
paket satın alır/değiştirir/iptal eder, faturalarını görür. Ödeme adapter arkasındadır
(Mock → iyzico); süper admin gelir/tahsilat özetini görür (blueprint M7).

## Kaynak akış referansları

- Blueprint M7: aylık abonelik, paket sınırları, iyzico/PayTR, deneme süresi, süper admin gelir panosu.
- iyzico entegrasyon modeli: kart PLATFORMDA TUTULMAZ; barındırılan Checkout Form'a yönlendirme
  (IyzicoPaymentProvider iskeletinde TODO-VERIFY notlarıyla; canlı anahtar sonrası doğrulanacak).
- KDV: platform hizmet faturasında %20 (plan fiyatları KDV hariç kabul edildi).

## Tasarım kararları (uygulanan)

| Karar | Uygulama |
|---|---|
| Tek gerçek kaynak | `Subscription` tablosu (tenant başına TEK kayıt, unique TenantId); `Tenant.SubscriptionStatus` her geçişte SENKRON tutulur (geriye uyumluluk) |
| Adapter | `IPaymentProvider` → `MockPaymentProvider` (varsayılan) / `IyzicoPaymentProvider` (iskelet); `Payments:Provider` yapılandırmasıyla seçilir |
| Mock test kartları | ~2 sn gecikme; **4111...** → başarı, **4000...** → "Ödeme alınamadı: kart reddedildi", boş → başarı; kart SAKLANMAZ |
| Deneme | Kayıtta otomatik 14 gün (`SubscriptionService.StartTrial`); `TrialEndsAt` geçince PastDue |
| Yenileme kontrolü | Arka plan job'ı YOK: `CheckAndUpdateStatusAsync` **login**, **/api/auth/me** (panel açılışı) ve **/api/billing/summary** çağrılarında tetiklenir |
| Plan değişikliği | ANINDA geçerli; kalan gün oranlaması YOK, yeni dönem bugünden başlar (bilinçli sadelik) |
| Düşürme koruması | Yeni plan sınırları mevcut kullanımın altındaysa (MaxClients **ve MaxUsers**) geçiş 400 ile engellenir |
| İptal | `CanceledAt` işaretlenir; dönem sonuna kadar aktif, sonra Canceled |
| Fatura numarası | Yıl bazlı, tenant-bağımsız GLOBAL sıra "2026-000001" (`InvoiceNumberSequence`, Year PK). **Eşzamanlılık notu:** oku-artır-yaz; `BillingInvoice.Number` unique index son savunma hattı, yüksek hacimde PostgreSQL SEQUENCE'e geçilecek |
| Fatura tutarı | Amount = plan fiyatı (KDV hariç), VatAmount = %20, TotalAmount tahsil edilir; PDF ÜRETİLMEZ (sonraki faz) |
| Yetki | subscribe/cancel yalnızca `TenantOwner`; summary/invoices/transactions tüm tenant kullanıcıları |
| Denetim izi | Her tahsilat girişimi (başarısız dahil) `PaymentTransaction` kaydı |
| Kota mesajı | Mükellef kota hatasına kullanılan/limit eklendi: "10/10 mükellef kullanımdasınız..." |

## Ön koşullar

1. Planlar seed edilmiş (baslangic/profesyonel/ofis).
2. Ödeme sağlayıcısı Mock (varsayılan) — ek yapılandırma gerekmez.

## Adım adım kullanıcı akışı

1. **Aksiyon:** Yeni ofis `/kayit` ile kaydolur.
   → **Beklenen:** Tenant + sahip kullanıcı + `Subscription(Trial, TrialEndsAt=+14g)` oluşur;
   Ayarlar → Abonelik'te "Deneme" rozeti ve "N gün kaldı" görünür (≤3 gün sarı uyarı bandı).
   → **Hatalar:** E-posta kayıtlı → 409 "Bu e-posta adresi zaten kayıtlı."
2. **Aksiyon:** Ayarlar → Abonelik'te paket kartı seçer, test kartı `4111...` girer, "Öde ve Paketi Etkinleştir".
   → **Beklenen:** `POST /api/billing/subscribe` ~2 sn sonra: yeşil bant "X paketine geçişiniz
   tamamlandı. 2026-000001 numaralı faturanız oluşturuldu."; durum Aktif, dönem bugün→+1 ay,
   fatura listede (tutar/KDV/toplam).
   → **Hatalar:** `4000...` kart → 400 **"Ödeme alınamadı: Kart bankanız tarafından reddedildi..."**,
   işlem Failed olarak kayıtlı; geçersiz kart biçimi → UI + backend Türkçe validasyon;
   personel (TenantStaff) → 403; düşürme sınır ihlali → "Bu pakete geçilemez: ...".
3. **Aksiyon:** "Aboneliği İptal Et" (onay penceresiyle).
   → **Beklenen:** "İptal talebiniz alındı. Aboneliğiniz dd.MM.yyyy tarihine kadar aktif kalacak.";
   sarı bilgi bandı görünür; ikinci iptal → 400 "İptal talebiniz zaten alınmış...".
4. **Aksiyon:** Dönem sonu geçtikten sonra tekrar giriş yapar / paneli açar.
   → **Beklenen:** Durum otomatik PastDue (iptal talepliyse Canceled); Abonelik ekranında
   kırmızı "paketinizi yenileyin" bandı; yeniden ödeme durumu Aktif'e döndürür.
5. **Aksiyon:** Mükellef sınırındayken yeni mükellef ekler.
   → **Beklenen:** 403 "10/10 mükellef kullanımdasınız. ... Ayarlar → Abonelik'ten paketinizi yükseltin."
6. **Aksiyon:** Süper admin `/admin`i açar.
   → **Beklenen:** Gelir Özeti kartları: bu ay tahsilat + fatura adedi, toplam tahsilat,
   abonelik dağılımı (Aktif/Deneme/Geciken/İptal).
7. **Aksiyon:** Girişliyken `/fiyatlandirma` sayfasındaki CTA'ya basar.
   → **Beklenen:** "/kayit" yerine "/panel/ayarlar"a (Abonelik) yönlenir; buton "Paketi Seç".

## Uç durumlar

- **Faz 5 öncesi tenant** (Subscription kaydı yok): summary `hasSubscription=false` ile Tenant
  üzerindeki durumu gösterir; ilk satın almada Subscription kaydı açılır.
- **Plan seed edilmemişse** kayıt yine tamamlanır (abonelik kaydı atlanır, uyarı loglanır).
- **iyzico anahtarsız** (`Payments:Provider=Iyzico` ama anahtar boş): "Ödeme alınamadı: Ödeme
  sağlayıcısı yapılandırılmamış..." — uygulama çökmez.
- **Sağlayıcı zaman aşımı/ağ hatası:** Türkçe retryable mesaj; işlem Failed kaydedilir.
- **Fatura numarası yarışı:** unique index çakışmayı engeller (istek tekrarı yeterli).

## Kabul kriteri

4111 kartıyla uçtan uca: kayıt → deneme → satın alma → fatura "2026-000001" → iptal → dönem
sonu PastDue/Canceled geçişi hatasız; 4000 kartıyla hata yolu Türkçe mesajla çalışır; admin
gelir kartları tahsilatla tutarlıdır.

## Test adımları

- Backend: SubscriptionService birim testleri (trial, geçişler, düşürme koruması, numara sırası) — xUnit (sonraki faz).
- Manuel: yukarıdaki 7 adım + uç durumlar; Playwright E2E sonraki fazda eklenecek.
