"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import {
  analyzeReconciliation,
  api,
  ClientResponse,
  dismissReconciliationFinding,
  ReconciliationFinding,
  ReconciliationReport,
} from "@/lib/api";

function formatDate(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

function formatTL(value: number): string {
  return (
    new Intl.NumberFormat("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value) + " ₺"
  );
}

/** Önceki ay "yyyy-MM" (kapanan dönem analiz edilir). */
function defaultPeriod(): string {
  const d = new Date();
  d.setMonth(d.getMonth() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

const kindStyles: Record<string, { border: string; badge: string; label: string }> = {
  Mukerrer: { border: "border-red-200 bg-red-50/50", badge: "bg-red-100 text-red-700", label: "Mükerrer" },
  Eksik: { border: "border-amber-200 bg-amber-50/50", badge: "bg-amber-100 text-amber-700", label: "Eksik" },
  Tutarsiz: { border: "border-orange-200 bg-orange-50/50", badge: "bg-orange-100 text-orange-700", label: "Tutarsız" },
};

function FindingCard({
  finding,
  onDismiss,
  dismissing,
}: {
  finding: ReconciliationFinding;
  onDismiss: (key: string) => void;
  dismissing: string | null;
}) {
  const style = kindStyles[finding.kind] ?? kindStyles.Eksik;
  return (
    <div className={`rounded-xl border p-4 shadow-sm ${style.border}`}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <span className={`rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide ${style.badge}`}>
            {style.label}
          </span>
          <h3 className="mt-2 text-sm font-semibold text-navy-900">{finding.title}</h3>
          <p className="mt-1 text-xs text-navy-600">{finding.suggestion}</p>
        </div>
        <button
          className="btn-secondary flex items-center gap-1.5 !px-3 !py-1.5 text-xs"
          onClick={() => onDismiss(finding.key)}
          disabled={dismissing !== null}
          title="Bu bulguyu kalıcı olarak gizler (sonraki analizlerde gösterilmez)."
        >
          {dismissing === finding.key && (
            <span className="h-3 w-3 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
          )}
          Yoksay
        </button>
      </div>

      {finding.documents.length > 0 && (
        <ul className="mt-3 divide-y divide-navy-50 rounded-lg border border-navy-100 bg-white">
          {finding.documents.map((doc) => (
            <li key={doc.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2 text-xs">
              <span className="text-navy-800">
                <span className="mr-2 rounded bg-navy-100 px-1.5 py-0.5 font-semibold text-navy-600">
                  {doc.docType}
                </span>
                {doc.label}
              </span>
              <span className="flex items-center gap-3">
                <span className="text-navy-500">{formatDate(doc.date)}</span>
                <span className="font-semibold text-navy-900">{formatTL(doc.amount)}</span>
                <Link
                  href={
                    doc.docType === "Fatura"
                      ? `/panel/faturalar?fatura=${doc.id}`
                      : "/panel/fisler"
                  }
                  className="font-semibold text-navy-600 underline hover:text-navy-900"
                >
                  {doc.docType === "Fatura" ? "Faturaya git" : "Fişlere git"}
                </Link>
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/**
 * Faz 8 — B5: Mükerrer / Eksik Fatura Mutabakatı. Mükellef + dönem seçilir, "Analiz Et"
 * ile bulgular gruplu kartlarda gösterilir (kırmızı mükerrer / sarı eksik / turuncu tutarsız).
 * "Yoksay" DB'ye yazılır (ReconciliationDismissal) — bulgu sonraki analizlerde gizlenir.
 */
export default function ReconciliationPage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const [clientId, setClientId] = useState<string>("");
  const [period, setPeriod] = useState<string>(defaultPeriod());

  const [report, setReport] = useState<ReconciliationReport | null>(null);
  const [analyzing, setAnalyzing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [dismissing, setDismissing] = useState<string | null>(null);

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

  async function handleAnalyze() {
    setError(null);
    setMessage(null);

    // Çift validasyon (backend de aynı kuralları doğrular).
    if (!clientId) {
      setError("Lütfen mükellef seçin.");
      return;
    }
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(period)) {
      setError("Dönem biçimi geçersiz. Örnek: 2026-06.");
      return;
    }

    setAnalyzing(true);
    try {
      setReport(await analyzeReconciliation(clientId, period));
    } catch (err) {
      setReport(null);
      setError(err instanceof Error ? err.message : "Analiz sırasında bir hata oluştu.");
    } finally {
      setAnalyzing(false);
    }
  }

  async function handleDismiss(key: string) {
    setError(null);
    setMessage(null);
    setDismissing(key);
    try {
      const result = await dismissReconciliationFinding(key);
      setMessage(result.message);
      // Bulguyu yerel rapordan hemen düş (yeniden analiz gerekmez).
      setReport((prev) =>
        prev
          ? {
              ...prev,
              mukerrer: prev.mukerrer.filter((f) => f.key !== key),
              eksik: prev.eksik.filter((f) => f.key !== key),
              tutarsiz: prev.tutarsiz.filter((f) => f.key !== key),
              dismissedCount: prev.dismissedCount + 1,
            }
          : prev
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Bulgu yok sayılırken bir hata oluştu.");
    } finally {
      setDismissing(null);
    }
  }

  const findings = report ? [...report.mukerrer, ...report.eksik, ...report.tutarsiz] : [];

  return (
    <div>
      <div>
        <h1 className="text-2xl font-bold text-navy-900">Mutabakat</h1>
        <p className="mt-1 text-sm text-navy-500">
          Mükerrer, eksik ve tutarsız faturaları dönem bazında tespit edin. (BA/BS formları
          kaldırıldığı için bu bütünlük kontrolü artık daha kritik.)
        </p>
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}
      {message && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {message}
        </div>
      )}
      {error && <div className="alert-error mt-4">{error}</div>}

      {/* Seçim + analiz */}
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
            onClick={handleAnalyze}
            className="btn-primary flex items-center gap-2"
            disabled={analyzing || clientsLoading || clients.length === 0}
          >
            {analyzing && (
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {analyzing ? "Analiz ediliyor..." : "Analiz Et"}
          </button>
        </div>
        {!clientsLoading && clients.length === 0 && (
          <p className="mt-3 text-xs text-amber-700">
            Analiz için önce Mükellefler sayfasından mükellef ekleyin.
          </p>
        )}
      </div>

      {/* Yükleniyor iskeleti */}
      {analyzing ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
              <div className="mt-3 h-4 w-2/3 rounded bg-navy-50" />
            </div>
          ))}
        </div>
      ) : report ? (
        <>
          {/* Özet şeridi */}
          <div className="mt-6 flex flex-wrap items-center gap-3 text-sm text-navy-600">
            <span>
              Dönemde <strong>{report.invoiceCount}</strong> gelen fatura, <strong>{report.receiptCount}</strong> fiş
              tarandı.
            </span>
            <span className="rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-semibold text-red-700">
              {report.mukerrer.length} mükerrer
            </span>
            <span className="rounded-full bg-amber-100 px-2.5 py-0.5 text-xs font-semibold text-amber-700">
              {report.eksik.length} eksik
            </span>
            <span className="rounded-full bg-orange-100 px-2.5 py-0.5 text-xs font-semibold text-orange-700">
              {report.tutarsiz.length} tutarsız
            </span>
            {report.dismissedCount > 0 && (
              <span className="text-xs text-navy-400">({report.dismissedCount} bulgu yok sayıldı)</span>
            )}
          </div>

          {findings.length === 0 ? (
            /* Boş durum */
            <div className="card mt-4 flex flex-col items-center py-14 text-center">
              <h2 className="text-lg font-semibold text-navy-900">Sorun bulunamadı ✓</h2>
              <p className="mt-2 max-w-md text-sm text-navy-500">
                Bu dönemde mükerrer, eksik veya tutarsız fatura tespit edilmedi.
                {report.dismissedCount > 0 && " (Yok sayılan bulgular gösterilmez.)"}
              </p>
            </div>
          ) : (
            <div className="mt-4 space-y-4">
              {report.mukerrer.map((f) => (
                <FindingCard key={f.key} finding={f} onDismiss={handleDismiss} dismissing={dismissing} />
              ))}
              {report.eksik.map((f) => (
                <FindingCard key={f.key} finding={f} onDismiss={handleDismiss} dismissing={dismissing} />
              ))}
              {report.tutarsiz.map((f) => (
                <FindingCard key={f.key} finding={f} onDismiss={handleDismiss} dismissing={dismissing} />
              ))}
            </div>
          )}
        </>
      ) : (
        /* İlk açılış boş durumu */
        !error && (
          <div className="card mt-6 flex flex-col items-center py-14 text-center">
            <h2 className="text-lg font-semibold text-navy-900">Analize hazır</h2>
            <p className="mt-2 max-w-md text-sm text-navy-500">
              Yukarıdan mükellef ve dönem seçip &quot;Analiz Et&quot; düğmesine basın. Bulgular
              mükerrer (kırmızı), eksik (sarı) ve tutarsız (turuncu) olarak gruplanır.
            </p>
          </div>
        )
      )}
    </div>
  );
}
