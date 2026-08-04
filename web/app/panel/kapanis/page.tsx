"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  getPeriodCloseChecklist,
  PeriodCheckStatusCode,
  PeriodCloseChecklist,
  prepareDeclaration,
} from "@/lib/api";

/** Önceki ay "yyyy-MM" (kapanan dönem kontrol edilir). */
function defaultPeriod(): string {
  const d = new Date();
  d.setMonth(d.getMonth() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

const statusStyles: Record<
  PeriodCheckStatusCode,
  { icon: string; card: string; text: string; label: string }
> = {
  Ok: { icon: "✓", card: "border-green-200 bg-green-50/50", text: "text-green-700", label: "Tamam" },
  Uyari: { icon: "⚠", card: "border-amber-200 bg-amber-50/50", text: "text-amber-700", label: "Dikkat" },
  Hata: { icon: "✗", card: "border-red-200 bg-red-50/50", text: "text-red-700", label: "Engel" },
};

/** "Beyana Hazırlık" yüzde halkası (SVG). */
function PercentRing({ percent, hasBlocker }: { percent: number; hasBlocker: boolean }) {
  const radius = 52;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference * (1 - Math.min(Math.max(percent, 0), 100) / 100);
  const color = hasBlocker ? "#b91c1c" : percent >= 85 ? "#15803d" : "#b45309";

  return (
    <div className="relative h-32 w-32">
      <svg viewBox="0 0 120 120" className="h-32 w-32 -rotate-90">
        <circle cx="60" cy="60" r={radius} fill="none" stroke="#e2e8f0" strokeWidth="10" />
        <circle
          cx="60"
          cy="60"
          r={radius}
          fill="none"
          stroke={color}
          strokeWidth="10"
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
        />
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center">
        <span className="text-2xl font-extrabold" style={{ color }}>
          %{percent}
        </span>
        <span className="text-[10px] font-medium text-navy-500">hazır</span>
      </div>
    </div>
  );
}

/**
 * Faz 8 — B10: Dönem Sonu Kontrol Listesi. Mükellef + dönem seçilince tüm kontroller
 * çalışır; her satır ilgili sayfaya link verir. Üstte "Beyana Hazırlık" yüzde halkası,
 * engel yoksa "KDV Taslağı Hazırla" kısayolu.
 */
export default function PeriodClosePage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const [clientId, setClientId] = useState<string>("");
  const [period, setPeriod] = useState<string>(defaultPeriod());

  const [checklist, setChecklist] = useState<PeriodCloseChecklist | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [preparing, setPreparing] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

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

  // Mükellef + dönem seçilince kontrol listesi otomatik yüklenir.
  useEffect(() => {
    if (!clientId || !/^\d{4}-(0[1-9]|1[0-2])$/.test(period)) return;
    let cancelled = false;
    (async () => {
      setLoading(true);
      setError(null);
      setMessage(null);
      try {
        const data = await getPeriodCloseChecklist(clientId, period);
        if (!cancelled) setChecklist(data);
      } catch (err) {
        if (!cancelled) {
          setChecklist(null);
          setError(err instanceof Error ? err.message : "Kontrol listesi yüklenirken bir hata oluştu.");
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [clientId, period]);

  async function handlePrepareKdv() {
    if (!clientId) return;
    setMessage(null);
    setError(null);
    setPreparing(true);
    try {
      const result = await prepareDeclaration(clientId, "KDV1", period);
      setMessage(`${result.message} Beyannameler sayfasından gönderebilirsiniz.`);
      // Taslak hazırlanınca kontrol listesi tazelenir.
      setChecklist(await getPeriodCloseChecklist(clientId, period));
    } catch (err) {
      setError(err instanceof Error ? err.message : "KDV taslağı hazırlanırken bir hata oluştu.");
    } finally {
      setPreparing(false);
    }
  }

  return (
    <div>
      <div>
        <h1 className="text-2xl font-bold text-navy-900">Dönem Kapanışı</h1>
        <p className="mt-1 text-sm text-navy-500">
          Beyanname öncesi tüm kontroller tek listede: &quot;beyana hazır mıyım?&quot; sorusunun cevabı.
        </p>
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
        </div>
        {!clientsLoading && clients.length === 0 && (
          <p className="mt-3 text-xs text-amber-700">
            Kontrol listesi için önce Mükellefler sayfasından mükellef ekleyin.
          </p>
        )}
      </div>

      {/* Yükleniyor iskeleti */}
      {loading ? (
        <div className="mt-6 space-y-3">
          <div className="card animate-pulse">
            <div className="h-24 w-24 rounded-full bg-navy-100" />
          </div>
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
            </div>
          ))}
        </div>
      ) : checklist ? (
        <>
          {/* Üst kart: yüzde halkası + KDV kısayolu */}
          <div className="card mt-6 flex flex-wrap items-center gap-8">
            <PercentRing percent={checklist.percent} hasBlocker={checklist.hasBlocker} />
            <div className="min-w-[220px] flex-1">
              <h2 className="text-lg font-bold text-navy-900">Beyana Hazırlık</h2>
              <p className="mt-1 text-sm text-navy-500">
                {checklist.hasBlocker
                  ? "Beyana engel durumlar var (kırmızı satırlar). Önce onları çözün."
                  : checklist.percent === 100
                    ? "Tüm kontroller temiz — dönem beyana hazır ✓"
                    : "Engel yok; sarı satırlar kontrol edilirse daha güvenli."}
              </p>
              {checklist.readyForDeclaration && (
                <button
                  onClick={handlePrepareKdv}
                  className="btn-primary mt-4 flex items-center gap-2"
                  disabled={preparing}
                >
                  {preparing && (
                    <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                  )}
                  {preparing ? "Hazırlanıyor..." : "KDV Taslağı Hazırla"}
                </button>
              )}
            </div>
          </div>

          {/* Kontrol kartları */}
          <div className="mt-6 space-y-3">
            {checklist.items.map((item) => {
              const style = statusStyles[item.status] ?? statusStyles.Uyari;
              return (
                <div
                  key={item.key}
                  className={`flex flex-wrap items-center justify-between gap-3 rounded-xl border p-4 shadow-sm ${style.card}`}
                >
                  <div className="flex items-start gap-3">
                    <span className={`text-lg font-bold ${style.text}`}>{style.icon}</span>
                    <div>
                      <p className="text-sm font-semibold text-navy-900">
                        {item.name}
                        <span className={`ml-2 rounded-full bg-white/70 px-2 py-0.5 text-[10px] font-bold ${style.text}`}>
                          {style.label}
                        </span>
                      </p>
                      <p className="mt-0.5 text-xs text-navy-600">{item.detail}</p>
                    </div>
                  </div>
                  <Link href={item.link} className="btn-secondary !px-3 !py-1.5 text-xs">
                    Sayfaya git
                  </Link>
                </div>
              );
            })}
          </div>
        </>
      ) : (
        !error &&
        !clientsLoading && (
          /* Boş durum (mükellef yok / seçim bekleniyor) */
          <div className="card mt-6 flex flex-col items-center py-14 text-center">
            <h2 className="text-lg font-semibold text-navy-900">Kontrole hazır</h2>
            <p className="mt-2 max-w-md text-sm text-navy-500">
              Mükellef ve dönem seçin; kontrol listesi otomatik çalışır.
            </p>
          </div>
        )
      )}
    </div>
  );
}
