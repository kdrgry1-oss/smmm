"use strict";

/**
 * GİB Dijital Vergi Dairesi portal akışları (Playwright / chromium).
 *
 * TASARIM İLKELERİ
 *  - Aldatıcı davranış YOK: `navigator.webdriver` maskelenmez, gizlenme
 *    (stealth) eklentisi kullanılmaz. Yalnızca normal bir tarayıcı gibi
 *    davranılır (Türkçe locale, İstanbul saat dilimi, gerçekçi user-agent).
 *  - Nazik davranış: adımlar arasında 1-2 sn bekleme, mükellef başına tek
 *    eşzamanlı oturum (session.withClientLock), toplam 90 sn zaman aşımı.
 *  - GÜVENLİK KURALI: captcha / SMS-OTP / e-Devlet gibi İNSAN gerektiren bir
 *    adım tespit edilirse robot ÇÖZMEYE ÇALIŞMAZ; ManualVerificationRequiredError
 *    fırlatır ve iş "Elle doğrulama gerekli" durumuna alınır. Bu kural esnetilmez.
 *  - İNSAN DESTEKLİ GİRİŞ (canlı teşhis 04.08.2026): GİB giriş ekranı HER girişte
 *    "Doğrulama Kodu" (captcha) istiyor. Bu yüzden robot şifreyle KENDİ BAŞINA
 *    giriş yapmaz; çekimler yalnız KAYITLI OTURUMLA (storageState) yapılır.
 *    Oturum yoksa/geçersizse {ok:false, requiresManual:true, needsCaptchaLogin:true}
 *    döner ve kullanıcı /login-begin + /login-complete akışıyla kodu KENDİSİ girer:
 *      loginBegin  → tarayıcı açılır, kimlik doldurulur, captcha görseli (base64 PNG)
 *                    döner; sayfa bellekte BEKLETİLİR (TTL 5 dk, en fazla 20 oturum).
 *      loginComplete → kullanıcının okuduğu kod yazılır, giriş yapılır, başarılıysa
 *                    storageState kaydedilir (12 saat, bkz. session.js). En fazla 3
 *                    deneme; hatalı kodda YENİ görselle aynı pendingId sürdürülür.
 *  - Şifre HİÇBİR log satırına, hata mesajına veya diske yazılmaz (bekleyen giriş
 *    kaydında yalnız BELLEKTE tutulur, oturum kapanınca düşer).
 *  - Seçicilerin tamamı selectors.js'tedir; burada sabit CSS/metin YOKTUR.
 */

const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { chromium } = require("playwright");
const S = require("./selectors");
const session = require("./session");
const captchaSolver = require("./captcha-solver");
const flowBudget = require("./flow-budget");

/** Tek isteğin toplam bütçesi. */
const REQUEST_TIMEOUT_MS = Number(process.env.REQUEST_TIMEOUT_MS || 150000);
/** Tek Playwright eyleminin varsayılan zaman aşımı. */
const ACTION_TIMEOUT_MS = Number(process.env.ACTION_TIMEOUT_MS || 20000);
/** Elle giriş akışında kullanıcıyı bekletmemek için kısa, kesin üst sınırlar. */
const LOGIN_NAVIGATION_TIMEOUT_MS = Number(process.env.LOGIN_NAVIGATION_TIMEOUT_MS || 10000);
const LOGIN_FIELD_TIMEOUT_MS = Number(process.env.LOGIN_FIELD_TIMEOUT_MS || 6000);
const LOGIN_CAPTCHA_TIMEOUT_MS = Number(process.env.LOGIN_CAPTCHA_TIMEOUT_MS || 10000);
const LOGIN_SUBMIT_TIMEOUT_MS = Number(process.env.LOGIN_SUBMIT_TIMEOUT_MS || 6000);
const SESSION_CHECK_TIMEOUT_MS = Number(process.env.SESSION_CHECK_TIMEOUT_MS || 8000);
const FAST_SCREEN_NAVIGATION_TIMEOUT_MS = Number(process.env.FAST_SCREEN_NAVIGATION_TIMEOUT_MS || 8000);
const API_REQUEST_TIMEOUT_MS = Number(process.env.API_REQUEST_TIMEOUT_MS || 10000);
/** Ekran görüntülerinin yazılacağı dizin (teşhis için). */
const SHOT_DIR = process.env.SHOT_DIR || "/tmp";

/** Gerçekçi masaüstü Chrome user-agent'ı (playwright imajındaki sürümle uyumlu). */
const USER_AGENT =
  process.env.ROBOT_USER_AGENT ||
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Nazik bekleme: istekler/adımlar arasında 1-2 sn. */
const polite = () => sleep(1000 + Math.floor(Math.random() * 1000));

// ---------------------------------------------------------------- teşhis

/**
 * O anki ekranın kısa teşhis özetini üretir: başlık, URL, görünür metnin ilk
 * 500 karakteri ve /tmp'e yazılan ekran görüntüsünün yolu.
 */
async function captureDiagnostic(page, label) {
  const info = { label };
  try {
    info.url = page.url();
  } catch (err) {
    info.url = "(alınamadı)";
  }
  try {
    info.title = await page.title();
  } catch (err) {
    info.title = "(alınamadı)";
  }
  try {
    const text = await page.evaluate(() => document.body ? document.body.innerText : "");
    info.text = String(text || "").replace(/\s+/g, " ").trim().slice(0, 500);
  } catch (err) {
    info.text = "(sayfa metni okunamadı)";
  }
  try {
    const file = path.join(
      SHOT_DIR,
      `gib-robot-${label}-${new Date().toISOString().replace(/[:.]/g, "-")}.png`
    );
    await page.screenshot({ path: file, fullPage: false });
    info.screenshot = file;
  } catch (err) {
    info.screenshot = null;
  }
  return info;
}

/** Teşhis nesnesini tek satırlık okunabilir metne çevirir (API yanıtındaki `diagnostic`). */
function formatDiagnostic(info) {
  if (!info) return null;
  const parts = [];
  if (info.title) parts.push(`Sayfa başlığı: "${info.title}"`);
  if (info.url) parts.push(`URL: ${info.url}`);
  if (info.text) parts.push(`Ekran metni (ilk 500): ${info.text}`);
  if (info.screenshot) parts.push(`Ekran görüntüsü: ${info.screenshot}`);
  if (info.tried && info.tried.length) parts.push(`Denenen seçiciler: ${info.tried.join(" | ")}`);
  return parts.join(" — ");
}

// ---------------------------------------------------------------- tarayıcı

/**
 * Tarayıcı + bağlam açar. storageState verilirse önceki oturum yüklenir.
 * Görsel/font/medya istekleri engellenir (hız + GİB'e daha az yük).
 * options.allowImages=true → görseller İNDİRİLİR (captcha görseli için şart).
 */
async function openContext(storageState, options = {}) {
  const browser = await chromium.launch({
    headless: true,
    args: ["--no-sandbox", "--disable-dev-shm-usage"],
  });

  flowBudget.track(browser);

  // sessionStorage storageState'in parçası DEĞİLDİR; kendi alanımızda taşıyoruz.
  const savedSessionStorage = storageState && storageState.__sessionStorage;
  const cleanState = storageState ? { ...storageState } : undefined;
  if (cleanState) delete cleanState.__sessionStorage;

  const context = await browser.newContext({
    locale: "tr-TR",
    timezoneId: "Europe/Istanbul",
    userAgent: USER_AGENT,
    viewport: { width: 1440, height: 900 },
    extraHTTPHeaders: { "Accept-Language": "tr-TR,tr;q=0.9" },
    storageState: cleanState || undefined,
  });

  // Kaydedilmiş sessionStorage her sayfa yüklemesinde geri yazılır (GİB portalı
  // oturum token'ını burada tutuyor — çerez tek başına yetmiyor).
  if (savedSessionStorage && Object.keys(savedSessionStorage).length > 0) {
    await context.addInitScript((entries) => {
      try {
        for (const [key, value] of Object.entries(entries)) {
          window.sessionStorage.setItem(key, value);
        }
      } catch (err) {
        // sessionStorage erişilemezse sessizce geçilir
      }
    }, savedSessionStorage);
  }

  context.setDefaultTimeout(ACTION_TIMEOUT_MS);
  context.setDefaultNavigationTimeout(ACTION_TIMEOUT_MS + 15000);

  // Ağ tasarrufu: resim/font/medya indirilmez (belge verisi HTML/XHR'dedir).
  // Captcha görselinin gerektiği giriş akışında (allowImages) görseller indirilir.
  const blockedTypes = options.allowImages ? ["font", "media"] : ["image", "font", "media"];
  await context.route("**/*", (route) => {
    const type = route.request().resourceType();
    if (blockedTypes.includes(type)) return route.abort();
    return route.continue();
  });

  return { browser, context };
}

/**
 * Sayfanın sessionStorage içeriğini düz nesne olarak okur (oturum token'ı burada).
 * Okunamazsa boş nesne döner — akış bozulmaz.
 */
async function captureSessionStorage(page) {
  try {
    return await page.evaluate(() => {
      const out = {};
      for (let i = 0; i < window.sessionStorage.length; i++) {
        const key = window.sessionStorage.key(i);
        out[key] = window.sessionStorage.getItem(key);
      }
      return out;
    });
  } catch (err) {
    return {};
  }
}

async function closeQuietly(browser) {
  try {
    if (browser) await browser.close();
  } catch (err) {
    // kapanış hatası akışı etkilemez
  }
}

// ---------------------------------------------------------------- JSON yanıt yakalama (Faz 18)
//
// ASIL İYİLEŞTİRME: DOM tablosu kazımak yerine, sayfa gezinirken portalın kendi
// XHR/fetch çağrılarının JSON yanıtları biriktirilir ve liste verisi BU yanıtlardan
// okunur. Alan adları canlıda doğrulanmadığı için eşleyici ESNEKTİR: anahtar adları
// normalize edilip (küçük harf + Türkçe karakter sadeleştirme + alfasayısal dışı
// karakterlerin atılması) desen listeleriyle eşlenir. Uygun dizi bulunamazsa mevcut
// DOM tablosu okuma yöntemine düşülür (readTable KORUNUR — fallback).

/** Tek JSON yanıtı için üst boyut sınırı (5 MB). */
const JSON_MAX_BYTES = 5 * 1024 * 1024;
/** Bellekte tutulan en fazla yanıt sayısı (en yeniler kalır). */
const JSON_MAX_RESPONSES = 60;
const PORTAL_ORIGIN = new URL(S.URLS.login).origin;

/**
 * Bağlama JSON yanıt toplayıcısı takar; biriken yanıtların dizisini döndürür.
 * Yalnız portal origin'i + content-type application/json + boyut < 5MB toplanır.
 */
function attachJsonCollector(context) {
  const collected = [];
  context.on("response", (response) => {
    (async () => {
      try {
        const url = response.url();
        if (!url.startsWith(PORTAL_ORIGIN)) return;
        const headers = response.headers();
        const type = String(headers["content-type"] || "").toLowerCase();
        if (!type.includes("application/json")) return;
        const declared = Number(headers["content-length"] || 0);
        if (declared > JSON_MAX_BYTES) return;
        const text = await response.text();
        if (!text || text.length > JSON_MAX_BYTES) return;
        let json;
        try {
          json = JSON.parse(text);
        } catch (err) {
          return; // JSON değilmiş — atla
        }
        collected.push({ url, json, text });
        if (collected.length > JSON_MAX_RESPONSES) collected.shift();
      } catch (err) {
        // toplama hatası çekim akışını asla bozmaz
      }
    })();
  });
  return collected;
}

/** Anahtar adını eşleme için normalize eder: küçük harf, Türkçe sadeleştirme, alfasayısal dışı at. */
function normalizeKeyName(key) {
  return String(key)
    .toLocaleLowerCase("tr-TR")
    .replace(/ç/g, "c")
    .replace(/ğ/g, "g")
    .replace(/ı/g, "i")
    .replace(/ö/g, "o")
    .replace(/ş/g, "s")
    .replace(/ü/g, "u")
    .replace(/[^a-z0-9]/g, "");
}

/**
 * ESNEK ALAN DESENLERİ — normalize edilmiş anahtar adlarıyla eşlenir (öncelik sırasıyla).
 * Canlıda gerçek alan adları görülünce (diagnostic'teki ham JSON'dan) kesinleştirilecek.
 */
