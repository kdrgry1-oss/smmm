"use strict";

/**
 * gib-robot — GİB Dijital Vergi Dairesi portal robotu (iç servis).
 *
 * Entegratör kullanmayan mükelleflerin gelen belgelerini ve e-Tebligatlarını,
 * mükellefin KENDİ kimliğiyle ve mali müşavirin yetkisi dahilinde çeker.
 *
 * AĞ: Bu servis DIŞARIYA AÇILMAZ. docker-compose'da port publish edilmez;
 * yalnız api servisiyle aynı iç ağda `http://gib-robot:8080` adresinden erişilir.
 * Kimlik doğrulama: `X-Robot-Token` başlığı = ROBOT_TOKEN ortam değişkeni.
 *
 * Uçlar (hepsi JSON):
 *   POST /login-test     {username, password}           → {ok, message, requiresManual?, needsCaptchaLogin?, diagnostic?}
 *   POST /login-begin    {username, password}           → {ok, pendingId, captchaImageBase64, expiresInSeconds, alreadyLoggedIn?, message}
 *   POST /login-complete {pendingId, code}              → {ok, message, captchaImageBase64?, attemptsLeft?, pendingExpired?}
 *   POST /documents      {username?, password?, from, to, sessionKey?, targetTaxId?} → {ok, items[], message, diagnostic?}
 *   POST /tebligat       {username?, password?, from, to, sessionKey?, targetTaxId?} → {ok, items[], message, diagnostic?}
 *   GET  /health                                        → {ok:true} (token gerektirmez)
 *
 * Faz 18 — MÜŞAVİR (SMMM) TEK GİRİŞ uçları:
 *   POST /advisor/login-begin    {username, password, sessionKey} → login-begin ile aynı;
 *                                oturum sessionKey ("advisor:<tenantId>") altına kaydedilir.
 *   POST /advisor/login-complete {pendingId, code}                → login-complete ile aynı.
 *   POST /advisor/clients        {sessionKey}                     → müşavire bağlı mükellefler
 *                                [{taxId, title}] (JSON yanıt yakalama ile).
 *   POST /advisor/session-status {sessionKey}                     → {ok, exists, expired, lastSeenAt}
 *                                (tarayıcı AÇILMAZ — yalnız diskteki oturum dosyası okunur).
 * /documents ve /tebligat sessionKey verilirse O oturumu kullanır (şifre gerekmez);
 * targetTaxId verilirse önce müşavir panelinde "mükellef değiştir/seç" akışı çalışır.
 *
 * İNSAN DESTEKLİ GİRİŞ (04.08.2026): GİB giriş ekranı her girişte "Doğrulama Kodu"
 * (captcha) istiyor. Robot kodu ÇÖZMEZ; /login-begin captcha görselini döndürür,
 * kullanıcı (mali müşavir) kodu okuyup /login-complete ile girer. Başarılı girişte
 * oturum (storageState) kaydedilir; /documents ve /tebligat bu KAYITLI OTURUMLA
 * çalışır, oturum yoksa {ok:false, requiresManual:true, needsCaptchaLogin:true} döner.
 *
 * GİZLİLİK: Kullanıcı adı, şifre ve doğrulama kodu HİÇBİR log satırına yazılmaz.
 * Loglarda yalnızca mükellef anahtarının kısaltılmış SHA-256 özeti ve pendingId'nin
 * ilk 8 karakteri görünür.
 */

const express = require("express");
const gib = require("./gib");
const session = require("./session");
const keepalive = require("./keepalive");

const PORT = Number(process.env.PORT || 8080);
const ROBOT_TOKEN = process.env.ROBOT_TOKEN || "";

const app = express();
app.disable("x-powered-by");
app.use(express.json({ limit: "256kb" }));

/** Loglarda mükellefi tanımak için kısa, geri döndürülemez etiket. */
function tag(username) {
  if (!username) return "?";
  return session.sessionKey(username).slice(0, 8);
}

