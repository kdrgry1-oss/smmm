"use strict";

/**
 * ═══════════════════════════════════════════════════════════════════════════
 *  GİB Dijital Vergi Dairesi — SEÇİCİ HARİTASI (TEK DOSYA)
 * ═══════════════════════════════════════════════════════════════════════════
 *
 * DİKKAT — BU DOSYADAKİ SEÇİCİLERİN HİÇBİRİ CANLIDA DOĞRULANMAMIŞTIR.
 * GİB portalı (https://dijital.gib.gov.tr/portal) bir Next.js SPA'dır; ekran
 * yapısı GİB tarafından haber verilmeden değiştirilebilir. Bu yüzden:
 *
 *  1) Her mantıksal öğe için BİRDEN ÇOK aday tanımlıdır (etiket → rol →
 *     placeholder → metin → CSS sırasıyla denenir).
 *  2) Hiçbir aday tutmazsa `resolve()` Türkçe "ekran değişmiş olabilir"
 *     hatası fırlatır; hata nesnesi o anki sayfa başlığı, URL'i ve görünür
 *     metnin ilk 500 karakterini taşır (server.js bunu `diagnostic` alanına
 *     koyar, ekran görüntüsünü /tmp'e yazar).
 *  3) Canlıda doğru seçici tespit edilince ilgili anahtarın `candidates`
 *     dizisinin BAŞINA eklenir — kod dosyalarına dokunmaya gerek yoktur.
 *
 * Aday tipleri:
 *   { label: "..." }              → page.getByLabel(..., { exact:false })
 *   { role: "button", name: "..."} → page.getByRole(role, { name })
 *   { placeholder: "..." }        → page.getByPlaceholder(...)
 *   { text: "..." }               → page.getByText(..., { exact:false })
 *   { css: "..." }                → page.locator(css)
 * Tümünde `name`/`label`/`text` değerleri RegExp de olabilir.
 */

/** Portal adresleri. */
const URLS = {
  /** Giriş ekranı (SPA). */
  login: "https://dijital.gib.gov.tr/portal",
  /** Giriş sonrası ana ekran (yönlendirme buraya benzer bir adrese olur). */
  home: "https://dijital.gib.gov.tr/portal",
};

/**
 * EKRAN ADRESLERİ — menü avlamak yerine DOĞRUDAN gidilecek adresler (canlı
 * teşhis 04.08.2026: menü öğesi bulunamadı; portal SPA'sında hizmetler arama
 * kutusunun arkasında olabiliyor). Sırayla denenir; ilk açılan kullanılır.
 * "/portal/e-tebligat" adresi canlıda 200 döndü (kimliksiz istekte giriş
 * ekranına yönlendirir) — bu yüzden listenin başında.
 */
const SCREEN_URLS = {
  tebligatMenu: [
    "https://dijital.gib.gov.tr/portal/e-tebligat",
    "https://dijital.gib.gov.tr/portal/eTebligat",
    "https://dijital.gib.gov.tr/portal/e-tebligatlarim",
    "https://dijital.gib.gov.tr/portal/eTebligatlarim",
    "https://dijital.gib.gov.tr/portal/tebligat",
  ],
  documentsMenu: [
    "https://dijital.gib.gov.tr/portal/e-belge",
    "https://dijital.gib.gov.tr/portal/e-belge-bilgilerim",
    "https://dijital.gib.gov.tr/portal/eBelge",
    "https://dijital.gib.gov.tr/portal/eBelgeBilgilerim",
    "https://dijital.gib.gov.tr/portal/adima-duzenlenen-belgeler",
    "https://dijital.gib.gov.tr/portal/adimaDuzenlenenBelgeler",
    "https://dijital.gib.gov.tr/portal/adima-duzenlenen-e-belgeler",
    "https://dijital.gib.gov.tr/portal/belgelerim",
  ],
  /**
   * Faz 18 — MÜŞAVİR MÜKELLEF LİSTESİ: SMMM'nin kendi oturumuyla "müşavir / mükellef
   * işlemleri" ekranı. Aday adresler CANLIDA DOĞRULANMAMIŞTIR; ilk açılan kullanılır,
   * hiçbiri açılmazsa hizmet arama kutusuna düşülür (gib.js → openAdvisorClientsScreen).
   */
  advisorClients: [
    "https://dijital.gib.gov.tr/portal/musavir-mukellef-islemleri",
    "https://dijital.gib.gov.tr/portal/mukellef-islemleri",
    "https://dijital.gib.gov.tr/portal/musavirlik-islemleri",
    "https://dijital.gib.gov.tr/portal/mukelleflerim",
    "https://dijital.gib.gov.tr/portal/smmm-islemleri",
  ],
};

