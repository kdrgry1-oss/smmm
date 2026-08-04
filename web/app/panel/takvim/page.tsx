"use client";

import { useCallback, useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  completeObligation,
  createManualObligation,
  GenerateCalendarResult,
  generateTaxCalendar,
  listTaxCalendar,
  reopenObligation,
  TaxCalendarList,
  TaxObligationItem,
} from "@/lib/api";

/** Varsayılan dönem: önceki ay (beyannameler bir önceki ayın işidir). */
function defaultPeriod(): string {
  const d = new Date();
  d.setMonth(d.getMonth() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

/**
 * Kalan gün sayacı (tebligat sayacı widget mantığı): geciken koyu kırmızı "Süre geçti",
 * ≤3 gün kırmızı, ≤7 gün sarı, diğerleri yeşil; tamamlananlar yeşil onay rozeti.
 */
function DeadlineBadge({ item }: { item: TaxObligationItem }) {
  if (item.status === "Tamamlandi") {
    return (
      <span className="rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-semibold text-green-700">
        ✓ Tamamlandı
      </span>
    );
  }
  const d = item.daysRemaining;
  if (d < 0) {
    return (
      <span className="rounded-full bg-red-800 px-2.5 py-0.5 text-xs font-bold text-white">
        Süre geçti ({-d} gün)
      </span>
    );
  }
  if (d === 0) {
    return (
      <span className="rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-bold text-red-700">
        ⚠ Bugün son gün
      </span>
    );
  }
  if (d <= 3) {
    return (
      <span className="rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-bold text-red-700">
        ⚠ {d} gün kaldı
      </span>
    );
  }
  if (d <= 7) {
    return (
      <span className="rounded-full bg-amber-100 px-2.5 py-0.5 text-xs font-semibold text-amber-700">
        {d} gün kaldı
      </span>
    );
  }
  return (
    <span className="rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-semibold text-green-700">
      {d} gün kaldı
    </span>
  );
}

export default function TakvimPage() {
  // Filtreler
  const [period, setPeriod] = useState(defaultPeriod());
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [selectedClientId, setSelectedClientId] = useState("");
  const [onlyOverdue, setOnlyOverdue] = useState(false);
  const [onlyPending, setOnlyPending] = useState(false);

  // Liste
  const [data, setData] = useState<TaxCalendarList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Dönem oluşturma
  const [generating, setGenerating] = useState(false);
  const [generateResult, setGenerateResult] = useState<GenerateCalendarResult | null>(null);
  const [generateError, setGenerateError] = useState<string | null>(null);

  // Satır işlemleri
  const [busyId, setBusyId] = useState<string | null>(null);

  // Manuel ekleme
  const [showManual, setShowManual] = useState(false);
  const [manualClientId, setManualClientId] = useState("");
  const [manualType, setManualType] = useState("");
  const [manualDueDate, setManualDueDate] = useState("");
  const [manualSaving, setManualSaving] = useState(false);
  const [manualError, setManualError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api<ClientResponse[]>("/api/clients");
        if (!cancelled) setClients(list);
      } catch {
        // mükellef listesi filtre içindir; hatası panoyu engellemez
      } finally {
        if (!cancelled) setClientsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const loadList = useCallback(
    async (opts: { period: string; clientId: string; onlyPending: boolean; onlyOverdue: boolean }) => {
      setLoading(true);
      setError(null);
      try {
        const result = await listTaxCalendar({
          period: opts.period,
          clientId: opts.clientId || undefined,
          onlyPending: opts.onlyPending,
          onlyOverdue: opts.onlyOverdue,
        });
        setData(result);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Vergi takvimi yüklenirken bir hata oluştu.");
      } finally {
        setLoading(false);
      }
    },
    []
  );

  useEffect(() => {
    loadList({ period, clientId: selectedClientId, onlyPending, onlyOverdue });
  }, [period, selectedClientId, onlyPending, onlyOverdue, loadList]);

  async function handleGenerate() {
    setGenerateError(null);
    setGenerateResult(null);
    setGenerating(true);
    try {
      const result = await generateTaxCalendar(period);
      setGenerateResult(result);
      await loadList({ period, clientId: selectedClientId, onlyPending, onlyOverdue });
    } catch (err) {
      setGenerateError(err instanceof Error ? err.message : "Dönem oluşturulurken bir hata oluştu.");
    } finally {
      setGenerating(false);
    }
  }

  async function handleToggle(item: TaxObligationItem) {
    setBusyId(item.id);
    setError(null);
    try {
      if (item.status === "Tamamlandi") await reopenObligation(item.id);
      else await completeObligation(item.id);
      await loadList({ period, clientId: selectedClientId, onlyPending, onlyOverdue });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Durum güncellenirken bir hata oluştu.");
    } finally {
      setBusyId(null);
    }
  }

  async function handleManualSave() {
    setManualError(null);

    if (!manualClientId) {
      setManualError("Mükellef seçin.");
      return;
    }
    if (!manualType.trim()) {
      setManualError("Yükümlülük türü zorunludur (ör. Geçici Vergi).");
      return;
    }
    if (!manualDueDate) {
      setManualError("Son gün tarihini seçin.");
      return;
    }

    setManualSaving(true);
    try {
      await createManualObligation({
        clientId: manualClientId,
        type: manualType.trim(),
        period,
        dueDate: manualDueDate,
      });
      setShowManual(false);
      setManualClientId("");
      setManualType("");
      setManualDueDate("");
      await loadList({ period, clientId: selectedClientId, onlyPending, onlyOverdue });
    } catch (err) {
      setManualError(err instanceof Error ? err.message : "Yükümlülük eklenirken bir hata oluştu.");
    } finally {
      setManualSaving(false);
    }
  }

  const items = data?.items ?? [];

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Vergi Takvimi</h1>
          <p className="mt-1 text-sm text-navy-500">
            Mükellef bazlı yükümlülük takibi — son gün sayaçları ve geciken uyarılarıyla.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <input
            type="month"
            className="input !w-auto"
            value={period}
            onChange={(e) => setPeriod(e.target.value)}
          />
          <button
            onClick={handleGenerate}
            className="btn-primary flex items-center gap-2"
            disabled={generating || !period}
            title="Seçilen ay için tüm aktif mükelleflere standart seti üretir (KDV 28'i, Muhtasar/SGK 26'sı). Mevcut kayıtlar korunur."
          >
            {generating && (
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {generating ? "Oluşturuluyor..." : "Dönemi Oluştur"}
          </button>
          <button className="btn-secondary" onClick={() => setShowManual((v) => !v)}>
            {showManual ? "Vazgeç" : "Manuel Ekle"}
          </button>
        </div>
      </div>

      {generateResult && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {generateResult.message}
        </div>
      )}
      {generateError && <div className="alert-error mt-4">{generateError}</div>}

      {/* Manuel ekleme formu */}
      {showManual && (
        <div className="card mt-4">
          <h2 className="text-sm font-semibold text-navy-900">
            Manuel Yükümlülük Ekle ({period} dönemi)
          </h2>
          {manualError && <div className="alert-error mt-3">{manualError}</div>}
          <div className="mt-3 flex flex-wrap gap-2">
            <select
              className="input min-w-[200px] !w-auto flex-1"
              value={manualClientId}
              onChange={(e) => setManualClientId(e.target.value)}
            >
              <option value="">Mükellef seçin</option>
              {clients.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </select>
            <input
              type="text"
              className="input min-w-[180px] !w-auto flex-1"
              placeholder="Tür (ör. Geçici Vergi, Yıllık Gelir V.)"
              maxLength={100}
              value={manualType}
              onChange={(e) => setManualType(e.target.value)}
              list="obligation-types"
            />
            <datalist id="obligation-types">
              <option value="KDV Beyannamesi" />
              <option value="Muhtasar" />
              <option value="Geçici Vergi" />
              <option value="SGK Bildirge" />
              <option value="Yıllık Gelir V." />
            </datalist>
            <input
              type="date"
              className="input !w-auto"
              title="Son gün"
              value={manualDueDate}
              onChange={(e) => setManualDueDate(e.target.value)}
            />
            <button className="btn-primary !px-4" onClick={handleManualSave} disabled={manualSaving}>
              {manualSaving ? "Ekleniyor..." : "Ekle"}
            </button>
          </div>
        </div>
      )}

      {/* Özet kartlar */}
      <div className="mt-6 grid gap-4 sm:grid-cols-3">
        <div className="card">
          <p className="text-sm font-medium text-navy-500">Toplam Yükümlülük</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-3xl font-extrabold text-navy-950">{data?.totalCount ?? "—"}</p>
          )}
        </div>
        <div className="card">
          <p className="text-sm font-medium text-navy-500">Bekleyen</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-3xl font-extrabold text-amber-600">{data?.pendingCount ?? "—"}</p>
          )}
        </div>
        <div className={`card ${(data?.overdueCount ?? 0) > 0 ? "!border-red-300 !bg-red-50" : ""}`}>
          <p className="text-sm font-medium text-navy-500">Geciken</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p
              className={`mt-1 text-3xl font-extrabold ${
                (data?.overdueCount ?? 0) > 0 ? "text-red-700" : "text-navy-950"
              }`}
            >
              {data?.overdueCount ?? "—"}
            </p>
          )}
        </div>
      </div>

      {/* Filtreler */}
      <div className="mt-6 flex flex-wrap items-center gap-4">
        <select
          className="input max-w-[260px]"
          value={selectedClientId}
          onChange={(e) => setSelectedClientId(e.target.value)}
          disabled={clientsLoading}
        >
          <option value="">{clientsLoading ? "Yükleniyor..." : "Tüm mükellefler"}</option>
          {clients.map((c) => (
            <option key={c.id} value={c.id}>
              {c.title}
            </option>
          ))}
        </select>
        <label className="flex items-center gap-2 text-sm text-navy-700">
          <input
            type="checkbox"
            checked={onlyPending}
            onChange={(e) => setOnlyPending(e.target.checked)}
          />
          Sadece bekleyen
        </label>
        <label className="flex items-center gap-2 text-sm text-navy-700">
          <input
            type="checkbox"
            checked={onlyOverdue}
            onChange={(e) => setOnlyOverdue(e.target.checked)}
          />
          Sadece geciken
        </label>
      </div>

      {error && (
        <div className="alert-error mt-4 flex items-center justify-between gap-4">
          <span>{error}</span>
          <button
            className="btn-secondary !px-3 !py-1.5 text-xs"
            onClick={() => loadList({ period, clientId: selectedClientId, onlyPending, onlyOverdue })}
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
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          {onlyPending || onlyOverdue || selectedClientId ? (
            <>
              <h2 className="text-lg font-semibold text-navy-900">Sonuç bulunamadı</h2>
              <p className="mt-2 text-sm text-navy-500">Filtre ölçütlerinizle eşleşen yükümlülük yok.</p>
            </>
          ) : (
            <>
              <h2 className="text-lg font-semibold text-navy-900">Bu dönem için yükümlülük yok</h2>
              <p className="mt-2 max-w-md text-sm text-navy-500">
                &quot;Dönemi Oluştur&quot; ile {period} dönemi için tüm aktif mükelleflerinize
                standart yükümlülük setini (KDV, Muhtasar, SGK) tek tıkla üretin.
              </p>
            </>
          )}
        </div>
      ) : items.length > 0 ? (
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[900px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Mükellef</th>
                <th className="px-4 py-3">Tür</th>
                <th className="px-4 py-3">Dönem</th>
                <th className="px-4 py-3">Son Gün</th>
                <th className="px-4 py-3">Kalan Süre</th>
                <th className="px-4 py-3">Kaynak</th>
                <th className="px-4 py-3">Tamamla</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {items.map((item) => (
                <tr
                  key={item.id}
                  className={`transition hover:bg-navy-50/60 ${item.isOverdue ? "bg-red-50/40" : ""}`}
                >
                  <td className="px-4 py-3 font-medium text-navy-900">{item.clientTitle ?? "—"}</td>
                  <td className="px-4 py-3 text-navy-700">{item.type}</td>
                  <td className="px-4 py-3">{item.period}</td>
                  <td className="px-4 py-3">{formatDate(item.dueDate)}</td>
                  <td className="px-4 py-3">
                    <DeadlineBadge item={item} />
                  </td>
                  <td className="px-4 py-3 text-xs text-navy-400">
                    {item.source === "Manuel" ? "Manuel" : "Otomatik"}
                  </td>
                  <td className="px-4 py-3">
                    <label className="flex items-center gap-2 text-xs text-navy-600">
                      <input
                        type="checkbox"
                        checked={item.status === "Tamamlandi"}
                        disabled={busyId === item.id}
                        onChange={() => handleToggle(item)}
                      />
                      {busyId === item.id
                        ? "..."
                        : item.status === "Tamamlandi"
                          ? `Tamamlandı${item.completedAt ? ` (${formatDate(item.completedAt)})` : ""}`
                          : "Tamamla"}
                    </label>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      <p className="mt-5 text-xs text-navy-400">
        Standart set: KDV Beyannamesi takip eden ayın 28&apos;i, Muhtasar ve SGK Bildirge 26&apos;sı
        (sabit gün tablosu — mevzuat değişirse tek yerden güncellenir). Aynı dönemde KDV1
        beyannamesi &quot;Onaylandı&quot; olan mükellefin KDV yükümlülüğü otomatik tamamlanır.
      </p>
    </div>
  );
}