/** Sağlık ucu — token gerektirmez (compose healthcheck kullanır). */
app.get("/health", (req, res) => {
  res.json({ ok: true, service: "gib-robot", sessionDir: session.SESSION_DIR });
});

/** Paylaşılan sır doğrulaması (sabit süreli karşılaştırma değil; sır tek ve uzundur). */
app.use((req, res, next) => {
  if (!ROBOT_TOKEN) {
    return res.status(500).json({
      ok: false,
      message:
        "Robot servisi yapılandırılmamış: ROBOT_TOKEN ortam değişkeni tanımlı değil. " +
        "deploy/.env dosyasına ROBOT_TOKEN ekleyip servisleri yeniden başlatın.",
      items: [],
    });
  }

  const token = req.get("X-Robot-Token");
  if (token !== ROBOT_TOKEN) {
    return res.status(401).json({
      ok: false,
      message: "Robot servisine erişim reddedildi (geçersiz X-Robot-Token).",
      items: [],
    });
  }

  return next();
});

/**
 * İstek gövdesi doğrulaması: kimlik + (istenirse) tarih aralığı.
 * Faz 18: sessionKey verilirse (müşavir modu) kullanıcı adı/şifre GEREKMEZ — çekim
 * kayıtlı oturumla yapılır. targetTaxId verilirse önce mükellef seçimi çalışır.
 */
function readBody(req, requireRange) {
  const body = req.body || {};
  const username = typeof body.username === "string" ? body.username.trim() : "";
  const password = typeof body.password === "string" ? body.password : "";
  const sessionKey = typeof body.sessionKey === "string" ? body.sessionKey.trim() : "";
  const targetTaxId = typeof body.targetTaxId === "string" ? body.targetTaxId.trim() : "";

  if (sessionKey.length > 200) {
    return { error: "sessionKey geçersiz (en fazla 200 karakter)." };
  }
  if (targetTaxId && !/^\d{10,11}$/.test(targetTaxId.replace(/\D/g, ""))) {
    return { error: "targetTaxId geçersiz (10-11 haneli VKN/TCKN bekleniyor)." };
  }

  if (!sessionKey && (!username || !password)) {
    return { error: "GİB kullanıcı kodu / TCKN ve şifre zorunludur." };
  }

  const identity = {
    username,
    password,
    sessionKey: sessionKey || undefined,
    targetTaxId: targetTaxId || undefined,
  };

  if (!requireRange) return identity;

  const from = typeof body.from === "string" ? body.from.trim() : "";
  const to = typeof body.to === "string" ? body.to.trim() : "";
  if (!from || !to) {
    return { error: "Tarih aralığı zorunludur (from, to)." };
  }
  if (isNaN(new Date(from).getTime()) || isNaN(new Date(to).getTime())) {
    return { error: "Tarih aralığı geçersiz. Beklenen biçim: yyyy-aa-gg." };
  }
  if (new Date(from) > new Date(to)) {
    return { error: "Başlangıç tarihi bitiş tarihinden sonra olamaz." };
  }

  return { ...identity, from, to };
}

