"use client";

import { useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  downloadTrialBalanceXlsx,
  getTrialBalance,
  TrialBalanceResult,
} from "@/lib/api";

/** Önceki ay "yyyy-MM" (kapanan dönemin mizanı incelenir). */
function defaultPeriod(): string {
  const d = new Date();
  d.setMonth(d.getMonth() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

function money(value: number): string {
  return value.toLocaleString("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/**
 * Faz 11 — E8: Mizan & uyum kontrolü. Onaylı fatura + fişlerden hesap bazlı mizan
 * (Luca fiş çekirdeğiyle birebir aynı dağılım), denge rozeti, uyum uyarı bantları ve
 * Excel indirme.
 */
export default function TrialBalancePage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const [clientId, setClientId] = useState<string>("");
  const [period, setPeriod] = useState<string>(defaultPeriod());

  const [result, setResult] = useState<TrialBalanceResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [downloading, setDownloading] = useState(false);

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

  // Mükellef + dönem seçilince mizan otomatik yüklenir.
  useEffect(() => {
    if (!clientId || !/^\d{4}-(0[1-9]|1[0-2])$/.test(period)) return;
    let cancelled = false;
    (async () => {
      setLoading(true);
      setError(null);
      try {
        const data = await getTrialBalance(clientId, period);
        if (!cancelled) setResult(data);
      } catch (err) {
        if (!cancelled) {
          setResult(null);
          setError(err instanceof Error ? err.message : "Mizan yüklenirken bir hata oluştu.");
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [clientId, period]);

  async function handleDownload() {
    if (!clientId) return;
    setDownloading(true);
    setError(null);
    try {
      const { blob, fileName } = await downloadTrialBalanceXlsx(clientId, period);
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = fileName ?? `mizan-${period}.xlsx`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Excel indirilirken bir hata oluştu.");
    } finally {
      setDownloading(false);
    }
  }

  const isEmpty = result !== null && result.rows.length === 0;

  return (
    <div>
      <h1 className="text-2xl font-bold text-navy-900">Mizan</h1>
      <p className="mt-1 text-sm text-navy-500">
        Onaylı fatura ve fişlerden hesap bazlı mizan — Luca&apos;ya aktarılan fişlerle birebir aynı dağılım.
      </p>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}
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
          <button
            onClick={handleDownload}
            className="btn-secondary"
            disabled={downloading || loading || !result || isEmpty}
            title={isEmpty ? "Boş mizan indirilemez" : undefined}
          >
            {downloading ? "İndiriliyor..." : "Excel İndir"}
          </button>
        </div>
        {!clientsLoading && clients.length === 0 && (
          <p className="mt-3 text-xs text-amber-700">
            Mizan için önce Mükellefler sayfasından mükellef ekleyin.
          </p>
        )}
      </div>

      {/* Yükleniyor iskeleti */}
      {loading ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
            </div>
          ))}
        </div>
      ) : result ? (
        isEmpty ? (
          <div className="card mt-6 flex flex-col items-center py-14 text-center">
            <h2 className="text-lg font-semibold text-navy-900">Bu dönemde onaylı belge yok</h2>
            <p className="mt-2 max-w-md text-sm text-navy-500">
              Mizan, dönemdeki ONAYLI fatura ve fişlerden üretilir. Önce faturaları kodlayıp onaylayın
              veya fiş yükleyin.
            </p>
          </div>
        ) : (
          <>
            {/* Denge rozeti + belge sayıları */}
            <div className="card mt-6 flex flex-wrap items-center justify-between gap-4">
              <div className="flex items-center gap-4">
                {result.denk ? (
                  <span className="rounded-full bg-green-100 px-4 py-1.5 text-sm font-bold text-green-700">
                    Denk ✓
                  </span>
                ) : (
                  <span className="rounded-full bg-red-100 px-4 py-1.5 text-sm font-bold text-red-700">
                    Dengesiz ✗ — fark {money(Math.abs(result.fark))} ₺
                  </span>
                )}
                <div className="text-sm text-navy-600">
                  Borç <strong>{money(result.toplamBorc)} ₺</strong> · Alacak{" "}
                  <strong>{money(result.toplamAlacak)} ₺</strong>
                </div>
              </div>
              <p className="text-xs text-navy-400">
                {result.faturaSayisi} onaylı fatura + {result.fisSayisi} onaylı fiş işlendi
              </p>
            </div>

            {/* Uyum kontrol bantları */}
            {result.uyumKontrolleri.length > 0 && (
              <div className="mt-4 space-y-2">
                {result.uyumKontrolleri.map((check) => (
                  <div
                    key={check.ad}
                    className={`rounded-lg border px-4 py-3 text-sm ${
                      check.durum === "Ok"
                        ? "border-green-200 bg-green-50 text-green-800"
                        : "border-amber-200 bg-amber-50 text-amber-800"
                    }`}
                  >
                    <span className="font-semibold">
                      {check.durum === "Ok" ? "✓" : "⚠"} {check.ad}:
                    </span>{" "}
                    {check.detay}
                  </div>
                ))}
              </div>
            )}

            {/* Şablon uyarıları (hesapsız satır vb.) */}
            {result.uyarilar.length > 0 && (
              <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-xs text-amber-800">
                <p className="font-semibold">Belge uyarıları:</p>
                <ul className="mt-1 list-inside list-disc space-y-0.5">
                  {result.uyarilar.map((u, i) => (
                    <li key={i}>{u}</li>
                  ))}
                </ul>
              </div>
            )}

            {/* Mizan tablosu */}
            <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
              <table className="w-full min-w-[760px] text-left text-sm">
                <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
                  <tr>
                    <th className="px-4 py-3">Hesap Kodu</th>
                    <th className="px-4 py-3">Hesap Adı</th>
                    <th className="px-4 py-3 text-right">Borç</th>
                    <th className="px-4 py-3 text-right">Alacak</th>
                    <th className="px-4 py-3 text-right">Borç Bakiye</th>
                    <th className="px-4 py-3 text-right">Alacak Bakiye</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-navy-50">
                  {result.rows.map((row) => (
                    <tr key={row.hesapKodu} className={row.hesapKodu === "(kodsuz)" ? "bg-amber-50/60" : ""}>
                      <td className="px-4 py-2.5 font-mono font-medium text-navy-900">{row.hesapKodu}</td>
                      <td className="px-4 py-2.5 text-navy-600">{row.hesapAdi || "—"}</td>
                      <td className="px-4 py-2.5 text-right font-mono">
                        {row.borc > 0 ? money(row.borc) : ""}
                      </td>
                      <td className="px-4 py-2.5 text-right font-mono">
                        {row.alacak > 0 ? money(row.alacak) : ""}
                      </td>
                      <td className="px-4 py-2.5 text-right font-mono">
                        {row.borcBakiye > 0 ? money(row.borcBakiye) : ""}
                      </td>
                      <td className="px-4 py-2.5 text-right font-mono">
                        {row.alacakBakiye > 0 ? money(row.alacakBakiye) : ""}
                      </td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr className="border-t border-navy-200 bg-navy-50 font-semibold text-navy-900">
                    <td className="px-4 py-3" colSpan={2}>
                      TOPLAM
                    </td>
                    <td className="px-4 py-3 text-right font-mono">{money(result.toplamBorc)}</td>
                    <td className="px-4 py-3 text-right font-mono">{money(result.toplamAlacak)}</td>
                    <td className="px-4 py-3 text-right font-mono">
                      {result.fark > 0 ? money(result.fark) : ""}
                    </td>
                    <td className="px-4 py-3 text-right font-mono">
                      {result.fark < 0 ? money(Math.abs(result.fark)) : ""}
                    </td>
                  </tr>
                </tfoot>
              </table>
            </div>
          </>
        )
      ) : (
        !error &&
        !clientsLoading && (
          <div className="card mt-6 flex flex-col items-center py-14 text-center">
            <h2 className="text-lg font-semibold text-navy-900">Mizana hazır</h2>
            <p className="mt-2 max-w-md text-sm text-navy-500">
              Mükellef ve dönem seçin; mizan otomatik hesaplanır.
            </p>
          </div>
        )
      )}
    </div>
  );
}
