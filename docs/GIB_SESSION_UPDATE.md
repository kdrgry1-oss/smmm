# GİB oturum düzeltmesi

Bu güncelleme GİB'in oturum süresini değiştirmez. Geçerli oturumun gereksiz kaybını ve yanlış bağlantı için giriş istenmesini azaltır. CAPTCHA/OTP kullanıcı tarafından tamamlanır.

## Kodda bulunan nedenler

- `gib-robot/gib.js` içindeki `runFlow`, belge çekimi sonunda yenilenen çerez ve tokenları kaydetmiyordu. Sonraki işlem eski durumla açılıyordu.
- `session.js` yazım hatasını gizliyordu; kullanıcıya başarılı giriş gösterilirken oturum diskte bulunmayabiliyordu.
- Giriş formunun görünmemesi yeterli başarı işareti sayılıyordu. Artık giriş göstergesi ve token birlikte aranır; belirsiz ekran yeniden giriş gerektiriyor diye sınıflandırılmaz.
- `sessionStorage` her gezinmede eski değerlerle yeniden doldurulabiliyordu. Geri yükleme doğru origin ile ve sekme başına bir kez yapılır.
- Bağlantı kimlikleri yeterince ayrılmıyordu. Şirket anahtarında büro/şirket/hesap; müşavir anahtarında büro/hesap kullanılır. Bekleyen giriş yalnız aynı bağlantıyla tamamlanır.
- Toplu taramada başarısız mükellefler sonuçta kaybolabiliyordu; arayüz yanlış müşavir girişini açabiliyordu. Sonuç artık şirket bazında hatayı gösterir ve ilgili bağlantıya yönlendirir.

Bu nedenler koddaki kusurlardır; canlı GİB hesabında tekrarlayan girişin hangi sıklıkla bunlardan kaynaklandığı üretim gözlemi olmadan ölçülmedi.

## Dağıtım ve geçiş

- API, web ve robot birlikte güncellenmelidir. Yeni API bekleyen girişi bağlantı anahtarıyla tamamlar; eski API ile yeni robot karıştırılmamalıdır.
- Şirket bağlantıları artık büro + şirket + kullanıcı hesabı özetiyle anahtarlanır. Eski kullanıcı-adı oturumları güvenli biçimde büroya bağlanamadığından otomatik aktarılmaz. Şirket başına bir kez yeniden giriş gerekir.
- Müşavir bağlantıları da büro + kullanıcı hesabı özetiyle anahtarlanır; kullanıcı kodu değişince önceki hesaba ait oturum kullanılmaz. Bu geçişte müşavir hesabı için de bir kez yeniden giriş gerekir.
- `/data/sessions` kalıcı volume olmalıdır. Kayıtlar AES-256-GCM ile şifrelenir, geçici dosya ve atomik yeniden adlandırma ile yazılır. Yazım başarısızsa işlem başarılı gösterilmez.
- Tercihen `GIB_SESSION_ENCRYPTION_KEY` için sır yöneticisinden 32 baytlık base64 anahtar verin. Anahtar yoksa volume içinde 0600 izinli `.session-key` üretilir. Bu varsayılan, aynı volume ve anahtar birlikte ele geçirilirse koruma sağlamaz. Anahtarı ve kayıtları birlikte güvenli yedekleyin; anahtar değişikliği yeniden giriş gerektirir.
- Tek robot süreci kullanın: eşzamanlılık kilidi ve bekleyen CAPTCHA oturumları bellek içindedir. Birden çok replica için dağıtık kilit ve giriş yönlendirmesi gerekir.
- GİB sunucu süresi dolarsa kullanıcı yeniden doğrular. Yerel TTL veya heartbeat bu süreyi garanti etmez.

## Kontroller

`npm test` (gib-robot), `dotnet test tests/MaliMusavir.Tests.csproj`, web içinde `npx tsc --noEmit --incremental false`.

Canlı kabul: tek şirketle giriş → iki ardışık çekim → robot yeniden başlatma → tekrar çekim. Ardından gerçek 401, ağ kesintisi ve yazılamayan volume ayrı denenmeli. Şirketler arası izolasyon ve müşavir hesabının seçilen VKN için gerçek yetkisi kontrollü ortamda doğrulanmalı. Canlı hesap bilgileri ve tokenlar günlüğe alınmamalı.

## Kapsam sınırları

- Bu değişiklik müşavirin her şirkete erişim yetkisi olduğunu varsaymaz veya yeni yetki vermez. Portalda şirket seçimi akışının gerçek VKN ile sunucu yanıtında doğrulanması canlı inceleme gerektirir; bu sürüm bunu doğrulanmış olarak sunmaz.
- KVKK hukuki dayanak, müşteri yetkilendirmesi/geri alma süreci ve saklama politikası yazılımla tek başına tamamlanmaz. Yerel masaüstü bağlantı uygulaması bu sürümde yoktur.
- Beş iş günü hesabı beş takvim gününe düzeltildi. Konudan çıkarılan 15/30 günlük mevcut süre modeli hukuken doğrulanmış bir süre motoru değildir. Eski kayıtlar otomatik değiştirilmez; bunlar ayrıca gözden geçirilmelidir.

## Kalıcı yetkilendirme için hedef mimari

Kalıcı olan uygulamadaki müşteri yetkisi olmalı; portal oturumu yenilenebilir teknik bir kayıt olmalıdır. Büro/müşteri/hesap kimliği yanında yetkilendiren kişi, işlem kapsamı, başlangıç-bitiş, geri alma ve son başarılı eşitleme tutulmalı. Yetki geri alınınca kuyruktaki işler durmalı, oturum kaldırılmalı. Bunlar bu yamada tamamlanmış bir yetki modülü değildir.

Uygun seçenek, müşavirin bilgisayarında çalışan bağlantı uygulamasının normal tarayıcı oturumunu yönetmesi ve yalnız yetkili müşterinin belgelerini merkezi panele taşımasıdır. Sunucu robotu kullanılacaksa ayrı sır yönetimi, dar personel yetkileri, hassas veri içermeyen işlem kayıtları, saklama/silme politikası ve müşteri bazlı erişim kontrolü gerekir. Resmî API bulunmaması tarayıcı otomasyonunu teknik olarak imkânsız yapmaz; fakat oturumun saklanması tek başına hukuki yetki oluşturmaz. CAPTCHA/OTP ve sunucunun oturum sonlandırması aşılmaz.

Kaynaklar: [GİB VUK 107/A](https://gib.gov.tr/mevzuat/kanun/434/madde/7635), [KVKK veri güvenliği yükümlülükleri](https://www.kvkk.gov.tr/Icerik/2040/Veri-Guvenligine-Iliskin-Yukumlulukler).