/**
 * GERÇEK API UÇLARI (Faz 19) — DOM kazımak yerine portalın kendi apigateway
 * servisleri çağrılır (gib.js → fetchTebligatViaApi / fetchDocumentsViaApi).
 *
 * tebligatListele: CANLIDA DOĞRULANDI (05.08.2026, gerçek oturum):
 *   POST, gövde {"meta":{"pagination":{"pageNo":1,"pageSize":50},"sortFieldName":"id",
 *   "sortType":"ASC","filters":[{"fieldName":"arsivDurum","values":["0"]}]}}
 *   ("0"=normal liste, "1"=arşivlenenler). Başlıklar: Content-Type: application/json +
 *   Authorization: Bearer <sessionStorage "token">. Çerez tek başına YETMİYOR.
 *   Token yoksa 401 {"code":"tdvd.auth.token.empty"}; gövde yanlışsa 409.
 *
 * belgeListele: uç adresi HENÜZ BİLİNMİYOR (null) → gib.js doğrudan mevcut
 *   DOM/JSON-yakalama akışına düşer. Canlıda tespit edilince (boş sonuç
 *   diagnostic'indeki /apigateway/ adres listesinden) buraya yazılır.
 *
 * Bilinen diğer uçlar (bilgi, henüz kullanılmıyor):
 *   GET /apigateway/etebligat/etebligat/tebligat-sayilari
 *   GET /apigateway/etebligat/etebligat/get-belge-tur?arsivDurum=0
 */
const API_ENDPOINTS = {
  tebligatListele:
    "https://dijital.gib.gov.tr/apigateway/etebligat/etebligat/tebligat-listele",
  belgeListele: null,
};

/**
 * Menü/ekran adları. GİB menü metinleri değişebildiği için regexp toleranslıdır
 * (ör. "Adıma Düzenlenen Belgeler" ↔ "Adıma Düzenlenen e-Belgeler").
 */
const SCREEN_NAMES = {
  documents: /e-?\s*Belge(\s*Bilgilerim)?|Adıma\s+Düzenlenen(\s+e-?)?\s*Belgeler/i,
  // "e-Tebligat", "e-Tebligatlarım", "E Tebligat" — hepsini kapsar.
  tebligat: /e-?\s*Tebligat(lar[ıi]m)?/i,
  // Faz 18: "Mükellef İşlemleri", "Müşavir İşlemleri", "Mükelleflerim" vb.
  advisorClients: /(Müşavir|Mükellef)\s*İşlemleri|Mükelleflerim/i,
};

