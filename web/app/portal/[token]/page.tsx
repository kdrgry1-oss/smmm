"use client";

import { useParams } from "next/navigation";
import { FormEvent, useEffect, useRef, useState } from "react";
import {
  PortalPendingRequest,
  PortalUploadCategoryCode,
  PortalUploadFileResult,
  portalInfo,
  portalLogin,
  portalPendingRequests,
  portalUpload,
} from "@/lib/api";
import { formatFileSize, resizeImagesForUpload } from "@/lib/imageResize";

/**
 * Mükellef Belge Portalı (Faz 7 — E1). PUBLIC sayfa: panel layout'u YOK, üyelik YOK.
 * Akış: PIN gir → belgeleri yükle. Dil bilinçli olarak çok basit tutulmuştur
 * ("Fişlerinizin fotoğrafını yükleyin"); butonlar mobil için büyüktür.
 * Portal token'ı yalnızca bileşen durumunda tutulur (2 saatlik oturum, kalıcı saklama yok).
 */

const categories: { code: PortalUploadCategoryCode; label: string; hint: string }[] = [
  { code: "Fis", label: "Fiş", hint: "Market, yakıt, yemek fişleri — fotoğrafını çekip yükleyin" },
  { code: "Fatura", label: "Fatura", hint: "Kağıt faturalarınız" },
  { code: "BankaEkstresi", label: "Banka Ekstresi", hint: "Bankadan indirdiğiniz hesap dökümü (PDF/Excel)" },
  { code: "Sozlesme", label: "Sözleşme", hint: "Kira, iş, hizmet sözleşmeleri" },
  { code: "Diger", label: "Diğer", hint: "Diğer tüm belgeler" },
];

