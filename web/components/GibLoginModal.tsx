"use client";

/**
 * Faz 17 — GİB insan destekli giriş modalı ("GİB Oturumu Aç").
 *
 * GİB Dijital Vergi Dairesi giriş ekranı HER girişte "Doğrulama Kodu" (captcha) istiyor.
 * Sistem bu kodu ÇÖZMEZ (güvenlik kuralı); robot giriş ekranını açıp kayıtlı kimliği
 * doldurur, captcha görselini buraya getirir, kullanıcı (mali müşavir) kodu okuyup girer.
 * Başarılı girişte oturum sunucuda 12 saat saklanır — bu süre boyunca gece otomatik
 * senkron dahil tüm çekimler doğrulama kodu istemeden çalışır.
 *
 * Akış:
 *  - Modal açılınca begin ucu çağrılır → captcha görseli BÜYÜK gösterilir.
 *  - Kod girilir (autofocus, Enter = gönder, en fazla 8 karakter) → complete ucu.
 *  - Hatalı kodda YENİ görsel yerinde güncellenir (en fazla 3 deneme).
 *  - "Yeni Kod" begin'i tekrar çağırır; başarıda yeşil mesaj gösterilip modal kapanır
 *    ve üst sayfa yenilenir (onSuccess).
 */

import { FormEvent, useCallback, useEffect, useRef, useState } from "react";
import {
  beginGibAdvisorLogin,
  beginGibLogin,
  completeGibAdvisorLogin,
  completeGibLogin,
} from "@/lib/api";

interface GibLoginModalProps {
  /**
   * Faz 18 — akış türü:
   *  - "client" (varsayılan): mükellefin KENDİ GİB kimliğiyle giriş (clientId zorunlu).
   *  - "advisor": MÜŞAVİRİN kendi GİB kimliğiyle TEK oturum (tenant düzeyi; clientId gerekmez).
   *    Bu oturum, müşavire bağlı TÜM mükelleflerin çekimleri için yeterlidir.
   */
  mode?: "client" | "advisor";
  /** mode="client" için zorunlu; advisor modunda kullanılmaz. */
  clientId?: string;
  /** Başlıkta gösterilen mükellef unvanı (opsiyonel). */
  clientTitle?: string | null;
  onClose: () => void;
  /** Giriş başarıyla tamamlanınca (oturum kaydedildi) çağrılır — üst sayfa listeyi yeniler. */
  onSuccess?: () => void;
}

