"use strict";

/**
 * OTURUMU CANLI TUTMA döngüsü (keepalive / heartbeat).
 *
 * GİB portalı hareketsiz oturumu kısa sürede düşürür. Bu döngü, diskte kayıtlı HER
 * oturum için periyodik olarak (GIB_KEEPALIVE_MINUTES, varsayılan 8 dk) hafif bir
 * "hareket" yapar: storageState yüklenir, giriş gerektiren hafif bir ekran açılır,
 * giriş göstergesi görünüyorsa oturum canlı sayılır ve storageState YENİDEN yazılır
 * (çerez süresi tazelenir + TTL kayan penceresi ilerler). Giriş ekranına düşülürse
 * dosya SİLİNMEZ; {expiredAt} işareti yazılır ki panel "yenilenmeli" gösterebilsin.
 *
 * NAZİKLİK KURALLARI:
 *  - Mükellefler arası 2-5 sn rastgele bekleme.
 *  - En fazla MAX_CONCURRENT (3) eşzamanlı tarayıcı.
 *  - Aynı mükellefte çekim işi sürüyorsa (kilit meşgul) o tur ATLANIR — beklenmez.
 *
 * GÜVENLİK: Bu döngü şifre KULLANMAZ (yalnız kayıtlı çerezler), captcha ÇÖZMEZ ve
 * hiçbir kimlik bilgisi loglamaz (loglarda yalnız anahtar özetinin ilk 8 karakteri).
 *
 * Süreç yeniden başlarsa oturumlar diskte (/data/sessions volume) olduğu için
 * döngü kaldığı yerden devam eder.
 *
 * Faz 18 — MÜŞAVİR OTURUMU: advisor:<tenantId> anahtarıyla kaydedilen SMMM oturumları
 * da diskte aynı biçimde durur; bu döngü onları AYIRT ETMEDEN yoklar. Tek müşavir
 * oturumu canlı kaldığı sürece o büronun TÜM mükellef çekimleri için yeterlidir.
 */

const gib = require("./gib");
const session = require("./session");

/** Tur aralığı (dakika). 0 veya negatif → keepalive KAPALI. */
const KEEPALIVE_MINUTES = (() => {
  const parsed = Number(process.env.GIB_KEEPALIVE_MINUTES);
  if (process.env.GIB_KEEPALIVE_MINUTES !== undefined && Number.isFinite(parsed)) {
    return parsed;
  }
  return 8;
})();

/** Aynı anda en fazla kaç oturum yoklanır (nazik davranış — üst sınır 3). */
const MAX_CONCURRENT = 3;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Mükellefler arası nazik bekleme: 2-5 sn. */
const politeGap = () => sleep(2000 + Math.floor(Math.random() * 3000));

/** Loglanabilir kısa etiket (geri döndürülemez — anahtar zaten SHA-256 özetidir). */
const shortKey = (key) => String(key).slice(0, 8);

let running = false;
let stopped = false;

/**
 * Tek oturumu yoklar. Kilit meşgulse (çekim sürüyor) beklemeden atlar.
 * @returns {"alive"|"expired"|"skipped"|"uncertain"|"error"}
 */
async function heartbeatOne(clientKey) {
  const outcome = await session.tryWithKeyLock(clientKey, async () => {
    // Kilit alındıktan sonra dosya yeniden okunur (bu arada değişmiş olabilir).
    const state = session.loadStateByKeyRaw
      ? session.loadStateByKeyRaw(clientKey)
      : null;
    const effective = state || readForHeartbeat(clientKey);
    if (!effective) return "skipped"; // dosya silinmiş/expired işaretli

    const result = await gib.sessionHeartbeat(effective);

    if (result.alive) {
      // Başarılı hareket → state yeniden yazılır: çerezler tazelenir, mtime ilerler
      // (TTL "son başarılı hareketten itibaren" sayılır — kayan pencere).
      session.saveStateByKey(clientKey, result.storageState);
      return "alive";
    }
    if (result.loggedOut) {
      session.markExpiredByKey(clientKey);
      return "expired";
    }
    return "uncertain"; // portal ekranı okunamadı — karar verilmedi, işaret yok
  });

  if (outcome.skipped) return "skipped";
  return outcome.result;
}

