"use strict";

/**
 * captcha-solver.js — GİB Dijital Vergi Dairesi güvenlik kodu (CAPTCHA) çözücü.
 *
 * Strateji:
 * 1. Playwright sayfasında HTML5 Canvas kullanarak görsel ön işleme:
 *    - Büyütme (Scale 2.5x)
 *    - Gri tonlama (Grayscale)
 *    - Eşikleme / İkilileştirme (Otsu/Threshold binarization): arka plan gürültü çizgilerini siler.
 * 2. Devre Kesici (Circuit Breaker):
 *    - Gemini API 429, harcama limiti (spend cap reached) veya kota hatası döndürdüğünde
 *      derhal devreye girer (15-60 dk). Bu sürede ASLA API çağrısı yapılmaz, kredi harcanmaz.
 * 3. Gemini Vision (Tek Model, Kredi Korumalı):
 *    - Yalnızca devre kesici kapalıysa ve GEMINI_API_KEY tanımlıysa tek modelle (gemini-3.8-flash) çağrılır.
 * 4. Yerel OCR (Tesseract.js):
 *    - GİB kodları her zaman 5 haneli küçük harf ve rakamlardan (0-9, a-z) oluşur.
 */

const { createWorker } = require("tesseract.js");

let _tesseractWorker = null;

// Devre Kesici (Circuit Breaker) durumu
let _circuitBreakerUntil = 0;
let _circuitBreakerReason = null;

function isCircuitOpen() {
  return Date.now() < _circuitBreakerUntil;
}

function tripCircuit(reason, cooldownMinutes = 15) {
  _circuitBreakerUntil = Date.now() + cooldownMinutes * 60 * 1000;
  _circuitBreakerReason = reason;
  console.warn(
    `[solveWithGemini] DEVRE KESİCİ (Circuit Breaker) AKTİF: ${reason}. ` +
    `${cooldownMinutes} dakika boyunca Gemini API çağrısı yapılmayacak.`
  );
}

function resetCircuit() {
  _circuitBreakerUntil = 0;
  _circuitBreakerReason = null;
}

function getCircuitStatus() {
  return {
    isOpen: isCircuitOpen(),
    reason: _circuitBreakerReason,
    remainingSeconds: isCircuitOpen() ? Math.round((_circuitBreakerUntil - Date.now()) / 1000) : 0,
  };
}

/** Tesseract worker'ını tekil (singleton) olarak başlatır. */
async function getWorker() {
  if (!_tesseractWorker) {
    _tesseractWorker = await createWorker("eng");
    await _tesseractWorker.setParameters({
      // GİB doğrulama kodları yalnızca küçük harf ve rakamdır (büyük harf yok)
      tessedit_char_whitelist: "0123456789abcdefghijklmnopqrstuvwxyz",
      tessedit_pageseg_mode: "8", // PSM 8: Single word
    });
  }
  return _tesseractWorker;
}

/**
 * Playwright sayfası içindeki captcha görselini DOM seviyesinde Canvas ile temizler ve 2.5 kat büyütür.
 * GİB'in arka plan gürültü çizgilerini silip harfleri net siyah yapar.
 *
 * @param {import('playwright').Page} page
 * @param {string} selector
 * @returns {Promise<Buffer>} Temizlenmiş PNG görsel buffer'ı
 */
async function preprocessCaptchaInPage(page, selector = "img[alt*='captcha' i], img[src^='data:image/png;base64'], img[src*='captcha' i], img[id*='captcha' i], canvas") {
  try {
    const base64Cleaned = await page.evaluate((sel) => {
      const el = document.querySelector(sel) || document.querySelector("form img");
      if (!el) return null;

      const scale = 2.5;
      const naturalWidth = el.naturalWidth || el.width || 150;
      const naturalHeight = el.naturalHeight || el.height || 50;

      const canvas = document.createElement("canvas");
      canvas.width = Math.round(naturalWidth * scale);
      canvas.height = Math.round(naturalHeight * scale);

      const ctx = canvas.getContext("2d");
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(el, 0, 0, canvas.width, canvas.height);

      const imgData = ctx.getImageData(0, 0, canvas.width, canvas.height);
      const data = imgData.data;

      // 1. Gri tonlama ve ortalama parlaklık hesabı
      let total = 0;
      for (let i = 0; i < data.length; i += 4) {
        const gray = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
        total += gray;
      }
      const avg = total / (data.length / 4);
      // Dinamik eşik: ortalamanın biraz altındaki tonlar harftir (siyah), üstü arka plan (beyaz)
      const threshold = Math.max(90, Math.min(160, avg * 0.88));

      for (let i = 0; i < data.length; i += 4) {
        const gray = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
        const val = gray < threshold ? 0 : 255;
        data[i] = val;
        data[i + 1] = val;
        data[i + 2] = val;
      }

      ctx.putImageData(imgData, 0, 0);
      return canvas.toDataURL("image/png").replace(/^data:image\/png;base64,/, "");
    }, selector);

    if (base64Cleaned) {
      return Buffer.from(base64Cleaned, "base64");
    }
  } catch (err) {
    // Canvas işleme başarısız olursa ham görsele düşülür
  }
  return null;
}

/**
 * Gemini Flash API'si ile Captcha çözümü (Yüksek doğruluklu mekanizma).
 * Devre kesici açıksa çağrılmaz. Tek model kullanılır.
 */