/** Tüm uçlar için ortak sarmalayıcı: doğrulama + log + hata yalıtımı. */
function handler(name, requireRange, run) {
  return async (req, res) => {
    const parsed = readBody(req, requireRange);
    if (parsed.error) {
      return res.status(400).json({ ok: false, message: parsed.error, items: [] });
    }

    // Faz 18: müşavir modunda etiket oturum anahtarından üretilir (şifre/VKN loglanmaz).
    const label = tag(parsed.sessionKey || parsed.username);
    const started = Date.now();
    console.log(`[${name}] başladı — mükellef:${label}${requireRange ? ` aralık:${parsed.from}..${parsed.to}` : ""}`);

    try {
      const result = await run(parsed);
      const ms = Date.now() - started;
      console.log(
        `[${name}] bitti — mükellef:${label} ok:${result.ok} kayıt:${(result.items || []).length} süre:${ms}ms` +
          (result.requiresManual ? " ELLE-DOĞRULAMA" : "")
      );
      if (!result.ok && result.diagnostic) {
        console.warn(`[${name}] teşhis — mükellef:${label} ${result.diagnostic}`);
      }
      // İş hatası da 200 ile döner; sözleşme `ok` alanıdır (backend bunu bekler).
      return res.json({ items: [], ...result });
    } catch (err) {
      // Buraya normalde düşülmez (gib.js hataları normalize eder) — son savunma hattı.
      console.error(`[${name}] beklenmeyen hata — mükellef:${label}`, err && err.message);
      return res.status(500).json({
        ok: false,
        message: "Robot serviste beklenmeyen bir hata oluştu. Sunucu günlüklerini kontrol edin.",
        items: [],
        diagnostic: err && err.message ? String(err.message).slice(0, 500) : null,
      });
    }
  };
}

app.post("/login-test", handler("login-test", false, (p) => gib.loginTest(p)));
app.post("/tebligat-document", async (req, res) => {
  const result = await require("./tebligat-document").fetchDocument(req.body || {});
  res.json(result);
});

app.post("/documents", handler("documents", true, (p) => require("./dvd-incoming").fetchIncoming(p)));
app.post("/tebligat", handler("tebligat", true, (p) => gib.fetchTebligat(p)));

/**
 * İNSAN DESTEKLİ GİRİŞ — 1. aşama: tarayıcı açılır, kimlik doldurulur, captcha
 * görseli döner; sayfa bellekte bekletilir (TTL 5 dk). Şifre/kod LOGLANMAZ.
 */
app.post("/login-begin", async (req, res) => {
  const parsed = readBody(req, false);
  if (parsed.error) {
    return res.status(400).json({ ok: false, message: parsed.error });
  }

  const label = tag(parsed.username);
  const started = Date.now();
  console.log(`[login-begin] başladı — mükellef:${label}`);

  try {
    const result = await gib.loginBegin(parsed);
    console.log(
      `[login-begin] bitti — mükellef:${label} ok:${result.ok}` +
        (result.alreadyLoggedIn ? " OTURUM-GEÇERLİ" : "") +
        (result.pendingId ? ` pending:${result.pendingId.slice(0, 8)}` : "") +
        ` süre:${Date.now() - started}ms`
    );
    return res.json(result);
  } catch (err) {
    console.error(`[login-begin] beklenmeyen hata — mükellef:${label}`, err && err.message);
    return res.status(500).json({
      ok: false,
      message: "Giriş başlatılamadı. Robot servis günlüklerini kontrol edin.",
      diagnostic: err && err.message ? String(err.message).slice(0, 500) : null,
    });
  }
});

/**
 * İNSAN DESTEKLİ GİRİŞ — 2. aşama: kullanıcının okuduğu doğrulama kodu girilir.
 * Başarıda oturum kaydedilir; hatalı kodda YENİ görselle aynı pendingId sürer
 * (en fazla 3 deneme).
 */
const loginCompleteHandler = async (req, res) => {
  const body = req.body || {};
  const pendingId = typeof body.pendingId === "string" ? body.pendingId.trim() : "";
  const code = typeof body.code === "string" ? body.code.trim() : "";

  if (!pendingId || !code) {
    return res.status(400).json({ ok: false, message: "pendingId ve doğrulama kodu zorunludur." });
  }
  if (code.length > 16) {
    return res.status(400).json({ ok: false, message: "Doğrulama kodu en fazla 16 karakter olabilir." });
  }

  const shortId = pendingId.slice(0, 8);
  const started = Date.now();
  console.log(`[login-complete] başladı — pending:${shortId}`);

  try {
    const result = await gib.loginComplete({ pendingId, code });
    console.log(
      `[login-complete] bitti — pending:${shortId} ok:${result.ok}` +
        (result.pendingExpired ? " İPTAL" : "") +
        (typeof result.attemptsLeft === "number" ? ` kalanDeneme:${result.attemptsLeft}` : "") +
        ` süre:${Date.now() - started}ms`
    );
    return res.json(result);
  } catch (err) {
    console.error(`[login-complete] beklenmeyen hata — pending:${shortId}`, err && err.message);
    return res.status(500).json({
      ok: false,
      message: "Giriş tamamlanamadı. Robot servis günlüklerini kontrol edin.",
      diagnostic: err && err.message ? String(err.message).slice(0, 500) : null,
    });
  }
};

