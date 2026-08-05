# Üretim Kurulum Runbook'u (SNET VDS — Ubuntu 22.04/24.04)

Sıfır sunucudan çalışır sisteme. Tahmini süre: 30-45 dk. Tüm komutlar sunucuda çalıştırılır.

## 0) İlk bağlantı ve güvenlik sıkılaştırma

```bash
ssh root@SUNUCU_IP

# Sistem güncelle
apt update && apt upgrade -y

# Yeni kullanıcı (root ile çalışma)
adduser deploy && usermod -aG sudo deploy

# SSH anahtarını KENDİ bilgisayarından kopyala (yeni terminal):
#   ssh-copy-id deploy@SUNUCU_IP

# SSH sıkılaştırma
sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin no/' /etc/ssh/sshd_config
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
systemctl restart ssh

# Güvenlik duvarı + fail2ban + otomatik güvenlik güncellemeleri
apt install -y ufw fail2ban unattended-upgrades gnupg
ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw --force enable
systemctl enable --now fail2ban
dpkg-reconfigure -plow unattended-upgrades
```

> Bu adımdan sonra `deploy` kullanıcısıyla devam et: `ssh deploy@SUNUCU_IP`

## 1) Docker kurulumu

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
# çıkıp tekrar gir (grup etkinleşsin)
```

## 2) Projeyi sunucuya kopyala

Kendi bilgisayarından:
```bash
scp -r malimusavir-saas deploy@SUNUCU_IP:/opt/malimusavir
```
(veya git deposuna koyduysan sunucuda `git clone`)

## 3) Ortam değişkenleri

```bash
cd /opt/malimusavir/deploy
cp .env.example .env
nano .env       # TÜM DEGISTIR alanlarını doldur
# Rastgele değer üretmek için: openssl rand -base64 32
```

**DOMAIN:** Alan adının A kaydını sunucu IP'sine yönlendir (Cloudflare kullanıyorsan önce "DNS only" yap, sertifika alındıktan sonra proxy'yi açabilirsin).

## 4) Başlat

```bash
cd /opt/malimusavir
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d --build
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env logs -f api   # ilk açılışı izle
```

Caddy sertifikayı otomatik alır → `https://ALANADIN` açılır. Süper admin: `.env`'deki e-posta/şifre.

## 5) Yedekleme kurulumu

```bash
# rclone (offsite yedek için — farklı sağlayıcıda S3-uyumlu depo öner)
sudo apt install -y rclone
rclone config          # remote oluştur, adını .env'deki RCLONE_REMOTE'a yaz

chmod +x /opt/malimusavir/deploy/backup.sh
# Gece 03:15 otomatik yedek:
(crontab -l 2>/dev/null; echo "15 3 * * * /opt/malimusavir/deploy/backup.sh >> /var/log/mm-backup.log 2>&1") | crontab -

# İLK YEDEĞİ ELLE AL ve GERİ YÜKLEMEYİ TEST ET (test edilmemiş yedek, yedek değildir):
/opt/malimusavir/deploy/backup.sh
```

### Geri yükleme
```bash
cd /var/backups/malimusavir
gpg -d mm-STAMP.tar.gpg > mm.tar && tar xf mm.tar
docker compose -f /opt/malimusavir/deploy/docker-compose.prod.yml --env-file /opt/malimusavir/deploy/.env \
  exec -T db pg_restore -U DB_KULLANICI -d malimusavir --clean < db-STAMP.dump
```

## 6) Güncelleme (yeni sürüm dağıtımı)

```bash
cd /opt/malimusavir
# yeni kodu kopyala/çek, sonra:
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d --build
```

## Kontrol listesi

- [ ] root SSH kapalı, sadece anahtarla giriş
- [ ] ufw aktif (22/80/443 dışında kapalı), fail2ban çalışıyor
- [ ] `.env`'de hiçbir DEGISTIR kalmadı
- [ ] HTTPS çalışıyor (Caddy sertifika aldı)
- [ ] Gece yedeği cron'da + offsite remote tanımlı
- [ ] Geri yükleme EN AZ BİR KEZ test edildi
- [ ] Süper admin şifresi güçlü ve sadece sende

