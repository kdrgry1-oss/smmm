# Mali Müşavir Mobil (Faz 6 — Flutter MVP)

SMMM'nin **sahada / mükellef yanında** kullandığı mobil uygulama. Tek kod tabanıyla
**iOS + Android + Huawei** hedeflenir (blueprint §4). Ana özellik: **fiş çekme** —
kamerayla fotoğraf çek veya galeriden çoklu seç → mükellef seç → kuyruklu yükleme →
OCR özeti (satıcı, tutar, KDV, önerilen hesap kodu, güven rozeti) → onay/red.

## Özellikler

- **Giriş:** e-posta + şifre → JWT; `flutter_secure_storage` (Keychain/Keystore) içinde
  saklanır. 401 alınınca oturum düşürülür ve login ekranına dönülür (web'deki
  `lib/api.ts` davranışıyla aynı: Bearer, 401 → login, hata gövdesi `{ message }`).
- **Ana ekran:** özet kartlar — mükellef sayısı, bekleyen fiş, okunmamış tebligat,
  süresi yaklaşan tebligat (kırmızı).
- **Mükellef listesi:** sunucu tarafı arama (unvan/VKN), çekmece (bottom sheet) detay.
- **Fiş çekme (ana özellik):** kamera / galeri çoklu seçim, yükleme kuyruğu +
  ilerleme çubuğu, hata durumunda **Yeniden Dene**, sonuçta OCR özeti kartı.
- **Fiş listesi:** durum filtreleri (Onay Bekliyor / Onaylandı / Reddedildi / ...),
  sayfalama, onay formu (düzeltme alanları + "yine de gider göster") ve gerekçeli red.
- **e-Tebligat:** tek liste, kalan süre renkli sayaç (kırmızı ≤ 3 gün / sarı ≤ 10 gün /
  yeşil), aciller üstte (backend sıralar), okundu işaretleme, manuel UETS senkronu.
- **Ayarlar:** API adresi (varsayılan `http://10.0.2.2:5000` — Android emülatörü) + çıkış.
- Tüm ekranlarda **boş / yükleniyor / hata** durumları ve Türkçe metinler (§0.5 ANA KURAL).
- Tema: web ile aynı **koyu lacivert (navy)** paleti, Material 3, `tr_TR` locale.

## Kurulum (platform klasörleri repo'da YOK — kendin üretiyorsun)

`android/`, `ios/` (ve istersen diğer platform) klasörleri bilinçli olarak repoya
konmadı; Flutter sürümüne uygun güncel şablonu sen üretirsin:

```bash
cd mobile
flutter create . --org com.malimusavir --project-name malimusavir_mobile
flutter pub get
flutter run          # cihaz/emülatör seçili olmalı
```

### 1) Android izin ve ağ ayarları

`android/app/src/main/AndroidManifest.xml`:

