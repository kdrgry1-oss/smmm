"use client";

import { useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  EDefterPeriodItem,
  EDefterPrecheckResult,
  listEDefterPeriods,
  markEDefterUploaded,
  PeriodCheckStatusCode,
  runEDefterPrecheck,
} from "@/lib/api";

/** Önceki ay "yyyy-MM" (berat bir önceki kapanan dönem için yüklenir). */
function defaultPeriod(): string {
  const d = new Date();
  d.setMonth(d.getMonth() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

const checkStyles: Record<
  PeriodCheckStatusCode,
  { icon: string; card: string; text: string; label: string }
> = {
  Ok: { icon: "✓", card: "border-green-200 bg-green-50/50", text: "text-green-700", label: "Tamam" },
  Uyari: { icon: "⚠", card: "border-amber-200 bg-amber-50/50", text: "text-amber-700", label: "Dikkat" },
  Hata: { icon: "✗", card: "border-red-200 bg-red-50/50", text: "text-red-700", label: "Engel" },
};

const statusBadges: Record<string, string> = {
  Hazirlaniyor: "bg-navy-100 text-navy-600",
  KontrolTamam: "bg-blue-100 text-blue-700",
  BeratYuklendi: "bg-green-100 text-green-700",
  Hata: "bg-red-100 text-red-700",
};

/**
 * Faz 11 — E5: e-Defter berat ön-kontrol ekranı. Mükellef + dönem seçilince kontrol seti
 * çalışır; SMMM beratı GİB portalında yükledikten sonra "Berat yüklendi" olarak işaretler.
 * DÜRÜST KAPSAM: gerçek XBRL-GL üretimi/imza bu fazda yok (üstteki bilgi bandında yazar).
 */
export default function EDefterPage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const [clientId, setClientId] = useState<string>("");
  const [period, setPeriod] = useState<string>(defaultPeriod());

  const [result, setResult] = useState<EDefterPrecheckResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const [history, setHistory] = useState<EDefterPeriodItem[] | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);

  const [marking, setMarking] = useState(false);
  const [markNote, setMarkNote] = useState("");
  const [showMarkForm, setShowMarkForm] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const data = await api<ClientResponse[]>("/api/clients");
        if (!cancelled) {
          setClients(data);
          if (data.length > 0) setClientId(data[0].id);
        }
      } catch (err) {
        if (!cancelled)
          setClientsError(err instanceof Error ? err.message : "Mükellefler yüklenirken bir hata oluştu.");
      } finally {
        if (!cancelled) setClientsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Mükellef + dönem seçilince kontroller ve geçmiş otomatik yüklenir.
  useEffect(() => {
    if (!clientId || !/^\d{4}-(0[1-9]|1[0-2])$/.test(period)) return;
    let cancelled = false;
    (async () => {
      setLoading(true);
      setError(null);
      setMessage(null);
      setShowMarkForm(false);
      try {
        const [check, list] = await Promise.all([
          runEDefterPrecheck(clientId, period),
          listEDefterPeriods(clientId),
        ]);
        if (!cancelled) {
          setResult(check);
          setHistory(list);
          setHistoryError(null);
        }
      } catch (err) {
        if (!cancelled) {
          setResult(null);
          setError(err instanceof Error ? err.message : "Ön-kontrol çalıştırılırken bir hata oluştu.");
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [clientId, period]);

  async function refresh() {
    if (!clientId) return;
    setLoading(true);
    setError(null);
    setMessage(null);
    try {
      const [check, list] = await Promise.all([
        runEDefterPrecheck(clientId, period),
        listEDefterPeriods(clientId),
      ]);
      setResult(check);
      setHistory(list);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Ön-kontrol çalıştırılırken bir hata oluştu.");
    } finally {
      setLoading(false);
    }
  }

  async function handleMarkUploaded() {
    if (!clientId) return;
    setMarking(true);
    setError(null);
    setMessage(null);
    try {
      await markEDefterUploaded(clientId, period, markNote);
      setMessage(`${period} dönemi "Berat yüklendi" olarak işaretlendi.`);
      setShowMarkForm(false);
      setMarkNote("");
      const [check, list] = await Promise.all([
        runEDefterPrecheck(clientId, period),
        listEDefterPeriods(clientId),
      ]);
      setResult(check);
      setHistory(list);
    } catch (err) {
      setError(err instanceof Error ? err.message : "İşaretleme sırasında bir hata oluştu.");
    } finally {
      setMarking(false);
    }
  }

  const alreadyUploaded = result?.status === "BeratYuklendi";

  return (
    <div>
      <h1 className="text-2xl font-bold text-navy-900">e-Defter</h1>
      <p className="mt-1 text-sm text-navy-500">
        Berat yüklemeden ÖNCE hataları yakalayın: dönem, VKN/unvan, sertifika, kapanış ve mizan kontrolleri.
      </p>

      {/* Dürüst kapsam notu */}
      <div className="alert-info mt-4 text-sm">
        Bu modül <strong>ön-kontrol ve durum takibi</strong> yapar. e-Defter XML (XBRL-GL) üretimi ve
        mali mühürle imzalama henüz platformda değildir — beratı GİB e-Defter uygulamasında yükledikten
        sonra buradan &quot;Berat yüklendi&quot; olarak işaretleyin.
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}
      {message && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {message}
        </div>
      )}
      {error && <div className="alert-error mt-4">{error}</div>}

      {/* Seçim */}
      <div className="card mt-6">
        <div className="flex flex-wrap items-end gap-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-navy-600">Mükellef</label>
            <select
              className="input max-w-[280px]"
              value={clientId}
              onChange={(e) => setClientId(e.target.value)}
              disabled={clientsLoading}
            >
              {clientsLoading ? (
                <option value="">Yükleniyor...</option>
              ) : clients.length === 0 ? (
                <option value="">Önce mükellef ekleyin</option>
              ) : (
                clients.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.title}
                  </option>
                ))
              )}
            </select>
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-navy-600">Dönem</label>
            <input
              type="month"
              className="input !w-auto"
              value={period}
              onChange={(e) => setPeriod(e.target.value)}
            />
          </div>
          <button onClick={refresh} className="btn-secondary" disabled={loading || !clientId}>
            {loading ? "Kontrol ediliyor..." : "Kontrolleri Yenile"}
          </button>
        </div>
        {!clientsLoading && clients.length === 0 && (
          <p className="mt-3 text-xs text-amber-700">
            Ön-kontrol için önce Mükellefler sayfasından mükellef ekleyin.
          </p>
        )}
      </div>

      {/* Yükleniyor iskeleti */}
      {loading ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3, 4, 5, 6].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
            </div>
          ))}
        </div>
      ) : result ? (
        <>
          {/* Özet + berat işaretleme */}
          <div className="card mt-6 flex flex-wrap items-center justify-between gap-4">
            <div>
              <p className="text-sm font-semibold text-navy-900">
                {period} dönemi durumu:{" "}
                <span
                  className={`ml-1 rounded-full px-2.5 py-0.5 text-xs font-bold ${
                    statusBadges[result.status] ?? "bg-navy-100 text-navy-600"
                  }`}
                >
                  {result.status === "Hazirlaniyor"
                    ? "Hazırlanıyor"
                    : result.status === "KontrolTamam"
                      ? "Kontrol tamam"
                      : result.status === "BeratYuklendi"
                        ? "Berat yüklendi"
                        : "Hata"}
                </span>
              </p>
              <p className="mt-1 text-xs text-navy-500">
                {result.hasError
                  ? "Kırmızı kontroller çözülmeden berat yüklenirse iptal/yeniden yükleme riski vardır."
                  : alreadyUploaded
                    ? "Bu dönemin beratı yüklendi olarak işaretli."
                    : "Engel yok — beratı GİB portalında yükleyebilirsiniz."}
              </p>
            </div>
            {!alreadyUploaded && (
              <div className="flex flex-col items-end gap-2">
                {showMarkForm ? (
                  <div className="flex flex-wrap items-center gap-2">
                    <input
                      type="text"
                      className="input !w-64"
                      placeholder="Not (isteğe bağlı, ör. GİB onay no)"
                      maxLength={500}
                      value={markNote}
                      onChange={(e) => setMarkNote(e.target.value)}
                    />
                    <button onClick={handleMarkUploaded} className="btn-primary" disabled={marking}>
                      {marking ? "İşaretleniyor..." : "Onayla"}
                    </button>
                    <button
                      onClick={() => setShowMarkForm(false)}
                      className="btn-secondary"
                      disabled={marking}
                    >
                      Vazgeç
                    </button>
                  </div>
                ) : (
                  <button onClick={() => setShowMarkForm(true)} className="btn-primary">
                    Berat yüklendi olarak işaretle
                  </button>
                )}
                {result.hasError && (
                  <p className="text-xs text-red-700">
                    ⚠ Kırmızı kontroller var — işaretleme sorumluluğu size aittir.
                  </p>
                )}
              </div>
            )}
          </div>

          {/* Kontrol kartları */}
          <div className="mt-6 space-y-3">
            {result.items.map((item) => {
              const style = checkStyles[item.durum] ?? checkStyles.Uyari;
              return (
                <div
                  key={item.key}
                  className={`rounded-xl border p-4 shadow-sm ${style.card}`}
                >
                  <div className="flex items-start gap-3">
                    <span className={`text-lg font-bold ${style.text}`}>{style.icon}</span>
                    <div className="flex-1">
                      <p className="text-sm font-semibold text-navy-900">
                        {item.ad}
                        <span
                          className={`ml-2 rounded-full bg-white/70 px-2 py-0.5 text-[10px] font-bold ${style.text}`}
                        >
                          {style.label}
                        </span>
                      </p>
                      <p className="mt-0.5 text-xs text-navy-600">{item.detay}</p>
                      {item.oneri && item.oneri !== "İşlem gerekmez." && (
                        <p className={`mt-1 text-xs font-medium ${style.text}`}>Öneri: {item.oneri}</p>
                      )}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </>
      ) : (
        !error &&
        !clientsLoading && (
          <div className="card mt-6 flex flex-col items-center py-14 text-center">
            <h2 className="text-lg font-semibold text-navy-900">Kontrole hazır</h2>
            <p className="mt-2 max-w-md text-sm text-navy-500">
              Mükellef ve dönem seçin; berat ön-kontrolü otomatik çalışır.
            </p>
          </div>
        )
      )}

      {/* Dönem geçmişi */}
      <div className="mt-8">
        <h2 className="text-lg font-bold text-navy-900">Dönem Geçmişi</h2>
        {historyError && <div className="alert-error mt-3">{historyError}</div>}
        {history === null ? (
          <div className="card mt-3 animate-pulse">
            <div className="h-5 w-1/3 rounded bg-navy-100" />
          </div>
        ) : history.length === 0 ? (
          <div className="card mt-3 py-8 text-center text-sm text-navy-500">
            Bu mükellef için henüz e-Defter dönem kaydı yok — ilk kontrol çalıştığında burada görünür.
          </div>
        ) : (
          <div className="mt-3 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
            <table className="w-full min-w-[640px] text-left text-sm">
              <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
                <tr>
                  <th className="px-4 py-3">Dönem</th>
                  <th className="px-4 py-3">Durum</th>
                  <th className="px-4 py-3">Son Kontrol</th>
                  <th className="px-4 py-3">Berat İşareti</th>
                  <th className="px-4 py-3">Not</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-navy-50">
                {history.map((p) => (
                  <tr key={p.id}>
                    <td className="px-4 py-3 font-mono font-medium text-navy-900">{p.period}</td>
                    <td className="px-4 py-3">
                      <span
                        className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                          statusBadges[p.status] ?? "bg-navy-100 text-navy-600"
                        }`}
                      >
                        {p.statusLabel}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-navy-600">
                      {p.checkedAt ? new Date(p.checkedAt).toLocaleString("tr-TR") : "—"}
                    </td>
                    <td className="px-4 py-3 text-navy-600">
                      {p.markedUploadedAt ? new Date(p.markedUploadedAt).toLocaleString("tr-TR") : "—"}
                    </td>
                    <td className="px-4 py-3 text-navy-600">{p.note ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
