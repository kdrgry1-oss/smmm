"use client";

import { useCallback, useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  ETebligatItem,
  ETebligatList,
  ETebligatSyncResult,
  isGibManualVerificationMessage,
  IntegrationResponse,
  listClientIntegrations,
  listETebligat,
  markETebligatRead,
  syncETebligat,
} from "@/lib/api";
import GibLoginModal from "@/components/GibLoginModal";

function formatDate(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

/** Faturalar sayfasındaki kalıp: <input type="date"> için yyyy-aa-gg. */
function toDateInputValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/**
 * 213 Sayılı VUK md. 107/A ve 7201 Sayılı Tebligat Kanunu md. 7/a:
 * Elektronik tebligat, muhatabın elektronik adresine ulaştığı tarihi izleyen
 * 5. iş gününün sonunda tebliğ edilmiş sayılır. Yasal hak düşürücü süreler bu tarihten sonra başlar.
 */
function getDeemedServedDate(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "—";
  let count = 0;
  const current = new Date(d);
  while (count < 5) {
    current.setDate(current.getDate() + 1);
    const day = current.getDay();
    if (day !== 0 && day !== 6) count++;
  }
  return current.toLocaleDateString("tr-TR");
}

/**
 * Kalan süre sayacı: >7 gün yeşil, 3-7 gün sarı, 1-2 gün kırmızı "⚠ N gün kaldı",
 * 0 ve altı koyu kırmızı "Süre doldu", süresiz belgeler gri "Süresiz".
 */
function DeadlineBadge({ item }: { item: ETebligatItem }) {
  if (item.daysRemaining === null) {
    return (
      <span className="rounded-full bg-navy-100 px-2.5 py-0.5 text-xs font-semibold text-navy-500">
        Süresiz
      </span>
    );
  }

  const d = item.daysRemaining;
  if (d <= 0) {
    return (
      <span className="rounded-full bg-red-800 px-2.5 py-0.5 text-xs font-bold text-white">
        Süre doldu
      </span>
    );
  }
  if (d < 3) {
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

export default function ETebligatPage() {
  // Mükellef seçimi
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);
  const [selectedClientId, setSelectedClientId] = useState<string>("");
  const [integrationsByClient, setIntegrationsByClient] = useState<Record<string, IntegrationResponse>>({});

  // Tebligat inceleme / okuma modalı
  const [selectedItem, setSelectedItem] = useState<ETebligatItem | null>(null);

  // Filtreler
  const [onlyUnread, setOnlyUnread] = useState(false);
  const [onlyUrgent, setOnlyUrgent] = useState(false);

  // Liste + özet
  const [data, setData] = useState<ETebligatList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Senkron
  const [syncing, setSyncing] = useState(false);
  const [syncResult, setSyncResult] = useState<ETebligatSyncResult | null>(null);
  const [syncError, setSyncError] = useState<string | null>(null);

  // Faz 15 — "Tebligatları Çek" tarih aralığı (Faturalar sayfasındaki kalıp).
  // GİB Portalı mükelleflerinde robot bu aralığı portal ekranındaki filtreye uygular;
  // diğer sağlayıcılar aralığı yok sayar.
  const [fromDate, setFromDate] = useState(() => {
    const d = new Date();
    d.setDate(d.getDate() - 90);
    return toDateInputValue(d);
  });
  const [toDate, setToDate] = useState(() => toDateInputValue(new Date()));

  // Okundu işaretleme (satır bazlı yükleniyor durumu)
  const [markingId, setMarkingId] = useState<string | null>(null);

  // Faz 17 — "Doğrulama Kodunu Gir" modalı: GİB oturumu düşen mükellef için kullanıcı
  // captcha kodunu girer (hata bandı mükellef bilgisi taşımadığından SEÇİLİ mükellef kullanılır).
  const [gibLoginOpen, setGibLoginOpen] = useState(false);
  const [primaryGibLoginOpen, setPrimaryGibLoginOpen] = useState(false);

  // Mükellefleri yükle
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [list, integrations] = await Promise.all([
          api<ClientResponse[]>("/api/clients"),
          listClientIntegrations(),
        ]);
        if (!cancelled) {
          setClients(list);
          setIntegrationsByClient(Object.fromEntries(integrations.map((item) => [item.clientId, item])));
        }
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

  const loadList = useCallback(
    async (opts: { clientId: string; onlyUnread: boolean; onlyUrgent: boolean }) => {
      setLoading(true);
      setError(null);
      try {
        const result = await listETebligat({
          clientId: opts.clientId || undefined,
          onlyUnread: opts.onlyUnread,
          onlyUrgent: opts.onlyUrgent,
        });
        setData(result);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Tebligatlar yüklenirken bir hata oluştu.");
      } finally {
        setLoading(false);
      }
    },
    []
  );

  useEffect(() => {
    loadList({ clientId: selectedClientId, onlyUnread, onlyUrgent });
  }, [selectedClientId, onlyUnread, onlyUrgent, loadList]);

  /**
   * "Tebligatları Çek": seçili mükellef (boşsa tümü) için tarih aralığındaki tebligatları
   * sağlayıcıdan çeker. GİB Portalı mükelleflerinde çekim gib-robot üzerinden yapılır.
   */
  async function handleSync() {
    setSyncError(null);
    setSyncResult(null);

    if (!fromDate || !toDate) {
      setSyncError("Başlangıç ve bitiş tarihlerini seçin.");
      return;
    }
    if (fromDate > toDate) {
      setSyncError("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
      return;
    }

    setSyncing(true);
    try {
      const result = await syncETebligat(selectedClientId || undefined, fromDate, toDate);
      setSyncResult(result);
      await loadList({ clientId: selectedClientId, onlyUnread, onlyUrgent });
    } catch (err) {
      setSyncError(err instanceof Error ? err.message : "Senkronizasyon sırasında bir hata oluştu.");
    } finally {
      setSyncing(false);
    }
  }

  async function handleMarkRead(id: string) {
    setMarkingId(id);
    try {
      await markETebligatRead(id);
      await loadList({ clientId: selectedClientId, onlyUnread, onlyUrgent });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Okundu işaretlenirken bir hata oluştu.");
    } finally {
      setMarkingId(null);
    }
  }

  const items = data?.items ?? [];
  const selectedClient = clients.find((client) => client.id === selectedClientId);
  const selectedIntegration = selectedClientId ? integrationsByClient[selectedClientId] : undefined;
  const selectedUsesPrimaryGib = selectedIntegration?.provider === "GibPortal";
  const selectedProviderName = selectedIntegration?.provider === "DoganDonusum"
    ? "Doğan Dönüşüm"
    : selectedIntegration?.provider === "Uyumsoft"
      ? "Uyumsoft"
      : selectedIntegration?.provider === "Elogo"
        ? "eLogo"
        : selectedIntegration?.provider === "Edm"
          ? "EDM Bilişim"
          : selectedIntegration?.provider ?? null;

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">e-Tebligat</h1>
          <p className="mt-1 text-sm text-navy-500">
            Tüm mükelleflerinize gelen tebligatlar tek panoda — yasal süre sayaçlarıyla.
          </p>
        </div>
        {/* Faz 15: tarih aralığı + "Tebligatları Çek" (Faturalar sayfasındaki kalıp). */}
        <div className="flex flex-wrap items-end gap-3">
          <div>
            <label htmlFor="tebligatFrom" className="label">
              Başlangıç
            </label>
            <input
              id="tebligatFrom"
              type="date"
              className="input"
              value={fromDate}
              max={toDate}
              onChange={(e) => setFromDate(e.target.value)}
            />
          </div>
          <div>
            <label htmlFor="tebligatTo" className="label">
              Bitiş
            </label>
            <input
              id="tebligatTo"
              type="date"
              className="input"
              value={toDate}
              min={fromDate}
              max={toDateInputValue(new Date())}
              onChange={(e) => setToDate(e.target.value)}
            />
          </div>
          <button
            onClick={handleSync}
            className="btn-primary flex items-center gap-2"
            disabled={syncing || clientsLoading}
            title={
              selectedClientId
                ? "Seçili mükellefin bu tarih aralığındaki tebligatlarını çeker."
                : "Tüm mükelleflerin bu tarih aralığındaki tebligatlarını çeker."
            }
          >
            {syncing && (
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {syncing ? "Çekiliyor..." : "Tebligatları Çek"}
          </button>
        </div>
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}
      {syncResult && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {syncResult.message}
        </div>
      )}
      {syncError && (
        <div className="alert-error mt-4 flex flex-wrap items-center justify-between gap-3">
          <span>{syncError}</span>
          {/* Faz 18: "GİB oturumu yenilenmeli" hatasında MÜŞAVİR oturumu yenilenir
              (advisor akışı — tek oturum tüm mükellefler için yeterlidir). */}
          {isGibManualVerificationMessage(syncError) && (
            <button
              className="rounded-md bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700"
              onClick={() => {
                if (!selectedClientId) setGibLoginOpen(true);
                else if (selectedUsesPrimaryGib) setPrimaryGibLoginOpen(true);
                else setGibLoginOpen(true);
              }}
              title="Seçili GİB kaynağının doğrulama kodunu girin."
            >
              Doğrulama Kodunu Gir
            </button>
          )}
        </div>
      )}

      {selectedClient && selectedProviderName && selectedProviderName !== "GibPortal" && selectedProviderName !== "EArsivPortal" && (
        <div className="mt-4 rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-950">
          <span><b>{selectedClient.title}</b> için kayıtlı entegratör: <b>{selectedProviderName}</b>.</span>
        </div>
      )}

      {/* Faz 18 — Müşavir GİB girişi modalı: başarıda çekim otomatik tekrarlanır. */}
      {gibLoginOpen && (
        <GibLoginModal
          mode="advisor"
          onClose={() => setGibLoginOpen(false)}
          onSuccess={() => {
            setSyncError(null);
            handleSync();
          }}
        />
      )}

      {primaryGibLoginOpen && selectedClient && (
        <GibLoginModal
          mode="client"
          clientId={selectedClient.id}
          clientTitle={selectedClient.title}
          onClose={() => setPrimaryGibLoginOpen(false)}
          onSuccess={() => {
            setSyncError(null);
            handleSync();
          }}
        />
      )}

      {/* Özet kartlar */}
      <div className="mt-6 grid gap-4 sm:grid-cols-3">
        <div className="card">
          <p className="text-sm font-medium text-navy-500">Toplam Tebligat</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-3xl font-extrabold text-navy-950">{data?.totalCount ?? "—"}</p>
          )}
        </div>
        <div className="card">
          <p className="text-sm font-medium text-navy-500">Okunmamış</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-3xl font-extrabold text-amber-600">{data?.unreadCount ?? "—"}</p>
          )}
        </div>
        <div className={`card ${(data?.urgentCount ?? 0) > 0 ? "!border-red-300 !bg-red-50" : ""}`}>
          <p className="text-sm font-medium text-navy-500">Süresi Yaklaşan (≤3 gün)</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p
              className={`mt-1 text-3xl font-extrabold ${
                (data?.urgentCount ?? 0) > 0 ? "text-red-700" : "text-navy-950"
              }`}
            >
              {data?.urgentCount ?? "—"}
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
            checked={onlyUnread}
            onChange={(e) => setOnlyUnread(e.target.checked)}
          />
          Sadece okunmamış
        </label>
        <label className="flex items-center gap-2 text-sm text-navy-700">
          <input
            type="checkbox"
            checked={onlyUrgent}
            onChange={(e) => setOnlyUrgent(e.target.checked)}
          />
          Sadece acil (≤3 gün)
        </label>
      </div>

      {error && (
        <div className="alert-error mt-4 flex items-center justify-between gap-4">
          <span>{error}</span>
          <button
            className="btn-secondary !px-3 !py-1.5 text-xs"
            onClick={() => loadList({ clientId: selectedClientId, onlyUnread, onlyUrgent })}
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
          {onlyUnread || onlyUrgent || selectedClientId ? (
            <>
              <h2 className="text-lg font-semibold text-navy-900">Sonuç bulunamadı</h2>
              <p className="mt-2 text-sm text-navy-500">
                Filtre ölçütlerinizle eşleşen tebligat yok.
              </p>
            </>
          ) : (
            <>
              <h2 className="text-lg font-semibold text-navy-900">
                Tebligat yok — hepsi kontrol altında ✓
              </h2>
              <p className="mt-2 max-w-md text-sm text-navy-500">
                Tarih aralığını seçip &quot;Tebligatları Çek&quot; ile mükelleflerinizin
                tebligat kutularını tarayabilirsiniz. Yeni tebligat geldiğinde süre sayacı
                burada başlar.
              </p>
            </>
          )}
        </div>
      ) : items.length > 0 ? (
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[1080px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Mükellef</th>
                <th className="px-4 py-3">Gönderen</th>
                <th className="px-4 py-3">Konu</th>
                <th className="px-4 py-3">Tebliğ Tarihi</th>
                <th className="px-4 py-3">Süre Türü</th>
                <th className="px-4 py-3">Kalan Süre</th>
                <th className="px-4 py-3">Okundu</th>
                <th className="px-4 py-3 text-right">İşlem</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {items.map((item) => (
                <tr
                  key={item.id}
                  onClick={() => setSelectedItem(item)}
                  className={`cursor-pointer transition hover:bg-navy-50/80 ${!item.isRead ? "bg-amber-50/40" : ""}`}
                >
                  <td className="px-4 py-3 font-medium text-navy-900">
                    {item.clientTitle ?? "—"}
                  </td>
                  <td className="px-4 py-3 text-navy-700">{item.sender}</td>
                  <td className="px-4 py-3">
                    <span className={!item.isRead ? "font-semibold text-navy-900" : "text-navy-700"}>
                      {item.subject}
                    </span>
                  </td>
                  <td className="px-4 py-3">{formatDate(item.deliveredAt)}</td>
                  <td className="px-4 py-3">
                    {item.deadlineType ?? "—"}
                    {item.legalDeadline && (
                      <span className="block text-xs text-navy-400">
                        Son gün: {formatDate(item.legalDeadline)}
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    <DeadlineBadge item={item} />
                  </td>
                  <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
                    {item.isRead ? (
                      <span className="text-xs font-medium text-green-700" title={item.readAt ? `Okundu: ${formatDate(item.readAt)}` : undefined}>
                        ✓ Okundu
                      </span>
                    ) : (
                      <button
                        onClick={() => handleMarkRead(item.id)}
                        className="btn-secondary flex items-center gap-1.5 !px-3 !py-1.5 text-xs"
                        disabled={markingId === item.id}
                      >
                        {markingId === item.id && (
                          <span className="h-3 w-3 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                        )}
                        Okundu işaretle
                      </button>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right" onClick={(e) => e.stopPropagation()}>
                    <button
                      type="button"
                      onClick={() => setSelectedItem(item)}
                      className="rounded-lg border border-navy-200 bg-white px-2.5 py-1 text-xs font-medium text-navy-700 shadow-sm hover:bg-navy-50 hover:text-navy-900"
                    >
                      İncele / Oku ↗
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      <p className="mt-5 text-xs text-navy-500">
        Kalan süre, tebliğ tarihini izleyen 5. iş günü sonunda &quot;tebliğ edilmiş sayılma&quot;
        kuralı + belge türüne göre ödeme (15 gün) / itiraz (30 gün) süresinden hesaplanır. Bu ekran
        yalnız şirket e-Tebligatlarını GİB Dijital Vergi Dairesi kaynağından izler; PTT UETS burada
        kullanılmaz. Müşavir yetkisiyle açılan GİB oturumu tanımlı tüm yetkili mükelleflerde kullanılır;
        GİB ek doğrulama isterse kod insan tarafından girilmelidir.
      </p>

      {/* Tebligat İnceleme ve Yasal Süre Modalı */}
      {selectedItem && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/60 p-4 backdrop-blur-sm"
          onClick={() => setSelectedItem(null)}
        >
          <div
            className="flex max-h-[92vh] w-full max-w-2xl flex-col rounded-2xl border border-navy-100 bg-white shadow-2xl"
            onClick={(e) => e.stopPropagation()}
          >
            {/* Modal Başlığı */}
            <div className="flex items-center justify-between border-b border-navy-100 px-6 py-4">
              <div className="flex items-center gap-3">
                <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-amber-100 text-amber-800">
                  <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                  </svg>
                </div>
                <div>
                  <h2 className="text-lg font-bold text-navy-950">e-Tebligat İnceleme</h2>
                  <p className="text-xs text-navy-500">
                    213 Sayılı VUK md. 107/A uyarınca yasal süre ve tebligat ayrıntıları
                  </p>
                </div>
              </div>
              <div className="flex items-center gap-2">
                <DeadlineBadge item={selectedItem} />
                <button
                  type="button"
                  onClick={() => setSelectedItem(null)}
                  className="rounded-lg p-1.5 text-navy-400 hover:bg-navy-50 hover:text-navy-700"
                >
                  <span className="sr-only">Kapat</span>
                  <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            </div>

            {/* Modal Gövdesi */}
            <div className="flex-1 overflow-y-auto p-6 space-y-6">
              {/* Temel Bilgiler Grid */}
              <div className="rounded-xl border border-navy-100 bg-navy-50/50 p-4">
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <div>
                    <span className="text-xs font-semibold uppercase tracking-wider text-navy-400">
                      Mükellef
                    </span>
                    <p className="mt-0.5 text-sm font-bold text-navy-900">
                      {selectedItem.clientTitle ?? "—"}
                    </p>
                    {clients.find((c) => c.id === selectedItem.clientId)?.taxId && (
                      <p className="text-xs font-mono text-navy-500">
                        VKN/TCKN: {clients.find((c) => c.id === selectedItem.clientId)?.taxId}
                      </p>
                    )}
                  </div>
                  <div>
                    <span className="text-xs font-semibold uppercase tracking-wider text-navy-400">
                      Gönderen Kurum / Vergi Dairesi
                    </span>
                    <p className="mt-0.5 text-sm font-semibold text-navy-900">
                      {selectedItem.sender}
                    </p>
                  </div>
                  <div className="sm:col-span-2">
                    <span className="text-xs font-semibold uppercase tracking-wider text-navy-400">
                      Tebligat Konusu &amp; Belge Türü
                    </span>
                    <p className="mt-0.5 text-sm font-medium text-navy-900 bg-white p-2.5 rounded-lg border border-navy-100">
                      {selectedItem.subject}
                    </p>
                  </div>
                  {selectedItem.externalId && (
                    <div className="sm:col-span-2">
                      <span className="text-xs font-semibold uppercase tracking-wider text-navy-400">
                        Belge / Tebliğ Referans No
                      </span>
                      <p className="mt-0.5 font-mono text-xs text-navy-600 select-all">
                        {selectedItem.externalId}
                      </p>
                    </div>
                  )}
                </div>
              </div>

              {/* Yasal Süre Zaman Çizelgesi */}
              <div>
                <h3 className="text-xs font-bold uppercase tracking-wider text-navy-500 mb-3">
                  Yasal Süre Takvimi (VUK md. 107/A)
                </h3>
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                  <div className="rounded-xl border border-navy-200 bg-white p-3.5 shadow-sm">
                    <div className="flex items-center gap-1.5 text-navy-500 text-xs font-semibold">
                      <span className="flex h-5 w-5 items-center justify-center rounded-full bg-navy-100 text-navy-700 text-[11px] font-bold">1</span>
                      Tebliğ Tarihi
                    </div>
                    <p className="mt-2 text-base font-bold text-navy-950">
                      {formatDate(selectedItem.deliveredAt)}
                    </p>
                    <p className="mt-1 text-[11px] text-navy-400">
                      Elektronik kutuya ulaştığı an
                    </p>
                  </div>

                  <div className="rounded-xl border border-blue-200 bg-blue-50/50 p-3.5 shadow-sm">
                    <div className="flex items-center gap-1.5 text-blue-800 text-xs font-semibold">
                      <span className="flex h-5 w-5 items-center justify-center rounded-full bg-blue-200 text-blue-900 text-[11px] font-bold">2</span>
                      Tebliğ Sayılma
                    </div>
                    <p className="mt-2 text-base font-bold text-blue-950">
                      {getDeemedServedDate(selectedItem.deliveredAt)}
                    </p>
                    <p className="mt-1 text-[11px] text-blue-600">
                      İzleyen 5. iş günü sonu
                    </p>
                  </div>

                  <div className="rounded-xl border border-amber-200 bg-amber-50/50 p-3.5 shadow-sm">
                    <div className="flex items-center gap-1.5 text-amber-800 text-xs font-semibold">
                      <span className="flex h-5 w-5 items-center justify-center rounded-full bg-amber-200 text-amber-900 text-[11px] font-bold">3</span>
                      Yasal Son Gün
                    </div>
                    <p className="mt-2 text-base font-bold text-amber-950">
                      {formatDate(selectedItem.legalDeadline)}
                    </p>
                    <p className="mt-1 text-[11px] text-amber-700 font-medium">
                      Tür: {selectedItem.deadlineType ?? "Belirtilmemiş"}
                    </p>
                  </div>
                </div>
              </div>

              {/* Hukuki Açıklama Kartı */}
              <div className="rounded-xl border border-blue-100 bg-blue-50/60 p-4 text-xs text-blue-900">
                <p className="font-semibold text-blue-950">Hukuki Süre Hesabı Bilgilendirmesi:</p>
                <p className="mt-1 leading-relaxed text-blue-800">
                  213 sayılı Vergi Usul Kanunu&apos;nun 107/A maddesi ve 7201 sayılı Tebligat Kanunu&apos;nun 7/a maddesi gereğince,
                  elektronik ortamda tebliğ edilen evrak muhatabın elektronik adresine ulaştığı tarihi izleyen beşinci günün
                  sonunda tebliğ edilmiş sayılır. Dava açma (30 gün), ödeme (15 gün) veya itiraz süreleri bu tebliğ edilmiş sayılma
                  tarihinden itibaren işlemeye başlar.
                </p>
              </div>

              {/* GİB Portalı Belge Görüntüleme Yönlendirmesi */}
              <div className="rounded-xl border border-amber-200 bg-amber-50/50 p-4">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <h4 className="text-sm font-bold text-amber-950">
                      Resmi Belge &amp; Ekler (PDF)
                    </h4>
                    <p className="mt-0.5 text-xs text-amber-800">
                      Gelir İdaresi Başkanlığı güvenlik protokolü gereği resmi üst yazı ve ekli tahakkuk belgeleri doğrudan GİB Dijital Vergi Dairesi portalında saklanır.
                    </p>
                  </div>
                  <a
                    href="https://dijital.gib.gov.tr/portal/eTebligatlarim"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-2 rounded-lg bg-amber-600 px-4 py-2 text-xs font-semibold text-white shadow-sm hover:bg-amber-700"
                  >
                    GİB e-Tebligat Portalında Aç ↗
                  </a>
                </div>
              </div>
            </div>

            {/* Modal Alt Kısmı (Aksiyonlar) */}
            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-navy-100 bg-navy-50/50 px-6 py-4">
              <button
                type="button"
                onClick={() => window.print()}
                className="inline-flex items-center gap-1.5 rounded-lg border border-navy-200 bg-white px-3 py-1.5 text-xs font-medium text-navy-700 shadow-sm hover:bg-navy-50"
              >
                🖨 Yazdır / PDF Kaydet
              </button>

              <div className="flex items-center gap-3">
                {!selectedItem.isRead && (
                  <button
                    type="button"
                    onClick={async () => {
                      if (selectedItem) {
                        await handleMarkRead(selectedItem.id);
                        setSelectedItem((prev) => prev ? { ...prev, isRead: true, readAt: new Date().toISOString() } : null);
                      }
                    }}
                    disabled={markingId === selectedItem.id}
                    className="rounded-lg bg-green-600 px-3.5 py-1.5 text-xs font-semibold text-white shadow-sm hover:bg-green-700 disabled:opacity-50"
                  >
                    {markingId === selectedItem.id ? "İşleniyor..." : "✓ Okundu Olarak İşaretle"}
                  </button>
                )}
                <button
                  type="button"
                  onClick={() => setSelectedItem(null)}
                  className="rounded-lg border border-navy-300 bg-white px-4 py-1.5 text-xs font-semibold text-navy-700 shadow-sm hover:bg-navy-50"
                >
                  Kapat
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