const SELECTORS = {
  // ---------------- Giriş ekranı ----------------

  loginUsername: {
    description: "Giriş ekranı — Kullanıcı Kodu / TCKN alanı",
    candidates: [
      { label: /Kullanıcı\s*Kodu/i },
      { label: /T\.?C\.?\s*Kimlik/i },
      { placeholder: /Kullanıcı\s*Kodu|TCKN|T\.?C\.?\s*Kimlik/i },
      { role: "textbox", name: /Kullanıcı\s*Kodu|TCKN/i },
      { css: "input#userid" },
      { css: "input[name='userid']" },
      { css: "input[name='username']" },
      { css: "form input[type='text']:not([type='hidden'])" },
    ],
  },

  loginPassword: {
    description: "Giriş ekranı — Şifre alanı",
    candidates: [
      { label: /Şifre|Parola/i },
      { placeholder: /Şifre|Parola/i },
      { css: "input#password" },
      { css: "input[name='password']" },
      { css: "input[type='password']" },
    ],
  },

  loginSubmit: {
    description: "Giriş ekranı — Giriş düğmesi",
    candidates: [
      { role: "button", name: /^\s*(Giriş(\s*Yap)?|Oturum\s*Aç)\s*$/i },
      { css: "button[type='submit']" },
      { css: "input[type='submit']" },
      { text: /^\s*Giriş\s*Yap\s*$/i },
    ],
  },

  /**
   * İNSAN DESTEKLİ DOĞRULAMA (canlı teşhis 04.08.2026): GİB giriş ekranı HER girişte
   * "Doğrulama Kodu" (captcha görseli) istiyor. Robot bu kodu ÇÖZMEZ (güvenlik kuralı);
   * görselin ekran görüntüsünü alır, kullanıcı (mali müşavir) kodu okuyup girer
   * (/login-begin + /login-complete akışı).
   */
  captchaImage: {
    description: "Giriş ekranı — Doğrulama Kodu (captcha) görseli",
    candidates: [
      { css: "img[src*='captcha' i]" },
      { css: "img[alt*='doğrulama' i]" },
      { css: "img[alt*='dogrulama' i]" },
      { css: "img[id*='captcha' i]" },
      { css: "canvas[id*='captcha' i]" },
      { css: "canvas" },
      // Giriş formundaki tek görsel de captcha olabilir (logo form dışındadır).
      { css: "form img" },
    ],
  },

  captchaInput: {
    description: "Giriş ekranı — Doğrulama Kodu alanı",
    candidates: [
      { label: /Doğrulama\s*Kodu/i },
      { placeholder: /Doğrulama\s*Kodu/i },
      { role: "textbox", name: /Doğrulama\s*Kodu/i },
      { css: "input[name*='captcha' i]" },
      { css: "input[id*='captcha' i]" },
      { css: "input[name*='dogrulama' i]" },
      // Son çare: formdaki son metin alanı (1. kullanıcı kodu, şifre type=password'dır).
      { css: "form input[type='text']:last-of-type" },
    ],
  },

  /**
   * Giriş BAŞARILI göstergeleri (biri görünürse oturum açılmış sayılır).
   * Oturum önbelleğinin (storageState) hâlâ geçerli olup olmadığı da bununla
   * denetlenir.
   */
  loggedInMarker: {
    description: "Giriş sonrası ana ekran göstergesi",
    candidates: [
      { role: "button", name: /Çıkış|Oturumu\s*Kapat/i },
      { role: "link", name: /Çıkış|Oturumu\s*Kapat/i },
      { text: /Çıkış\s*Yap/i },
      { text: /Mükellef\s*Bilgileri|Vergi\s*Kimlik\s*No/i },
      { css: "[data-testid='user-menu']" },
    ],
  },

  /** Hatalı kimlik uyarısı (giriş ekranında kalındıysa neden budur). */
  loginError: {
    description: "Giriş ekranı — hata mesajı kutusu",
    candidates: [
      { text: /Kullanıcı\s*kodu\s*veya\s*şifre/i },
      { text: /hatalı|geçersiz|yanlış/i },
      { css: ".error, .alert-danger, [role='alert']" },
    ],
  },

  // ---------------- Menü / gezinme ----------------

  documentsMenu: {
    description: "Menü — \"e-Belge Bilgilerim\" veya \"Adıma Düzenlenen Belgeler\" bağlantısı",
    candidates: [
      { role: "link", name: SCREEN_NAMES.documents },
      { role: "button", name: SCREEN_NAMES.documents },
      { role: "menuitem", name: SCREEN_NAMES.documents },
      { text: SCREEN_NAMES.documents },
      { css: "a[href*='e-belge']" },
      { css: "a[href*='eBelge']" },
      { css: "a[href*='adima-duzenlenen']" },
      { css: "a[href*='belge']" },
    ],
  },

  tebligatMenu: {
    description: "Menü — \"e-Tebligat\" bağlantısı",
    candidates: [
      { role: "link", name: SCREEN_NAMES.tebligat },
      { role: "button", name: SCREEN_NAMES.tebligat },
      { role: "menuitem", name: SCREEN_NAMES.tebligat },
      { text: SCREEN_NAMES.tebligat },
      { css: "a[href*='tebligat']" },
    ],
  },

  /**
   * Portalın hizmet arama kutusu ("Arama yapmak istediğiniz hizmeti yazınız").
   * Menü avı yerine ikinci yol olarak kullanılır (gib.js → openScreen).
   */
  serviceSearch: {
    description: "Portal hizmet arama kutusu",
    optional: true,
    candidates: [
      { placeholder: /Arama\s*yapmak\s*istediğiniz\s*hizmeti/i },
      { placeholder: /hizmet\s*ara|ara[mn]a/i },
      { role: "searchbox" },
      { role: "textbox", name: /ara/i },
      { css: "input[type='search']" },
      { css: "input[placeholder*='hizmet' i]" },
    ],
  },

  /** Menü kapalıysa (hamburger) önce bu denenir — bulunamazsa sessizce atlanır. */
  menuToggle: {
    description: "Menüyü açan düğme (varsa)",
    optional: true,
    candidates: [
      { role: "button", name: /Menü|Menu/i },
      { css: "button[aria-label*='menu' i]" },
      { css: "button.hamburger, .navbar-toggler" },
    ],
  },

  // ---------------- Tarih aralığı filtresi (iki ekranda ortak) ----------------

  dateFrom: {
    description: "Tarih aralığı — Başlangıç tarihi alanı",
    candidates: [
      { label: /Başlangıç\s*Tarihi/i },
      { label: /^\s*Başlangıç\s*$/i },
      { placeholder: /Başlangıç|gg[./]aa[./]yyyy/i },
      { css: "input[name*='baslangic' i]" },
      { css: "input[name*='startDate' i]" },
      { css: "input[type='date']:nth-of-type(1)" },
    ],
  },

  dateTo: {
    description: "Tarih aralığı — Bitiş tarihi alanı",
    candidates: [
      { label: /Bitiş\s*Tarihi/i },
      { label: /^\s*Bitiş\s*$/i },
      { placeholder: /Bitiş/i },
      { css: "input[name*='bitis' i]" },
      { css: "input[name*='endDate' i]" },
      { css: "input[type='date']:nth-of-type(2)" },
    ],
  },

  querySubmit: {
    description: "Tarih aralığı — Sorgula / Ara düğmesi",
    candidates: [
      { role: "button", name: /^\s*(Sorgula|Ara|Listele|Filtrele|Getir)\s*$/i },
      { css: "button[type='submit']" },
      { text: /^\s*Sorgula\s*$/i },
    ],
  },

  // ---------------- Sonuç tablosu ----------------

  resultTable: {
    description: "Sonuç tablosu",
    candidates: [
      { role: "table" },
      { css: "table" },
      { css: "[role='grid']" },
      { css: ".p-datatable, .ant-table, .MuiTable-root" },
    ],
  },

  resultRow: {
    description: "Sonuç tablosu satırı",
    candidates: [
      { css: "table tbody tr" },
      { css: "[role='row']:not(:first-child)" },
      { css: ".p-datatable-tbody > tr, .ant-table-tbody > tr" },
    ],
  },

  /** "Kayıt bulunamadı" boş durumu — hata DEĞİLDİR, boş liste döndürülür. */
  emptyResult: {
    description: "Sonuç yok bilgisi",
    optional: true,
    candidates: [
      { text: /Kayıt\s*bulunamadı|Sonuç\s*bulunamadı|Görüntülenecek\s*kayıt\s*yok/i },
      { css: ".p-datatable-emptymessage, .ant-empty" },
    ],
  },

  // ---------------- Belge tablosu sütun başlıkları ----------------
  //
  // Sütun sırası GİB tarafında değişebildiği için satır alanları sabit indeksle
  // DEĞİL, başlık metnine göre eşlenir (gib.js → mapRowByHeaders). Aşağıdaki
  // desenler başlık metniyle eşleştirilir; hiçbiri tutmazsa alan boş kalır
  // (kayıt yine de döner, teşhis mesajında eksik sütun bildirilir).

  documentColumns: {
    uuid: /ETTN|UUID|Belge\s*Numarası\s*\(ETTN\)/i,
    documentNumber: /Belge\s*(No|Numarası)|Fatura\s*(No|Numarası)/i,
    issueDate: /Düzenlenme\s*Tarihi|Belge\s*Tarihi|Fatura\s*Tarihi|^Tarih$/i,
    senderTaxId: /VKN|TCKN|Vergi\s*Kimlik/i,
    senderTitle: /Ünvan|Unvan|Gönderen|Satıcı|Düzenleyen/i,
    taxExclusiveAmount: /Matrah|Vergi\s*Hariç|KDV\s*Hariç/i,
    taxTotal: /KDV|Vergi\s*Tutarı/i,
    payableAmount: /Ödenecek|Genel\s*Toplam|Toplam\s*Tutar|^Tutar$/i,
    currency: /Para\s*Birimi|Döviz/i,
    documentType: /Belge\s*Türü|Belge\s*Tipi|Senaryo/i,
  },

  tebligatColumns: {
    externalId: /Tebligat\s*(No|Numarası)|Barkod|Evrak\s*No|^No$/i,
    subject: /Konu|Açıklama|Tebligat\s*Konusu/i,
    sentAt: /Tebliğ\s*Tarihi|Gönderim\s*Tarihi|^Tarih$/i,
    readAt: /Okunma\s*Tarihi|Görüntülenme\s*Tarihi/i,
    taxOffice: /Vergi\s*Dairesi|Gönderen\s*(Kurum|Birim)/i,
    documentUrlOrText: /Belge|Ek|İndir|Görüntüle/i,
  },

  // ---------------- Faz 18: Müşavir → mükellef seçimi ----------------
  //
  // SMMM tek oturumla girer; "mükellef işlemleri" özelliğiyle bağlı mükellefin
  // ekranlarına geçer. Aday desenler CANLIDA DOĞRULANMAMIŞTIR — canlı teşhiste
  // doğrulanan seçici, ilgili candidates dizisinin BAŞINA eklenir.

  clientSwitcher: {
    description: "Müşavir paneli — mükellef seçme kutusu / düğmesi",
    candidates: [
      { role: "button", name: /Mükellef|Müşteri|İşlem\s*Yapılacak/i },
      { role: "combobox", name: /Mükellef|Müşteri|İşlem\s*Yapılacak/i },
      { css: "select[name*='mukellef' i]" },
      { css: "select[id*='mukellef' i]" },
      { text: /Mükellef\s*Seç/i },
      { text: /İşlem\s*Yapılacak\s*Mükellef/i },
      { css: "[class*='mukellef' i][role='button']" },
    ],
  },

  clientSearchInput: {
    description: "Müşavir paneli — mükellef arama kutusu (VKN/TCKN/unvan)",
    optional: true,
    candidates: [
      { placeholder: /VKN|TCKN|Vergi\s*Kimlik|Mükellef\s*Ara|Unvan/i },
      { role: "searchbox" },
      { role: "textbox", name: /Mükellef|VKN|TCKN/i },
      { css: "input[name*='mukellef' i]" },
      { css: "input[placeholder*='mükellef' i]" },
      { css: "input[type='search']" },
    ],
  },

  clientResultRow: {
    description: "Müşavir paneli — mükellef arama sonucu satırı (VKN metniyle eşleşen)",
    candidates: [
      { css: "table tbody tr" },
      { css: "[role='option']" },
      { css: "[role='row']" },
      { css: "li" },
      { css: ".p-datatable-tbody > tr, .ant-table-tbody > tr" },
    ],
  },

  /** Seçim onay düğmesi (varsa) — bulunamazsa sessizce atlanır. */
  clientSelectConfirm: {
    description: "Müşavir paneli — mükellef seçim onayı",
    optional: true,
    candidates: [
      { role: "button", name: /^\s*(Seç|Onayla|Devam(\s*Et)?|Tamam)\s*$/i },
      { text: /^\s*Seç\s*$/i },
    ],
  },

  // ---------------- İnsan gerektiren adım tespiti (GÜVENLİK KURALI) ----------------
  //
  // Bunlardan biri görülürse robot ÇÖZMEYE ÇALIŞMAZ; işi "Elle doğrulama
  // gerekli" durumuna alır ve kullanıcıya bildirir. Bu davranış esnetilmez.

  manualVerification: {
    description: "Captcha / SMS-OTP / ek doğrulama göstergeleri",
    candidates: [
      { css: "img[src*='captcha' i]" },
      { css: "input[name*='captcha' i]" },
      { css: "input[id*='captcha' i]" },
      { css: "canvas[id*='captcha' i]" },
      { css: "iframe[src*='recaptcha' i]" },
      { css: "iframe[src*='hcaptcha' i]" },
      { css: ".g-recaptcha, .h-captcha" },
      { css: "input[name*='otp' i]" },
      { css: "input[name*='sms' i]" },
      { text: /doğrulama\s*kodu/i },
      { text: /güvenlik\s*kodu/i },
      { text: /tek\s*kullanımlık\s*şifre/i },
      { text: /cep\s*telefon(unuz|unuza)\s*gönderilen/i },
      { text: /SMS\s*ile\s*gönderilen/i },
      { text: /e-?Devlet\s*(ile\s*)?giriş/i },
      { text: /mobil\s*imza|e-?imza/i },
    ],
  },
};