/**
 * Heartbeat için dosyayı TTL denetimi YAPMADAN okur: eski görünen oturum bile
 * yoklanır — canlıysa tazelenir, düşmüşse expired işaretlenir. Expired işaretli
 * dosyalar null döner (yeniden giriş şart; yoklamanın anlamı yok).
 */
function readForHeartbeat(clientKey) {
  const entry = session
    .listSessions()
    .find((s) => s.clientKey === clientKey && !s.expired);
  if (!entry) return null;
  // listSessions expired olmayanı doğruladı; ham içerik loadStateByKey ile alınamaz
  // (TTL reddedebilir) → dosya doğrudan okunur.
  try {
    const fs = require("fs");
    const parsed = JSON.parse(
      fs.readFileSync(require("path").join(session.SESSION_DIR, `${clientKey}.json`), "utf8")
    );
    if (!parsed || typeof parsed !== "object" || parsed.expiredAt) return null;
    return parsed;
  } catch (err) {
    return null;
  }
}

/** Canlı oturumları yokla; düşen veya önceden düşmüş oturumlar için yenileme bildir. */
async function runSweep() {
  const sessions = session.listSessions();
  if (sessions.length === 0) return;

  console.log(`[keepalive] tur başladı — oturum:${sessions.length}`);
  const counts = { alive: 0, expired: 0, skipped: 0, uncertain: 0, error: 0 };
  const queue = sessions.map((s) => s.clientKey);

  async function worker() {
    while (queue.length > 0 && !stopped) {
      const clientKey = queue.shift();
      try {
        const status = sessions.find(s => s.clientKey === clientKey)?.expired ? "expired" : await heartbeatOne(clientKey);
        counts[status] = (counts[status] || 0) + 1;
        if (status === "expired") {
          await require("./session-renewal-event").notifyExpired(clientKey);
          console.warn(
            `[keepalive] oturum DÜŞTÜ, expired işaretlendi — mükellef:${shortKey(clientKey)} ` +
              "(otomatik giriş olayı gönderildi)"
          );
        } else if (status === "skipped") {
          console.log(
            `[keepalive] atlandı (çekim işi sürüyor) — mükellef:${shortKey(clientKey)}`
          );
        }
      } catch (err) {
        counts.error += 1;
        console.warn(
          `[keepalive] yoklama hatası — mükellef:${shortKey(clientKey)}: ${err && err.message}`
        );
      }
      if (queue.length > 0) await politeGap(); // mükellefler arası 2-5 sn
    }
  }

  const workers = [];
  for (let i = 0; i < Math.min(MAX_CONCURRENT, sessions.length); i++) {
    workers.push(worker());
  }
  await Promise.all(workers);

  console.log(
    `[keepalive] tur bitti — canlı:${counts.alive} düşen:${counts.expired} ` +
      `atlanan:${counts.skipped} belirsiz:${counts.uncertain} hata:${counts.error}`
  );
}

/** Döngüyü başlatır (server.js süreç açılışında çağırır). */
function start() {
  if (running) return;
  if (!(KEEPALIVE_MINUTES > 0)) {
    console.log("[keepalive] GIB_KEEPALIVE_MINUTES <= 0 — oturum canlı tutma KAPALI.");
    return;
  }
  running = true;
  console.log(
    `[keepalive] oturum canlı tutma açık — her ${KEEPALIVE_MINUTES} dakikada bir hafif hareket, ` +
      `en fazla ${MAX_CONCURRENT} eşzamanlı, TTL kayan pencere ${session.SESSION_TTL_HOURS} saat.`
  );

  (async () => {
    // İlk tur hemen değil, yarım aralık sonra: süreç açılışındaki yoğunlukla çakışmasın.
    await sleep(Math.max(30000, (KEEPALIVE_MINUTES * 60 * 1000) / 2));
    while (!stopped) {
      try {
        await runSweep();
      } catch (err) {
        // Tur hatası döngüyü ÖLDÜRMEZ; sonraki turda tekrar denenir.
        console.error("[keepalive] tur hatası:", err && err.message);
      }
      await sleep(KEEPALIVE_MINUTES * 60 * 1000);
    }
  })();
}

/** Döngüyü durdurur (kapanışta çağrılır; süren yoklamalar kendi tarayıcısını kapatır). */
function stop() {
  stopped = true;
}

module.exports = { start, stop, runSweep, KEEPALIVE_MINUTES, MAX_CONCURRENT };