app.post("/login-complete", loginCompleteHandler);

// ---------------------------------------------------------------- Faz 18: müşavir (SMMM) uçları

/**
 * MÜŞAVİR GİRİŞİ — 1. aşama. /login-begin ile aynı akış; tek fark oturumun
 * sessionKey ("advisor:<tenantId>") altına kaydedilmesidir. Şifre LOGLANMAZ.
 */
app.post("/advisor/login-begin", async (req, res) => {
  const body = req.body || {};
  const username = typeof body.username === "string" ? body.username.trim() : "";
  const password = typeof body.password === "string" ? body.password : "";
  const sessionKey = typeof body.sessionKey === "string" ? body.sessionKey.trim() : "";

  if (!username || !password || !sessionKey) {
    return res.status(400).json({
      ok: false,
      message: "Müşavir GİB kullanıcı kodu, şifre ve sessionKey zorunludur.",
    });
  }
  if (sessionKey.length > 200) {
    return res.status(400).json({ ok: false, message: "sessionKey geçersiz." });
  }

  const label = tag(sessionKey);
  const started = Date.now();
  console.log(`[advisor/login-begin] başladı — müşavir:${label}`);

  try {
    const result = await gib.loginBegin({ username, password, sessionKey });
    console.log(
      `[advisor/login-begin] bitti — müşavir:${label} ok:${result.ok}` +
        (result.alreadyLoggedIn ? " OTURUM-GEÇERLİ" : "") +
        (result.pendingId ? ` pending:${result.pendingId.slice(0, 8)}` : "") +
        ` süre:${Date.now() - started}ms`
    );
    return res.json(result);
  } catch (err) {
    console.error(`[advisor/login-begin] beklenmeyen hata — müşavir:${label}`, err && err.message);
    return res.status(500).json({
      ok: false,
      message: "Müşavir girişi başlatılamadı. Robot servis günlüklerini kontrol edin.",
      diagnostic: err && err.message ? String(err.message).slice(0, 500) : null,
    });
  }
});

/**
 * MÜŞAVİR GİRİŞİ — 2. aşama: /login-complete ile birebir aynı davranış (pendingId
 * girişte hangi anahtar altında başlatıldıysa oturum oraya kaydedilir).
 */
app.post("/advisor/login-complete", loginCompleteHandler);

/**
 * OTOMATİK GİRİŞ (Auto-Login with OCR):
 * GİB giriş ekranını açar, şifreyi girer, captcha'yı otomatik çözer ve oturumu açar.
 */
app.post("/auto-login", handler("auto-login", false, (p) => gib.autoLogin(p)));
app.post("/advisor/auto-login", handler("advisor/auto-login", false, (p) => gib.autoLogin(p)));

/**
 * MÜŞAVİRE BAĞLI MÜKELLEF LİSTESİ: müşavir oturumuyla portaldaki mükellef listesi
 * ekranı açılır ve dönen JSON yakalanır → [{taxId, title}]. Şifre GEREKMEZ; oturum
 * yoksa needsCaptchaLogin bayraklı yanıt döner.
 */