/** Bir adayı Playwright locator'a çevirir. */
function toLocator(page, candidate) {
  if (candidate.label !== undefined) return page.getByLabel(candidate.label, { exact: false });
  if (candidate.placeholder !== undefined) return page.getByPlaceholder(candidate.placeholder);
  if (candidate.role !== undefined) {
    return candidate.name === undefined
      ? page.getByRole(candidate.role)
      : page.getByRole(candidate.role, { name: candidate.name });
  }
  if (candidate.text !== undefined) return page.getByText(candidate.text, { exact: false });
  if (candidate.css !== undefined) return page.locator(candidate.css);
  throw new Error("Geçersiz seçici adayı: " + JSON.stringify(candidate));
}

/** Adayın kısa, loglanabilir gösterimi (teşhis mesajlarında kullanılır). */
function describeCandidate(candidate) {
  const key = Object.keys(candidate)[0];
  const value = candidate[key];
  return `${key}=${value instanceof RegExp ? value.toString() : String(value)}`;
}

/**
 * Ekranın değiştiğini bildiren hata. server.js bu hatayı yakalayıp
 * `diagnostic` alanını doldurur.
 */
class ScreenChangedError extends Error {
  constructor(message, details) {
    super(message);
    this.name = "ScreenChangedError";
    this.screenChanged = true;
    this.details = details || {};
  }
}