const JSON_FIELD_PATTERNS = {
  tebligat: {
    externalId: [/tebligatno/, /barkod/, /evrakno/, /belgeid/, /id$/],
    subject: [/konu/, /aciklama/, /belgeturu/],
    sentAt: [/tebligtarihi/, /gonderimtarihi/, /tarih/],
    readAt: [/okunmatarihi/, /goruntulenme/],
    taxOffice: [/vergidairesi/, /gonderen/, /kurum/, /birim/],
  },
  documents: {
    uuid: [/ettn/, /uuid/],
    documentNumber: [/belgeno/, /faturano/, /numara/],
    issueDate: [/duzenlenmetarihi/, /belgetarihi/, /tarih/],
    senderTaxId: [/vkn/, /tckn/, /vergikimlik/],
    senderTitle: [/unvan/, /gonderen/, /satici/],
    taxExclusiveAmount: [/matrah/, /kdvharic/],
    taxTotal: [/kdv/, /vergi/],
    payableAmount: [/odenecek/, /geneltoplam/, /toplam/],
    currency: [/parabirimi/, /doviz/],
    documentType: [/belgeturu/, /belgetipi/, /senaryo/],
  },
  advisorClients: {
    taxId: [/vkn/, /tckn/, /vergikimlik/, /vergino/],
    title: [/unvan/, /adsoyad/, /mukellef/, /^ad$/],
  },
};

/** JSON değerinden ISO tarih üretir (string, epoch saniye/milisaniye desteklenir). */
function jsonDate(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === "number") {
    const ms = value > 1e12 ? value : value > 1e9 ? value * 1000 : null;
    if (!ms) return null;
    const d = new Date(ms);
    return isNaN(d.getTime()) ? null : d.toISOString().slice(0, 10);
  }
  return parseDate(String(value));
}

/** Tek kaydı desen tablosuna göre alanlara eşler (ilk eşleşen desen kazanır). */
function mapRecordByPatterns(record, patterns) {
  const normalized = {};
  for (const [key, value] of Object.entries(record)) {
    const nk = normalizeKeyName(key);
    if (nk && !(nk in normalized)) normalized[nk] = value;
  }
  const keys = Object.keys(normalized);
  const out = {};
  for (const [field, regexList] of Object.entries(patterns)) {
    for (const rx of regexList) {
      const hit = keys.find((nk) => rx.test(nk));
      if (hit !== undefined) {
        out[field] = normalized[hit];
        break;
      }
    }
  }
  return out;
}

/** JSON ağacındaki nesne dizilerini toplar (derinlik sınırlı — devasa gövdelerde takılmaz). */
function collectObjectArrays(node, depth, out) {
  if (!node || depth > 6) return;
  if (Array.isArray(node)) {
    if (
      node.length > 0 &&
      node.every((x) => x && typeof x === "object" && !Array.isArray(x))
    ) {
      out.push(node);
    }
    return;
  }
  if (typeof node === "object") {
    for (const value of Object.values(node)) collectObjectArrays(value, depth + 1, out);
  }
}

/** Eşlenmiş kayıt ilgili tür için "geçerli liste elemanı" gibi görünüyor mu? */
function recordLooksValid(kind, m) {
  if (kind === "tebligat") {
    return jsonDate(m.sentAt) !== null && (m.subject !== undefined || m.taxOffice !== undefined);
  }
  if (kind === "documents") {
    return (
      jsonDate(m.issueDate) !== null &&
      (m.uuid !== undefined || m.documentNumber !== undefined || m.senderTitle !== undefined)
    );
  }
  if (kind === "advisorClients") {
    const tax = String(m.taxId ?? "").replace(/\D/g, "");
    return (tax.length === 10 || tax.length === 11) &&
      m.title !== undefined && String(m.title).trim().length > 0;
  }
  return false;
}

/**
 * Biriken yanıtlar içinde ilgili türe uyan İLK diziyi arar (EN YENİ yanıttan geriye).
 * Dizinin en az yarısı geçerli görünüyorsa o dizi seçilir.
 * @returns {{records: object[], source: {url, text}}|null}
 */
/**
 * Liste JSON'u gelene kadar bekler (GİB ekranı veriyi sayfa açıldıktan SONRA XHR ile
 * çeker). Bulunursa hemen döner; süre dolarsa sessizce geçilir (DOM fallback devreye
 * girer). Ek olarak yanıt gelmese bile ekrandaki "boş liste" durumu beklemeyi kısaltır.
 */
async function waitForListJson(responses, kind, timeoutMs = 12000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (findListInResponses(responses, kind)) return true;
    await sleep(500);
  }
  return false;
}

function findListInResponses(responses, kind) {
  const patterns = JSON_FIELD_PATTERNS[kind];
  for (let i = responses.length - 1; i >= 0; i--) {
    const arrays = [];
    collectObjectArrays(responses[i].json, 0, arrays);
    for (const arr of arrays) {
      const mapped = arr.map((rec) => mapRecordByPatterns(rec, patterns));
      const valid = mapped.filter((m) => recordLooksValid(kind, m));
      if (valid.length > 0 && valid.length >= Math.ceil(arr.length / 2)) {
        return { records: valid, source: responses[i] };
      }
    }
  }
  return null;
}

/**
 * Teşhis için ham JSON örneği (ilk 1500 karakter): seçilen yanıt varsa o, yoksa
 * biriken EN BÜYÜK yanıt (alan adlarını canlıda görüp eşleyiciyi kesinleştirmek için).
 * Yalnız hata/boş sonuç durumunda yanıta konur.
 */
function jsonDiagnosticSample(responses, captured) {
  const source = captured
    ? captured.source
    : [...responses].sort((a, b) => b.text.length - a.text.length)[0];
  if (!source) {
    return responses.length === 0
      ? "Sayfa hiç JSON yanıtı üretmedi (ekran açılmamış ya da veri başka biçimde geliyor olabilir)."
      : null;
  }
  // Yakalanan tüm JSON adresleri de yazılır: doğru ucu canlıda tespit etmek için.
  const urls = responses.slice(-8).map((r) => r.url).join(" , ");
  return (
    `Yakalanan JSON uçları (son 8): ${urls} — ` +
    `Ham JSON (${source.url}; ilk 1500): ${source.text.slice(0, 1500)}`
  );
}

// ---------------------------------------------------------------- gerçek API çekimi (Faz 19)
//
// CANLI TEŞHİS 05.08.2026 (gerçek oturumda doğrulandı): e-Tebligat verisi portalın
// apigateway servisinden gelir (selectors.js → API_ENDPOINTS.tebligatListele).
// Oturum token'ı sessionStorage("token") içindedir — çerez tek başına YETMİYOR.
// İstek bu yüzden SAYFA BAĞLAMINDA (page.evaluate + fetch, credentials:'include')
// yapılır; token tarayıcıdan dışarı ÇIKARILMAZ ve HİÇBİR parçası loglanmaz.

/** API listelerinde sayfa boyutu (canlıda doğrulanan gövdeyle aynı). */
const API_PAGE_SIZE = 50;
/** Güvenlik sınırı: liste başına en fazla bu kadar sayfa çekilir (50×20 = 1000 kayıt). */
const API_MAX_PAGES = 20;

/**
 * Sayfa bağlamında portal API'sine JSON POST atar. Token sessionStorage'dan okunur;
 * yoksa {tokenMissing:true}, ağ hatasında {fetchError} döner (fırlatmaz — karar
 * Node tarafında verilir). Başarıda {status, text} döner.
 */
async function apiPostJson(page, url, body) {
  return page.evaluate(
    async ({ url, body, timeoutMs }) => {
      let token = null;
      try {
        token = window.sessionStorage.getItem("token");
      } catch (err) {
        token = null;
      }
      if (!token) return { tokenMissing: true };
      try {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs);
        try {
          const res = await fetch(url, {
            method: "POST",
            credentials: "include",
            headers: {
              "Content-Type": "application/json",
              Authorization: "Bearer " + token,
            },
            body: JSON.stringify(body),
            signal: controller.signal,
          });
          const text = await res.text();
          return { status: res.status, text: String(text || "") };
        } finally {
          clearTimeout(timer);
        }
      } catch (err) {
        return { fetchError: String((err && err.message) || err) };
      }
    },
    { url, body, timeoutMs: API_REQUEST_TIMEOUT_MS }
  );
}

/** API çağrısı başarısız (401 DIŞI) — çağıran DOM fallback'ine geçebilir. */
function apiFailedError(message) {
  const err = new Error(message);
  err.apiFailed = true;
  return err;
}

/**
 * apiPostJson sonucunu doğrular: 200 + geçerli JSON + sonucKodu 1 ise JSON'u döndürür.
 * Token yok / 401 → captchaLoginRequiredError (oturum düşmüş — mevcut akış devralır).
 * Diğer her hata apiFailed bayraklı yükselir; mesajda HAM YANITIN İLK 500 karakteri
 * bulunur (uç/gövde değişikliğini canlıda teşhis etmek için). Token mesajlara yazılmaz.
 */
function ensureApiResponse(res, label) {
  if (res.status === 403) {
    const err = captchaLoginRequiredError();
    err.message = 'GİB mevcut oturumun e-Tebligat erişimini reddetti (403). Oturum yenilenmeli; yenilemeden sonra devam ederse GİB erişim yetkisi kontrol edilmeli.';
    err.accessDenied = true;
    throw err;
  }
  if (res.tokenMissing || res.status === 401) {
    // sessionStorage'da token yok ya da API 401 dedi → oturum düşmüş.
    throw captchaLoginRequiredError();
  }
  if (res.fetchError) {
    throw apiFailedError(`${label}: ağ hatası — ${String(res.fetchError).slice(0, 500)}`);
  }
  if (res.status !== 200) {
    throw apiFailedError(
      `${label}: HTTP ${res.status}. Ham yanıt (ilk 500): ${res.text.slice(0, 500)}`
    );
  }
  let json;
  try {
    json = JSON.parse(res.text);
  } catch (err) {
    throw apiFailedError(
      `${label}: yanıt JSON olarak çözülemedi. Ham yanıt (ilk 500): ${res.text.slice(0, 500)}`
    );
  }
  if (json && typeof json.sonucKodu === "number" && json.sonucKodu !== 1) {
    throw apiFailedError(
      `${label}: sonucKodu=${json.sonucKodu}. Ham yanıt (ilk 500): ${res.text.slice(0, 500)}`
    );
  }
  return json;
}

/**
 * API tarih ayrıştırıcısı: hem "dd/MM/yyyy HH:mm:ss" (tebligZamani, mukellefOkumaZamani,
 * gonderimZamani) hem ISO ("2025-12-28T03:21:19.835+03:00" — kayitZamani) destekler.
 * parseDate'in üzerine ince bir katmandır (parseDate BOZULMAZ); yyyy-aa-gg döndürür.
 */
function parseApiDate(raw) {
  if (!raw) return null;
  const text = String(raw).trim();
  // "02/11/2025 12:08:39" → gün/ay/yıl (saat kısmı yok sayılır).
  const dmy = text.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?$/);
  if (dmy) {
    return `${dmy[3]}-${String(dmy[2]).padStart(2, "0")}-${String(dmy[1]).padStart(2, "0")}`;
  }
  return parseDate(text);
}

/**
 * e-Tebligat listesini GERÇEK API'den çeker (DOM kazıma YOK). Sayfa GİB portalında
 * açıkken çağrılır (SPA token'ı sessionStorage'a koymuş olmalı). arsivDurum "0"
 * (normal) ve "1" (arşivlenenler) AYRI AYRI, pageNo döngüsüyle TÜM sayfalar
 * (data.count'a göre, en fazla API_MAX_PAGES) alınıp birleştirilir; arşiv kayıtları
 * `arsivlenmis: true` bayrağı taşır. Tarih filtresi (from/to, sentAt üzerinden)
 * İSTEMCİDE uygulanır. Oturum düşmüşse captchaLoginRequiredError, diğer hatalarda
 * apiFailed bayraklı hata (ham yanıtın ilk 500 karakteriyle) yükselir.
 *
 * @returns {Promise<{items: object[], total: number, pagesFetched: string[]}>}
 */
