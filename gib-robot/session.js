"use strict";

/**
 * Oturum önbelleği + mükellef başına eşzamanlılık kilidi.
 *
 * OTURUM ÖNBELLEĞİ: Başarılı girişten sonra Playwright `storageState`
 * (çerezler + localStorage) mükellef anahtarının SHA-256 özetiyle
 * /data/sessions/{hash}.json dosyasına yazılır. Sonraki isteklerde önce bu
 * state ile denenir; geçersizse (GİB oturumu düşürmüşse) yeniden giriş yapılır.
 * Böylece her çekimde şifre kullanılmaz ve GİB'e giden giriş sayısı düşer.
 *
 * GÜVENLİK: Dosya adı yalnızca özettir (kullanıcı adı dosya adında GEÇMEZ),
 * içerik yalnız oturum çerezleridir — şifre HİÇBİR ZAMAN diske yazılmaz.
 * Dosyalar 0600 izniyle oluşturulur.
 */

const crypto = require("crypto");
const fs = require("fs");
const path = require("path");

const SESSION_DIR = process.env.SESSION_DIR || "/data/sessions";

/**
 * Oturum dosyası bu süreden eskiyse hiç denenmez (GİB oturumları kısa ömürlüdür).
 * Süre saat cinsinden `GIB_SESSION_TTL_HOURS` ortam değişkeniyle ayarlanır
 * (varsayılan 12; geçersiz/negatif değerde 12'ye düşülür).
 */
const SESSION_TTL_HOURS = (() => {
  const parsed = Number(process.env.GIB_SESSION_TTL_HOURS);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 12;
})();
const SESSION_MAX_AGE_MS = SESSION_TTL_HOURS * 60 * 60 * 1000;

/**
 * OTURUM KİMLİĞİ ANAHTARLAMASI (Faz 18 — müşavir tek giriş):
 * Girdi iki türden biri olabilir; ikisi de aynı yoldan SHA-256 ile özetlenir:
 *   - "eskiKullaniciAdi"        → mükellefin kendi GİB kullanıcı kodu (GERİYE UYUMLU:
 *                                  eski oturum dosyaları aynı özeti üretir, korunur).
 *   - "advisor:<tenantId>"      → mali müşavirin (SMMM) TENANT düzeyindeki tek oturumu.
 *     (İsteğe bağlı "client:<username>" biçimi de geçerlidir — düz metin anahtar gibi
 *      özetlenir; mevcut dosyalarla çakışmaz.)
 * Dosya adı yalnız özettir; hangi türden olduğu diske YANSIMAZ.
 */
function sessionKey(username) {
  return crypto.createHash("sha256").update(String(username || "").trim()).digest("hex");
}

/** Müşavir (SMMM) oturum anahtarı üretici — backend ile aynı biçim. */
function advisorKey(tenantId) {
  return `advisor:${String(tenantId || "").trim()}`;
}

function sessionPath(username) {
  return path.join(SESSION_DIR, `${sessionKey(username)}.json`);
}

/** Anahtar (özet) üzerinden dosya yolu — keepalive döngüsü kullanıcı adını BİLMEZ. */
function sessionPathByKey(key) {
  return path.join(SESSION_DIR, `${String(key)}.json`);
}

function ensureDir() {
  try {
    fs.mkdirSync(SESSION_DIR, { recursive: true, mode: 0o700 });
    return true;
  } catch (err) {
    // Volume bağlanmamışsa önbellek devre dışı kalır; akış her seferinde giriş yapar.
    console.warn(`[session] Oturum dizini oluşturulamadı (${SESSION_DIR}): ${err.message}`);
    return false;
  }
}

/**
 * Kayıtlı storageState'i döndürür; yoksa/eskiyse/bozuksa/EXPIRED işaretliyse null.
 *
 * TTL KAYAN PENCEREDİR: dosya her başarılı kullanım/heartbeat'te yeniden yazıldığı
 * için mtime tazelenir — "12 saat" SON başarılı hareketten itibaren sayılır.
 */
function loadState(username) {
  return loadStateByKey(sessionKey(username));
}

/** loadState'in anahtar (özet) tabanlı hali — TTL ve expired denetimi AYNIDIR. */
function loadStateByKey(key) {
  const raw = readRawByKey(key);
  if (!raw) return null;
  if (!raw.parsed.__sessionStorage?.token) return null;
  if (raw.parsed.expiredAt) return null; // heartbeat "düştü" demiş → yeniden giriş şart
  if (Date.now() - raw.mtimeMs > SESSION_MAX_AGE_MS) return null;
  return raw.parsed;
}

/**
 * Dosyayı TTL denetimi YAPMADAN okur (keepalive için: eski görünse bile canlılık
 * tarayıcıyla sınanır; sonuç heartbeat'e göre tazelenir ya da expired işaretlenir).
 * @returns {{parsed: object, mtimeMs: number}|null}
 */
function readRawByKey(key) {
  const file = sessionPathByKey(key);
  try {
    const stat = fs.statSync(file);
    const parsed = JSON.parse(fs.readFileSync(file, "utf8"));
    if (!parsed || typeof parsed !== "object") return null;
    return { parsed, mtimeMs: stat.mtimeMs };
  } catch (err) {
    return null;
  }
}

/** storageState'i diske yazar (hata durumunda sessizce geçilir — akış bozulmaz). */
function saveState(username, state) {
  saveStateByKey(sessionKey(username), state);
}

/** saveState'in anahtar tabanlı hali. Yazım mtime'ı tazeler → TTL penceresi ilerler. */
function saveStateByKey(key, state) {
  // Never replace a usable saved session with an incomplete SPA snapshot.
  if (!state?.__sessionStorage?.token) return;
  if (!ensureDir()) return;
  const file = sessionPathByKey(key);
  try {
    fs.writeFileSync(file, JSON.stringify(state), { mode: 0o600 });
  } catch (err) {
    console.warn(`[session] Oturum durumu yazılamadı: ${err.message}`);
  }
}