/** İnsan doğrulaması gerektiğini bildiren hata (ÇÖZÜLMEYE ÇALIŞILMAZ). */
class ManualVerificationRequiredError extends Error {
  constructor(message, details) {
    super(message);
    this.name = "ManualVerificationRequiredError";
    this.requiresManual = true;
    this.details = details || {};
  }
}

/**
 * Verilen anahtar için ilk GÖRÜNÜR adayı bulur.
 * @returns {Promise<import('playwright').Locator|null>} optional=true ve hiçbiri
 *          bulunamazsa null; aksi halde ScreenChangedError fırlatır.
 */
async function resolve(page, key, options = {}) {
  const spec = SELECTORS[key];
  if (!spec) throw new Error(`Tanımsız seçici anahtarı: ${key}`);

  const timeout = options.timeout ?? 4000;
  const tried = [];

  for (const candidate of spec.candidates) {
    tried.push(describeCandidate(candidate));
    try {
      const locator = toLocator(page, candidate).first();
      await locator.waitFor({ state: "visible", timeout });
      return locator;
    } catch (err) {
      // Bu aday tutmadı; sıradakini dene.
    }
  }

  if (spec.optional || options.optional) return null;

  throw new ScreenChangedError(
    `GİB ekranı beklenen öğeyi içermiyor: ${spec.description}. Ekran değişmiş olabilir.`,
    { selectorKey: key, description: spec.description, tried }
  );
}

/**
 * Anahtarın herhangi bir adayı sayfada GÖRÜNÜYOR mu? (bekleme kısa tutulur —
 * "yok" cevabı da hızlı alınmalıdır).
 */
async function isPresent(page, key, timeout = 1200) {
  const spec = SELECTORS[key];
  if (!spec) throw new Error(`Tanımsız seçici anahtarı: ${key}`);

  for (const candidate of spec.candidates) {
    try {
      const locator = toLocator(page, candidate).first();
      await locator.waitFor({ state: "visible", timeout });
      return true;
    } catch (err) {
      // sıradaki aday
    }
  }
  return false;
}

module.exports = {
  URLS,
  SCREEN_URLS,
  API_ENDPOINTS,
  SCREEN_NAMES,
  SELECTORS,
  resolve,
  isPresent,
  toLocator,
  describeCandidate,
  ScreenChangedError,
  ManualVerificationRequiredError,
};