async function fetchTebligatViaApi(page, { from, to }) {
  const url = S.API_ENDPOINTS.tebligatListele;
  const fromDate = parseDate(from);
  const toDate = parseDate(to);
  const collected = [];
  const pagesFetched = [];

  for (const arsivDurum of ["0", "1"]) {
    for (let pageNo = 1; pageNo <= API_MAX_PAGES; pageNo++) {
      const body = {
        meta: {
          pagination: { pageNo, pageSize: API_PAGE_SIZE },
          sortFieldName: "id",
          sortType: "ASC",
          filters: [{ fieldName: "arsivDurum", values: [arsivDurum] }],
        },
      };
      const res = await apiPostJson(page, url, body);
      const json = ensureApiResponse(res, "e-Tebligat API");

      const data = (json && json.data) || {};
      const list = Array.isArray(data.tebligatDtoList) ? data.tebligatDtoList : [];
      for (const dto of list) collected.push({ dto, arsivlenmis: arsivDurum === "1" });
      pagesFetched.push(`arsiv=${arsivDurum} sayfa=${pageNo} (+${list.length})`);

      const count = Number(data.count || 0);
      if (list.length === 0 || pageNo * API_PAGE_SIZE >= count) break;
      await sleep(400); // nazik davranış: sayfalar arasında kısa bekleme
    }
  }

  const items = collected
    .map(({ dto, arsivlenmis }, i) => {
      // sentAt önceliği: tebligZamani → gonderimZamani → kayitZamani (ISO).
      const sentAt =
        parseApiDate(dto.tebligZamani) ||
        parseApiDate(dto.gonderimZamani) ||
        parseApiDate(dto.kayitZamani);

      // Tarih filtresi İSTEMCİDE: aralık dışı kayıtlar elenir (tarihsizler kalır).
      if (sentAt && fromDate && toDate && (sentAt < fromDate || sentAt > toDate)) return null;

      const kurum = String(dto.kurumAciklama || "").trim();
      const altKurum = String(dto.altKurum || "").trim();
      const taxOffice = kurum ? (altKurum ? `${kurum} — ${altKurum}` : kurum) : altKurum || null;

      return {
        externalId:
          String(dto.tebligId ?? "").trim() ||
          `GIBTEB-${sentAt || "tarihsiz"}-${String(dto.belgeNo || "").trim()}-${i}`,
        subject: String(dto.belgeTuruAciklama || "").trim() || "Tebligat",
        sentAt,
        readAt: parseApiDate(dto.mukellefOkumaZamani),
        taxOffice,
        // Yalnız belgeNo (dizin eklenmez — karar 05.08.2026).
        documentUrlOrText: String(dto.belgeNo || "").trim() || null,
        arsivlenmis,
      };
    })
    .filter(Boolean);

  return { items, total: collected.length, pagesFetched };
}

/**
 * Belge listesi için AYNI API kalıbı — ANCAK uç adresi HENÜZ BİLİNMİYOR
 * (selectors.js → API_ENDPOINTS.belgeListele = null). Uç bilinmiyorsa null döner ve
 * çağıran doğrudan mevcut DOM/JSON-yakalama akışına düşer. Uç canlıda tespit edilince
 * (boş sonuç diagnostic'indeki /apigateway/ adres listesinden) YALNIZ selectors.js
 * güncellenir; alan eşlemesi esnek desenlerle (JSON_FIELD_PATTERNS.documents) yapılır
 * ve gerçek alan adları görülünce kesinleştirilir.
 *
 * @returns {Promise<{records: object[], pagesFetched: string[]}|null>}
 */
async function fetchDocumentsViaApi(page, { from, to }) {
  const url = S.API_ENDPOINTS.belgeListele;
  if (!url) return null; // uç bilinmiyor — mevcut akış kullanılacak

  const records = [];
  const pagesFetched = [];

  for (let pageNo = 1; pageNo <= API_MAX_PAGES; pageNo++) {
    const body = {
      meta: {
        pagination: { pageNo, pageSize: API_PAGE_SIZE },
        sortFieldName: "id",
        sortType: "ASC",
        filters: [],
      },
    };
    const res = await apiPostJson(page, url, body);
    const json = ensureApiResponse(res, "Belge API");

    // Gerçek alan adları canlıda görülmediği için esnek eşleyici kullanılır.
    const arrays = [];
    collectObjectArrays(json, 0, arrays);
    let pageRecords = [];
    for (const arr of arrays) {
      const mapped = arr.map((rec) => mapRecordByPatterns(rec, JSON_FIELD_PATTERNS.documents));
      const valid = mapped.filter((m) => recordLooksValid("documents", m));
      if (valid.length > 0 && valid.length >= Math.ceil(arr.length / 2)) {
        pageRecords = valid;
        break;
      }
    }
    records.push(...pageRecords);
    pagesFetched.push(`sayfa=${pageNo} (+${pageRecords.length})`);

    const count = Number((json && json.data && json.data.count) || 0);
    if (pageRecords.length === 0 || !count || pageNo * API_PAGE_SIZE >= count) break;
    await sleep(400);
  }

  return { records, pagesFetched };
}

/**
 * Yakalanan JSON yanıtları içinden /apigateway/ adreslerini (tekilleştirilmiş) listeler.
 * Belgeler ekranında BOŞ sonuçta diagnostic'e yazılır — gerçek belge listeleme ucunu
 * canlıda tespit edip API_ENDPOINTS.belgeListele'ye yazmak için kullanılacak.
 */
function apigatewayUrlList(responses) {
  const urls = [
    ...new Set(
      responses.map((r) => r.url).filter((u) => typeof u === "string" && u.includes("/apigateway/"))
    ),
  ];
  return urls.length > 0
    ? `Görülen /apigateway/ uçları: ${urls.slice(0, 15).join(" , ")}`
    : null;
}

// ---------------------------------------------------------------- giriş

/** "GİB oturumu yenilenmeli" — kullanıcıya gösterilen standart Türkçe mesaj. */
const CAPTCHA_LOGIN_MESSAGE =
  "GİB oturumu yenilenmeli — doğrulama kodu girilmeli. Panelden \"GİB Oturumu Aç\" ile " +
  "giriş ekranındaki doğrulama kodunu girin; oturum kaydedildikten sonra çekimler otomatik sürer.";

/**
 * Kayıtlı oturum yok/geçersiz → kullanıcı doğrulama kodunu girmeli.
 * (requiresManual + needsCaptchaLogin bayraklı hata; toErrorResponse eşler.)
 */
function captchaLoginRequiredError(details) {
  const err = new Error(CAPTCHA_LOGIN_MESSAGE);
  err.requiresManual = true;
  err.needsCaptchaLogin = true;
  if (details) err.details = details;
  return err;
}

/**
 * İnsan doğrulaması gerekiyor mu? Gerekiyorsa ManualVerificationRequiredError
 * fırlatır (ÇÖZMEYE ÇALIŞILMAZ — güvenlik kuralı). Ekran GİRİŞ ekranıysa
 * (oturum düşmüş → captcha'lı giriş formu) needsCaptchaLogin bayrağı da konur:
 * kullanıcı tek tıkla doğrulama kodunu girip oturumu yenileyebilir.
 */
async function assertNoManualStep(page, label) {
  const present = await S.isPresent(page, "manualVerification", 1000);
  if (!present) return;

  const info = await captureDiagnostic(page, `manual-${label}`);
  const onLoginScreen = await S.isPresent(page, "loginPassword", 800);

  const err = new S.ManualVerificationRequiredError(
    onLoginScreen
      ? CAPTCHA_LOGIN_MESSAGE
      : "GİB ek doğrulama istedi (captcha / SMS doğrulama kodu / e-Devlet). " +
        "Güvenlik gereği sistem bu adımı kendisi çözmez; bu mükellef için elle doğrulama gerekiyor.",
    info
  );
  if (onLoginScreen) err.needsCaptchaLogin = true;
  throw err;
}

/** Oturum açık mı? (giriş göstergesi görünüyor veya sessionStorage'da token var) */
async function isLoggedIn(page) {
  // 1. Gösterge varsa (Çıkış vb.) kesinlikle giriş yapılmıştır
  if (await S.isPresent(page, "loggedInMarker", 1000)) return true;

  // 2. sessionStorage'da token var mı?
  let hasToken = false;
  try {
    hasToken = await page.waitForFunction(() => {
      const token = window.sessionStorage.getItem("token");
      return typeof token === "string" && token.trim().length > 0;
    }, null, { timeout: 3500 }).then(() => true).catch(() => false);
  } catch {
    hasToken = false;
  }

  if (hasToken) {
    // Token varsa: sayfada aktif bir loginError kutusu yoksa oturum açılmıştır
    const hasError = await S.isPresent(page, "loginError", 500);
    if (!hasError) return true;
  }

  // 3. Gösterge yoksa: giriş formu yoksa ve URL login değilse
  const loginForm = await S.isPresent(page, "loginPassword", 800);
  return !loginForm && !page.url().toLowerCase().includes("login");
}

/**
 * KAYITLI OTURUMLA girişi doğrular. GİB giriş ekranı HER girişte "Doğrulama Kodu"
 * (captcha) istediği için robot şifreyle KENDİ BAŞINA giriş YAPMAZ (güvenlik kuralı:
 * captcha ÇÖZÜLMEZ). Oturum geçersizse needsCaptchaLogin bayraklı hata yükselir;
 * kullanıcı /login-begin + /login-complete akışıyla kodu kendisi girer.
 *
 * @returns {Promise<{page: import('playwright').Page, reusedSession: boolean}>}
 */
async function ensureSession(context, page, username) {
  await page.goto(S.URLS.login, { waitUntil: "commit", timeout: SESSION_CHECK_TIMEOUT_MS });
  // Token ve giriş işareti çoğu kez ilk render'da gelir; sabit 1-2 sn bekleme yerine
  // kısa seçici denetimiyle devam edilir.

  // Önbellekten gelen oturum hâlâ geçerli mi?
  if (await isLoggedIn(page)) {
    return { page, reusedSession: true };
  }

  // Giriş ekranına düşüldü → captcha zorunlu → İNSAN girmeli. Robot denemez.
  const info = await captureDiagnostic(page, "session-expired");
  throw captchaLoginRequiredError(info);
}

// ---------------------------------------------------------------- gezinme + tablo

/**
 * Bir ekrana gider. SIRA (canlı teşhis 04.08.2026: menü avı başarısız oldu —
 * portal SPA'sında menü öğeleri hizmet arama kutusunun arkasında olabiliyor):
 *   1) DOĞRUDAN ADRES: selectors.js → SCREEN_URLS[menuKey] listesindeki adresler
 *      sırayla denenir; sayfada sonuç tablosu/boş-sonuç/tarih filtresi görünürse başarı.
 *   2) HİZMET ARAMA KUTUSU: portalın "Arama yapmak istediğiniz hizmeti yazınız"
 *      kutusuna ekran adı yazılıp ilk sonuca tıklanır.
 *   3) MENÜ AVI: eski davranış (hamburger + bağlantı adayları).
 * Üçü de tutmazsa ScreenChangedError (ham teşhisle) yükselir.
 *
 * options.fast (Faz 19): veri GERÇEK API'den çekilecekse ekranın "hazır" görünmesi
 * (tablo/filtre) BEKLENMEZ ve networkidle beklenmez — SPA'nın yüklenip token'ı
 * sessionStorage'a koyması için gezinme yeterlidir. Giriş ekranına düşülmediyse
 * ilk açılan adres başarı sayılır (hız: tablo beklemesi tamamen kalkar).
 */