export default function GibLoginModal({
  mode = "client",
  clientId,
  clientTitle,
  onClose,
  onSuccess,
}: GibLoginModalProps) {
  // begin durumu
  const [loading, setLoading] = useState(true);
  const [loadingStep, setLoadingStep] = useState(0);
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [captchaImage, setCaptchaImage] = useState<string | null>(null);
  const [expiresInSeconds, setExpiresInSeconds] = useState<number | null>(null);

  // complete durumu
  const [code, setCode] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [attemptsLeft, setAttemptsLeft] = useState<number | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  const inputRef = useRef<HTMLInputElement | null>(null);
  const closeTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  /** Başarıda modal kısa süre yeşil mesajı gösterir, sonra kapanıp üst sayfayı yeniler. */
  const finishWithSuccess = useCallback(
    (message: string) => {
      setSuccess(message);
      setError(null);
      closeTimerRef.current = setTimeout(() => {
        onSuccess?.();
        onClose();
      }, 2000);
    },
    [onClose, onSuccess]
  );

  /** begin ucu: captcha görselini getirir (Yeni Kod düğmesi de bunu çağırır). */
  const startLogin = useCallback(async () => {
    setLoading(true);
    setError(null);
    setSuccess(null);
    setCode("");
    setPendingId(null);
    setCaptchaImage(null);
    setAttemptsLeft(null);
    try {
      const result =
        mode === "advisor" ? await beginGibAdvisorLogin() : await beginGibLogin(clientId!);
      if (result.alreadyLoggedIn) {
        // Giriş yapıldı (kayıtlı oturum veya OCR ile otomatik çözüldü)
        finishWithSuccess(
          result.message || "GİB oturumu başarıyla açıldı (OCR ile çözüldü) — uyarı kaldırıldı."
        );
        return;
      }
      if (!result.success || !result.pendingId || !result.captchaImage) {
        setError(
          result.message ||
            "GİB giriş ekranı açılamadı. Lütfen daha sonra tekrar deneyin."
        );
        return;
      }
      setPendingId(result.pendingId);
      setCaptchaImage(result.captchaImage);
      setExpiresInSeconds(result.expiresInSeconds);
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "GİB girişi başlatılırken bir hata oluştu."
      );
    } finally {
      setLoading(false);
    }
  }, [clientId, mode, finishWithSuccess]);

  // Modal açılınca giriş YALNIZ BİR KEZ başlatılır (üst bileşen yeniden render olsa da
  // begin tekrarlanmaz); kapanırken başarı zamanlayıcısı temizlenir.
  useEffect(() => {
    startLogin();
    return () => {
      if (closeTimerRef.current) clearTimeout(closeTimerRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Adım geçiş animasyonu: kullanıcıya robotun aktif çalıştığını gösterir
  useEffect(() => {
    if (!loading) {
      setLoadingStep(0);
      return;
    }
    const timer = setInterval(() => {
      setLoadingStep((prev) => (prev < 3 ? prev + 1 : prev));
    }, 1800);
    return () => clearInterval(timer);
  }, [loading]);

  // Captcha görseli gelince kod alanına odaklan (autofocus).
  useEffect(() => {
    if (captchaImage && !loading && !success) inputRef.current?.focus();
  }, [captchaImage, loading, success]);

  /** complete ucu: kullanıcının okuduğu kodla girişi tamamlar (Enter da gönderir). */
  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (submitting || loading || success) return;

    const trimmed = code.trim();
    if (!trimmed) {
      setError("Görseldeki doğrulama kodunu girin.");
      return;
    }
    if (!pendingId) {
      setError('Bekleyen giriş oturumu yok. "Yeni Kod" ile başlayın.');
      return;
    }

    setSubmitting(true);
    setError(null);
    try {
      const result =
        mode === "advisor"
          ? await completeGibAdvisorLogin(pendingId, trimmed)
          : await completeGibLogin(clientId!, pendingId, trimmed);
      if (result.success) {
        finishWithSuccess(
          "Oturum kaydedildi — 12 saat boyunca doğrulama kodu istenmeyecek."
        );
        return;
      }

      // Hatalı kod / şifre: yeni görsel yerinde güncellenir; Türkçe mesaj gösterilir.
      setCode("");
      setAttemptsLeft(result.attemptsLeft);
      if (result.pendingExpired) {
        setPendingId(null);
        setCaptchaImage(null);
        setError(
          `${result.message || "Bekleyen giriş oturumu sona erdi."} ` +
            '"Yeni Kod" ile tekrar deneyebilirsiniz.'
        );
      } else {
        if (result.captchaImage) setCaptchaImage(result.captchaImage);
        setError(result.message || "Giriş başarısız. Yeni görseldeki kodu girin.");
        inputRef.current?.focus();
      }
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Giriş tamamlanırken bir hata oluştu."
      );
    } finally {
      setSubmitting(false);
    }
  }

  function handleClose() {
    if (submitting) return;
    if (closeTimerRef.current) {
      clearTimeout(closeTimerRef.current);
      // Başarı zamanlayıcısı elle kapatmayla öne alınır — üst sayfa yine yenilenir.
      onSuccess?.();
    }
    onClose();
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/50 px-4"
      onClick={handleClose}
    >
      <div
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-xl bg-white p-6 shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-lg font-bold text-navy-900">
          {mode === "advisor" ? "Müşavir GİB Oturumu Aç" : "GİB Oturumu Aç"}
        </h2>
        <p className="mt-1 text-sm text-navy-500">
          {clientTitle ? `${clientTitle} — ` : ""}
          {mode === "advisor"
            ? "Müşavir kimliğinizle Dijital Vergi Dairesi girişi için görseldeki doğrulama kodunu girin. Tek oturum, tüm mükellefleriniz için yeterlidir."
            : "Dijital Vergi Dairesi girişi için görseldeki doğrulama kodunu girin."}
        </p>

        {success ? (
          <div className="mt-5 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm font-medium text-green-800">
            ✓ {success}
          </div>
        ) : loading ? (
          <div className="mt-6 flex flex-col items-center gap-4 py-8 text-center">
            <div className="relative flex items-center justify-center">
              <span className="h-12 w-12 animate-spin rounded-full border-4 border-navy-100 border-t-navy-800" />
              <span className="absolute text-[10px] font-black text-navy-800 tracking-tighter">
                GİB
              </span>
            </div>
            <div className="space-y-1">
              <p className="text-sm font-semibold text-navy-900 transition-all">
                {
                  [
                    "GİB Dijital Vergi Dairesi portalına bağlanılıyor...",
                    "GİB giriş ekranı açıldı, kimlik bilgileri hazırlanıyor...",
                    "Güvenlik kodu (CAPTCHA) yapay zeka ile otomatik çözülüyor...",
                    "Oturum ve erişim yetkileri doğrulanıyor...",
                  ][loadingStep]
                }
              </p>
              <p className="text-xs text-navy-500">
                Robot otomatik giriş yapıyor. Kod çözülürse bu pencere kendiliğinden kapanacaktır.
              </p>
            </div>
            {/* 4 Aşamalı İlerleme Çubuğu */}
            <div className="flex items-center gap-1.5 mt-2">
              {[0, 1, 2, 3].map((s) => (
                <div
                  key={s}
                  className={`h-1.5 rounded-full transition-all duration-500 ${
                    s === loadingStep
                      ? "w-10 bg-navy-800"
                      : s < loadingStep
                        ? "w-8 bg-navy-600"
                        : "w-6 bg-navy-200"
                  }`}
                />
              ))}
            </div>
          </div>
        ) : (
          <form onSubmit={handleSubmit} className="mt-5 space-y-4" noValidate>
            {captchaImage ? (
              <div className="flex flex-col items-center gap-2 rounded-lg border border-navy-100 bg-navy-50 p-4">
                {/* Captcha görseli data URL olarak BÜYÜK gösterilir (min 220px genişlik). */}
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img
                  src={`data:image/png;base64,${captchaImage}`}
                  alt="GİB doğrulama kodu görseli"
                  className="h-auto w-full max-w-[320px] rounded-md border border-navy-200 bg-white"
                  style={{ minWidth: 220 }}
                />
                {expiresInSeconds !== null && (
                  <p className="text-xs text-navy-400">
                    Kodun {Math.round(expiresInSeconds / 60)} dakika içinde girilmesi gerekir.
                  </p>
                )}
              </div>
            ) : (
              <div className="rounded-lg border border-navy-100 bg-navy-50 px-4 py-3 text-sm text-navy-600">
                Doğrulama kodu görseli yok. &quot;Yeni Kod&quot; ile tekrar deneyin.
              </div>
            )}

            {error && <div className="alert-error">{error}</div>}
            {attemptsLeft !== null && attemptsLeft > 0 && !error?.includes("deneme") && (
              <p className="text-xs text-amber-700">{attemptsLeft} deneme hakkı kaldı.</p>
            )}

            <div>
              <label htmlFor="gibCaptchaCode" className="label">
                Doğrulama Kodu
              </label>
              <input
                id="gibCaptchaCode"
                ref={inputRef}
                type="text"
                inputMode="text"
                autoComplete="off"
                maxLength={8}
                className="input font-mono text-lg tracking-widest"
                placeholder="Görseldeki kod"
                value={code}
                onChange={(e) => setCode(e.target.value)}
                disabled={submitting || !pendingId}
              />
              <p className="mt-1 text-xs text-navy-400">
                Sistem bu kodu güvenlik gereği kendisi çözmez; kod yalnızca GİB&apos;e
                iletilir. Başarılı girişte oturum 12 saat saklanır ve gece otomatik
                senkron kod istemeden çalışır.
              </p>
            </div>

            <div className="flex flex-wrap items-center justify-end gap-2">
              <button
                type="button"
                className="btn-secondary"
                onClick={handleClose}
                disabled={submitting}
              >
                Vazgeç
              </button>
              <button
                type="button"
                className="btn-secondary"
                onClick={startLogin}
                disabled={submitting}
                title="Yeni bir doğrulama kodu görseli getirir (girişe baştan başlar)."
              >
                Yeni Kod
              </button>
              <button
                type="submit"
                className="btn-primary flex items-center gap-2"
                disabled={submitting || !pendingId || !code.trim()}
              >
                {submitting && (
                  <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                )}
                {submitting ? "Giriş yapılıyor..." : "Girişi Tamamla"}
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