function formatDate(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

export default function PortalPage() {
  const params = useParams<{ token: string }>();
  const token = typeof params?.token === "string" ? params.token : "";

  // Link kontrolü
  const [checking, setChecking] = useState(true);
  const [linkInvalid, setLinkInvalid] = useState(false);

  // PIN girişi
  const [pin, setPin] = useState("");
  const [loggingIn, setLoggingIn] = useState(false);
  const [loginError, setLoginError] = useState<string | null>(null);

  // Oturum
  const [portalToken, setPortalToken] = useState<string | null>(null);
  const [clientTitle, setClientTitle] = useState("");
  const [requests, setRequests] = useState<PortalPendingRequest[]>([]);

  // Yükleme formu
  const [category, setCategory] = useState<PortalUploadCategoryCode>("Fis");
  const [files, setFiles] = useState<File[]>([]);
  const [note, setNote] = useState("");
  const [selectedRequestId, setSelectedRequestId] = useState("");
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploadMessage, setUploadMessage] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);

  // Bu oturumdaki yükleme geçmişi
  const [history, setHistory] = useState<PortalUploadFileResult[]>([]);

  const fileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!token) {
      setLinkInvalid(true);
      setChecking(false);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        await portalInfo(token);
        if (!cancelled) setLinkInvalid(false);
      } catch {
        if (!cancelled) setLinkInvalid(true);
      } finally {
        if (!cancelled) setChecking(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [token]);

  async function handleLogin(e: FormEvent) {
    e.preventDefault();
    setLoginError(null);

    if (!/^\d{6}$/.test(pin)) {
      setLoginError("PIN 6 haneli olmalıdır. Mali müşavirinizin verdiği PIN'i girin.");
      return;
    }

    setLoggingIn(true);
    try {
      const res = await portalLogin(token, pin);
      setPortalToken(res.token);
      setClientTitle(res.clientTitle);
      setRequests(res.pendingRequests);
      setPin("");
    } catch (err) {
      setLoginError(err instanceof Error ? err.message : "Giriş yapılamadı. Lütfen tekrar deneyin.");
    } finally {
      setLoggingIn(false);
    }
  }

  async function addFiles(list: FileList | null) {
    if (!list) return;
    // Faz 12: fiş fotoğrafları seçilirken tarayıcıda SESSİZCE küçültülür (uzun kenar 1600 px,
    // JPEG q0.75) — listede nihai boyut görünür. PDF/HEIC/ofis belgeleri olduğu gibi geçer;
    // küçültme hatası dosya eklemeyi asla engellemez.
    const incoming = await resizeImagesForUpload(Array.from(list));
    setFiles((prev) => {
      const merged = [...prev];
      for (const f of incoming) {
        if (!merged.some((m) => m.name === f.name && m.size === f.size)) merged.push(f);
      }
      return merged.slice(0, 20); // tek seferde en fazla 20 dosya
    });
    setUploadError(null);
    setUploadMessage(null);
  }

  async function handleUpload() {
    if (!portalToken) return;
    setUploadError(null);
    setUploadMessage(null);

    if (files.length === 0) {
      setUploadError("Önce dosya seçin veya fotoğraf çekin.");
      return;
    }

    setUploading(true);
    try {
      const res = await portalUpload(
        portalToken,
        files,
        category,
        note,
        selectedRequestId || undefined
      );
      setUploadMessage(res.message);
      setHistory((prev) => [...res.items, ...prev]);
      setFiles([]);
      setNote("");
      setSelectedRequestId("");
      if (fileInputRef.current) fileInputRef.current.value = "";

      // Bekleyen talepler tazelenir (talep karşılandıysa listeden düşer).
      try {
        setRequests(await portalPendingRequests(portalToken));
      } catch {
        // talep listesi bilgilendirmedir; hatası yüklemeyi gölgelemez
      }
    } catch (err) {
      setUploadError(err instanceof Error ? err.message : "Yükleme sırasında bir hata oluştu. Lütfen tekrar deneyin.");
    } finally {
      setUploading(false);
    }
  }

  // ---------- Görünümler ----------

  if (checking) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-navy-50 px-6">
        <p className="text-sm text-navy-500">Yükleniyor...</p>
      </main>
    );
  }

  if (linkInvalid) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-navy-50 px-6">
        <div className="card w-full max-w-md text-center">
          <h1 className="text-xl font-bold text-navy-900">Bağlantı geçerli değil</h1>
          <p className="mt-3 text-sm text-navy-500">
            Bu belge portalı bağlantısı geçersiz veya kapatılmış. Lütfen mali müşavirinizle
            iletişime geçin ve yeni bir bağlantı isteyin.
          </p>
        </div>
      </main>
    );
  }

  // PIN giriş ekranı — login öncesi yalnızca jenerik "Belge Portalı" (unvan gösterilmez).
  if (!portalToken) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-navy-50 px-6">
        <div className="w-full max-w-md">
          <p className="mb-8 text-center text-xl font-bold text-navy-900">
            Belge<span className="text-navy-400">Portalı</span>
          </p>
          <div className="card">
            <h1 className="text-xl font-bold text-navy-900">Hoş geldiniz</h1>
            <p className="mt-1 text-sm text-navy-500">
              Mali müşavirinizin size verdiği 6 haneli PIN ile giriş yapın.
            </p>

            {loginError && <div className="alert-error mt-4">{loginError}</div>}

            <form onSubmit={handleLogin} className="mt-6 space-y-4" noValidate>
              <div>
                <label htmlFor="pin" className="label">
                  PIN (6 hane)
                </label>
                <input
                  id="pin"
                  type="password"
                  inputMode="numeric"
                  maxLength={6}
                  className="input !py-4 text-center text-2xl tracking-[0.5em]"
                  placeholder="••••••"
                  value={pin}
                  onChange={(e) => setPin(e.target.value.replace(/\D/g, "").slice(0, 6))}
                  autoComplete="one-time-code"
                />
              </div>
              <button type="submit" className="btn-primary w-full !py-4 text-base" disabled={loggingIn}>
                {loggingIn ? "Giriş yapılıyor..." : "Giriş Yap"}
              </button>
            </form>

            <p className="mt-6 text-center text-xs text-navy-400">
              PIN'inizi bilmiyorsanız mali müşavirinizden isteyin.
            </p>
          </div>
        </div>
      </main>
    );
  }

  // Oturum açık: yükleme ekranı
  const selectedCategory = categories.find((c) => c.code === category);

  return (
    <main className="min-h-screen bg-navy-50 pb-16">
      <header className="border-b border-navy-100 bg-navy-950 px-6 py-4 text-white">
        <p className="text-lg font-bold">
          Belge<span className="text-navy-400">Portalı</span>
        </p>
        <p className="mt-0.5 text-sm text-navy-300">{clientTitle}</p>
      </header>

      <div className="mx-auto max-w-xl space-y-6 px-4 pt-6">
        {/* Bekleyen belge talepleri */}
        {requests.length > 0 && (
          <div className="card !border-red-200">
            <div className="flex items-center gap-2">
              <span className="rounded-full bg-red-600 px-2.5 py-0.5 text-xs font-bold text-white">
                {requests.length}
              </span>
              <h2 className="text-base font-semibold text-navy-900">
                Mali müşaviriniz şu belgeleri bekliyor
              </h2>
            </div>
            <ul className="mt-3 space-y-2">
              {requests.map((r) => (
                <li
                  key={r.id}
                  className="flex items-center justify-between rounded-lg border border-red-100 bg-red-50 px-3 py-2.5"
                >
                  <span className="text-sm font-medium text-navy-900">{r.title}</span>
                  {r.dueDate && (
                    <span className="ml-3 shrink-0 text-xs font-semibold text-red-700">
                      Son gün: {formatDate(r.dueDate)}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </div>
        )}

        {/* Yükleme kartı */}
        <div className="card">
          <h2 className="text-base font-semibold text-navy-900">Belge Yükleyin</h2>
          <p className="mt-1 text-sm text-navy-500">
            Fişlerinizin fotoğrafını yükleyin — gerisini biz hallederiz.
          </p>

          {/* Kategori seçimi — büyük dokunmatik butonlar */}
          <div className="mt-4 grid grid-cols-2 gap-2 sm:grid-cols-3">
            {categories.map((c) => (
              <button
                key={c.code}
                type="button"
                onClick={() => setCategory(c.code)}
                className={`rounded-lg border px-3 py-3 text-sm font-semibold transition ${
                  category === c.code
                    ? "border-navy-900 bg-navy-900 text-white"
                    : "border-navy-200 bg-white text-navy-700 hover:bg-navy-50"
                }`}
              >
                {c.label}
              </button>
            ))}
          </div>
          {selectedCategory && (
            <p className="mt-2 text-xs text-navy-400">{selectedCategory.hint}</p>
          )}

          {/* Talep eşleştirme (opsiyonel) */}
          {requests.length > 0 && (
            <div className="mt-4">
              <label htmlFor="request" className="label">
                Hangi talep için yüklüyorsunuz? (isteğe bağlı)
              </label>
              <select
                id="request"
                className="input"
                value={selectedRequestId}
                onChange={(e) => setSelectedRequestId(e.target.value)}
              >
                <option value="">Talep seçmeden yükle</option>
                {requests.map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.title}
                  </option>
                ))}
              </select>
            </div>
          )}

          {/* Sürükle-bırak + dosya seçimi */}
          <div
            className={`mt-4 rounded-xl border-2 border-dashed px-4 py-8 text-center transition ${
              dragOver ? "border-navy-500 bg-navy-50" : "border-navy-200 bg-white"
            }`}
            onDragOver={(e) => {
              e.preventDefault();
              setDragOver(true);
            }}
            onDragLeave={() => setDragOver(false)}
            onDrop={(e) => {
              e.preventDefault();
              setDragOver(false);
              addFiles(e.dataTransfer.files);
            }}
          >
            <p className="text-sm text-navy-500">
              Dosyaları buraya sürükleyin veya
            </p>
            <button
              type="button"
              className="btn-primary mt-3 w-full !py-4 text-base sm:w-auto"
              onClick={() => fileInputRef.current?.click()}
            >
              Dosya Seç / Fotoğraf Çek
            </button>
            <input
              ref={fileInputRef}
              type="file"
              multiple
              accept="image/*,.pdf,.heic,.xls,.xlsx,.csv,.doc,.docx"
              className="hidden"
              onChange={(e) => addFiles(e.target.files)}
            />
            <p className="mt-3 text-xs text-navy-400">
              En fazla 20 dosya, her biri 10 MB'ye kadar (JPG, PNG, PDF, Excel, Word).
            </p>
          </div>

          {/* Seçilen dosyalar */}
          {files.length > 0 && (
            <ul className="mt-4 space-y-1.5">
              {files.map((f, i) => (
                <li
                  key={`${f.name}-${i}`}
                  className="flex items-center justify-between rounded-lg bg-navy-50 px-3 py-2 text-sm"
                >
                  <span className="flex min-w-0 items-baseline gap-2">
                    <span className="truncate text-navy-800">{f.name}</span>
                    <span className="shrink-0 text-xs text-navy-400">{formatFileSize(f.size)}</span>
                  </span>
                  <button
                    type="button"
                    className="ml-3 shrink-0 text-xs font-semibold text-red-600 hover:underline"
                    onClick={() => setFiles((prev) => prev.filter((_, idx) => idx !== i))}
                  >
                    Kaldır
                  </button>
                </li>
              ))}
            </ul>
          )}

          {/* Not */}
          <div className="mt-4">
            <label htmlFor="note" className="label">
              Not (isteğe bağlı)
            </label>
            <input
              id="note"
              type="text"
              maxLength={500}
              className="input"
              placeholder="Örn: Haziran ayı yakıt fişleri"
              value={note}
              onChange={(e) => setNote(e.target.value)}
            />
          </div>

          {uploadError && <div className="alert-error mt-4">{uploadError}</div>}
          {uploadMessage && (
            <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
              {uploadMessage}
            </div>
          )}

          <button
            type="button"
            className="btn-primary mt-5 w-full !py-4 text-base"
            onClick={handleUpload}
            disabled={uploading}
          >
            {uploading ? "Yükleniyor..." : `Yükle (${files.length} dosya)`}
          </button>
        </div>

        {/* Bu oturumdaki yükleme geçmişi */}
        {history.length > 0 && (
          <div className="card">
            <h2 className="text-base font-semibold text-navy-900">Bu Oturumda Yüklenenler</h2>
            <ul className="mt-3 space-y-1.5">
              {history.map((h, i) => (
                <li
                  key={`${h.fileName}-${i}`}
                  className="flex items-start justify-between gap-3 rounded-lg bg-navy-50 px-3 py-2 text-sm"
                >
                  <div className="min-w-0">
                    <p className="truncate font-medium text-navy-800">{h.fileName}</p>
                    {h.message && (
                      <p className={`text-xs ${h.success ? "text-navy-400" : "text-red-600"}`}>
                        {h.message}
                      </p>
                    )}
                  </div>
                  <span
                    className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-semibold ${
                      h.success ? "bg-green-100 text-green-700" : "bg-red-100 text-red-700"
                    }`}
                  >
                    {h.success ? "✓ Alındı" : "Hata"}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}

        <p className="text-center text-xs text-navy-400">
          Yüklediğiniz belgeler doğrudan mali müşavirinize iletilir.
        </p>
      </div>
    </main>
  );
}