async function openScreen(page, menuKey, options = {}) {
  const fast = options.fast === true;
  const screenName = menuKey === "tebligatMenu" ? S.SCREEN_NAMES.tebligat : S.SCREEN_NAMES.documents;
  const urls = (S.SCREEN_URLS && S.SCREEN_URLS[menuKey]) || [];
  const tried = [];

  // 1) Doğrudan adres denemeleri
  for (const url of urls) {
    tried.push(`url=${url}`);
    try {
      await page.goto(url, {
        waitUntil: fast ? "commit" : "domcontentloaded",
        timeout: fast ? FAST_SCREEN_NAVIGATION_TIMEOUT_MS : 5000,
      });
      if (!fast) {
        await page.waitForLoadState("networkidle", { timeout: 3000 }).catch(() => {});
        await polite();
      } else {
        await sleep(300);
      }
      await assertNoManualStep(page, "screen");

      // Giriş ekranına düşmediysek ve ekranda liste/filtre işareti varsa başarı.
      const backToLogin = await S.isPresent(page, "loginPassword", 800);
      if (backToLogin) continue;

      if (fast) return; // API doğrudan çağrılacak — tablo/filtre beklemesi yok

      const looksReady =
        (await S.isPresent(page, "resultTable", 2500)) ||
        (await S.isPresent(page, "emptyResult", 800)) ||
        (await S.isPresent(page, "dateFrom", 800));
      if (looksReady) return;
    } catch (err) {
      if (err && (err.requiresManual || err.loginFailed)) throw err;
      // sıradaki adresi dene
    }
  }

  // 2) Portal hizmet arama kutusu
  try {
    const search = await S.resolve(page, "serviceSearch", { optional: true, timeout: 2500 });
    if (search) {
      tried.push("serviceSearch");
      const term = menuKey === "tebligatMenu" ? "e-Tebligat" : "e-Belge";
      await search.click();
      await search.fill(term);
      await sleep(1200);
      const result = page.getByText(screenName, { exact: false }).first();
      await result.waitFor({ state: "visible", timeout: 5000 });
      await result.click();
      await page.waitForLoadState("networkidle", { timeout: 3000 }).catch(() => {});
      await polite();
      await assertNoManualStep(page, "screen");
      return;
    }
  } catch (err) {
    if (err && (err.requiresManual || err.loginFailed)) throw err;
  }

  // 3) Klasik menü avı (eski davranış)
  const toggle = await S.resolve(page, "menuToggle", { optional: true, timeout: 1200 });
  if (toggle) {
    try {
      await toggle.click();
      await sleep(600);
    } catch (err) {
      // menü zaten açık olabilir
    }
  }

  try {
    const menu = await S.resolve(page, menuKey, { timeout: 8000 });
    await menu.click();
    await page.waitForLoadState("networkidle", { timeout: 3000 }).catch(() => {});
    await polite();
    await assertNoManualStep(page, "screen");
  } catch (err) {
    if (err && err.details) {
      err.details.tried = [...tried, ...(err.details.tried || [])];
      err.details.currentUrl = page.url();
    }
    throw err;
  }
}

// ---------------------------------------------------------------- Faz 18: müşavir → mükellef seçimi

/**
 * MÜKELLEF SEÇİMİ (müşavir tek oturum modeli): SMMM oturumunda "mükellef değiştir/seç"
 * kutusu açılır, VKN yazılır, eşleşen satıra tıklanır, (varsa) seçim onaylanır.
 * Kutu/satır bulunamazsa ScreenChangedError + teşhis (sayfa metni ilk 500 + ekran
 * görüntüsü yolu) yükselir — mevcut hata kalıbı.
 */
async function selectClient(page, targetTaxId) {
  const taxId = String(targetTaxId || "").replace(/\D/g, "");
  if (!taxId || (taxId.length !== 10 && taxId.length !== 11)) {
    throw new Error(`Geçersiz mükellef VKN/TCKN: "${String(targetTaxId)}".`);
  }

  const switcher = await S.resolve(page, "clientSwitcher", { timeout: 10000 });

  // Native <select> ise seçenek metninden/değerinden VKN aranır.
  const tagName = await switcher
    .evaluate((el) => el.tagName.toLowerCase())
    .catch(() => "");
  if (tagName === "select") {
    const value = await switcher.evaluate((el, tax) => {
      const opt = Array.from(el.options).find(
        (o) => (o.textContent || "").includes(tax) || (o.value || "").includes(tax)
      );
      return opt ? opt.value : null;
    }, taxId);
    if (value === null) {
      throw new S.ScreenChangedError(
        `Müşavir panelindeki mükellef listesinde ${taxId} numaralı mükellef bulunamadı. ` +
          "Mükellef GİB'de müşavire bağlı olmayabilir.",
        await captureDiagnostic(page, "client-select")
      );
    }
    await switcher.selectOption(value);
    await polite();
    await assertNoManualStep(page, "client-select");
    return;
  }

  await switcher.click();
  await sleep(600);

  // Arama kutusu varsa VKN yazılır (yoksa liste doğrudan görünüyordur — atlanır).
  const search = await S.resolve(page, "clientSearchInput", { optional: true, timeout: 2500 });
  if (search) {
    await search.click().catch(() => {});
    await search.fill(taxId);
    await sleep(1200);
  }

  // VKN metnini taşıyan ilk sonuç satırı.
  let row = null;
  const tried = [];
  for (const candidate of S.SELECTORS.clientResultRow.candidates) {
    tried.push(S.describeCandidate(candidate));
    try {
      const locator = S.toLocator(page, candidate).filter({ hasText: taxId }).first();
      await locator.waitFor({ state: "visible", timeout: 2500 });
      row = locator;
      break;
    } catch (err) {
      // sıradaki aday
    }
  }
  if (!row) {
    // Son çare: VKN metninin kendisi tıklanabilir olabilir.
    try {
      const byText = page.getByText(taxId, { exact: false }).first();
      await byText.waitFor({ state: "visible", timeout: 2500 });
      row = byText;
    } catch (err) {
      // bulunamadı — aşağıda teşhisle hata
    }
  }
  if (!row) {
    const info = await captureDiagnostic(page, "client-select");
    info.tried = tried;
    throw new S.ScreenChangedError(
      `Müşavir oturumunda ${taxId} numaralı mükellef seçilemedi (arama sonucu satırı yok). ` +
        "Mükellef GİB'de müşavire bağlı olmayabilir veya ekran değişmiş olabilir.",
      info
    );
  }

  await row.click();
  await sleep(800);

  // Seçim onayı (varsa).
  const confirm = await S.resolve(page, "clientSelectConfirm", { optional: true, timeout: 1500 });
  if (confirm) {
    await confirm.click().catch(() => {});
    await sleep(600);
  }

  await page.waitForLoadState("networkidle", { timeout: 3000 }).catch(() => {});
  await polite();
  await assertNoManualStep(page, "client-select");
}

/**
 * Müşavir mükellef listesi ekranını açar: önce SCREEN_URLS.advisorClients adresleri,
 * sonra portal hizmet arama kutusu. İkisi de tutmazsa ScreenChangedError + teşhis.
 */
async function openAdvisorClientsScreen(page) {
  const urls = (S.SCREEN_URLS && S.SCREEN_URLS.advisorClients) || [];
  const tried = [];

  for (const url of urls) {
    tried.push(`url=${url}`);
    try {
      await page.goto(url, { waitUntil: "domcontentloaded", timeout: 20000 });
      await page.waitForLoadState("networkidle", { timeout: 3000 }).catch(() => {});
      await polite();
      await assertNoManualStep(page, "advisor-clients");

      const backToLogin = await S.isPresent(page, "loginPassword", 800);
      if (backToLogin) continue;

      const looksReady =
        (await S.isPresent(page, "resultTable", 2000)) ||
        (await S.isPresent(page, "clientSwitcher", 1000)) ||
        (await S.isPresent(page, "emptyResult", 800));
      if (looksReady) return;
    } catch (err) {
      if (err && (err.requiresManual || err.loginFailed)) throw err;
      // sıradaki adresi dene
    }
  }

  // Portal hizmet arama kutusu
  try {
    await page
      .goto(S.URLS.home, { waitUntil: "domcontentloaded", timeout: 20000 })
      .catch(() => {});
    const search = await S.resolve(page, "serviceSearch", { optional: true, timeout: 2500 });
    if (search) {
      tried.push("serviceSearch");
      await search.click();
      await search.fill("Mükellef İşlemleri");
      await sleep(1200);
      const result = page.getByText(S.SCREEN_NAMES.advisorClients, { exact: false }).first();
      await result.waitFor({ state: "visible", timeout: 5000 });
      await result.click();
      await page.waitForLoadState("networkidle", { timeout: 3000 }).catch(() => {});
      await polite();
      await assertNoManualStep(page, "advisor-clients");
      return;
    }
  } catch (err) {
    if (err && (err.requiresManual || err.loginFailed)) throw err;
  }

  const info = await captureDiagnostic(page, "advisor-clients");
  info.tried = tried;
  throw new S.ScreenChangedError(
    "GİB portalında müşavir mükellef listesi ekranı bulunamadı. Ekran adresi/adı değişmiş " +
      "olabilir; selectors.js → SCREEN_URLS.advisorClients adaylarının güncellenmesi gerekebilir.",
    info
  );
}