app.post("/advisor/clients", async (req, res) => {
  const body = req.body || {};
  const sessionKey = typeof body.sessionKey === "string" ? body.sessionKey.trim() : "";
  if (!sessionKey || sessionKey.length > 200) {
    return res.status(400).json({ ok: false, message: "sessionKey zorunludur.", items: [] });
  }

  const label = tag(sessionKey);
  const started = Date.now();
  console.log(`[advisor/clients] başladı — müşavir:${label}`);

  try {
    const result = await gib.fetchAdvisorClients({ sessionKey });
    console.log(
      `[advisor/clients] bitti — müşavir:${label} ok:${result.ok} ` +
        `kayıt:${(result.items || []).length} süre:${Date.now() - started}ms`
    );
    if (!result.ok && result.diagnostic) {
      console.warn(`[advisor/clients] teşhis — müşavir:${label} ${result.diagnostic}`);
    }
    return res.json({ items: [], ...result });
  } catch (err) {
    console.error(`[advisor/clients] beklenmeyen hata — müşavir:${label}`, err && err.message);
    return res.status(500).json({
      ok: false,
      message: "Mükellef listesi alınamadı. Robot servis günlüklerini kontrol edin.",
      items: [],
      diagnostic: err && err.message ? String(err.message).slice(0, 500) : null,
    });
  }
});

/**
 * OTURUM DURUMU (rozet için): tarayıcı AÇILMAZ; yalnız diskteki oturum dosyası okunur.
 *   exists=false            → hiç giriş yapılmamış.
 *   expired=true            → keepalive/çekim oturumun düştüğünü işaretlemiş (yenilenmeli).
 *   lastSeenAt              → son başarılı hareket (ISO) — canlı oturumda dosya mtime'ı.
 */
app.post("/advisor/session-status", (req, res) => {
  const body = req.body || {};
  const sessionKey = typeof body.sessionKey === "string" ? body.sessionKey.trim() : "";
  if (!sessionKey || sessionKey.length > 200) {
    return res.status(400).json({ ok: false, message: "sessionKey zorunludur." });
  }

  const raw = session.readRawByKey(session.sessionKey(sessionKey));
  if (!raw) {
    return res.json({ ok: true, exists: false, expired: false, lastSeenAt: null });
  }

  const expired = Boolean(raw.parsed.expiredAt);
  const tooOld = !expired && Date.now() - raw.mtimeMs > session.SESSION_TTL_HOURS * 3600 * 1000;
  return res.json({
    ok: true,
    exists: true,
    expired: expired || tooOld,
    lastSeenAt: expired
      ? raw.parsed.lastSeenAt || null
      : new Date(raw.mtimeMs).toISOString(),
  });
});

// Bilinmeyen uçlar
app.use((req, res) => {
  res.status(404).json({ ok: false, message: "Bilinmeyen uç.", items: [] });
});

// Express hata yakalayıcı (ör. bozuk JSON gövdesi)
app.use((err, req, res, _next) => {
  console.error("[server] istek hatası:", err && err.message);
  res.status(400).json({ ok: false, message: "İstek gövdesi okunamadı (geçersiz JSON).", items: [] });
});

const server = app.listen(PORT, "0.0.0.0", () => {
  console.log(
    `gib-robot ${PORT} portunda dinliyor. Oturum dizini: ${session.SESSION_DIR}. ` +
      `Token ${ROBOT_TOKEN ? "tanımlı" : "TANIMSIZ (istekler reddedilecek)"}.`
  );

  // OTURUMU CANLI TUTMA: kayıtlı oturumlar periyodik olarak yoklanır (varsayılan 8 dk).
  // Amaç, kullanıcının sürekli doğrulama kodu girmesini önlemek. GIB_KEEPALIVE_MINUTES=0
  // ile kapatılabilir. Captcha ÇÖZÜLMEZ, şifre KULLANILMAZ (yalnız kayıtlı çerezler).
  keepalive.start();
});

// Konteyner durdurulurken açık istekler tamamlanır.
function shutdown(signal) {
  console.log(`gib-robot ${signal} aldı, kapanıyor...`);
  keepalive.stop();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 15000).unref();
}
process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("SIGINT", () => shutdown("SIGINT"));