## Şema değişiklikleri (mevcut DB'ye elle uygulanacak SQL)

> `EnsureCreated` mevcut veritabanına kolon/index EKLEMEZ. Aşağıdaki SQL'ler, ilgili sürüm
> dağıtılırken **bir kez** çalıştırılmalıdır (yeni/sıfır kurulumda gerek yoktur — şema zaten
> böyle oluşur). Çalıştırma:
> `docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env exec -T db psql -U DB_KULLANICI -d malimusavir`

### Sürüm: Mizan + geçmiş fiş listesi içe aktarma (unvan-bazlı satıcı eşleşmesi)

```sql
-- 1) Yeni kolonlar: normalize satıcı unvanı (VKN'siz eşleşme anahtarı)
ALTER TABLE "VendorDefaults" ADD COLUMN IF NOT EXISTS "VendorNameNormalized" character varying(300) NOT NULL DEFAULT '';
ALTER TABLE "VendorCariMaps" ADD COLUMN IF NOT EXISTS "VendorNameNormalized" character varying(300) NOT NULL DEFAULT '';

-- 2) Eski unique index'ler kaldırılır (VKN'siz kayıtlar artık unvanla ayrışır)
DROP INDEX IF EXISTS "IX_VendorDefaults_TenantId_ClientId_SenderTaxId";
DROP INDEX IF EXISTS "IX_VendorCariMaps_TenantId_ClientId_SenderTaxId";

-- 3) Yeni unique index'ler: VKN + normalize unvan birleşimi
CREATE UNIQUE INDEX IF NOT EXISTS "IX_VendorDefaults_Tenant_Client_TaxId_NameKey"
    ON "VendorDefaults" ("TenantId", "ClientId", "SenderTaxId", "VendorNameNormalized");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_VendorCariMaps_Tenant_Client_TaxId_NameKey"
    ON "VendorCariMaps" ("TenantId", "ClientId", "SenderTaxId", "VendorNameNormalized");

-- 4) Unvan-bazlı ikincil eşleşme sorguları için yardımcı index'ler
CREATE INDEX IF NOT EXISTS "IX_VendorDefaults_Client_NameKey"
    ON "VendorDefaults" ("ClientId", "VendorNameNormalized");
CREATE INDEX IF NOT EXISTS "IX_VendorCariMaps_Client_NameKey"
    ON "VendorCariMaps" ("ClientId", "VendorNameNormalized");
```

Not: Eski kayıtlarda `VendorNameNormalized` boş kalır — motor bu kayıtları VKN üzerinden
eşleştirmeye devam eder; onay/öğrenme akışı boş anahtarları zamanla geri doldurur.

### Sürüm: Banka hesap hareketleri (ekstre içe aktarma + kodlama)

> Dört YENİ tablo. `EnsureCreated` mevcut veritabanına tablo EKLEMEZ; bu betik bir kez
> çalıştırılmalıdır. Betik `IF NOT EXISTS` kullandığından tekrar çalıştırmak zararsızdır.