/** GİB ekranlarının beklediği gg.aa.yyyy biçimi. */
function toTrDate(isoDate) {
  const d = new Date(isoDate);
  if (isNaN(d.getTime())) return null;
  const pad = (n) => String(n).padStart(2, "0");
  return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()}`;
}

/** input[type=date] için yyyy-aa-gg biçimi. */
function toIsoDate(isoDate) {
  const d = new Date(isoDate);
  if (isNaN(d.getTime())) return null;
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/**
 * Tarih aralığını doldurup sorgular. Alanın türüne göre (date / metin) uygun
 * biçim yazılır; alan bulunamazsa ScreenChangedError yükselir.
 */
async function applyDateRange(page, from, to) {
  const fromField = await S.resolve(page, "dateFrom", { timeout: 8000 });
  const toField = await S.resolve(page, "dateTo", { timeout: 8000 });

  async function writeDate(locator, value) {
    let type = "";
    try {
      type = (await locator.getAttribute("type")) || "";
    } catch (err) {
      type = "";
    }
    const text = type.toLowerCase() === "date" ? toIsoDate(value) : toTrDate(value);
    if (!text) throw new Error(`Geçersiz tarih değeri: ${value}`);
    await locator.fill("");
    await locator.fill(text);
    // Takvim açılırsa kapat (alan dışına tıklamak yerine ESC — daha güvenli).
    await page.keyboard.press("Escape").catch(() => {});
  }

  await writeDate(fromField, from);
  await writeDate(toField, to);
  await polite();

  const submit = await S.resolve(page, "querySubmit", { timeout: 8000 });
  await submit.click();
  await page.waitForLoadState("networkidle", { timeout: 4000 }).catch(() => {});
  await polite();
  await assertNoManualStep(page, "query");
}

/**
 * Tarih aralığını uygulamayı DENER. Ekranda tarih alanları yoksa çekim durmaz:
 * false döner ve çağıran listeyi kendi filtreler (uydurma veri üretilmez, yalnız
 * aralık dışı satırlar elenir). Diğer hatalar (captcha vb.) olduğu gibi yükselir.
 */
async function tryApplyDateRange(page, from, to) {
  try {
    await applyDateRange(page, from, to);
    return true;
  } catch (err) {
    if (err && err.screenChanged) return false;
    throw err;
  }
}

/**
 * Sonuç tablosunu başlık metinlerine göre okur (sabit sütun indeksi KULLANILMAZ —
 * GİB sütun sırasını değiştirirse eşleme yine doğru kalır).
 *
 * @param {object} columnPatterns selectors.js'teki documentColumns / tebligatColumns
 * @returns {Promise<{rows: object[], missingColumns: string[], empty: boolean}>}
 */
async function readTable(page, columnPatterns) {
  // Boş sonuç bilgisi varsa liste boştur (hata değildir).
  const empty = await S.isPresent(page, "emptyResult", 1200);

  // Tablo bulunamazsa AKIŞ DURMAZ (canlı teşhis 05.08.2026: GİB ekranında klasik
  // <table> yok — veri JSON'da; DOM yalnız yedek yoldur). Boş liste + teşhis notu döner.
  const table = await S.resolve(page, "resultTable", { timeout: empty ? 3000 : 8000, optional: true });
  if (!table) {
    return {
      rows: [],
      missingColumns: [],
      empty: true,
      tableMissing: !empty,
    };
  }

  // Başlıklar + satırlar tek DOM okumasıyla alınır (SPA yeniden render riskini azaltır).
  const raw = await table.evaluate((el) => {
    const headerCells = Array.from(el.querySelectorAll("thead th, thead td, [role='columnheader']"));
    let headers = headerCells.map((c) => (c.innerText || c.textContent || "").replace(/\s+/g, " ").trim());

    const bodyRows = Array.from(el.querySelectorAll("tbody tr"));
    let rows = bodyRows.map((r) =>
      Array.from(r.querySelectorAll("td, th, [role='gridcell']")).map((c) =>
        (c.innerText || c.textContent || "").replace(/\s+/g, " ").trim()
      )
    );

    // thead yoksa ilk satır başlık kabul edilir.
    if (headers.length === 0 && rows.length > 0) {
      headers = rows[0];
      rows = rows.slice(1);
    }

    // Bağlantı taşıyan hücrelerin href'leri (tebligat belgesi için).
    const links = bodyRows.map((r) =>
      Array.from(r.querySelectorAll("a[href]")).map((a) => a.getAttribute("href"))
    );

    return { headers, rows, links };
  });

  const headers = raw.headers || [];
  const index = {};
  const missingColumns = [];

  for (const [field, pattern] of Object.entries(columnPatterns)) {
    const i = headers.findIndex((h) => h && pattern.test(h));
    if (i >= 0) index[field] = i;
    else missingColumns.push(field);
  }

  const rows = (raw.rows || [])
    .filter((cells) => cells.some((c) => c && c.length > 0))
    .map((cells, rowIndex) => {
      const record = { _cells: cells, _links: (raw.links || [])[rowIndex] || [] };
      for (const [field, i] of Object.entries(index)) {
        record[field] = cells[i] ?? "";
      }
      return record;
    });

  return { rows, missingColumns, empty: rows.length === 0 };
}

// ---------------------------------------------------------------- değer dönüştürme

/**
 * "1.234,56" / "12.345" / "1,234.56" / "2.400,00 TL" → Number (çözülemezse 0).
 *
 * GİB ekranı Türkçe biçim kullanır (nokta binlik, virgül ondalık) ama tek biçime
 * bel bağlanmaz; ayırıcı düzeninden karar verilir:
 *  - Hem virgül hem nokta varsa: SONDAKİ ayırıcı ondalıktır.
 *  - Yalnız bir tür ayırıcı varsa: birden çok geçiyorsa binliktir; tek geçiyorsa
 *    ardından TAM 3 hane geliyorsa binlik (12.345 → 12345), değilse ondalıktır.
 */
function parseAmount(raw) {
  if (raw === null || raw === undefined) return 0;
  let text = String(raw).replace(/[^\d,.\-]/g, "").trim();
  if (!text) return 0;

  const commas = (text.match(/,/g) || []).length;
  const dots = (text.match(/\./g) || []).length;

  if (commas > 0 && dots > 0) {
    if (text.lastIndexOf(",") > text.lastIndexOf(".")) {
      text = text.replace(/\./g, "").replace(",", "."); // TR: 1.234,56
    } else {
      text = text.replace(/,/g, ""); // EN: 1,234.56
    }
  } else if (commas > 0 || dots > 0) {
    const sep = commas > 0 ? "," : ".";
    const count = commas > 0 ? commas : dots;
    const decimals = text.length - text.lastIndexOf(sep) - 1;
    const isThousands = count > 1 || decimals === 3;
    text = isThousands
      ? text.split(sep).join("")
      : text.replace(sep, ".");
  }

  const value = Number.parseFloat(text);
  return Number.isFinite(value) ? Math.round(value * 100) / 100 : 0;
}

/** "05.03.2026" / "2026-03-05" / "05.03.2026 14:22" → ISO tarih (yyyy-aa-gg) veya null. */
function parseDate(raw) {
  if (!raw) return null;
  const text = String(raw).trim();

  const tr = text.match(/^(\d{1,2})[./-](\d{1,2})[./-](\d{4})/);
  if (tr) {
    const [, d, m, y] = tr;
    return `${y}-${String(m).padStart(2, "0")}-${String(d).padStart(2, "0")}`;
  }

  const iso = text.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (iso) return `${iso[1]}-${iso[2]}-${iso[3]}`;

  const parsed = new Date(text);
  if (!isNaN(parsed.getTime())) return parsed.toISOString().slice(0, 10);
  return null;
}

/** Metinden 10/11 haneli VKN/TCKN çeker. */
function parseTaxId(raw) {
  if (!raw) return "";
  const match = String(raw).match(/\b(\d{10,11})\b/);
  return match ? match[1] : String(raw).replace(/\D/g, "").slice(0, 11);
}

// ---------------------------------------------------------------- insan destekli giriş
//
// GİB giriş ekranı her girişte "Doğrulama Kodu" (captcha) istiyor (canlı teşhis
// 04.08.2026). Robot kodu ÇÖZMEZ; iki aşamalı akışla kullanıcıya çözdürür:
//   1) loginBegin   → tarayıcı açılır, kullanıcı adı + şifre doldurulur, captcha
//                     görselinin ekran görüntüsü (base64 PNG) döner; sayfa bekletilir.
//   2) loginComplete → kullanıcının okuduğu kod yazılır, "Giriş Yap"a basılır;
//                     başarılıysa storageState kaydedilir (bkz. session.js TTL).

/** Bekleyen giriş oturumunun ömrü (kodun girilmesi için tanınan süre). */
const PENDING_TTL_MS = Number(process.env.PENDING_LOGIN_TTL_MS || 5 * 60 * 1000);
/** Aynı anda bekletilen en fazla giriş oturumu (aşılırsa EN ESKİSİ kapatılır). */
const PENDING_MAX = Number(process.env.PENDING_LOGIN_MAX || 20);
/** Aynı pendingId için en fazla kod denemesi; sonrasında oturum iptal edilir. */
const PENDING_MAX_ATTEMPTS = 3;

/**
 * pendingId → {browser, context, page, username, password, createdAt, attempts, timer}
 * Şifre YALNIZ bellekte tutulur (hatalı denemede formu yeniden doldurmak için) ve
 * oturum kapanınca düşer; hiçbir log/dosyaya yazılmaz.
 */
const pendingLogins = new Map();

/** Bekleyen giriş oturumunu kapatır (tarayıcı dahil). */
async function closePendingLogin(pendingId) {
  const entry = pendingLogins.get(pendingId);
  if (!entry) return;
  pendingLogins.delete(pendingId);
  clearTimeout(entry.timer);
  await closeQuietly(entry.browser);
}

/** Giriş ekranındaki captcha görselinin ekran görüntüsü (base64 PNG). */
async function captureCaptchaImage(page, timeout = LOGIN_CAPTCHA_TIMEOUT_MS) {
  const locator = await S.resolve(page, "captchaImage", { timeout });
  const buffer = await locator.screenshot({ type: "png" });
  return buffer.toString("base64");
}

/**
 * 1. AŞAMA — girişi başlatır: kimliği doldurur, captcha görselini döndürür,
 * sayfayı bellekte bekletir.
 *
 * @returns {Promise<{ok:boolean, pendingId?:string, captchaImageBase64?:string,
 *   expiresInSeconds?:number, alreadyLoggedIn?:boolean, message:string, diagnostic?:string}>}
 */
async function loginBegin({ username, password, sessionKey }) {
  if (!username || !password) {
    return { ok: false, message: "GİB kullanıcı kodu ve şifresi zorunludur.", items: [] };
  }

  // Faz 18: oturum, verilirse sessionKey ("advisor:<tenantId>") altına, verilmezse
  // GERİYE UYUMLU olarak kullanıcı adı altına kaydedilir.
  const storageKey = sessionKey || username;

  // Kayıtlı oturum hâlâ geçerliyse doğrulama koduna hiç gerek yok.
  const cached = session.loadState(storageKey);
  if (cached) {
    const { browser: checkBrowser, context: checkContext } = await openContext(cached);
    try {
      const page = await checkContext.newPage();
      await page.goto(S.URLS.login, { waitUntil: "commit", timeout: LOGIN_NAVIGATION_TIMEOUT_MS });
      await sleep(500);
      if (await isLoggedIn(page)) {
        return {
          ok: true,
          alreadyLoggedIn: true,
          message:
            "Kayıtlı GİB oturumu hâlâ geçerli — doğrulama koduna gerek yok; çekimler otomatik sürecek.",
        };
      }
      // Süresi dolmuş oturum SİLİNMEZ, "expired" işaretlenir (panel "yenilenmeli"
      // diyebilsin); taze girişe geçilir — başarılı giriş dosyayı yeniden yazar.
      session.markExpired(storageKey);
    } catch (err) {
      // Önbellek denemesi akışı durdurmaz; taze girişe geçilir.
    } finally {
      await closeQuietly(checkBrowser);
    }
  }

  // Eşzamanlı bekleyen oturum sınırı: en eskisi kapatılır (kaynak koruması).
  if (pendingLogins.size >= PENDING_MAX) {
    const oldest = [...pendingLogins.entries()].sort(
      (a, b) => a[1].createdAt - b[1].createdAt
    )[0];
    if (oldest) {
      console.warn(`[login-begin] bekleyen oturum sınırı (${PENDING_MAX}) aşıldı; en eski oturum kapatılıyor.`);
      await closePendingLogin(oldest[0]);
    }
  }

  // Captcha görseli gerektiği için görseller İNDİRİLİR (allowImages).
  const { browser, context } = await openContext(null, { allowImages: true });
  try {
    const page = await context.newPage();
    // HIZ (kullanıcı geri bildirimi 04.08.2026: giriş ~52 sn sürüyordu):
    // "networkidle" GİB SPA'sında arka plan istekleri yüzünden geç doluyor; bunun
    // yerine doğrudan alanların görünmesini bekliyoruz (alan çıkınca sayfa hazırdır).
    // GİB SPA arka plan çağrılarını uzun süre açık tutabiliyor. Yanıtın geldiği an
    // devam edip doğrudan form alanlarını beklemek, kullanıcıyı networkidle'a bağlamaz.
    await page.goto(S.URLS.login, { waitUntil: "commit", timeout: LOGIN_NAVIGATION_TIMEOUT_MS });

    const userField = await S.resolve(page, "loginUsername", { timeout: LOGIN_FIELD_TIMEOUT_MS });
    const passField = await S.resolve(page, "loginPassword", { timeout: LOGIN_FIELD_TIMEOUT_MS });
    await userField.fill(String(username));
    await passField.fill(String(password));

    const captchaImageBase64 = await captureCaptchaImage(page);

    const pendingId = crypto.randomUUID();
    const entry = {
      browser,
      context,
      page,
      username,
      storageKey, // oturumun diske yazılacağı anahtar (advisor:<tenantId> veya kullanıcı adı)
      password, // yalnız bellekte — hatalı denemede formu yeniden doldurmak için
      createdAt: Date.now(),
      attempts: 0,
      timer: setTimeout(() => {
        console.log(`[login-begin] bekleyen oturum süresi doldu, kapatılıyor: ${pendingId.slice(0, 8)}`);
        closePendingLogin(pendingId);
      }, PENDING_TTL_MS),
    };
    if (entry.timer.unref) entry.timer.unref();
    pendingLogins.set(pendingId, entry);

    return {
      ok: true,
      pendingId,
      captchaImageBase64,
      expiresInSeconds: Math.floor(PENDING_TTL_MS / 1000),
      message: "Doğrulama kodu bekleniyor. Görseldeki kodu okuyup girin.",
    };
  } catch (err) {
    // Başarısız başlangıç: teşhis alınır, tarayıcı bekletilmez.
    try {
      const pages = context.pages();
      if (!err.details && !err.diagnosticInfo && pages.length > 0) {
        err.diagnosticInfo = await captureDiagnostic(pages[0], "login-begin");
      }
    } catch (diagErr) {
      // teşhis alınamazsa akış yine de normalize edilir
    }
    await closeQuietly(browser);
    return toErrorResponse(err);
  }
}

/**
 * 2. AŞAMA — girişi tamamlar: kullanıcının okuduğu kodu yazar, "Giriş Yap"a basar.
 * Başarıda storageState kaydedilir; hatalı kodda AYNI pendingId ile YENİ captcha
 * görseli döner (en fazla 3 deneme, sonra oturum iptal edilir).
 *
 * @returns {Promise<{ok:boolean, message:string, captchaImageBase64?:string,
 *   pendingId?:string, attemptsLeft?:number, pendingExpired?:boolean, diagnostic?:string}>}
 */
async function loginComplete({ pendingId, code }) {
  const entry = pendingLogins.get(pendingId);
  if (!entry) {
    return {
      ok: false,
      pendingExpired: true,
      message:
        "Bekleyen giriş oturumu bulunamadı veya süresi doldu (5 dakika). " +
        "\"Yeni Kod\" ile tekrar başlayın.",
    };
  }

  const { page, context, username } = entry;
  try {
    const captchaField = await S.resolve(page, "captchaInput", { timeout: LOGIN_FIELD_TIMEOUT_MS });
    await captchaField.fill("");
    await captchaField.fill(String(code).trim());
    await polite();

    const submit = await S.resolve(page, "loginSubmit", { timeout: LOGIN_SUBMIT_TIMEOUT_MS });
    await Promise.all([
      // Arka plan çağrıları yüzünden networkidle beklenmez; DOM hazır olunca sonucu
      // kontrol etmek yeterlidir ve tamamlamayı saniyeler yerine dakikalara uzatmaz.
      page.waitForLoadState("domcontentloaded", { timeout: LOGIN_SUBMIT_TIMEOUT_MS }).catch(() => {}),
      submit.click(),
    ]);
    await polite();

    if (await isLoggedIn(page)) {
      // Oturum diske yazılır (session.js kalıbı: anahtar = kullanıcı adı özeti).
      // KRİTİK (canlı teşhis 04.08.2026): GİB portalı oturum bilgisini sessionStorage'da
      // tutuyor; Playwright storageState'i sessionStorage'ı İÇERMEZ → yalnız çerezle
      // açılan yeni tarayıcı giriş ekranına düşüyordu. Bu yüzden sessionStorage ayrıca
      // okunup state'e iliştirilir (openContext geri yükler).
      try {
        const state = await context.storageState();
        state.__sessionStorage = await captureSessionStorage(page);
        // Faz 18: anahtar, girişte verilen storageKey'dir (advisor:<tenantId> veya
        // GERİYE UYUMLU kullanıcı adı) — bkz. loginBegin.
        session.saveState(entry.storageKey || username, state);
      } catch (err) {
        console.warn("[login-complete] Oturum durumu kaydedilemedi:", err.message);
      }
      await closePendingLogin(pendingId);
      return {
        ok: true,
        message: "Giriş başarılı, oturum kaydedildi. Çekimler bu oturumla otomatik sürecek.",
      };
    }

    // Başarısız: neden ekrandan okunur (şifre/kod ASLA loglanmaz).
    entry.attempts += 1;
    let reason = "";
    const errorBox = await S.resolve(page, "loginError", { optional: true, timeout: 1500 });
    if (errorBox) {
      try {
        reason = (await errorBox.innerText()).replace(/\s+/g, " ").trim().slice(0, 200);
      } catch (err) {
        reason = "";
      }
    }
    const baseMessage = reason
      ? `Giriş başarısız. Portal mesajı: ${reason}`
      : "Giriş başarısız. Doğrulama kodu veya şifre hatalı olabilir.";

    if (entry.attempts >= PENDING_MAX_ATTEMPTS) {
      await closePendingLogin(pendingId);
      return {
        ok: false,
        pendingExpired: true,
        message: `${baseMessage} ${PENDING_MAX_ATTEMPTS} deneme başarısız oldu; "Yeni Kod" ile baştan başlayın.`,
      };
    }

    // GİB başarısız girişte formu sıfırlayabilir → kimlik alanları boşsa yeniden doldurulur.
    try {
      const userField = await S.resolve(page, "loginUsername", { optional: true, timeout: 2500 });
      const passField = await S.resolve(page, "loginPassword", { optional: true, timeout: 2500 });
      if (userField && !(await userField.inputValue().catch(() => ""))) {
        await userField.fill(String(entry.username));
      }
      if (passField && !(await passField.inputValue().catch(() => ""))) {
        await passField.fill(String(entry.password));
      }
    } catch (err) {
      // form yeniden doldurulamazsa kullanıcı "Yeni Kod" ile baştan başlayabilir
    }

    // Yeni captcha görseli (başarısız denemede GİB genelde görseli yeniler).
    let captchaImageBase64 = null;
    try {
      captchaImageBase64 = await captureCaptchaImage(page);
    } catch (err) {
      // görsel alınamazsa istemci "Yeni Kod" ile baştan başlar
    }

    const attemptsLeft = PENDING_MAX_ATTEMPTS - entry.attempts;
    return {
      ok: false,
      pendingId,
      captchaImageBase64,
      attemptsLeft,
      message: `${baseMessage} Yeni görseldeki kodu girin (${attemptsLeft} deneme hakkı kaldı).`,
    };
  } catch (err) {
    // Beklenmeyen hata: yarım kalmış sayfa bekletilmez, oturum kapatılır.
    if (!err.details && !err.diagnosticInfo) {
      err.diagnosticInfo = await captureDiagnostic(page, "login-complete").catch(() => null);
    }
    await closePendingLogin(pendingId);
    const response = toErrorResponse(err);
    response.pendingExpired = true;
    return response;
  }
}

/** Captcha görselini yenilemek için refresh butonuna veya görselin üzerine tıklar. */
async function refreshCaptcha(page) {
  try {
    const refreshBtn = await page.$(
      "button[title*='yenile' i], [aria-label*='yenile' i], .captcha-refresh, img[src*='refresh' i]"
    );
    if (refreshBtn) {
      await refreshBtn.click().catch(() => {});
      return;
    }
    const img = await S.resolve(page, "captchaImage", { optional: true, timeout: 1500 });
    if (img) {
      await img.click().catch(() => {});
    }
  } catch (_) {}
}

/**
 * OTOMATİK GİRİŞ (Auto-Login with Local OCR):
 * GİB giriş ekranını açar, kullanıcı adı ve şifreyi doldurur.
 * Captcha görselini yerel OCR (captcha-solver.js) ile okur, kod alanına yazar ve giriş yapar.
 * Hatalı kod durumunda görseli yenileyip en fazla 4 kez tekrar dener.
 * Başarılı olursa storageState ve __sessionStorage'ı kaydeder.
 *
 * @returns {Promise<{ok:boolean, message:string, method?:string, diagnostic?:string}>}
 */
async function autoLogin({ username, password, sessionKey }) {
  if (!username || !password) {
    return { ok: false, message: "GİB kullanıcı kodu ve şifresi zorunludur.", items: [] };
  }

  const storageKey = sessionKey || username;
  console.log(`[auto-login] Otomatik giriş başlatılıyor — anahtar:${storageKey}`);

  const { browser, context } = await openContext(null, { allowImages: true });
  try {
    const page = await context.newPage();
    await page.goto(S.URLS.login, { waitUntil: "commit", timeout: LOGIN_NAVIGATION_TIMEOUT_MS });

    const userField = await S.resolve(page, "loginUsername", { timeout: LOGIN_FIELD_TIMEOUT_MS });
    const passField = await S.resolve(page, "loginPassword", { timeout: LOGIN_FIELD_TIMEOUT_MS });
    await userField.fill(String(username));
    await passField.fill(String(password));

    const MAX_OCR_ATTEMPTS = 4;
    for (let attempt = 1; attempt <= MAX_OCR_ATTEMPTS; attempt++) {
      console.log(`[auto-login] Captcha denemesi ${attempt}/${MAX_OCR_ATTEMPTS}`);

      // Captcha locator'ını al
      const captchaLocator = await S.resolve(page, "captchaImage", { timeout: LOGIN_CAPTCHA_TIMEOUT_MS });
      const rawBuffer = await captchaLocator.screenshot({ type: "png" });

      // Yerel OCR / Preprocessed Canvas ile çöz
      const solution = await captchaSolver.solveCaptcha(rawBuffer, page);
      console.log(
        `[auto-login] Captcha çözüm sonucu (${solution?.method || "yok"}): ${solution?.code || "okunamadı"}`
      );

      if (!solution || !solution.code || solution.code.length < 4) {
        console.warn("[auto-login] Captcha okunamadı, görsel yenileniyor...");
        await refreshCaptcha(page);
        await polite();
        continue;
      }

      const captchaField = await S.resolve(page, "captchaInput", { timeout: LOGIN_FIELD_TIMEOUT_MS });
      await captchaField.fill("");
      await captchaField.fill(solution.code.trim());
      await polite();

      const submit = await S.resolve(page, "loginSubmit", { timeout: LOGIN_SUBMIT_TIMEOUT_MS });
      await submit.click();

      // Yanıtı bekle: oturum açılana kadar veya hata mesajı çıkana kadar aktif döngü (maks 8 sn)
      let loginSuccess = false;
      const startWait = Date.now();
      while (Date.now() - startWait < 8000) {
        if (await isLoggedIn(page)) {
          loginSuccess = true;
          break;
        }
        if (await S.isPresent(page, "loginError", 300)) {
          break;
        }
        await sleep(300);
      }

      if (loginSuccess) {
        try {
          const state = await context.storageState();
          state.__sessionStorage = await captureSessionStorage(page);
          session.saveState(storageKey, state);
          console.log(`[auto-login] Giriş BAŞARILI, oturum kaydedildi (${solution.method} ile çözüldü).`);
        } catch (saveErr) {
          console.warn("[auto-login] Oturum durumu kaydedilemedi:", saveErr.message);
        }
        await closeQuietly(browser);
        return {
          ok: true,
          message: `Otomatik giriş başarılı (${solution.method} OCR ile çözüldü). Oturum kaydedildi.`,
          method: solution.method,
        };
      }

      // Başarısız: Hata nedenini kontrol et
      const errorBox = await S.resolve(page, "loginError", { optional: true, timeout: 1500 });
      let reason = "";
      if (errorBox) {
        reason = (await errorBox.innerText().catch(() => "")).replace(/\s+/g, " ").trim();
        console.warn(`[auto-login] Giriş başarısız, portal mesajı: ${reason}`);
      }

      // Kullanıcı adı / şifre silindiyse tekrar doldur
      try {
        const u = await S.resolve(page, "loginUsername", { optional: true, timeout: 1500 });
        const p = await S.resolve(page, "loginPassword", { optional: true, timeout: 1500 });
        if (u && !(await u.inputValue().catch(() => ""))) await u.fill(String(username));
        if (p && !(await p.inputValue().catch(() => ""))) await p.fill(String(password));
      } catch (_) {}

      // Görseli yenile
      await refreshCaptcha(page);
      await polite();
    }

    await closeQuietly(browser);
    return {
      ok: false,
      requiresManual: true,
      needsCaptchaLogin: true,
      message: `Otomatik OCR girişi ${MAX_OCR_ATTEMPTS} denemede doğrulanamadı. Lütfen manuel giriş yapın.`,
    };
  } catch (err) {
    await closeQuietly(browser);
    return toErrorResponse(err);
  }
}

// ---------------------------------------------------------------- oturum heartbeat (keepalive)

/**
 * OTURUMU CANLI TUTMA "hareketi" (keepalive.js çağırır): kayıtlı storageState ile
 * headless sayfa açılır, giriş gerektiren HAFİF bir ekrana gidilir ve giriş göstergesine
 * bakılır. Belge çekilmez, form doldurulmaz — yalnız normal bir sayfa ziyareti.
 *
 * Dönüş:
 *   { alive: true,  storageState }  → oturum canlı; state YENİDEN kaydedilmeli
 *                                     (çerez süresi tazelenir, TTL penceresi ilerler).
 *   { alive: false, loggedOut: true }   → giriş ekranına düşüldü; oturum DÜŞMÜŞ.
 *   { alive: false, uncertain: true }   → ne gösterge ne giriş formu görüldü (ör. portal
 *                                     bakımda) — karar VERİLMEZ, işaretleme yapılmaz.
 */
async function sessionHeartbeat(storageState) {
  const { browser, context } = await openContext(storageState);
  try {
    const page = await context.newPage();

    /** Sayfayı açıp durumu okur: "alive" / "loggedOut" / "uncertain". */
    async function probe(url) {
      await page.goto(url, { waitUntil: "domcontentloaded", timeout: 20000 });
      await page.waitForLoadState("networkidle", { timeout: 3000 }).catch(() => {});
      if (await isLoggedIn(page)) return "alive";
      if (await S.isPresent(page, "loginPassword", 1500)) return "loggedOut";
      return "uncertain";
    }

    // Önce hafif servis ekranı (e-Tebligat), gösterge yoksa portal ana ekranı denenir.
    let status;
    try {
      status = await probe(S.SCREEN_URLS.tebligatMenu[0]);
    } catch (err) {
      status = "uncertain"; // ağ hatası oturum hakkında hüküm vermez
    }
    if (status === "uncertain") {
      try {
        status = await probe(S.URLS.home);
      } catch (err) {
        // ikinci deneme de açılamadı → uncertain kalır
      }
    }

    if (status === "alive") {
      const state = await context.storageState();
      // sessionStorage'ı da tazele (oturum token'ı orada; bkz. openContext).
      state.__sessionStorage = await captureSessionStorage(page);
      return { alive: true, storageState: state };
    }
    if (status === "loggedOut") return { alive: false, loggedOut: true };
    return { alive: false, uncertain: true };
  } finally {
    await closeQuietly(browser);
  }
}

// ---------------------------------------------------------------- dış API

/**
 * Yalnızca giriş testi. Belge çekmez. Kayıtlı oturum yoksa needsCaptchaLogin döner
 * (robot şifreyle kendi başına giriş DENEMEZ — captcha kuralı).
 * @returns {Promise<{ok:boolean, message:string, requiresManual?:boolean, needsCaptchaLogin?:boolean, diagnostic?:string}>}
 */
async function loginTest({ username, password }) {
  return runFlow(username, password, async ({ reusedSession }) => ({
    ok: true,
    message: reusedSession
      ? "GİB portalına bağlantı başarılı (kayıtlı oturum kullanıldı)."
      : "GİB portalına giriş başarılı.",
    items: [],
  }));
}

/**
 * "Adıma Düzenlenen Belgeler" ekranından tarih aralığındaki gelen belgeleri çeker.
 *
 * Faz 18: sessionKey verilirse (müşavir modu) o oturum kullanılır; targetTaxId
 * verilirse önce "mükellef değiştir/seç" akışı çalışır. Veri önceliği: ekran açılırken
 * biriken JSON yanıtları (esnek alan eşleyici) → bulunamazsa DOM tablosu (readTable).
 */
async function fetchDocuments({ username, password, from, to, sessionKey, targetTaxId }) {
  return runFlow(username, password, async ({ page, jsonResponses }) => {
    await openScreen(page, "documentsMenu");

    // 0) GERÇEK API (Faz 19 kalıbı): uç adresi bilinirse (API_ENDPOINTS.belgeListele)
    // liste doğrudan API'den çekilir; null olduğu sürece fetchDocumentsViaApi null
    // döner ve mevcut DOM/JSON-yakalama akışı AYNEN çalışır.
    let apiCaptured = null;
    let apiError = null;
    try {
      apiCaptured = await fetchDocumentsViaApi(page, { from, to });
      if (apiCaptured) {
        console.log(
          `[documents] API: ${apiCaptured.records.length} kayıt döndü; ` +
            `çekilen sayfalar: ${apiCaptured.pagesFetched.join(", ")}`
        );
      }
    } catch (err) {
      if (err && (err.requiresManual || err.needsCaptchaLogin || err.accessDenied)) throw err; // oturum düşmüş
      apiError = err;
      console.warn(
        `[documents] API çekimi başarısız — mevcut akışa dönülüyor: ${String(err && err.message).slice(0, 200)}`
      );
    }

    let rangeApplied = false;
    let captured = null;
    if (!apiCaptured) {
      // Ekranda tarih filtresi bulunamazsa çekim DURMAZ; liste okunup robot tarafında filtrelenir.
      rangeApplied = await tryApplyDateRange(page, from, to);

      // 1) JSON yakalama (asıl yol): ekran açılırken/sorgulanırken dönen yanıtlar taranır.
      // Liste XHR'ı sayfadan sonra geldiği için kısa süre beklenir.
      await waitForListJson(jsonResponses, "documents", 12000);
      captured = findListInResponses(jsonResponses, "documents");
    }
    let rows;
    let missingColumns = [];
    let empty;
    let viaJson = false;
    let viaApi = false;
    const recordSource = apiCaptured ? apiCaptured.records : captured ? captured.records : null;
    if (apiCaptured) viaApi = true;
    if (captured) viaJson = true;
    if (recordSource) {
      // Metin alanları String'e, tarih ISO'ya çevrilir; tutarlar parseAmount'a olduğu gibi gider.
      const str = (v) => (v === null || v === undefined ? "" : String(v));
      rows = recordSource.map((r) => ({
        uuid: str(r.uuid),
        documentNumber: str(r.documentNumber),
        issueDate: jsonDate(r.issueDate) || "",
        senderTaxId: str(r.senderTaxId),
        senderTitle: str(r.senderTitle),
        taxExclusiveAmount: r.taxExclusiveAmount,
        taxTotal: r.taxTotal,
        payableAmount: r.payableAmount,
        currency: str(r.currency),
        documentType: str(r.documentType),
        _links: [],
      }));
      empty = rows.length === 0;
    } else {
      // 2) Fallback: mevcut DOM tablosu okuma yöntemi (korunur).
      const table = await readTable(page, S.SELECTORS.documentColumns);
      rows = table.rows;
      missingColumns = table.missingColumns;
      empty = table.empty;
    }

    const fromDate = parseDate(from);
    const toDate = parseDate(to);

    const items = rows
      .map((row, i) => {
        const uuid = (row.uuid || "").trim();
        const documentNumber = (row.documentNumber || "").trim();
        // Kimliksiz kayıt yazılmaz; ikisi de yoksa satır atlanır.
        if (!uuid && !documentNumber) return null;

        const issueDate = parseDate(row.issueDate);
        // Ekranda filtre uygulanamadıysa aralık burada uygulanır.
        if (!rangeApplied && issueDate && fromDate && toDate) {
          if (issueDate < fromDate || issueDate > toDate) return null;
        }

        const taxExclusiveAmount = parseAmount(row.taxExclusiveAmount);
        const taxTotal = parseAmount(row.taxTotal);
        const payableAmount = parseAmount(row.payableAmount);

        return {
          // ETTN yoksa belge no + tarih birleşimi mükerrer anahtarı olur.
          uuid: uuid || `GIBPORTAL-${documentNumber}-${issueDate || i}`,
          documentNumber,
          issueDate,
          senderTaxId: parseTaxId(row.senderTaxId),
          senderTitle: (row.senderTitle || "").trim(),
          taxExclusiveAmount,
          taxTotal,
          payableAmount:
            payableAmount || Math.round((taxExclusiveAmount + taxTotal) * 100) / 100,
          currency: (row.currency || "TRY").trim() || "TRY",
          documentType: (row.documentType || "").trim(),
        };
      })
      .filter(Boolean);

    let message = empty
      ? "Seçilen tarih aralığında GİB portalında belge bulunamadı."
      : `${items.length} belge alındı.`;
    if (!rangeApplied) {
      message += " (Not: Ekranda tarih filtresi bulunamadı; liste robot tarafından filtrelendi.)";
    }
    if (missingColumns.length > 0) {
      message +=
        ` (Uyarı: GİB tablosunda şu sütunlar tanınamadı: ${missingColumns.join(", ")} —` +
        " ekran değişmiş olabilir, selectors.js içindeki documentColumns desenlerini güncelleyin.)";
    }
    if (viaApi) message += " (Kaynak: Belge API.)";
    else if (viaJson) message += " (Kaynak: JSON yanıt yakalama.)";
    if (apiError) {
      // API'nin neden başarısız olduğu (ham yanıtın ilk 500 karakteri dahil) yanıta konur.
      message += ` (Not: Belge API çekimi başarısız oldu, mevcut akış kullanıldı — ${apiError.message})`;
    }

    // Boş sonuçta ham JSON örneği + görülen /apigateway/ adresleri teşhise konur:
    // gerçek belge listeleme ucu buradan tespit edilip API_ENDPOINTS.belgeListele'ye yazılacak.
    const diagnostic =
      items.length === 0
        ? [
            jsonDiagnosticSample(jsonResponses, captured),
            apigatewayUrlList(jsonResponses),
            apiError ? `API hatası: ${apiError.message}` : null,
          ]
            .filter(Boolean)
            .join(" — ") || null
        : null;

    return { ok: true, message, items, ...(diagnostic ? { diagnostic } : {}) };
  }, { sessionKey, targetTaxId });
}

/**
 * "e-Tebligat" ekranından tarih aralığındaki tebligatları çeker.
 *
 * Faz 19 (canlı doğrulama 05.08.2026): veri artık GERÇEK API'den gelir
 * (fetchTebligatViaApi — tebligat-listele ucu). Ekran yalnız SPA'nın token'ı
 * sessionStorage'a koyması için FAST modda açılır (tablo/networkidle beklemesi YOK).
 * API başarılıysa DOM'a hiç girilmez. API 401 derse oturum düşmüştür (mevcut
 * captchaLoginRequiredError akışı); BAŞKA bir hata dönerse hata mesajı (ham yanıtın
 * ilk 500 karakteriyle) saklanır ve ancak o zaman eski DOM/JSON-yakalama fallback'i denenir.
 *
 * Faz 18: sessionKey (müşavir oturumu) + targetTaxId (önce mükellef seç) desteklenir.
 */
async function fetchTebligat({ username, password, from, to, sessionKey, targetTaxId }) {
  return runFlow(username, password, async ({ page, jsonResponses }) => {
    // 1) Hızlı yol: oturum doğrulamasından sonra doğrudan GİB API'sini dene. Çoğu
    // çekimde token zaten sessionStorage'dadır; ekran gezintisi tamamen atlanır.
    let apiError = null;
    try {
      const api = await fetchTebligatViaApi(page, { from, to });
      // Loglama: kayıt sayısı + çekilen sayfalar (token/şifre ASLA loglanmaz).
      console.log(
        `[tebligat] API: ${api.items.length} kayıt döndü (ham ${api.total}); ` +
          `çekilen sayfalar: ${api.pagesFetched.join(", ")}`
      );
      const message =
        api.items.length === 0
          ? "Seçilen tarih aralığında GİB portalında e-Tebligat bulunamadı. (Kaynak: e-Tebligat API.)"
          : `${api.items.length} tebligat alındı. (Kaynak: e-Tebligat API.)`;
      return { ok: true, message, items: api.items };
    } catch (err) {
      // Oturum düşmüş (401 / token yok) → mevcut captcha'lı giriş akışı devralır.
      if (err && (err.requiresManual || err.needsCaptchaLogin || err.accessDenied)) throw err;
      apiError = err;
      console.warn(
        `[tebligat] API çekimi başarısız — DOM fallback'i denenecek: ${String(err && err.message).slice(0, 200)}`
      );
    }

    // 2) Token/servis bu sayfada henüz hazırlanmadıysa tek kısa ekran açıp API'yi bir
    // kez daha dene. Başarıda DOM kazıması yapılmaz.
    await openScreen(page, "tebligatMenu", { fast: true });
    try {
      const api = await fetchTebligatViaApi(page, { from, to });
      console.log(
        `[tebligat] API (ekran sonrası): ${api.items.length} kayıt döndü (ham ${api.total}); ` +
          `çekilen sayfalar: ${api.pagesFetched.join(", ")}`
      );
      return {
        ok: true,
        message: api.items.length === 0
          ? "Seçilen tarih aralığında GİB portalında e-Tebligat bulunamadı. (Kaynak: e-Tebligat API.)"
          : `${api.items.length} tebligat alındı. (Kaynak: e-Tebligat API.)`,
        items: api.items,
      };
    } catch (err) {
      if (err && (err.requiresManual || err.needsCaptchaLogin || err.accessDenied)) throw err;
      apiError = err;
      console.warn(
        `[tebligat] API ikinci deneme başarısız — fallback: ${String(err && err.message).slice(0, 200)}`
      );
    }

    // 3) DOM/JSON-yakalama fallback'i (yalnız API iki kez başarısızsa).
    // Bazı GİB ekranlarında tarih filtresi yoktur → filtre yoksa tüm liste okunur.
    const rangeApplied = await tryApplyDateRange(page, from, to);

    // JSON yakalama: GİB ekranı listeyi XHR ile SONRADAN çekiyor → yanıt gelene
    // kadar kısa süre beklenir (canlı teşhis 05.08.2026: sayfada <table> yok).
    await waitForListJson(jsonResponses, "tebligat", 5000);
    const captured = findListInResponses(jsonResponses, "tebligat");
    let rows;
    let missingColumns = [];
    let empty;
    let viaJson = false;
    if (captured) {
      viaJson = true;
      const str = (v) => (v === null || v === undefined ? "" : String(v));
      rows = captured.records.map((r) => ({
        externalId: str(r.externalId),
        subject: str(r.subject),
        sentAt: jsonDate(r.sentAt) || "",
        readAt: jsonDate(r.readAt) || "",
        taxOffice: str(r.taxOffice),
        documentUrlOrText: "",
        _links: [],
      }));
      empty = rows.length === 0;
    } else {
      // 2) Fallback: mevcut DOM tablosu okuma yöntemi (korunur).
      const table = await readTable(page, S.SELECTORS.tebligatColumns);
      rows = table.rows;
      missingColumns = table.missingColumns;
      empty = table.empty;
    }

    const fromDate = parseDate(from);
    const toDate = parseDate(to);

    const items = rows
      .map((row, i) => {
        const sentAt = parseDate(row.sentAt);
        const externalId =
          (row.externalId || "").trim() ||
          `GIBTEB-${sentAt || "tarihsiz"}-${(row.subject || "").trim().slice(0, 40)}-${i}`;

        // Ekranda filtre uygulanamadıysa aralık burada uygulanır.
        if (!rangeApplied && sentAt && fromDate && toDate) {
          if (sentAt < fromDate || sentAt > toDate) return null;
        }

        const href = (row._links || []).find((h) => h && h.length > 0) || null;

        return {
          externalId,
          subject: (row.subject || "").trim() || "Konu belirtilmemiş tebligat",
          sentAt,
          readAt: parseDate(row.readAt),
          taxOffice: (row.taxOffice || "").trim() || null,
          // Belge bağlantısı varsa mutlak adres, yoksa hücre metni.
          documentUrlOrText: href
            ? new URL(href, S.URLS.login).toString()
            : (row.documentUrlOrText || "").trim() || null,
        };
      })
      .filter(Boolean);

    let message = empty
      ? "Seçilen tarih aralığında GİB portalında e-Tebligat bulunamadı."
      : `${items.length} tebligat alındı.`;
    if (!rangeApplied) {
      message += " (Not: Ekranda tarih filtresi bulunamadı; liste robot tarafından filtrelendi.)";
    }
    if (missingColumns.length > 0) {
      message +=
        ` (Uyarı: GİB tablosunda şu sütunlar tanınamadı: ${missingColumns.join(", ")} —` +
        " ekran değişmiş olabilir, selectors.js içindeki tebligatColumns desenlerini güncelleyin.)";
    }
    if (viaJson) message += " (Kaynak: JSON yanıt yakalama.)";
    // API'nin neden başarısız olduğu (ham yanıtın ilk 500 karakteri dahil) yanıta konur.
    if (apiError) {
      message += ` (Not: e-Tebligat API çekimi başarısız oldu, DOM fallback'i kullanıldı — ${apiError.message})`;
    }

    // Boş sonuçta ham JSON örneği teşhise konur (alan adlarını canlıda görmek için).
    const diagnostic =
      items.length === 0
        ? [jsonDiagnosticSample(jsonResponses, captured), apiError ? `API hatası: ${apiError.message}` : null]
            .filter(Boolean)
            .join(" — ") || null
        : null;

    return { ok: true, message, items, ...(diagnostic ? { diagnostic } : {}) };
  }, { sessionKey, targetTaxId });
}

