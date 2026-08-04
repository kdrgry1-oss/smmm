# MaliMüşavir SaaS — Faz 0-5

Türkiye'deki mali müşavirler (SMMM) için çok kiracılı (multi-tenant) SaaS platformu.

Tamamlanan fazlar: **Faz 0** kimlik/tenant/mükellef + süper admin, **Faz 1** e-Fatura senkronu
(adapter + Mock/Nilvera), **Faz 2** otomatik hesap/cari kod motoru + Luca CSV dışa aktarma,
**Faz 3** fiş OCR → gider (IReceiptOcrProvider + MockOcrProvider, gider gösterilebilirlik,
onay/öğrenme), **Faz 4** e-Tebligat tek pano (IETebligatProvider + MockUetsProvider, yasal
süre sayaçları), **Faz 5** abonelik/ödeme (IPaymentProvider + MockPay/iyzico iskeleti, 14 gün
deneme, faturalama) + GİB e-Beyanname (IEBeyannameProvider + MockGib, KDV1 taslak → gönderim →
tahakkuk). Ayrıntılar: `../MaliMusavir_SaaS_Blueprint.md` ve `docs/flow-specs/`.

## Mimari Özet

- **backend/MaliMusavir.Api** — .NET 8 Web API
  - PostgreSQL + EF Core 8 (Npgsql)
  - JWT kimlik doğrulama (BCrypt şifre hash'i)
  - Çok kiracılı izolasyon: `AppDbContext` içinde `ICurrentUser` tabanlı global query filter
    (SuperAdmin tüm veriyi görür, diğer roller yalnızca kendi tenant'ını görür)
  - Swagger (Bearer şemalı) — geliştirme ortamında `/swagger`
  - Açılışta otomatik seed: 3 plan (Başlangıç / Profesyonel / Ofis) + süper admin kullanıcı
- **web/** — Next.js 14 (App Router) + TypeScript + Tailwind CSS
  - Landing, fiyatlandırma, kayıt/giriş, korumalı panel (mükellef CRUD), süper admin paneli
  - Tüm ekranlarda boş / yükleniyor / hata durumları ve Türkçe hata mesajları (§0.5 ANA KURAL)
- **docker-compose.yml** — PostgreSQL 16

## Gereksinimler

- .NET 8 SDK
- Node.js 20+
- Docker (PostgreSQL için)

## Çalıştırma

1. **Veritabanı** (proje kökünde):

   ```bash
   docker compose up -d
   ```

2. **Backend**:

   ```bash
   cd backend/MaliMusavir.Api
   dotnet run
   ```

   API varsayılan olarak `http://localhost:5000` üzerinde çalışır.
   Açılışta veritabanı şeması oluşturulur, planlar ve süper admin kullanıcı seed edilir.
   Swagger: `http://localhost:5000/swagger`

3. **Web**:

   ```bash
   cd web
   cp .env.local.example .env.local
   npm install
   npm run dev
   ```

   Uygulama: `http://localhost:3000`

## OCR Sağlayıcı Seçimi (Ocr:Provider)

Fiş OCR sağlayıcısı `backend/MaliMusavir.Api/appsettings.json` → `Ocr:Provider` ile
(üretimde `.env` → `OCR_PROVIDER`) seçilir; değer büyük/küçük harf duyarsızdır:

| Sağlayıcı | Açıklama | Gerekli anahtar |
| --- | --- | --- |
| `Mock` (varsayılan) | Anahtar gerektirmez; dosya adından deterministik demo fişler üretir | — |
| `LlmVision` | Anthropic Messages API (claude-sonnet-4-5) — görüntü **ve PDF** okur | `Ocr:Anthropic:ApiKey` (`ANTHROPIC_API_KEY`) |
| `OpenAi` | OpenAI Chat Completions (gpt-4o-mini) — yalnız görüntü okur, PDF için LlmVision/Gemini kullanın | `Ocr:OpenAi:ApiKey` (`OPENAI_API_KEY`) |
| `Gemini` | Google Gemini generateContent (gemini-2.5-flash, zayıf sonuçta gemini-2.5-pro fallback) — görüntü **ve PDF** okur | `Ocr:Gemini:ApiKey` (`GEMINI_API_KEY`) |

`LlmVision`, `OpenAi` ve `Gemini` aynı Türkçe fiş uzmanı prompt/şemasını (`Ocr/OcrPrompt.cs`) ve
doğrulama katmanını (`Ocr/OcrValidator.cs`) paylaşır; anahtar boşsa uygulama yine açılır,
OCR çağrısı açıklayıcı Türkçe hata döner.

## Fiş Görseli Küçültme (ImageProcessing)

Fiş fotoğrafları üç katmanda (mobil `image_picker` → web canvas → sunucu ImageSharp)
depolama için minimum boyuta indirilir: uzun kenar **1600 px**, **JPEG kalite 75**,
EXIF/konum metadata'sı silinir (KVKK). PDF'e dokunulmaz. Sunucuda sıra **"önce OCR,
sonra sıkıştırma"**dır: OCR her zaman ORİJİNAL görüntüyü okur (temp/ altındaki
dokunulmamış kopya), sıkıştırılmış kopya okuma bittikten sonra diske yazılır —
`ImageProcessing:Enabled` yalnızca bu depolama sıkıştırmasını kontrol eder
(önerilen: `true`). Ayrıntı: `docs/flow-specs/faz12-gorsel-kucultme.md`.
Sunucu tarafı yapılandırma (`backend/MaliMusavir.Api/appsettings.json`):

```json
"ImageProcessing": {
  "Enabled": true,
  "MaxLongEdge": 1600,
  "JpegQuality": 75
}
```

> **Lisans notu:** görsel işleme için kullanılan **SixLabors.ImageSharp**, Six Labors
> Split License ile dağıtılır — yıllık geliri 1M USD altındaki şirketler için ücretsizdir;
> üstündeki kullanımlar ticari lisans gerektirir.

## Varsayılan Süper Admin

`backend/MaliMusavir.Api/appsettings.json` → `SuperAdmin` bölümünden okunur:

| Alan | Değer |
| --- | --- |
| E-posta | `admin@malimusavir.local` |
| Şifre | `Admin!12345` |

> Üretim ortamına çıkmadan önce bu bilgileri ve `Jwt:Key` değerini mutlaka değiştirin.

Süper admin ile giriş yaptıktan sonra `http://localhost:3000/admin` adresinden
tenant listesi, istatistikler ve abonelik durumu yönetimine erişilir.

## Klasör Yapısı

```
malimusavir-saas/
├── docker-compose.yml        # PostgreSQL 16
├── .gitignore
├── README.md
├── backend/
│   └── MaliMusavir.Api/
│       ├── Program.cs        # DI, JWT, CORS, Swagger, hata middleware, seed
│       ├── appsettings.json
│       ├── Domain/           # Entity'ler (Tenant, AppUser, Plan, Client)
│       ├── Infrastructure/   # AppDbContext (tenant filter), DbSeeder, CurrentUser
│       ├── Services/         # TokenService (JWT)
│       ├── Contracts/        # DTO'lar + validasyon attribute'ları
│       └── Controllers/      # Auth, Clients, Plans, Admin
└── web/
    ├── app/
    │   ├── page.tsx              # Landing
    │   ├── fiyatlandirma/        # Plan kartları (API + statik fallback)
    │   ├── giris/  kayit/        # Kimlik formları
    │   ├── panel/                # Korumalı alan (dashboard, mükellefler, ayarlar)
    │   └── admin/                # Süper admin paneli
    ├── lib/api.ts                # fetch wrapper (token, 401 yönlendirme, TR hata mesajları)
    └── package.json
```

## API Uçları (özet)

| Uç | Açıklama |
| --- | --- |
| `POST /api/auth/register` | Yeni tenant + sahip kullanıcı (trial abonelik) |
| `POST /api/auth/login` | Giriş, JWT döner |
| `GET /api/auth/me` | Oturum bilgisi |
| `GET /api/plans` | Aktif planlar (herkese açık) |
| `GET/POST /api/clients` | Mükellef listesi (q, include=archived) / ekleme (kota kontrolü) |
| `GET/PUT /api/clients/{id}` | Tek mükellef / güncelleme |
| `POST /api/clients/{id}/archive` `/unarchive` | Arşivleme işlemleri |
| `GET /api/admin/tenants` | Tüm firmalar (SuperAdmin) |
| `GET /api/admin/stats` | Platform istatistikleri (SuperAdmin) |
| `GET /api/admin/billing/overview` | Gelir/tahsilat özeti (SuperAdmin) |
| `POST /api/admin/tenants/{id}/status` | Abonelik durumu değiştir (SuperAdmin) |
| `GET /api/billing/summary` | Mevcut plan/durum/dönem + deneme kalan gün |
| `POST /api/billing/subscribe` | Paket satın al/değiştir (mock kart: 4111 başarı / 4000 hata) |
| `POST /api/billing/cancel` | İptal (dönem sonuna kadar aktif) |
| `GET /api/billing/invoices` `/transactions` | Abonelik faturaları / ödeme işlemleri |
| `POST /api/declarations/prepare` | KDV1 taslağı hazırla (mükerrer çağrı yeniden hesaplar) |
| `GET /api/declarations` `/{id}` | Beyanname panosu / detay (KDV kırılımlı özet) |
| `POST /api/declarations/{id}/send` `/refresh-status` | GİB'e gönder / tahakkuk sorgula |

## Sonraki Fazlar

1. **Faz 6 — Flutter mobil uygulama:** sahadan fiş çekme + hızlı onay
2. **Gerçek sağlayıcı bağlantıları:** Nilvera canlı anahtar, Google DocAI/AWS Textract OCR, PTT UETS
   web servisi, iyzico canlı anahtarları, GİB e-Beyanname kimlik + token — hepsi mevcut adapter
   arayüzlerine tek sınıf olarak eklenir
3. Zamanlanmış senkron (gece job'ları), bildirimler (e-posta/SMS/push), KKEG/tevkifat otomatiği (B2/B3),
   abonelik fatura PDF'i (e-Arşiv), diğer beyanname türleri (KDV2/muhtasar/damga)
4. Ekip yönetimi (TenantStaff davetleri), şifre sıfırlama, EF Core migration'a geçiş

> **Not:** Fiş dosyaları `backend/MaliMusavir.Api/storage/` altında (webroot dışı) saklanır ve
> `.gitignore`'dadır. Şema `EnsureCreated` ile oluşturulduğundan yeni tablolar (Subscriptions,
> PaymentTransactions, BillingInvoices, Declarations vb.) için geliştirme DB'sini sıfırlayın:
> `docker compose down -v && docker compose up -d`.