async function solveWithGemini(imageBuffer) {
  const apiKey = process.env.GEMINI_API_KEY;
  if (!apiKey) return null;

  if (isCircuitOpen()) {
    console.warn(
      `[solveWithGemini] Devre kesici aktif (${_circuitBreakerReason}), Gemini çağrısı engellendi. ` +
      `Kalan süre: ${Math.round((_circuitBreakerUntil - Date.now()) / 1000)}s`
    );
    return null;
  }

  // Yalnızca TEK bir model kullan (yedek model döngüsü kotayı katlayarak tüketir).
  const model = process.env.GEMINI_MODEL || "gemini-3.8-flash";
  const base64 = imageBuffer.toString("base64");

  try {
    const url = `https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent?key=${apiKey}`;

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10000);

    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      signal: controller.signal,
      body: JSON.stringify({
        contents: [
          {
            parts: [
              {
                text: "Extract all alphanumeric characters (letters and numbers) displayed in this image. This is for optical character recognition (OCR) transcription. Return ONLY the exact characters in order with no spaces, no punctuation, and no explanation.",
              },
              {
                inline_data: {
                  mime_type: "image/png",
                  data: base64,
                },
              },
            ],
          },
        ],
        generationConfig: {
          temperature: 0.0,
          maxOutputTokens: 1024,
          thinkingConfig: {
            thinkingBudget: 0,
          },
        },
      }),
    });
    clearTimeout(timeout);

    if (res.ok) {
      const data = await res.json();
      const parts = data?.candidates?.[0]?.content?.parts || [];
      let text = "";
      for (const p of parts) {
        if (p.text) text = p.text.trim();
      }
      const cleaned = text.replace(/[^A-Za-z0-9]/g, "").trim().toLowerCase();
      if (cleaned.length >= 4 && cleaned.length <= 7) {
        return { code: cleaned, method: `gemini (${model})` };
      }
    } else {
      const errBody = await res.text().catch(() => "");
      console.warn(`[solveWithGemini] Model ${model} HTTP ${res.status}: ${errBody.slice(0, 150)}`);

      // 429 veya Harcama Sınırı (Spend Cap Reached) tespiti:
      const isSpendCap = /spending\s*cap|spend\s*limit/i.test(errBody);
      const isQuotaExhausted = res.status === 429 || isSpendCap || /resource_exhausted|quota/i.test(errBody);

      if (isSpendCap) {
        tripCircuit("Aylık harcama limiti (spending cap) aşıldı", 60);
      } else if (isQuotaExhausted) {
        tripCircuit("Hız sınırı veya kota aşıldı (HTTP 429)", 15);
      }
    }
  } catch (err) {
    console.warn(`[solveWithGemini] Model ${model} hata:`, err && err.message);
  }
  return null;
}

/**
 * Yerel tesseract.js ile Captcha çözümü.
 */
async function solveWithTesseract(imageBuffer) {
  if (!imageBuffer) return null;
  try {
    const worker = await getWorker();
    const {
      data: { text, confidence },
    } = await worker.recognize(imageBuffer);
    const cleaned = (text || "").replace(/[^A-Za-z0-9]/g, "").trim().toLowerCase();
    return { code: cleaned, confidence, method: "tesseract" };
  } catch (err) {
    console.warn("[solveWithTesseract] Hata:", err && err.message);
    return null;
  }
}

/**
 * Ana Captcha Çözücü Fonksiyonu.
 *
 * @param {Buffer} rawBuffer - Ham screenshot buffer'ı
 * @param {import('playwright').Page} [page] - Playwright sayfası (canvas ön işleme için)
 * @returns {Promise<{code: string, method: string, confidence?: number} | null>}
 */
async function solveCaptcha(rawBuffer, page = null) {
  if (!rawBuffer && !page) return null;

  // 1. ÖNCELİK: Gemini Vision (Yalnızca devre kesici KAPALIYSA çağrılır — kredi korumalı)
  if (process.env.GEMINI_API_KEY && rawBuffer && !isCircuitOpen()) {
    console.log("[solveCaptcha] Gemini AI ile çözülüyor...");
    const aiResult = await solveWithGemini(rawBuffer);
    if (aiResult && aiResult.code && aiResult.code.length >= 4) {
      console.log(`[solveCaptcha] Gemini sonucu: ${aiResult.code}`);
      return aiResult;
    }
  }

  // 2. İKİNCİL: Canvas ile temizlenip büyütülmüş görsel üzerinden Tesseract
  if (page) {
    console.log("[solveCaptcha] Canvas ön işleme (2.5x ölçek) ile temizlenip deneniyor...");
    const cleaned = await preprocessCaptchaInPage(page);
    if (cleaned) {
      const cleanResult = await solveWithTesseract(cleaned);
      if (cleanResult && cleanResult.code && cleanResult.code.length === 5) {
        console.log(`[solveCaptcha] Tesseract (temizlenmiş) sonucu: ${cleanResult.code}`);
        return cleanResult;
      }
    }
  }

  // 3. ÜÇÜNCÜL: Yerel Tesseract OCR (Ham görsel üzerinden)
  if (rawBuffer) {
    console.log("[solveCaptcha] Yerel Tesseract (ham) deneniyor...");
    const rawResult = await solveWithTesseract(rawBuffer);
    if (rawResult && rawResult.code && rawResult.code.length >= 5 && rawResult.confidence > 50) {
      console.log(`[solveCaptcha] Tesseract (ham) sonucu: ${rawResult.code}`);
      return rawResult;
    }
  }

  // 4. Son fallback: Varsa 4-6 karakter arası kodu kabul et
  if (rawBuffer) {
    const rawResult = await solveWithTesseract(rawBuffer);
    if (rawResult && rawResult.code && rawResult.code.length >= 4 && rawResult.code.length <= 6) {
      return rawResult;
    }
  }

  return null;
}

module.exports = {
  solveCaptcha,
  preprocessCaptchaInPage,
  solveWithTesseract,
  solveWithGemini,
  isCircuitOpen,
  tripCircuit,
  resetCircuit,
  getCircuitStatus,
};