/**
 * Faz 18 — MÜŞAVİR MÜKELLEF LİSTESİ: SMMM oturumuyla portaldaki mükellef listesi
 * ekranına gidilir ve dönen JSON yakalanır → [{taxId, title}]. JSON bulunamazsa DOM
 * tablosu fallback'i denenir; o da tutmazsa Türkçe hata + teşhis (sayfa metni ilk 500
 * + ekran görüntüsü yolu + ham JSON örneği) döner.
 */
async function fetchAdvisorClients({ sessionKey }) {
  return runFlow(null, null, async ({ page, jsonResponses }) => {
    await openAdvisorClientsScreen(page);

    const captured = findListInResponses(jsonResponses, "advisorClients");
    const seen = new Set();
    let items = [];

    const push = (taxRaw, titleRaw) => {
      const taxId = String(taxRaw ?? "").replace(/\D/g, "");
      const title = String(titleRaw ?? "").replace(/\s+/g, " ").trim();
      if ((taxId.length !== 10 && taxId.length !== 11) || !title) return;
      if (seen.has(taxId)) return;
      seen.add(taxId);
      items.push({ taxId, title });
    };

    if (captured) {
      for (const r of captured.records) push(r.taxId, r.title);
    }

    if (items.length === 0) {
      // DOM tablosu fallback'i (JSON'da uygun dizi yoksa).
      try {
        const { rows } = await readTable(page, {
          taxId: /VKN|TCKN|Vergi\s*Kimlik|Vergi\s*No/i,
          title: /Ünvan|Unvan|Ad\s*Soyad|Mükellef/i,
        });
        for (const row of rows) push(row.taxId, row.title);
      } catch (err) {
        // tablo da yok — aşağıda teşhisle dönülür
      }
    }

    if (items.length === 0) {
      const info = await captureDiagnostic(page, "advisor-clients-empty");
      return {
        ok: false,
        message:
          "Müşavir oturumunda mükellef listesi okunamadı. GİB hesabınıza bağlı mükellef " +
          "olmayabilir veya ekran/servis değişmiş olabilir; teşhis bilgisi sunucu günlüğündedir.",
        items: [],
        diagnostic:
          [formatDiagnostic(info), jsonDiagnosticSample(jsonResponses, captured)]
            .filter(Boolean)
            .join(" — ") || null,
      };
    }

    return { ok: true, message: `${items.length} mükellef bulundu.`, items };
  }, { sessionKey });
}

