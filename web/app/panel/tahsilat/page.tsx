"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";
import {
  api,
  ApiError,
  ClientResponse,
  deleteClientFee,
  FeeInvoiceItem,
  FeeInvoiceStatusCode,
  FeeList,
  generateFees,
  getClientFee,
  listFees,
  markFeePaid,
  reopenFee,
  saveClientFee,
} from "@/lib/api";

/** İçinde bulunulan ay "yyyy-MM" (tahsilat cari ay üzerinden takip edilir). */
function currentPeriod(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

const statusBadges: Record<FeeInvoiceStatusCode, { label: string; cls: string }> = {
  Bekliyor: { label: "Bekliyor", cls: "bg-amber-100 text-amber-700" },
  Odendi: { label: "Ödendi", cls: "bg-green-100 text-green-700" },
  Gecikti: { label: "Gecikti", cls: "bg-red-100 text-red-700" },
};

const statusFilters: { value: "" | FeeInvoiceStatusCode; label: string }[] = [
  { value: "", label: "Tümü" },
  { value: "Bekliyor", label: "Bekleyen" },
  { value: "Odendi", label: "Ödenen" },
  { value: "Gecikti", label: "Geciken" },
];

function money(value: number): string {
  return value.toLocaleString("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/**
 * Faz 11 — E7: SMMM ücret/tahsilat takibi. Dönem seçici + özet kartlar + tahakkuk tablosu +
 * mükellef ücret tanımı modalı. Dürüst kapsam: e-Arşiv hizmet faturası kesimi ve otomatik
 * hatırlatma bu fazda yok — takip elle "Ödendi" işaretiyle yürür.
 */
export default function FeesPage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const [period, setPeriod] = useState<string>(currentPeriod());
  const [statusFilter, setStatusFilter] = useState<"" | FeeInvoiceStatusCode>("");

  const [data, setData] = useState<FeeList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const [generating, setGenerating] = useState(false);
  const [rowBusy, setRowBusy] = useState<string | null>(null);

  // "Ödendi" işaretleme mini formu (tutar düzeltmeli).
  const [payingId, setPayingId] = useState<string | null>(null);
  const [payAmount, setPayAmount] = useState("");

  // Ücret tanımı modalı.
  const [feeModalOpen, setFeeModalOpen] = useState(false);
  const [feeClientId, setFeeClientId] = useState("");
  const [feeAmount, setFeeAmount] = useState("");
  const [feeStartDate, setFeeStartDate] = useState("");
  const [feeNote, setFeeNote] = useState("");
  const [feeExisting, setFeeExisting] = useState(false);
  const [feeLoading, setFeeLoading] = useState(false);
  const [feeSaving, setFeeSaving] = useState(false);
  const [feeDeleting, setFeeDeleting] = useState(false);
  const [feeError, setFeeError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api<ClientResponse[]>("/api/clients");
        if (!cancelled) setClients(list);
      } catch (err) {
        if (!cancelled)
          setClientsError(err instanceof Error ? err.message : "Mükellefler yüklenirken bir hata oluştu.");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const load = useCallback(async () => {
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(period)) return;
    setLoading(true);
    setError(null);
    try {
      const result = await listFees({ period, status: statusFilter || undefined });
      setData(result);
    } catch (err) {
      setData(null);
      setError(err instanceof Error ? err.message : "Tahsilat verileri yüklenirken bir hata oluştu.");
    } finally {
      setLoading(false);
    }
  }, [period, statusFilter]);

  useEffect(() => {
    load();
  }, [load]);

  async function handleGenerate() {
    setGenerating(true);
    setMessage(null);
    setError(null);
    try {
      const result = await generateFees(period);
      setMessage(result.message);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Dönem oluşturulurken bir hata oluştu.");
    } finally {
      setGenerating(false);
    }
  }

  async function handleMarkPaid(invoice: FeeInvoiceItem) {
    // Tutar alanı boşsa tahakkuk tutarı tahsil edilmiş sayılır (backend de aynı kuralı uygular).
    const trimmed = payAmount.trim().replace(",", ".");
    let amount: number | undefined;
    if (trimmed) {
      amount = Number(trimmed);
      if (!Number.isFinite(amount) || amount <= 0) {
        setError("Tahsil edilen tutar 0'dan büyük bir sayı olmalıdır.");
        return;
      }
    }

    setRowBusy(invoice.id);
    setMessage(null);
    setError(null);
    try {
      await markFeePaid(invoice.id, amount);
      setMessage(`${invoice.clientTitle ?? "Mükellef"} — ${invoice.period} tahakkuku ödendi olarak işaretlendi.`);
      setPayingId(null);
      setPayAmount("");
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "İşaretleme sırasında bir hata oluştu.");
    } finally {
      setRowBusy(null);
    }
  }

  async function handleReopen(invoice: FeeInvoiceItem) {
    setRowBusy(invoice.id);
    setMessage(null);
    setError(null);
    try {
      await reopenFee(invoice.id);
      setMessage("Ödendi işareti geri alındı.");
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "İşlem sırasında bir hata oluştu.");
    } finally {
      setRowBusy(null);
    }
  }

  async function openFeeModal(clientId?: string) {
    const targetId = clientId || clients[0]?.id || "";
    setFeeClientId(targetId);
    setFeeAmount("");
    setFeeStartDate("");
    setFeeNote("");
    setFeeExisting(false);
    setFeeError(null);
    setFeeModalOpen(true);
    if (targetId) await loadFeeDefinition(targetId);
  }

  async function loadFeeDefinition(clientId: string) {
    setFeeLoading(true);
    setFeeError(null);
    setFeeExisting(false);
    setFeeAmount("");
    setFeeStartDate("");
    setFeeNote("");
    try {
      const fee = await getClientFee(clientId);
      setFeeExisting(true);
      setFeeAmount(String(fee.monthlyFee));
      setFeeStartDate(fee.startDate.slice(0, 10));
      setFeeNote(fee.note ?? "");
    } catch (err) {
      // 404 = tanım yok → boş form (hata değildir).
      if (!(err instanceof ApiError && err.status === 404)) {
        setFeeError(err instanceof Error ? err.message : "Ücret tanımı yüklenirken bir hata oluştu.");
      }
    } finally {
      setFeeLoading(false);
    }
  }

  async function handleFeeSave(e: FormEvent) {
    e.preventDefault();
    setFeeError(null);

    // UI doğrulaması (backend aynı kuralları ayrıca uygular — çift validasyon).
    if (!feeClientId) {
      setFeeError("Mükellef seçimi zorunludur.");
      return;
    }
    const amount = Number(feeAmount.trim().replace(",", "."));
    if (!Number.isFinite(amount) || amount <= 0) {
      setFeeError("Aylık ücret 0'dan büyük bir sayı olmalıdır.");
      return;
    }

    setFeeSaving(true);
    try {
      await saveClientFee(feeClientId, {
        monthlyFee: amount,
        startDate: feeStartDate || null,
        note: feeNote.trim() || null,
      });
      setFeeModalOpen(false);
      setMessage("Ücret tanımı kaydedildi. \"Dönemi Oluştur\" ile tahakkuk üretebilirsiniz.");
      await load();
    } catch (err) {
      setFeeError(err instanceof Error ? err.message : "Kaydetme sırasında bir hata oluştu.");
    } finally {
      setFeeSaving(false);
    }
  }

  async function handleFeeDelete() {
    if (!feeClientId) return;
    setFeeDeleting(true);
    setFeeError(null);
    try {
      await deleteClientFee(feeClientId);
      setFeeModalOpen(false);
      setMessage("Ücret tanımı silindi (mevcut tahakkuklar korunur).");
      await load();
    } catch (err) {
      setFeeError(err instanceof Error ? err.message : "Silme sırasında bir hata oluştu.");
    } finally {
      setFeeDeleting(false);
    }
  }

  const hasAnyData = (data?.totalCount ?? 0) > 0;

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Tahsilat</h1>
          <p className="mt-1 text-sm text-navy-500">
            Kendi hizmet ücretinizin takibi: mükellef bazlı aylık ücret, tahakkuk ve tahsilat durumu.
          </p>
        </div>
        <div className="flex flex-wrap gap-3">
          <button onClick={() => openFeeModal()} className="btn-secondary" disabled={clients.length === 0}>
            Ücret Tanımla
          </button>
          <button onClick={handleGenerate} className="btn-primary" disabled={generating}>
            {generating ? "Oluşturuluyor..." : "Dönemi Oluştur"}
          </button>
        </div>
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}
      {message && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {message}
        </div>
      )}
      {error && <div className="alert-error mt-4">{error}</div>}

      {/* Dönem + filtre */}
      <div className="mt-6 flex flex-wrap items-end gap-4">
        <div>
          <label className="mb-1 block text-xs font-medium text-navy-600">Dönem</label>
          <input
            type="month"
            className="input !w-auto"
            value={period}
            onChange={(e) => setPeriod(e.target.value)}
          />
        </div>
        <div className="flex flex-wrap gap-2">
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
        </div>
      </div>

      {/* Özet kartlar */}
      <div className="mt-6 grid gap-6 sm:grid-cols-3">
        <div className="card">
          <p className="text-sm font-medium text-navy-500">Beklenen Toplam</p>
          {loading ? (
            <div className="mt-2 h-9 w-24 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-2xl font-extrabold text-navy-950">
              {data ? `${money(data.expectedTotal)} ₺` : "—"}
            </p>
          )}
          <p className="mt-2 text-xs text-navy-400">{data?.totalCount ?? 0} tahakkuk</p>
        </div>
        <div className="card">
          <p className="text-sm font-medium text-navy-500">Tahsil Edilen</p>
          {loading ? (
            <div className="mt-2 h-9 w-24 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-2xl font-extrabold text-green-700">
              {data ? `${money(data.paidTotal)} ₺` : "—"}
            </p>
          )}
          <p className="mt-2 text-xs text-navy-400">{data?.paidCount ?? 0} ödeme</p>
        </div>
        <div className={`card ${(data?.overdueCount ?? 0) > 0 ? "!border-red-300 !bg-red-50" : ""}`}>
          <p className="text-sm font-medium text-navy-500">Geciken</p>
          {loading ? (
            <div className="mt-2 h-9 w-24 animate-pulse rounded bg-navy-100" />
          ) : (
            <p
              className={`mt-1 text-2xl font-extrabold ${
                (data?.overdueCount ?? 0) > 0 ? "text-red-700" : "text-navy-950"
              }`}
            >
              {data ? `${money(data.overdueTotal)} ₺` : "—"}
            </p>
          )}
          <p
            className={`mt-2 text-xs ${
              (data?.overdueCount ?? 0) > 0 ? "font-semibold text-red-700" : "text-navy-400"
            }`}
          >
            {(data?.overdueCount ?? 0) > 0
              ? `⚠ ${data?.overdueCount} tahakkukun vadesi geçti`
              : "Geciken tahsilat yok ✓"}
          </p>
        </div>
      </div>

      {/* Tablo */}
      {loading ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
            </div>
          ))}
        </div>
      ) : !hasAnyData ? (
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          <h2 className="text-lg font-semibold text-navy-900">
            {clients.length === 0 ? "Henüz mükellef yok" : "Ücret tanımı yapılmadı"}
          </h2>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            {clients.length === 0
              ? "Önce Mükellefler sayfasından mükellef ekleyin."
              : "Mükelleflere aylık ücret tanımlayın; ardından \"Dönemi Oluştur\" ile tahakkukları üretin."}
          </p>
          {clients.length > 0 && (
            <button onClick={() => openFeeModal()} className="btn-primary mt-6">
              Ücret Tanımla
            </button>
          )}
        </div>
      ) : data && data.items.length === 0 ? (
        <div className="card mt-6 py-10 text-center text-sm text-navy-500">
          Bu filtreyle eşleşen tahakkuk yok.
        </div>
      ) : (
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[860px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Mükellef</th>
                <th className="px-4 py-3">Dönem</th>
                <th className="px-4 py-3">Tutar</th>
                <th className="px-4 py-3">Vade</th>
                <th className="px-4 py-3">Durum</th>
                <th className="px-4 py-3">Tahsilat</th>
                <th className="px-4 py-3 text-right">İşlemler</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {data?.items.map((invoice) => {
                const badge = statusBadges[invoice.status] ?? statusBadges.Bekliyor;
                return (
                  <tr key={invoice.id} className={invoice.status === "Gecikti" ? "bg-red-50/40" : ""}>
                    <td className="px-4 py-3 font-medium text-navy-900">
                      <button
                        onClick={() => openFeeModal(invoice.clientId)}
                        className="text-left hover:underline"
                        title="Ücret tanımını düzenle"
                      >
                        {invoice.clientTitle ?? "—"}
                      </button>
                    </td>
                    <td className="px-4 py-3 font-mono">{invoice.period}</td>
                    <td className="px-4 py-3 font-mono">{money(invoice.amount)} ₺</td>
                    <td className="px-4 py-3 text-navy-600">
                      {new Date(invoice.dueDate).toLocaleDateString("tr-TR")}
                      {invoice.status === "Gecikti" && (
                        <span className="ml-1 text-xs font-semibold text-red-700">
                          ({Math.abs(invoice.daysRemaining)} gün geçti)
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${badge.cls}`}>
                        {badge.label}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-navy-600">
                      {invoice.paidAt
                        ? `${money(invoice.paidAmount ?? invoice.amount)} ₺ — ${new Date(invoice.paidAt).toLocaleDateString("tr-TR")}`
                        : "—"}
                    </td>
                    <td className="px-4 py-3 text-right">
                      {invoice.status === "Odendi" ? (
                        <button
                          onClick={() => handleReopen(invoice)}
                          disabled={rowBusy === invoice.id}
                          className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50 disabled:opacity-50"
                        >
                          {rowBusy === invoice.id ? "İşleniyor..." : "Geri Al"}
                        </button>
                      ) : payingId === invoice.id ? (
                        <div className="flex items-center justify-end gap-2">
                          <input
                            type="text"
                            inputMode="decimal"
                            className="input !w-28 !py-1 text-xs"
                            placeholder={money(invoice.amount)}
                            value={payAmount}
                            onChange={(e) => setPayAmount(e.target.value)}
                            title="Boş bırakılırsa tahakkuk tutarı tahsil edilmiş sayılır"
                          />
                          <button
                            onClick={() => handleMarkPaid(invoice)}
                            disabled={rowBusy === invoice.id}
                            className="rounded-md bg-green-600 px-2.5 py-1 text-xs font-semibold text-white hover:bg-green-700 disabled:opacity-50"
                          >
                            {rowBusy === invoice.id ? "..." : "Onayla"}
                          </button>
                          <button
                            onClick={() => {
                              setPayingId(null);
                              setPayAmount("");
                            }}
                            className="rounded-md px-2 py-1 text-xs font-semibold text-navy-500 hover:bg-navy-50"
                          >
                            Vazgeç
                          </button>
                        </div>
                      ) : (
                        <button
                          onClick={() => {
                            setPayingId(invoice.id);
                            setPayAmount("");
                          }}
                          className="rounded-md px-2.5 py-1 text-xs font-semibold text-green-700 hover:bg-green-50"
                        >
                          Ödendi
                        </button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {/* Ücret tanımı modalı */}
      {feeModalOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/50 px-4"
          onClick={() => !feeSaving && !feeDeleting && setFeeModalOpen(false)}
        >
          <div
            className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-xl bg-white p-6 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="text-lg font-bold text-navy-900">Mükellef Ücret Tanımı</h2>
            <p className="mt-1 text-xs text-navy-500">
              Aylık hizmet ücreti; &quot;Dönemi Oluştur&quot; her ay bu tutardan tahakkuk üretir.
            </p>

            {feeError && <div className="alert-error mt-4">{feeError}</div>}

            <form onSubmit={handleFeeSave} className="mt-5 space-y-4" noValidate>
              <div>
                <label className="label">
                  Mükellef <span className="text-red-500">*</span>
                </label>
                <select
                  className="input"
                  value={feeClientId}
                  onChange={(e) => {
                    setFeeClientId(e.target.value);
                    if (e.target.value) loadFeeDefinition(e.target.value);
                  }}
                  disabled={feeLoading}
                >
                  {clients.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.title}
                    </option>
                  ))}
                </select>
              </div>

              {feeLoading ? (
                <div className="animate-pulse rounded-lg bg-navy-50 p-4">
                  <div className="h-4 w-1/2 rounded bg-navy-100" />
                </div>
              ) : (
                <>
                  {feeExisting && (
                    <p className="rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 text-xs text-blue-700">
                      Bu mükellefin mevcut ücret tanımı yüklendi — kaydederseniz güncellenir.
                    </p>
                  )}
                  <div>
                    <label className="label">
                      Aylık Ücret (₺) <span className="text-red-500">*</span>
                    </label>
                    <input
                      type="text"
                      inputMode="decimal"
                      className="input"
                      placeholder="Örn: 2500"
                      value={feeAmount}
                      onChange={(e) => setFeeAmount(e.target.value)}
                    />
                  </div>
                  <div>
                    <label className="label">Başlangıç Ayı</label>
                    <input
                      type="date"
                      className="input"
                      value={feeStartDate}
                      onChange={(e) => setFeeStartDate(e.target.value)}
                    />
                    <p className="mt-1 text-xs text-navy-400">
                      Boş bırakılırsa bugünden itibaren geçerli olur.
                    </p>
                  </div>
                  <div>
                    <label className="label">Not</label>
                    <input
                      type="text"
                      className="input"
                      maxLength={500}
                      placeholder="Örn: KDV dahil, sözleşme 2026"
                      value={feeNote}
                      onChange={(e) => setFeeNote(e.target.value)}
                    />
                  </div>
                </>
              )}

              <div className="flex items-center justify-between gap-3 pt-2">
                {feeExisting ? (
                  <button
                    type="button"
                    onClick={handleFeeDelete}
                    disabled={feeDeleting || feeSaving}
                    className="rounded-md px-2.5 py-1.5 text-xs font-semibold text-red-600 hover:bg-red-50 disabled:opacity-50"
                  >
                    {feeDeleting ? "Siliniyor..." : "Tanımı Sil"}
                  </button>
                ) : (
                  <span />
                )}
                <div className="flex gap-3">
                  <button
                    type="button"
                    className="btn-secondary"
                    onClick={() => setFeeModalOpen(false)}
                    disabled={feeSaving || feeDeleting}
                  >
                    Vazgeç
                  </button>
                  <button type="submit" className="btn-primary" disabled={feeSaving || feeDeleting || feeLoading}>
                    {feeSaving ? "Kaydediliyor..." : "Kaydet"}
                  </button>
                </div>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
