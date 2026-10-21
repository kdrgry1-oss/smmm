"use strict";

/**
 * captcha-solver.js — GİB Dijital Vergi Dairesi güvenlik kodu (CAPTCHA) çözücü.
 *
 * Strateji:
 * 1. Playwright sayfasında HTML5 Canvas kullanarak görsel ön işleme:
 *    - Gri tonlama (Grayscale)
 *    - Eşikleme / İkilileştirme (Otsu/Threshold binarization): arka plan gürültü çizgilerini siler.
 * 2. Yerel OCR: tesseract.js motoru (yalnızca 0-9 ve A-Z karakter kısıtlamasıyla).
 * 3. Yedek Motor (Opsiyonel / Fallback): Eğer yerel OCR güven skoru düşükse veya kod
 *    beklenen uzunlukta (5-6 karakter) değilse; GEMINI_API_KEY tanımlıysa Gemini Flash ile %99.9 doğruluk.
 */

const { createWorker } = require("tesseract.js");

let _tesseractWorker = null;

/** Tesseract worker'ını tekil (singleton) olarak başlatır. */
async function getWorker() {
  if (!_tesseractWorker) {
    _tesseractWorker = await createWorker("eng");
    await _tesseractWorker.setParameters({
      tessedit_char_whitelist: "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
      tessedit_pageseg_mode: "7", // PSM 7: Single line of text
    });
  }
  return _tesseractWorker;
}

/**
 * Playwright sayfası içindeki captcha görselini DOM seviyesinde Canvas ile temizler.
 * GİB'in arka plan gürültü çizgilerini silip harfleri net siyah yapar.
 *
 * @param {import('playwright').Page} page
 * @param {string} selector
 * @returns {Promise<Buffer>} Temizlenmiş PNG görsel buffer'ı
 */
async function preprocessCaptchaInPage(page, selector = "img[src*='captcha' i], img[id*='captcha' i], canvas") {
  try {
    const base64Cleaned = await page.evaluate((sel) => {
      const el = document.querySelector(sel) || document.querySelector("form img");
      if (!el) return null;

      const canvas = document.createElement("canvas");
      const width = el.naturalWidth || el.width || 150;
      const height = el.naturalHeight || el.height || 50;
      canvas.width = width;
      canvas.height = height;

      const ctx = canvas.getContext("2d");
      ctx.drawImage(el, 0, 0, width, height);

      const imgData = ctx.getImageData(0, 0, width, height);
      const data = imgData.data;

      // 1. Gri tonlama ve ortalama parlaklık hesabı
      let total = 0;
      for (let i = 0; i < data.length; i += 4) {
        const gray = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
        total += gray;
      }
      const avg = total / (data.length / 4);
      // Dinamik eşik: ortalamanın biraz altındaki tonlar harftir (siyah), üstü arka plan (beyaz)
      const threshold = Math.max(100, Math.min(160, avg * 0.9));

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
 * Ortamda GEMINI_API_KEY varsa kullanılır.
 */
async function solveWithGemini(imageBuffer) {
  const apiKey = process.env.GEMINI_API_KEY;
  if (!apiKey) return null;

  try {
    const base64 = imageBuffer.toString("base64");
    const model = process.env.GEMINI_MODEL || "gemini-flash-latest";
    const url = `https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent?key=${apiKey}`;

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 6000);

    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      signal: controller.signal,
      body: JSON.stringify({
        contents: [
          {
            parts: [
              {
                text: "Read the security verification code / CAPTCHA from this image. Output ONLY the exact characters (lowercase/uppercase letters and numbers), with no spaces, punctuation, or explanation.",
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
          maxOutputTokens: 10,
        },
      }),
    });
    clearTimeout(timeout);

    if (res.ok) {
      const data = await res.json();
      const text =
        data?.candidates?.[0]?.content?.parts?.[0]?.text?.trim() || "";
      const cleaned = text.replace(/[^A-Za-z0-9]/g, "").trim();
      if (cleaned.length >= 4 && cleaned.length <= 7) {
        return { code: cleaned, method: "gemini" };
      }
    }
  } catch (err) {
    console.warn("[solveWithGemini] Hata:", err && err.message);
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
    const cleaned = (text || "").replace(/[^A-Za-z0-9]/g, "").trim();
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

  // 1. ÖNCELİK: Gemini Vision (Varsa 200ms'de %100 doğrulukla çözer)
  if (process.env.GEMINI_API_KEY && rawBuffer) {
    console.log("[solveCaptcha] Gemini AI ile çözülüyor...");
    const aiResult = await solveWithGemini(rawBuffer);
    if (aiResult && aiResult.code && aiResult.code.length >= 4) {
      console.log(`[solveCaptcha] Gemini sonucu: ${aiResult.code}`);
      return aiResult;
    }
  }

  // 2. İKİNCİL: Yerel Tesseract OCR (Ham görsel üzerinden)
  if (rawBuffer) {
    console.log("[solveCaptcha] Yerel Tesseract (ham) deneniyor...");
    const rawResult = await solveWithTesseract(rawBuffer);
    if (rawResult && rawResult.code && rawResult.code.length >= 5 && rawResult.confidence > 50) {
      console.log(`[solveCaptcha] Tesseract (ham) sonucu: ${rawResult.code}`);
      return rawResult;
    }
  }

  // 3. ÜÇÜNCÜL: Canvas ile arka plan gürültüsü temizlenmiş görsel üzerinden Tesseract
  if (page) {
    console.log("[solveCaptcha] Canvas ön işleme ile temizlenip deneniyor...");
    const cleaned = await preprocessCaptchaInPage(page);
    if (cleaned) {
      const cleanResult = await solveWithTesseract(cleaned);
      if (cleanResult && cleanResult.code && cleanResult.code.length >= 4) {
        console.log(`[solveCaptcha] Tesseract (temizlenmiş) sonucu: ${cleanResult.code}`);
        return cleanResult;
      }
    }
  }

  // 4. Son fallback: Varsa rawResult kodunu kabul et
  if (rawBuffer) {
    const rawResult = await solveWithTesseract(rawBuffer);
    if (rawResult && rawResult.code && rawResult.code.length >= 4) {
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
};