/**
 * Ortak akış sarmalayıcısı: kilit → oturum önbelleği → tarayıcı → oturum doğrulama →
 * iş → kapatma. Tüm hatalar Türkçe mesaj + `diagnostic` alanıyla normalize edilir.
 *
 * İNSAN DESTEKLİ MODEL: kayıtlı oturum yoksa TARAYICI HİÇ AÇILMADAN, geçersizse
 * tek denemeden sonra needsCaptchaLogin yanıtı döner (şifreyle otomatik giriş YOK —
 * GİB her girişte captcha istiyor ve robot captcha ÇÖZMEZ).
 */
async function runFlow(username, password, work, options = {}) {
  const sessionKey = options.sessionKey || null;
  const targetTaxId = options.targetTaxId || null;

  // Kimlik: sessionKey verildiyse (müşavir modu) şifre GEREKMEZ — çekim yalnız
  // kayıtlı oturumla yapılır; verilmediyse eski sözleşme (kullanıcı adı + şifre) geçerlidir.
  if (!sessionKey && (!username || !password)) {
    return {
      ok: false,
      message: "GİB kullanıcı kodu ve şifresi zorunludur.",
      items: [],
    };
  }

  // Oturum/kilit kimliği: advisor:<tenantId> (Faz 18) veya GERİYE UYUMLU kullanıcı adı.
  const identity = sessionKey || username;

  return session
    .withClientLock(identity, async () => {
      let cached = session.loadState(identity);
      if (!cached && username && password) {
        console.log(`[runFlow] Kayıtlı oturum yok, otomatik OCR girişi deneniyor — ${identity}`);
        const autoRes = await flowBudget.bounded(() => autoLogin({ username, password, sessionKey }), REQUEST_TIMEOUT_MS).catch(() => null);
        if (autoRes && autoRes.ok) {
          cached = session.loadState(identity);
        }
      }
      if (!cached) throw captchaLoginRequiredError();
      if ([...pendingLogins.values()].some(e => e.storageKey === identity)) {
        const err = new Error("Bu hesapta doğrulama işlemi sürüyor."); err.busy = true; throw err;
      }

      /** Tek deneme: tarayıcı aç → oturum doğrula → (mükellef seç) → iş → kapat. */
      async function attemptOnce(storageState) {
        const { browser, context } = await openContext(storageState);
        // Faz 18: sayfa gezinirken portalın JSON yanıtları biriktirilir (veri
        // önceliği JSON'dadır; bulunamazsa DOM tablosu fallback'i çalışır).
        const jsonResponses = attachJsonCollector(context);
        const page = await context.newPage();
        try {
          const { reusedSession } = await ensureSession(context, page, identity);
          // Müşavir oturumu + hedef mükellef: önce "mükellef değiştir/seç" akışı.
          if (targetTaxId) {
            await selectClient(page, targetTaxId);
          }
          return await work({ page, context, reusedSession, jsonResponses });
        } catch (err) {
          // Teşhis bilgisi (varsa) hataya iliştirilir; tarayıcı kapanmadan alınmalıdır.
          if (!err.details && !err.diagnosticInfo) {
            err.diagnosticInfo = await captureDiagnostic(page, "error");
          }
          throw err;
        } finally {
          await closeQuietly(browser);
        }
      }

      try {
        return await flowBudget.bounded(() => attemptOnce(cached), REQUEST_TIMEOUT_MS);
      } catch (err) {
        if (err && err.needsCaptchaLogin) {
          session.markExpired(identity);
          if (username && password) {
            console.log(`[runFlow] Oturum düşmüş, otomatik OCR ile yenilenip tekrar denenecek — ${identity}`);
            const autoRes = await flowBudget.bounded(() => autoLogin({ username, password, sessionKey }), REQUEST_TIMEOUT_MS).catch(() => null);
            if (autoRes && autoRes.ok) {
              const fresh = session.loadState(identity);
              if (fresh) {
                return await flowBudget.bounded(() => attemptOnce(fresh), REQUEST_TIMEOUT_MS);
              }
            }
          }
        }
        throw err;
      }
    })
    .catch((err) => toErrorResponse(err));
}

