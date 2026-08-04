"use client";

import { useCallback, useEffect, useState } from "react";
import {
  cancelJob,
  listJobs,
  OutboundJobItem,
  OutboundJobList,
  OutboundJobStatusCode,
  retryJobNow,
} from "@/lib/api";

const statusBadges: Record<OutboundJobStatusCode, { label: string; cls: string }> = {
  Bekliyor: { label: "Bekliyor", cls: "bg-amber-100 text-amber-700" },
  Calisiyor: { label: "Çalışıyor", cls: "bg-blue-100 text-blue-700" },
  Basarili: { label: "Başarılı", cls: "bg-green-100 text-green-700" },
  Basarisiz: { label: "Başarısız", cls: "bg-red-100 text-red-700" },
  Iptal: { label: "İptal", cls: "bg-navy-100 text-navy-600" },
};

const statusFilters: { value: "" | OutboundJobStatusCode; label: string }[] = [
  { value: "", label: "Tümü" },
  { value: "Bekliyor", label: "Bekleyen" },
  { value: "Basarili", label: "Başarılı" },
  { value: "Basarisiz", label: "Başarısız" },
  { value: "Iptal", label: "İptal" },
];

/**
 * Faz 11 — E6: Gönderim kuyruğu ekranı. GİB/entegratör çöktüğünde işler burada bekler ve
 * sistem müsait olduğunda otomatik yeniden denenir (1, 5, 15, 60, 180 dk geri çekilme).
 * Liste 15 saniyede bir kendini tazeler (worker tarama aralığıyla uyumlu).
 */
export default function OutboundQueuePage() {
  const [data, setData] = useState<OutboundJobList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<"" | OutboundJobStatusCode>("");
  const [rowBusy, setRowBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const load = useCallback(
    async (silent = false) => {
      if (!silent) setLoading(true);
      try {
        const result = await listJobs(statusFilter || undefined);
        setData(result);
        setError(null);
      } catch (err) {
        // Sessiz tazelemede mevcut liste korunur; ilk yüklemede hata gösterilir.
        if (!silent)
          setError(err instanceof Error ? err.message : "Kuyruk yüklenirken bir hata oluştu.");
      } finally {
        if (!silent) setLoading(false);
      }
    },
    [statusFilter]
  );

  useEffect(() => {
    load();
  }, [load]);

  // Otomatik tazeleme: worker 15 sn'de bir çalışır; ekran da aynı ritimde güncellenir.
  useEffect(() => {
    const timer = setInterval(() => load(true), 15000);
    return () => clearInterval(timer);
  }, [load]);

  async function handleCancel(job: OutboundJobItem) {
    setRowBusy(job.id);
    setMessage(null);
    setError(null);
    try {
      await cancelJob(job.id);
      setMessage("İş iptal edildi.");
      await load(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : "İptal sırasında bir hata oluştu.");
    } finally {
      setRowBusy(null);
    }
  }

  async function handleRetryNow(job: OutboundJobItem) {
    setRowBusy(job.id);
    setMessage(null);
    setError(null);
    try {
      await retryJobNow(job.id);
      setMessage("İş sıraya alındı; en geç birkaç saniye içinde denenecek.");
      await load(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Yeniden deneme sırasında bir hata oluştu.");
    } finally {
      setRowBusy(null);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Gönderim Kuyruğu</h1>
          <p className="mt-1 text-sm text-navy-500">
            GİB/entegratör sistemleri çöktüğünde gönderimler burada bekler ve otomatik yeniden denenir —
            gece ekran başında beklemeye gerek yok.
          </p>
        </div>
        <button onClick={() => load()} className="btn-secondary" disabled={loading}>
          {loading ? "Yükleniyor..." : "Yenile"}
        </button>
      </div>

      {message && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {message}
        </div>
      )}
      {error && <div className="alert-error mt-4">{error}</div>}

      {/* Filtre */}
      <div className="mt-6 flex flex-wrap items-center gap-2">
        {statusFilters.map((f) => (
          <button
            key={f.value}
            onClick={() => setStatusFilter(f.value)}
            className={`rounded-full px-3 py-1.5 text-xs font-semibold transition ${
              statusFilter === f.value
                ? "bg-navy-900 text-white"
                : "bg-white text-navy-600 ring-1 ring-navy-200 hover:bg-navy-50"
            }`}
          >
            {f.label}
          </button>
        ))}
        {data && data.pendingCount > 0 && (
          <span className="ml-auto text-xs font-medium text-amber-700">
            {data.pendingCount} iş sırada — sistem müsait olduğunda otomatik gönderilecek
          </span>
        )}
      </div>

      {/* Liste */}
      {loading && !data ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
            </div>
          ))}
        </div>
      ) : !data || data.items.length === 0 ? (
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          <h2 className="text-lg font-semibold text-navy-900">
            {statusFilter ? "Bu durumda iş yok" : "Kuyruk boş ✓"}
          </h2>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            {statusFilter
              ? "Filtreyi değiştirerek diğer işleri görebilirsiniz."
              : "Bekleyen gönderim yok. Beyanname gönderiminde \"Kuyruğa Al\" seçeneğini kullandığınızda veya bir gönderim geçici hata aldığında işler burada görünür."}
          </p>
        </div>
      ) : (
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[860px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">İş</th>
                <th className="px-4 py-3">Durum</th>
                <th className="px-4 py-3">Deneme</th>
                <th className="px-4 py-3">Sonraki Deneme</th>
                <th className="px-4 py-3">Son Hata</th>
                <th className="px-4 py-3 text-right">İşlemler</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {data.items.map((job) => {
                const badge = statusBadges[job.status] ?? statusBadges.Bekliyor;
                const actionable = job.status === "Bekliyor" || job.status === "Basarisiz";
                return (
                  <tr key={job.id}>
                    <td className="px-4 py-3">
                      <p className="font-medium text-navy-900">{job.typeLabel}</p>
                      <p className="text-xs text-navy-400">
                        Oluşturma: {new Date(job.createdAt).toLocaleString("tr-TR")}
                      </p>
                    </td>
                    <td className="px-4 py-3">
                      <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${badge.cls}`}>
                        {badge.label}
                      </span>
                    </td>
                    <td className="px-4 py-3 font-mono text-navy-700">
                      {job.attemptCount}/{job.maxAttempts}
                    </td>
                    <td className="px-4 py-3 text-navy-600">
                      {job.status === "Bekliyor"
                        ? new Date(job.nextAttemptAt).toLocaleString("tr-TR")
                        : job.completedAt
                          ? `Bitti: ${new Date(job.completedAt).toLocaleString("tr-TR")}`
                          : "—"}
                    </td>
                    <td className="max-w-[280px] px-4 py-3 text-xs text-red-700">
                      {job.lastError ?? <span className="text-navy-400">—</span>}
                    </td>
                    <td className="px-4 py-3 text-right">
                      {actionable ? (
                        <div className="flex justify-end gap-2">
                          <button
                            onClick={() => handleRetryNow(job)}
                            disabled={rowBusy === job.id}
                            className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50 disabled:opacity-50"
                          >
                            {rowBusy === job.id ? "İşleniyor..." : "Şimdi Dene"}
                          </button>
                          <button
                            onClick={() => handleCancel(job)}
                            disabled={rowBusy === job.id}
                            className="rounded-md px-2.5 py-1 text-xs font-semibold text-red-600 hover:bg-red-50 disabled:opacity-50"
                          >
                            İptal
                          </button>
                        </div>
                      ) : (
                        <span className="text-xs text-navy-400">—</span>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