/** Geçersiz oturumu siler (bir sonraki istek şifreyle giriş yapar). */
function clearState(username) {
  try {
    fs.unlinkSync(sessionPath(username));
  } catch (err) {
    // dosya zaten yok
  }
}

/**
 * Oturumu "düşmüş" olarak İŞARETLER (dosya SİLİNMEZ — panel "yenilenmeli" diyebilsin).
 * Çerezler artık işe yaramadığı için içerik yalnız {expiredAt, lastSeenAt} olarak yazılır;
 * lastSeenAt = son başarılı hareketin zamanı (önceki mtime) korunur.
 */
function markExpiredByKey(key) {
  if (!ensureDir()) return;
  const raw = readRawByKey(key);
  const lastSeenAt = raw
    ? raw.parsed.lastSeenAt || new Date(raw.mtimeMs).toISOString()
    : null;
  try {
    fs.writeFileSync(
      sessionPathByKey(key),
      JSON.stringify({ expiredAt: new Date().toISOString(), lastSeenAt }),
      { mode: 0o600 }
    );
  } catch (err) {
    console.warn(`[session] Oturum expired işaretlenemedi: ${err.message}`);
  }
}

/**
 * Diskteki tüm oturumların özeti (GET /sessions ucu + keepalive turu kullanır).
 * @returns {{clientKey: string, lastSeenAt: string|null, expired: boolean}[]}
 */
function listSessions() {
  let files;
  try {
    files = fs.readdirSync(SESSION_DIR).filter((f) => /^[0-9a-f]{64}\.json$/.test(f));
  } catch (err) {
    return []; // dizin yoksa oturum da yoktur
  }

  const result = [];
  for (const file of files) {
    const key = file.slice(0, -5);
    const raw = readRawByKey(key);
    if (!raw) continue; // bozuk dosya listeye girmez
    const expired = Boolean(raw.parsed.expiredAt);
    result.push({
      clientKey: key,
      lastSeenAt: expired
        ? raw.parsed.lastSeenAt || null
        : new Date(raw.mtimeMs).toISOString(),
      expired,
    });
  }
  return result;
}

// ---------------- Mükellef başına tek oturum (basit bellek içi kilit) ----------------
//
// Aynı mükellef için EŞZAMANLI iki tarayıcı oturumu açılmaz: GİB tarafında
// oturum çakışmasına ve gereksiz yüke yol açar. Kilit bekleyen istek sıraya
// girer; LOCK_TIMEOUT_MS içinde sıra gelmezse Türkçe hata döner.

const locks = new Map(); // key -> Promise zinciri
const LOCK_TIMEOUT_MS = Number(process.env.LOCK_TIMEOUT_MS || 120000);

/**
 * fn'i aynı mükellef için sıraya alarak çalıştırır.
 * @param {string} username
 * @param {() => Promise<any>} fn
 */
function withClientLock(username, fn) {
  return withKeyLock(sessionKey(username), fn);
}

/** Kilit şu an meşgul mü? (bekleme YAPMAZ — keepalive "meşgulse atla" için). */
function isKeyBusy(key) {
  return locks.has(key);
}

/**
 * Kilit MEŞGULSE hiç beklemeden atlar (keepalive turu çekim işini bekletmez);
 * boşsa fn'i kilit altında çalıştırır.
 * @returns {Promise<{skipped: true} | {skipped: false, result: any}>}
 */
async function tryWithKeyLock(key, fn) {
  if (isKeyBusy(key)) return { skipped: true };
  const result = await withKeyLock(key, fn);
  return { skipped: false, result };
}

/** withClientLock'un anahtar (özet) tabanlı çekirdeği. */
async function withKeyLock(key, fn) {
  const previous = locks.get(key) || Promise.resolve();

  let release;
  const current = new Promise((resolve) => {
    release = resolve;
  });
  // Zincir: sıradaki istek, bu isteğin bitmesini bekler.
  const chain = previous.then(() => current);
  locks.set(key, chain);

  // Sıranın gelmesini bekle (zaman aşımlı).
  let timer;
  const waited = await Promise.race([
    previous.then(() => "ok"),
    new Promise((resolve) => {
      timer = setTimeout(() => resolve("timeout"), LOCK_TIMEOUT_MS);
    }),
  ]);
  clearTimeout(timer);

  if (waited === "timeout") {
    release(); // zinciri tıkama: arkadakiler ilerleyebilsin
    chain.finally(() => { if (locks.get(key) === chain) locks.delete(key); });
    const err = new Error(
      "Bu mükellef için başka bir çekim işlemi sürüyor. Lütfen birkaç dakika sonra tekrar deneyin."
    );
    err.busy = true;
    throw err;
  }

  try {
    return await fn();
  } finally {
    release();
    // Zincirin sonundaysak haritadan düş (bellek sızıntısı olmasın).
    if (locks.get(key) === chain) locks.delete(key);
  }
}

/** Kullanıcı adı tabanlı expired işaretleyici (gib.js akışları kullanır). */
function markExpired(username) {
  markExpiredByKey(sessionKey(username));
}

module.exports = {
  SESSION_DIR,
  SESSION_TTL_HOURS,
  sessionKey,
  advisorKey,
  sessionPath,
  readRawByKey,
  loadState,
  loadStateByKey,
  saveState,
  saveStateByKey,
  clearState,
  markExpired,
  markExpiredByKey,
  listSessions,
  withClientLock,
  withKeyLock,
  tryWithKeyLock,
  isKeyBusy,
};