/** Hatayı sabit sözleşmeli JSON yanıta çevirir (şifre ASLA yer almaz). */
function toErrorResponse(err) {
  const info = err && (err.details || err.diagnosticInfo);
  const diagnostic = formatDiagnostic(info);

  if (err && err.requiresManual) {
    return {
      ok: false,
      requiresManual: true,
      // true → oturum yok/geçersiz; kullanıcı /login-begin akışıyla doğrulama kodu girmeli.
      needsCaptchaLogin: err.needsCaptchaLogin === true,
      message: err.message,
      items: [],
      diagnostic,
    };
  }

  if (err && err.screenChanged) {
    return {
      ok: false,
      message:
        err.message +
        " Sunucudaki gib-robot servisinin selectors.js dosyasındaki adayların güncellenmesi gerekebilir.",
      items: [],
      diagnostic:
        [diagnostic, err.details && err.details.tried ? `Denenen seçiciler: ${err.details.tried.join(" | ")}` : null]
          .filter(Boolean)
          .join(" — ") || null,
    };
  }

  if (err && err.busy) {
    return { ok: false, message: err.message, items: [], diagnostic: null };
  }

  if (err && err.timedOut) {
    return { ok: false, message: err.message, items: [], diagnostic };
  }

  const message =
    err && err.message
      ? `GİB portalından veri alınamadı: ${err.message}`
      : "GİB portalından veri alınamadı. Lütfen daha sonra tekrar deneyin.";

  return { ok: false, message, items: [], diagnostic };
}

// All automatic renewal requests share one account lock and one in-flight result.
const renewalFlights = new Map();
function coordinatedAutoLogin(args) {
  const identity = args.sessionKey || args.username;
  const key = session.sessionKey(identity);
  if (renewalFlights.has(key)) return renewalFlights.get(key);
  const task = session.withClientLock(identity, async () => {
    if ([...pendingLogins.values()].some(e => e.storageKey === identity))
      return {ok:false, requiresManual:true, needsCaptchaLogin:true, message:"Bu hesap için açık doğrulama işlemini tamamlayın."};
    const cached = session.loadState(identity);
    if (cached) {
      const status = await flowBudget.bounded(() => sessionHeartbeat(cached), 45000);
      if (status.alive) { session.saveState(identity, status.storageState); return {ok:true, alreadyLoggedIn:true}; }
      if (status.uncertain) return {ok:false, retryable:true, message:"GİB oturumunun durumu doğrulanamadı; daha sonra tekrar denenecek."};
      session.markExpired(identity);
    }
    return await flowBudget.bounded(() => autoLogin(args), 300000);
  }).catch(toErrorResponse).finally(()=>renewalFlights.delete(key));
  renewalFlights.set(key,task);
  return task;
}
async function coordinatedLoginBegin(args) {
  const identity=args.sessionKey||args.username;
  return session.withClientLock(identity,async()=>{
    for(const [id,e] of pendingLogins) if(e.storageKey===identity) await closePendingLogin(id);
    return loginBegin(args);
  }).catch(toErrorResponse);
}
async function coordinatedLoginComplete(args) {
  const entry=pendingLogins.get(args.pendingId);
  if(!entry) return loginComplete(args);
  return session.withClientLock(entry.storageKey,()=>loginComplete(args)).catch(toErrorResponse);
}

module.exports = {
  loginTest,
  loginBegin: coordinatedLoginBegin,
  loginComplete: coordinatedLoginComplete,
  autoLogin: coordinatedAutoLogin,
  fetchDocuments,
  fetchTebligat,
  fetchAdvisorClients,
  sessionHeartbeat,
  // testler / teşhis için dışa açılır
  parseAmount,
  parseDate,
  parseApiDate,
  parseTaxId,
  toTrDate,
  toIsoDate,
  formatDiagnostic,
};