```sql
CREATE TABLE IF NOT EXISTS "BankAccounts" (
    "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "ClientId" uuid NOT NULL,
    "BankName" character varying(150) NOT NULL, "Iban" character varying(34) NOT NULL,
    "AccountNumber" character varying(50), "CurrencyCode" character varying(3) NOT NULL,
    "AccountCode" character varying(30), "LastBalance" numeric(18,2),
    "LastBalanceAt" timestamp with time zone, "IsArchived" boolean NOT NULL DEFAULT false,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BankAccounts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BankAccounts_Clients_ClientId" FOREIGN KEY ("ClientId")
        REFERENCES "Clients" ("Id") ON DELETE CASCADE);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankAccounts_ClientId_Iban" ON "BankAccounts" ("ClientId", "Iban");

CREATE TABLE IF NOT EXISTS "BankTransactions" (
    "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "ClientId" uuid NOT NULL,
    "BankAccountId" uuid NOT NULL, "TransactionDate" timestamp with time zone NOT NULL,
    "Direction" integer NOT NULL, "Amount" numeric(18,2) NOT NULL,
    "CurrencyCode" character varying(3) NOT NULL, "BalanceAfter" numeric(18,2),
    "CounterpartyName" character varying(300), "CounterpartyIban" character varying(34),
    "CounterpartyTaxId" character varying(11), "Description" character varying(1000) NOT NULL,
    "ReferenceNo" character varying(100), "TransactionType" character varying(50),
    "SuggestedAccountCode" character varying(30), "AccountCode" character varying(30),
    "CariCode" character varying(30), "Confidence" double precision,
    "CodingStatus" integer NOT NULL DEFAULT 0, "Source" integer NOT NULL DEFAULT 0,
    "ImportId" uuid, "DedupKey" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BankTransactions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BankTransactions_BankAccounts_BankAccountId" FOREIGN KEY ("BankAccountId")
        REFERENCES "BankAccounts" ("Id") ON DELETE CASCADE);
-- MÜKERRER KORUMASI: aynı ekstre iki kez yüklense de hareket çiftlenmez.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankTransactions_BankAccountId_DedupKey"
    ON "BankTransactions" ("BankAccountId", "DedupKey");
CREATE INDEX IF NOT EXISTS "IX_BankTransactions_ClientId_TransactionDate"
    ON "BankTransactions" ("ClientId", "TransactionDate");
CREATE INDEX IF NOT EXISTS "IX_BankTransactions_ClientId_CodingStatus"
    ON "BankTransactions" ("ClientId", "CodingStatus");

CREATE TABLE IF NOT EXISTS "BankStatementImports" (
    "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "ClientId" uuid NOT NULL,
    "BankAccountId" uuid, "FileName" character varying(300) NOT NULL,
    "Format" character varying(20) NOT NULL, "ParsedCount" integer NOT NULL DEFAULT 0,
    "ImportedCount" integer NOT NULL DEFAULT 0, "SkippedDuplicates" integer NOT NULL DEFAULT 0,
    "PeriodFrom" timestamp with time zone, "PeriodTo" timestamp with time zone,
    "Warning" character varying(1000), "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BankStatementImports" PRIMARY KEY ("Id"));
CREATE INDEX IF NOT EXISTS "IX_BankStatementImports_ClientId_CreatedAt"
    ON "BankStatementImports" ("ClientId", "CreatedAt");

CREATE TABLE IF NOT EXISTS "BankCounterpartyMaps" (
    "Id" uuid NOT NULL, "TenantId" uuid NOT NULL, "ClientId" uuid NOT NULL,
    "CounterpartyIban" character varying(34), "CounterpartyTaxId" character varying(11),
    "CounterpartyNameNormalized" character varying(300) NOT NULL DEFAULT '',
    "CariCode" character varying(30), "AccountCode" character varying(30),
    "DisplayName" character varying(300), "UseCount" integer NOT NULL DEFAULT 1,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BankCounterpartyMaps" PRIMARY KEY ("Id"));
CREATE INDEX IF NOT EXISTS "IX_BankCounterpartyMaps_ClientId_Iban"
    ON "BankCounterpartyMaps" ("ClientId", "CounterpartyIban");
CREATE INDEX IF NOT EXISTS "IX_BankCounterpartyMaps_ClientId_TaxId"
    ON "BankCounterpartyMaps" ("ClientId", "CounterpartyTaxId");
CREATE INDEX IF NOT EXISTS "IX_BankCounterpartyMaps_ClientId_NameKey"
    ON "BankCounterpartyMaps" ("ClientId", "CounterpartyNameNormalized");
```

## Banka hesap hareketleri — kaynak stratejisi

**Neden API değil, ekstre içe aktarma?** Türkiye'de bankaların serbestçe kullanılabilir açık
API'si yoktur; kurumsal API'ler **banka başına ayrı sözleşme** gerektirir. Ekstre dosyası ise
her bankanın internet şubesinden indirilebilir. Bu yüzden birincil yol dosyadır:

| Biçim | Uzantı | Not |
| --- | --- | --- |
| MT940 (SWIFT) | `.sta` `.mt940` `.txt` | Kurumsal internet bankacılığının standart çıktısı — en zengin veri |
| CSV | `.csv` | Ayraç (`;` `,` tab) otomatik sezilir |
| Excel | `.xlsx` | Başlık satırından sütun sezgisi (Türkçe başlıklar tanınır) |

