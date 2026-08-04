"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import {
  api,
  bulkApproveEInvoices,
  ClientResponse,
  EInvoiceListItem,
  getReviewQueue,
} from "@/lib/api";
import AccountCodeText from "@/components/AccountCodeText";

function formatDate(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

function formatMoney(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat("tr-TR", {
      style: "currency",
      currency: currency || "TRY",
      minimumFractionDigits: 2,
    }).format(value);
  } catch {
    return `${value.toFixed(2)} ${currency}`;
  }
}

/**
 * Onay Kuyruğu: motorun düşük güvenle kodladığı (NeedsReview) faturalar.
 * Satırdaki "Faturaya Git" detay çekmecesini otomatik açar (?fatura=<id>).
 * Seçim kutuları + "Toplu Onayla" ile önerilen kodlar düzeltmesiz kesinleştirilir.
 */
export default function ReviewQueuePage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);
  const [selectedClientId, setSelectedClientId] = useState<string>("");

  const [items, setItems] = useState<EInvoiceListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Satır seçimi + toplu onay
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [bulkApproving, setBulkApproving] = useState(false);
  const [bulkResult, setBulkResult] = useState<string | null>(null);
  const [bulkError, setBulkError] = useState<string | null>(null);

  // Mükellefleri yükle
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const data = await api<ClientResponse[]>("/api/clients");
        if (!cancelled) setClients(data);
      } catch (err) {
        if (!cancelled)
          setClientsError(
            err instanceof Error ? err.message : "Mükellefler yüklenirken bir hata oluştu."
          );
      } finally {
        if (!cancelled) setClientsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const loadQueue = useCallback(async (clientId: string) => {
    setLoading(true);
    setError(null);
    try {
      const data = await getReviewQueue(clientId || undefined);
      setItems(data);
      // Liste yenilenince seçim sıfırlanır (mükellef değişimi / toplu onay sonrası).
      setSelectedIds(new Set());
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Onay kuyruğu yüklenirken bir hata oluştu."
      );
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadQueue(selectedClientId);
  }, [selectedClientId, loadQueue]);

  function toggleSelected(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  const allSelected = items.length > 0 && items.every((i) => selectedIds.has(i.id));

  function toggleSelectAll() {
    setSelectedIds(allSelected ? new Set() : new Set(items.map((i) => i.id)));
  }

  /** "Toplu Onayla": onay penceresi → bulk-approve → sonuç bandı → kuyruk yenile. */
  async function handleBulkApprove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    const confirmed = window.confirm(
      `${ids.length} fatura önerilen kodlarla onaylanacak. Motor bu eşlemeleri öğrenecek. Devam edilsin mi?`
    );
    if (!confirmed) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkApproving(true);
    try {
      const result = await bulkApproveEInvoices(ids);
      setBulkResult(result.message);
      await loadQueue(selectedClientId);
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Toplu onay sırasında bir hata oluştu.");
    } finally {
      setBulkApproving(false);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Onay Kuyruğu</h1>
          <p className="mt-1 text-sm text-navy-500">
            Motorun düşük güvenle kodladığı faturalar. Onayladıkça sistem öğrenir, kuyruk kısalır.
          </p>
        </div>
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}

      {/* Mükellef seçici */}
      <div className="mt-6 flex flex-wrap items-center gap-4">
        <select
          className="input max-w-[260px]"
          value={selectedClientId}
          onChange={(e) => setSelectedClientId(e.target.value)}
          disabled={clientsLoading}
        >
          <option value="">
            {clientsLoading ? "Yükleniyor..." : "Tüm mükellefler"}
          </option>
          {clients.map((c) => (
            <option key={c.id} value={c.id}>
              {c.title}
            </option>
          ))}
        </select>
        {!loading && items.length > 0 && (
          <span className="text-sm text-navy-500">{items.length} fatura onay bekliyor</span>
        )}
      </div>

      {/* Toplu onay sonuç bandı */}
      {bulkResult && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {bulkResult}
        </div>
      )}
      {bulkError && <div className="alert-error mt-4">{bulkError}</div>}

      {/* Seçim varken yapışkan toplu işlem çubuğu */}
      {selectedIds.size > 0 && (
        <div className="sticky top-0 z-30 mt-4 flex flex-wrap items-center gap-3 rounded-xl border border-navy-200 bg-white px-4 py-3 shadow-md">
          <span className="text-sm font-semibold text-navy-900">
            {selectedIds.size} fatura seçili
          </span>
          <button
            onClick={handleBulkApprove}
            className="btn-primary flex items-center gap-2 !px-3 !py-1.5 text-xs"
            disabled={bulkApproving}
            title="Seçilen faturaların önerilen kodlarını düzeltmesiz onaylar; motor bu eşlemeleri öğrenir."
          >
            {bulkApproving && (
              <span className="h-3 w-3 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {bulkApproving ? "Onaylanıyor..." : "Toplu Onayla"}
          </button>
          <button
            onClick={() => setSelectedIds(new Set())}
            className="text-xs font-semibold text-navy-500 hover:text-navy-700"
            disabled={bulkApproving}
          >
            Vazgeç
          </button>
        </div>
      )}

      {error && (
        <div className="alert-error mt-4 flex items-center justify-between gap-4">
          <span>{error}</span>
          <button
            className="btn-secondary !px-3 !py-1.5 text-xs"
            onClick={() => loadQueue(selectedClientId)}
          >
            Tekrar dene
          </button>
        </div>
      )}

      {/* Yükleniyor iskeleti */}
      {loading ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
              <div className="mt-3 h-4 w-1/4 rounded bg-navy-50" />
            </div>
          ))}
        </div>
      ) : items.length === 0 && !error ? (
        /* Boş durum */
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          <h2 className="text-lg font-semibold text-navy-900">Onay bekleyen fatura yok 🎉</h2>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            Tüm faturalar ya otomatik kodlandı ya da onaylandı. Yeni faturaları kodlamak için
            Faturalar sayfasından &quot;Dönemi Kodla&quot; kullanabilirsiniz.
          </p>
          <Link href="/panel/faturalar" className="btn-secondary mt-5 !px-4 !py-2 text-sm">
            Faturalara Git
          </Link>
        </div>
      ) : items.length > 0 ? (
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[980px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="w-10 px-4 py-3">
                  <input
                    type="checkbox"
                    className="h-4 w-4 rounded border-navy-300"
                    aria-label="Listedeki tüm faturaları seç"
                    checked={allSelected}
                    onChange={toggleSelectAll}
                  />
                </th>
                <th className="px-4 py-3">Tarih</th>
                <th className="px-4 py-3">Fatura No</th>
                <th className="px-4 py-3">Satıcı</th>
                <th className="px-4 py-3">Önerilen Kod</th>
                <th className="px-4 py-3 text-right">Toplam</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {items.map((invoice) => (
                <tr key={invoice.id} className="transition hover:bg-navy-50/60">
                  <td className="px-4 py-3">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-navy-300"
                      aria-label={`${invoice.invoiceNumber} numaralı faturayı seç`}
                      checked={selectedIds.has(invoice.id)}
                      onChange={() => toggleSelected(invoice.id)}
                    />
                  </td>
                  <td className="px-4 py-3">{formatDate(invoice.issueDate)}</td>
                  <td className="px-4 py-3 font-mono text-xs">{invoice.invoiceNumber}</td>
                  <td className="px-4 py-3">
                    <span className="font-medium text-navy-900">{invoice.senderTitle}</span>
                    <span className="block font-mono text-xs text-navy-400">
                      VKN: {invoice.senderTaxId}
                    </span>
                  </td>
                  {/* Kuyruktaki faturalar NeedsReview → kodlar her zaman öneri görünümünde. */}
                  <td className="px-4 py-3">
                    <AccountCodeText
                      value={invoice.accountCodeSummary}
                      approved={false}
                      showSuggestionTag
                    />
                  </td>
                  <td className="px-4 py-3 text-right font-semibold text-navy-900">
                    {formatMoney(invoice.payableAmount, invoice.currencyCode)}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <Link
                      href={`/panel/faturalar?fatura=${invoice.id}`}
                      className="btn-primary !px-3 !py-1.5 text-xs"
                    >
                      Faturaya Git
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </div>
  );
}