- `<manifest>` altına (release build'lerde internet için gereklidir):

  ```xml
  <uses-permission android:name="android.permission.INTERNET"/>
  ```

- Yerel geliştirmede API `http://` olduğundan `<application ...>` etiketine:

  ```xml
  android:usesCleartextTraffic="true"
  ```

  (Canlıda API https olacağı için bu satır üretim sürümünden kaldırılmalı ya da
  `network_security_config` ile yalnız geliştirme adresine daraltılmalıdır.)

- `image_picker` Android'de sistem kamera/galeri uygulamasını kullandığı için ek
  çalışma zamanı izni gerektirmez. Manifest'e `android.permission.CAMERA` **eklemeyin**;
  eklenirse izni uygulamanın kendisinin istemesi gerekir.

- `flutter_secure_storage` için `android/app/build.gradle` içinde `minSdkVersion 21`
  (veya üzeri) önerilir.

### 2) iOS izinleri

`ios/Runner/Info.plist` içine:

```xml
<key>NSCameraUsageDescription</key>
<string>Fiş fotoğrafı çekmek için kamera erişimi gereklidir.</string>
<key>NSPhotoLibraryUsageDescription</key>
<string>Galeriden fiş görseli seçmek için fotoğraf erişimi gereklidir.</string>
```

Yerel geliştirmede `http://` API'ye erişim için (yalnız geliştirme sürümünde):

```xml
<key>NSAppTransportSecurity</key>
<dict>
  <key>NSAllowsArbitraryLoads</key>
  <true/>
</dict>
```

### 3) API adresi

| Ortam | Adres |
|---|---|
| Android emülatörü | `http://10.0.2.2:5000` (varsayılan — host'un localhost'u) |
| iOS simülatörü | `http://localhost:5000` |
| Gerçek cihaz (aynı Wi-Fi) | `http://<bilgisayarın-yerel-IP'si>:5000` |

Uygulama içinden **Ayarlar → API adresi** ile değiştirilir; güvenli depoda saklanır.
Backend'i başlatmak için repo kökünde: `docker compose up -d` + `dotnet run`
(bkz. ana `README.md`).

## Huawei notu (HMS)

- Bu fazdaki tüm özellikler (kamera/galeri, güvenli depolama, HTTP) **Google
  servisleri gerektirmez**; uygulama Huawei cihazlarda (AppGallery dağıtımı) aynen çalışır.
- **Push bildirimi BU FAZDA YOK.** Sonraki fazda bildirim eklenirken çift adaptör
  kurulacak: Google'lı cihazlarda `firebase_messaging` (FCM), Huawei cihazlarda
  `huawei_push` (HMS Push Kit), iOS'ta APNs. Tek kod tabanında çalışma zamanında
  GMS/HMS tespiti ile seçilecek.

## Proje yapısı

```
mobile/
├── pubspec.yaml               # http, image_picker, flutter_secure_storage, intl, provider
├── analysis_options.yaml
└── lib/
    ├── main.dart              # tr_TR init, MultiProvider, tema, auth yönlendirmesi
    ├── config.dart            # AppConfig: API adresi (güvenli depo, 10.0.2.2 varsayılanı)
    ├── theme.dart             # Navy paleti (web tailwind ile aynı) + Material 3 koyu tema
    ├── formatters.dart        # ₺/tarih tr_TR biçimleri, Türkçe ondalık ayrıştırma
    ├── api/api_client.dart    # Bearer, 401 yakalama, { message } çıkarımı, multipart+ilerleme
    ├── models/                # Backend DTO alan adlarıyla birebir fromJson
    │   ├── auth_response.dart │ client.dart │ receipt.dart │ tebligat.dart
    ├── state/                 # provider (ChangeNotifier)
    │   ├── auth_provider.dart │ dashboard_provider.dart │ clients_provider.dart
    │   ├── receipts_provider.dart │ tebligat_provider.dart │ upload_queue_provider.dart
    ├── screens/
    │   ├── login_screen.dart │ home_screen.dart │ clients_screen.dart
    │   ├── receipt_upload_screen.dart │ receipts_screen.dart
    │   ├── receipt_approve_screen.dart │ tebligat_screen.dart │ settings_screen.dart
    └── widgets/
        ├── status_badge.dart │ confidence_badge.dart │ deadline_counter.dart
        ├── empty_state.dart │ error_banner.dart
```

## Ekran ↔ Backend endpoint eşleşmesi

| Ekran | Endpoint | Yöntem |
|---|---|---|
| Giriş | `/api/auth/login` | POST |
| Ana ekran (özet) | `/api/clients` · `/api/receipts?status=NeedsReview&pageSize=1` · `/api/etebligat` | GET |
| Mükellefler | `/api/clients?q=...` | GET |
| Fiş çekme (kuyruk) | `/api/clients/{clientId}/receipts` (multipart `files`) | POST |
| Fiş listesi | `/api/receipts?status=&page=&pageSize=` | GET |
| Fiş onayı | `/api/receipts/{id}/approve` | POST |
| Fiş reddi | `/api/receipts/{id}/reject` | POST |
| e-Tebligat listesi | `/api/etebligat?onlyUnread=&onlyUrgent=` | GET |
| e-Tebligat okundu | `/api/etebligat/{id}/mark-read` | POST |
| e-Tebligat senkron | `/api/etebligat/sync` | POST |

## Kalan işler (sonraki fazlar)

- **Push bildirimi:** FCM (`firebase_messaging`) + HMS Push Kit (`huawei_push`) ayrımı,
  yeni tebligat / OCR sonucu bildirimleri, backend'e cihaz token kaydı ucu.
- **Offline kuyruk:** çekimde internet yoksa fişleri cihazda saklayıp bağlantı gelince
  otomatik yükleme (şimdilik kuyruk yalnız uygulama açıkken ve çevrimiçiyken çalışır).
- **Fiş görseli önizleme:** `/api/receipts/{id}/file` ucundan yetkili görsel indirme
  ve detay ekranında gösterme.
- **Flutter integration test** (§0.5 kalite kapısı) + `flutter analyze` CI adımı.
- Huawei AppGallery / Play Store / App Store dağıtım yapılandırmaları (imzalama,
  sürüm kanalları), kayıt (register) akışının mobile eklenmesi, biometrik kilit.