Banka kurumsal API'si sözleşmeyle geldiğinde, aynı `ParsedBankLine` sözleşmesini üreten bir
adaptör yazılır — mükerrer eleme, kodlama ve UI **değişmez**.

**Mükerrer koruması:** Banka referansı (dekont no) varsa ondan, yoksa
tarih+yön+tutar+açıklama+bakiye SHA-256 özetinden `DedupKey` üretilir ve
`(BankAccountId, DedupKey)` benzersizdir. Aynı ekstre iki kez yüklenirse hareketler
**çiftlenmez** — muhasebede çift kayıt riski yoktur.

**Kodlama/öğrenme:** SMMM bir hareketi onayladığında karşı taraf → hesap/cari eşlemesi
`BankCounterpartyMaps`'e yazılır; sonraki ekstrelerde aynı karşı taraf otomatik kodlanır
(eşleşme sırası **IBAN → VKN/TCKN → normalize unvan**, güven skoru 0.95/0.85/0.70).
Otomatik kodlanan hareket **Approved yapılmaz** — her zaman SMMM onayına düşer.

## GİB Portal Robotu (`gib-robot` servisi)

**Ne yapar?** Entegratör kullanmayan mükelleflerin **gelen belgelerini** ("Adıma Düzenlenen
Belgeler") ve **e-Tebligatlarını**, mükellefin GİB Dijital Vergi Dairesi kimliğiyle otomatik
çeker. Mali müşavir kimliği panelden **bir kez** girer (Mükellefler → Entegrasyon →
"GİB Portalı (entegratör yok)"); sonrasında:

- her gece **03:00**'te (sunucu saati) son **7 gün** için tüm GibPortal mükelleflerine
  `GibPortalSync` işi kuyruğa alınır (`DailyPortalSyncWorker`),
- Faturalar sayfasındaki "Senkronize Et" ve e-Tebligat sayfasındaki "Tebligatları Çek"
  düğmeleri aynı robotu elle tetikler.

**Mimari:** `gib-robot` ayrı bir konteynerdir (Node 20 + Playwright/chromium).
**Dışarıya açılmaz** — compose'da `ports` tanımı yoktur; yalnız `api` ile aynı iç ağda
`http://gib-robot:8080` adresinden erişilir ve her istek `X-Robot-Token` başlığıyla
(`ROBOT_TOKEN`) doğrulanır. `.env`'e üretim değerini şöyle üretip yazın:

```bash
openssl rand -hex 32     # çıktıyı deploy/.env → ROBOT_TOKEN satırına yaz
```

### Güvenlik kuralı: captcha / SMS-OTP çözülmez — insan destekli doğrulama kodu akışı

GİB Dijital Vergi Dairesi giriş ekranı **her girişte** "Doğrulama Kodu" (captcha görseli)
istiyor (canlı teşhis 04.08.2026). Sistem bu kodu **ÇÖZMEZ** — bu kural esnetilmez.
Bunun yerine **insan destekli** iki aşamalı giriş kullanılır:

1. Kullanıcı panelde **"GİB Oturumu Aç"** (Mükellefler sayfası; hata bantlarında
   "Doğrulama Kodunu Gir") düğmesine basar. Robot giriş ekranını açar, kayıtlı kimliği
   doldurur ve captcha görselini panele getirir.
2. Mali müşavir görseldeki kodu okuyup girer (en fazla 3 deneme; hatalı kodda yeni
   görsel gösterilir). Başarılı girişte oturum sunucuda **12 saat** saklanır
   (`gib-robot` env: `GIB_SESSION_TTL_HOURS`, varsayılan 12).

Oturum geçerliyken tüm çekimler (elle senkron + gece 03:00 otomatik senkron) doğrulama
kodu istemeden çalışır. Oturum geçersizse gece senkronu ilgili mükellefi
**"GİB oturumu yenilenmeli"** (Elle doğrulama gerekli) olarak işaretler ve **tekrar
denemez**; sabah kullanıcı mükellef satırındaki düğmeyle kodu tek tıkla girer, uyarı
otomatik temizlenir ve çekimler kaldığı yerden sürer. SMS-OTP / e-Devlet gibi diğer
insan gerektiren adımlar da aynı şekilde asla otomatik çözülmez.

### KVKK / veri koruma notu

- Mükellef GİB kimliği yalnızca **AES-256 ile şifreli** olarak saklanır
  (`ClientIntegration.ApiKeyEncrypted`; anahtar `.env` → `AES_KEY`). Düz metin hiçbir yerde
  saklanmaz, **hiçbir log satırına yazılmaz** ve API yanıtlarında yalnızca maskeli kullanıcı
  kodu döner.
- Şifre robot'a yalnızca **iç Docker ağı** üzerinden, istek gövdesinde iletilir; robot
  şifreyi diske yazmaz (doğrulama kodu beklenirken yalnız bellekte tutulur ve oturum
  kapanınca düşer). Diske yazılan tek şey oturum çerezleridir (`gib-sessions` volume,
  `/data/sessions/{sha256}.json`, 0600 izin, `GIB_SESSION_TTL_HOURS` — varsayılan 12 saat
  — sonra geçersiz) — bu sayede her çekimde yeniden doğrulama kodu gerekmez.
- Robot **yalnızca o mükellefin kendi verisini**, mali müşavirin mevcut yetkisi dahilinde
  çeker; başka mükellefin verisine erişmez, veri üçüncü taraflara gönderilmez.
- Mükellef ilişkisi sona erdiğinde entegrasyonu "Bağlantıyı Sil" ile kaldırın; kimlik
  kaydı silinir. Oturum önbelleğini de temizlemek için:
  `docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env exec gib-robot sh -c 'rm -f /data/sessions/*.json'`

### Seçici (selector) bozulduğunda teşhis

GİB portalı bir Next.js SPA'dır ve ekran yapısı haber verilmeden değişebilir. Tüm seçiciler
**tek dosyada** toplanmıştır: `gib-robot/selectors.js` (her öğe için birden çok aday:
etiket → rol → placeholder → metin → CSS).

Bir öğe bulunamazsa robot Türkçe *"GİB ekranı beklenen öğeyi içermiyor … Ekran değişmiş
olabilir"* hatası döner; hata `diagnostic` alanında **sayfa başlığı, URL, görünür metnin ilk
500 karakteri ve ekran görüntüsünün yolunu** taşır. Teşhis adımları:

```bash
# 1) Robot günlükleri (teşhis satırları ve ekran görüntüsü dosya adı burada)
docker logs --tail 200 gib-robot
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env logs -f gib-robot

# 2) Ekran görüntüsünü sunucuya çıkar (dosya adı günlükte yazar: /tmp/gib-robot-<adım>-<zaman>.png)
docker cp gib-robot:/tmp/gib-robot-login-failed-2026-08-04T03-00-00-000Z.png ./

# 3) Sağlık kontrolü (token gerektirmez, sadece iç ağdan)
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env exec api \
  sh -c 'wget -qO- http://gib-robot:8080/health'

# 4) Düzeltme: doğru seçiciyi gib-robot/selectors.js içindeki ilgili anahtarın
#    candidates dizisinin BAŞINA ekle, sonra sadece robotu yeniden derle:
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d --build gib-robot
```

Sütun başlıkları tanınamazsa çekim durmaz; sonuç mesajına *"Uyarı: … sütunlar tanınamadı"*
notu eklenir → `selectors.js` içindeki `documentColumns` / `tebligatColumns` desenlerini
güncelleyin.

> **Not:** Robot ilk kez dağıtılırken imaj indirmesi büyüktür (Playwright chromium imajı
> ~1.5 GB). İlk `up -d --build` uzun sürebilir; sonraki dağıtımlarda katman önbelleklenir.

## Notlar

- DB portu dışarı açık değil (sadece Docker iç ağı) — böyle kalmalı.
- `gib-robot` de dışarı açık değil; yalnız `api` erişir (`ROBOT_TOKEN` paylaşılan sır).
- API anahtarları geldiğinde `.env`'e yaz → `up -d` ile yeniden başlat (OCR_PROVIDER=LlmVision, PAYMENTS_PROVIDER=Iyzico).
- Disk takibi: `df -h` + `docker system df`. %80'i geçince görselleri S3-uyumlu depoya taşıma fazını konuşalım.
