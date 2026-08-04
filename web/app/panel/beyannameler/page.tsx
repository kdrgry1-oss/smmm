"use client";

import { useCallback, useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  DeclarationItem,
  DeclarationStatusCode,
  DeclarationTypeCode,
  getDeclaration,
  listDeclarations,
  prepareDeclaration,
  refreshDeclarationStatus,
  queueDeclarationSend,
  sendDeclaration,
} from "@/lib/api";

function formatTL(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return new Intl.NumberFormat("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value) + " ₺";
}

function formatDate(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

function formatDateTime(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return isNaN(d.getTime())
    ? "—"
    : d.toLocaleDateString("tr-TR") + " " + d.toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" });
}

/** Önceki ay "yyyy-MM" (beyan dönemi varsayılanı — kapanan dönem beyan edilir). */
function defaultPeriod(): string {
  const d = new Date();
  d.setMonth(d.getMonth() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

const statusLabels: Record<DeclarationStatusCode, string> = {
  Draft: "Taslak",
  Ready: "Hazır",
  Sent: "Gönderildi",
  Approved: "Onaylandı",
  Error: "Hata",
};

const typeLabels: Record<DeclarationTypeCode, string> = {
  KDV1: "KDV-1",
  KDV2: "KDV-2",
  Muhtasar: "Muhtasar",
  Damga: "Damga",
};

function StatusBadge({ status }: { status: DeclarationStatusCode }) {
  const styles: Record<DeclarationStatusCode, string> = {
    Draft: "bg-navy-100 text-navy-600",
    Ready: "bg-blue-100 text-blue-700",
    Sent: "bg-amber-100 text-amber-700",
    Approved: "bg-green-100 text-green-700",
    Error: "bg-red-100 text-red-700",
  };
  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${styles[status]}`}>
      {statusLabels[status]}
    </span>
  );
}

export default function DeclarationsPage() {
  // Mükellefler
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);

  // Taslak hazırlama formu
  const [formClientId, setFormClientId] = useState<string>("");
  const [formType, setFormType] = useState<DeclarationTypeCode>("KDV1");
  const [formPeriod, setFormPeriod] = useState<string>(defaultPeriod());
  const [preparing, setPreparing] = useState(false);

  // Durum panosu (liste) + filtreler
  const [filterClientId, setFilterClientId] = useState<string>("");
  const [filterPeriod, setFilterPeriod] = useState<string>("");
  const [items, setItems] = useState<DeclarationItem[]>([]);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState<string | null>(null);

  // Seçili beyanname kartı + işlem durumları
  const [detail, setDetail] = useState<DeclarationItem | null>(null);
  const [detailLoadingId, setDetailLoadingId] = useState<string | null>(null);
  const [actionBusy, setActionBusy] = useState<"send" | "queue" | "refresh" | null>(null);

  // İşlem mesajları
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api<ClientResponse[]>("/api/clients");
        if (!cancelled) {
          setClients(list);
          if (list.length > 0) setFormClientId(list[0].id);
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

  const loadList = useCallback(async (opts: { clientId: string; period: string }) => {
    setListLoading(true);
    setListError(null);
    try {
      const list = await listDeclarations({
        clientId: opts.clientId || undefined,
        period: opts.period || undefined,
      });
      setItems(list);
    } catch (err) {
      setListError(err instanceof Error ? err.message : "Beyannameler yüklenirken bir hata oluştu.");
    } finally {
      setListLoading(false);
    }
  }, []);

  useEffect(() => {
    loadList({ clientId: filterClientId, period: filterPeriod });
  }, [filterClientId, filterPeriod, loadList]);

  async function handlePrepare() {
    setMessage(null);
    setError(null);

    // Çift validasyon (backend de aynı kuralları doğrular).
    if (!formClientId) {
      setError("Lütfen mükellef seçin.");
      return;
    }
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(formPeriod)) {
      setError("Dönem biçimi geçersiz. Örnek: 2026-06.");
      return;
    }

    setPreparing(true);
    try {
      const result = await prepareDeclaration(formClientId, formType, formPeriod);
      setMessage(result.message);
      setDetail(result.declaration);
      await loadList({ clientId: filterClientId, period: filterPeriod });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Taslak hazırlanırken bir hata oluştu.");
    } finally {
      setPreparing(false);
    }
  }

  async function handleOpenDetail(id: string) {
    setMessage(null);
    setError(null);
    setDetailLoadingId(id);
    try {
      setDetail(await getDeclaration(id));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Beyanname yüklenirken bir hata oluştu.");
    } finally {
      setDetailLoadingId(null);
    }
  }

  async function handleSend(id: string) {
    setMessage(null);
    setError(null);
    setActionBusy("send");
    try {
      const result = await sendDeclaration(id);
      setMessage(result.message);
      setDetail(result.declaration);
      await loadList({ clientId: filterClientId, period: filterPeriod });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Gönderim sırasında bir hata oluştu.");
    } finally {
      setActionBusy(null);
    }
  }

  /**
   * Faz 11 (E6): anında göndermek yerine gönderim kuyruğuna atar — GİB yoğun/kapalıyken
   * sistem müsait olduğunda otomatik gönderilir (üstel geri çekilmeyle 5 deneme).
   */
  async function handleQueueSend(id: string) {
    setMessage(null);
    setError(null);
    setActionBusy("queue");
    try {
      const result = await queueDeclarationSend(id);
      setMessage(result.message);
      await loadList({ clientId: filterClientId, period: filterPeriod });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Kuyruğa alma sırasında bir hata oluştu.");
    } finally {
      setActionBusy(null);
    }
  }

  async function handleRefresh(id: string) {
    setMessage(null);
    setError(null);
    setActionBusy("refresh");
    try {
      const result = await refreshDeclarationStatus(id);
      setMessage(result.message);
      setDetail(result.declaration);
      await loadList({ clientId: filterClientId, period: filterPeriod });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Durum sorgulanırken bir hata oluştu.");
    } finally {
      setActionBusy(null);
    }
  }

  const summary = detail?.kdvSummary ?? null;
  const summary2 = detail?.kdv2Summary ?? null;

  return (
    <div>
      <div>
        <h1 className="text-2xl font-bold text-navy-900">Beyannameler</h1>
        <p className="mt-1 text-sm text-navy-500">
          Motor verisinden KDV taslağı hazırlayın, GİB&apos;e gönderin, tahakkuku tek ekrandan takip edin.
        </p>
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}
      {message && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {message}
        </div>
      )}
      {error && <div className="alert-error mt-4">{error}</div>}

      {/* Taslak hazırlama */}
      <div className="card mt-6">
        <h2 className="text-sm font-semibold text-navy-900">KDV Taslağı Hazırla</h2>
        <p className="mt-1 text-xs text-navy-500">
          Dönem içindeki <strong>onaylanmış</strong> gelen faturalar ve fişlerden indirilecek KDV kırılımı,
          önceki dönem beyanından devreden KDV otomatik hesaplanır.
        </p>
        <div className="mt-4 flex flex-wrap items-end gap-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-navy-600">Mükellef</label>
            <select
              className="input max-w-[280px]"
              value={formClientId}
              onChange={(e) => setFormClientId(e.target.value)}
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
            <label className="mb-1 block text-xs font-medium text-navy-600">Tür</label>
            <select
              className="input !w-auto"
              value={formType}
              onChange={(e) => setFormType(e.target.value as DeclarationTypeCode)}
            >
              <option value="KDV1">KDV-1</option>
              <option value="KDV2">KDV-2 (tevkifat)</option>
              <option value="Muhtasar" disabled>
                Muhtasar (yakında)
              </option>
              <option value="Damga" disabled>
                Damga (yakında)
              </option>
            </select>
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-navy-600">Dönem</label>
            <input
              type="month"
              className="input !w-auto"
              value={formPeriod}
              onChange={(e) => setFormPeriod(e.target.value)}
            />
          </div>
          <button
            onClick={handlePrepare}
            className="btn-primary flex items-center gap-2"
            disabled={preparing || clientsLoading || clients.length === 0}
          >
            {preparing && (
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {preparing ? "Hesaplanıyor..." : "KDV Taslağı Hazırla"}
          </button>
        </div>
        {!clientsLoading && clients.length === 0 && (
          <p className="mt-3 text-xs text-amber-700">
            Beyanname hazırlamak için önce Mükellefler sayfasından mükellef ekleyin.
          </p>
        )}
      </div>

      {/* Seçili beyanname kartı */}
      {detail && (
        <div className="card mt-6 border-2 border-navy-200">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <div className="flex items-center gap-3">
                <h2 className="text-lg font-bold text-navy-900">
                  {typeLabels[detail.type]} — {detail.period}
                </h2>
                <StatusBadge status={detail.status} />
              </div>
              <p className="mt-1 text-sm text-navy-500">{detail.clientTitle ?? "—"}</p>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              {(detail.status === "Draft" || detail.status === "Ready" || detail.status === "Error") && (
                <button
                  onClick={() => handleSend(detail.id)}
                  className="btn-primary flex items-center gap-2"
                  disabled={actionBusy !== null}
                >
                  {actionBusy === "send" && (
                    <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                  )}
                  {actionBusy === "send"
                    ? "Gönderiliyor..."
                    : detail.status === "Error"
                      ? "Yeniden Gönder"
                      : "GİB'e Gönder"}
                </button>
              )}
              {(detail.status === "Draft" || detail.status === "Ready" || detail.status === "Error") && (
                <button
                  onClick={() => handleQueueSend(detail.id)}
                  className="btn-secondary flex items-center gap-2"
                  disabled={actionBusy !== null}
                  title="Anında göndermek yerine kuyruğa alır; sistem müsait olduğunda otomatik gönderilir."
                >
                  {actionBusy === "queue" && (
                    <span className="h-4 w-4 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                  )}
                  {actionBusy === "queue" ? "Kuyruğa alınıyor..." : "Kuyruğa Al"}
                </button>
              )}
              {detail.status === "Sent" && (
                <button
                  onClick={() => handleRefresh(detail.id)}
                  className="btn-secondary flex items-center gap-2"
                  disabled={actionBusy !== null}
                >
                  {actionBusy === "refresh" && (
                    <span className="h-4 w-4 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                  )}
                  {actionBusy === "refresh" ? "Sorgulanıyor..." : "Durumu Sorgula"}
                </button>
              )}
              <button onClick={() => setDetail(null)} className="btn-secondary !px-3 !py-1.5 text-xs">
                Kapat
              </button>
            </div>
          </div>

          {detail.status === "Error" && detail.errorMessage && (
            <div className="alert-error mt-4">
              <strong>GİB hatası:</strong> {detail.errorMessage}
            </div>
          )}

          {detail.status === "Approved" && (
            <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
              Beyanname onaylandı ({formatDateTime(detail.approvedAt)}). Tahakkuk tutarı:{" "}
              <strong>{formatTL(detail.tahakkukAmount)}</strong>
              {(detail.tahakkukAmount ?? 0) === 0 && " — ödenecek KDV çıkmadı (devreden dönem)."}
            </div>
          )}

          {detail.status === "Sent" && (
            <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
              GİB&apos;e gönderildi ({formatDateTime(detail.sentAt)}) — paket no:{" "}
              <strong>{detail.packageRef}</strong>. İşleme yaklaşık 30 saniye sürer; ardından
              &quot;Durumu Sorgula&quot; ile tahakkuku kontrol edin.
            </div>
          )}

          {/* Faz 8 (B2): KDV2 özet tablosu — sorumlu sıfatıyla tevkifat kırılımı */}
          {detail.type === "KDV2" ? (
            summary2 ? (
              <div className="mt-5">
                <h3 className="text-sm font-semibold text-navy-900">
                  Sorumlu Sıfatıyla Beyan (Tevkifat Kırılımı)
                </h3>
                {summary2.kirilimlar.length === 0 ? (
                  <p className="mt-2 text-sm text-navy-500">
                    Bu dönemde tevkifatlı <strong>onaylı</strong> alış faturası bulunamadı. Tevkifat
                    içeren faturaları önce Faturalar/Onay Kuyruğu ekranından onaylayın, ardından
                    taslağı yeniden hazırlayın.
                  </p>
                ) : (
                  <table className="mt-2 w-full text-left text-sm">
                    <thead className="border-b border-navy-100 text-xs uppercase tracking-wide text-navy-500">
                      <tr>
                        <th className="py-2 pr-4">Kod</th>
                        <th className="py-2 pr-4">Açıklama</th>
                        <th className="py-2 pr-4">Oran</th>
                        <th className="py-2 pr-4 text-right">Matrah</th>
                        <th className="py-2 pr-4 text-right">Tevkifat KDV</th>
                        <th className="py-2 text-right">Belge</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-navy-50">
                      {summary2.kirilimlar.map((k) => (
                        <tr key={k.kod + k.oran}>
                          <td className="py-2 pr-4 font-mono text-navy-800">{k.kod}</td>
                          <td className="py-2 pr-4 text-navy-700">{k.aciklama}</td>
                          <td className="py-2 pr-4 font-mono">{k.oran}</td>
                          <td className="py-2 pr-4 text-right">{formatTL(k.matrah)}</td>
                          <td className="py-2 pr-4 text-right font-medium">{formatTL(k.tevkifatKdv)}</td>
                          <td className="py-2 text-right text-navy-500">{k.belgeSayisi}</td>
                        </tr>
                      ))}
                    </tbody>
                    <tfoot>
                      <tr className="border-t border-navy-100">
                        <td colSpan={4} className="py-2 pr-4 font-semibold text-navy-700">
                          Sorumlu sıfatıyla ödenecek KDV
                        </td>
                        <td className="py-2 pr-4 text-right font-bold text-red-700">
                          {formatTL(summary2.odenecekKdv)}
                        </td>
                        <td />
                      </tr>
                    </tfoot>
                  </table>
                )}
                <p className="mt-3 text-xs text-navy-400">
                  Kaynak: {summary2.faturaSayisi} tevkifatlı onaylı alış faturası ({summary2.satirSayisi}{" "}
                  satır). Son hesaplama: {formatDateTime(detail.preparedAt)}.
                </p>
              </div>
            ) : (
              <p className="mt-4 text-sm text-navy-500">
                Bu beyanname için hesap özeti bulunamadı. &quot;KDV Taslağı Hazırla&quot; ile yeniden hesaplayın.
              </p>
            )
          ) : /* KDV1 özet tablosu */
          summary ? (
            <div className="mt-5 grid gap-6 lg:grid-cols-2">
              <div>
                <h3 className="text-sm font-semibold text-navy-900">İndirilecek KDV Kırılımı</h3>
                {summary.indirimKirilimlari.length === 0 ? (
                  <p className="mt-2 text-sm text-navy-500">
                    Bu dönemde onaylanmış gelen fatura/fiş bulunamadı. Önce Faturalar/Fişler
                    ekranından belgeleri onaylayın, ardından taslağı yeniden hazırlayın.
                  </p>
                ) : (
                  <table className="mt-2 w-full text-left text-sm">
                    <thead className="border-b border-navy-100 text-xs uppercase tracking-wide text-navy-500">
                      <tr>
                        <th className="py-2 pr-4">Hesap</th>
                        <th className="py-2 pr-4">Oran</th>
                        <th className="py-2 pr-4 text-right">Matrah</th>
                        <th className="py-2 pr-4 text-right">KDV</th>
                        <th className="py-2 text-right">Belge</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-navy-50">
                      {summary.indirimKirilimlari.map((b) => (
                        <tr key={b.hesapKodu + b.kdvOrani}>
                          <td className="py-2 pr-4 font-mono text-navy-800">{b.hesapKodu}</td>
                          <td className="py-2 pr-4">%{b.kdvOrani}</td>
                          <td className="py-2 pr-4 text-right">{formatTL(b.matrah)}</td>
                          <td className="py-2 pr-4 text-right font-medium">{formatTL(b.kdv)}</td>
                          <td className="py-2 text-right text-navy-500">{b.belgeSayisi}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
                <p className="mt-3 text-xs text-navy-400">
                  Kaynak: {summary.gelenFaturaSayisi} onaylı gelen fatura + {summary.fisSayisi} onaylı fiş.
                  {summary.gidenFaturaSayisi === 0 &&
                    " Satış (giden) faturası senkronu olmadığından hesaplanan KDV 0 görünür."}
                </p>
              </div>

              <div>
                <h3 className="text-sm font-semibold text-navy-900">Beyan Özeti</h3>
                <dl className="mt-2 divide-y divide-navy-50 text-sm">
                  <div className="flex justify-between py-2">
                    <dt className="text-navy-500">Hesaplanan KDV (satışlar)</dt>
                    <dd className="font-medium text-navy-900">{formatTL(summary.hesaplananKdv)}</dd>
                  </div>
                  <div className="flex justify-between py-2">
                    <dt className="text-navy-500">İndirilecek KDV toplamı</dt>
                    <dd className="font-medium text-navy-900">{formatTL(summary.indirilecekKdvToplam)}</dd>
                  </div>
                  {/* Faz 8 (B3): KKEG nedeniyle indirimden düşülen KDV (varsa) */}
                  {(summary.kkegIndirilemeyenKdv ?? 0) > 0 && (
                    <div className="flex justify-between py-2">
                      <dt className="text-orange-700">KKEG nedeniyle indirilemeyen KDV</dt>
                      <dd className="font-medium text-orange-700">
                        −{formatTL(summary.kkegIndirilemeyenKdv)}
                        <span className="ml-1 text-xs text-navy-400">(düşüldü)</span>
                      </dd>
                    </div>
                  )}
                  {/* Faz 8 (B2): tevkif edilen KDV bilgisi (KDV2 ile beyan edilir) */}
                  {(summary.tevkifatKdvToplam ?? 0) > 0 && (
                    <div className="flex justify-between py-2">
                      <dt className="text-purple-700">Tevkif edilen KDV (KDV2&apos;de beyan)</dt>
                      <dd className="font-medium text-purple-700">{formatTL(summary.tevkifatKdvToplam)}</dd>
                    </div>
                  )}
                  <div className="flex justify-between py-2">
                    <dt className="text-navy-500">Önceki dönemden devreden</dt>
                    <dd className="font-medium text-navy-900">{formatTL(summary.oncekiDonemdenDevredenKdv)}</dd>
                  </div>
                  <div className="flex justify-between py-2">
                    <dt className="font-semibold text-navy-700">Ödenecek KDV</dt>
                    <dd
                      className={`font-bold ${summary.odenecekKdv > 0 ? "text-red-700" : "text-navy-900"}`}
                    >
                      {formatTL(summary.odenecekKdv)}
                    </dd>
                  </div>
                  <div className="flex justify-between py-2">
                    <dt className="font-semibold text-navy-700">Sonraki döneme devreden</dt>
                    <dd
                      className={`font-bold ${
                        summary.sonrakiDonemeDevredenKdv > 0 ? "text-green-700" : "text-navy-900"
                      }`}
                    >
                      {formatTL(summary.sonrakiDonemeDevredenKdv)}
                    </dd>
                  </div>
                </dl>
                <p className="mt-3 text-xs text-navy-400">
                  Son hesaplama: {formatDateTime(detail.preparedAt)}
                </p>
              </div>
            </div>
          ) : (
            <p className="mt-4 text-sm text-navy-500">
              Bu beyanname için hesap özeti bulunamadı. &quot;KDV Taslağı Hazırla&quot; ile yeniden hesaplayın.
            </p>
          )}
        </div>
      )}

      {/* Durum panosu */}
      <div className="mt-8 flex flex-wrap items-center justify-between gap-4">
        <h2 className="text-lg font-bold text-navy-900">Dönem Beyannameleri</h2>
        <div className="flex flex-wrap items-center gap-3">
          <select
            className="input max-w-[240px]"
            value={filterClientId}
            onChange={(e) => setFilterClientId(e.target.value)}
            disabled={clientsLoading}
          >
            <option value="">Tüm mükellefler</option>
            {clients.map((c) => (
              <option key={c.id} value={c.id}>
                {c.title}
              </option>
            ))}
          </select>
          <input
            type="month"
            className="input !w-auto"
            value={filterPeriod}
            onChange={(e) => setFilterPeriod(e.target.value)}
            title="Dönem filtresi (boş = tüm dönemler)"
          />
          {filterPeriod && (
            <button className="btn-secondary !px-3 !py-1.5 text-xs" onClick={() => setFilterPeriod("")}>
              Dönemi temizle
            </button>
          )}
        </div>
      </div>

      {listError && (
        <div className="alert-error mt-4 flex items-center justify-between gap-4">
          <span>{listError}</span>
          <button
            className="btn-secondary !px-3 !py-1.5 text-xs"
            onClick={() => loadList({ clientId: filterClientId, period: filterPeriod })}
          >
            Tekrar dene
          </button>
        </div>
      )}

      {listLoading ? (
        <div className="mt-4 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
              <div className="mt-3 h-4 w-1/4 rounded bg-navy-50" />
            </div>
          ))}
        </div>
      ) : items.length === 0 && !listError ? (
        <div className="card mt-4 flex flex-col items-center py-14 text-center">
          {filterClientId || filterPeriod ? (
            <>
              <h3 className="text-lg font-semibold text-navy-900">Sonuç bulunamadı</h3>
              <p className="mt-2 text-sm text-navy-500">Filtre ölçütlerinizle eşleşen beyanname yok.</p>
            </>
          ) : (
            <>
              <h3 className="text-lg font-semibold text-navy-900">Henüz beyanname yok</h3>
              <p className="mt-2 max-w-md text-sm text-navy-500">
                Yukarıdan mükellef ve dönem seçip &quot;KDV Taslağı Hazırla&quot; ile ilk beyanname
                taslağınızı oluşturun.
              </p>
            </>
          )}
        </div>
      ) : items.length > 0 ? (
        <div className="mt-4 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[960px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Mükellef</th>
                <th className="px-4 py-3">Tür</th>
                <th className="px-4 py-3">Dönem</th>
                <th className="px-4 py-3">Durum</th>
                <th className="px-4 py-3">Paket No</th>
                <th className="px-4 py-3 text-right">Tahakkuk</th>
                <th className="px-4 py-3">Hazırlanma</th>
                <th className="px-4 py-3"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {items.map((d) => (
                <tr key={d.id} className="transition hover:bg-navy-50/60">
                  <td className="px-4 py-3 font-medium text-navy-900">{d.clientTitle ?? "—"}</td>
                  <td className="px-4 py-3">{typeLabels[d.type]}</td>
                  <td className="px-4 py-3">{d.period}</td>
                  <td className="px-4 py-3">
                    <StatusBadge status={d.status} />
                    {d.status === "Error" && d.errorMessage && (
                      <span className="mt-1 block max-w-[260px] text-xs text-red-700" title={d.errorMessage}>
                        {d.errorMessage.length > 80 ? d.errorMessage.slice(0, 80) + "…" : d.errorMessage}
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3 font-mono text-xs text-navy-600">{d.packageRef ?? "—"}</td>
                  <td className="px-4 py-3 text-right">{formatTL(d.tahakkukAmount)}</td>
                  <td className="px-4 py-3 text-navy-600">{formatDate(d.preparedAt)}</td>
                  <td className="px-4 py-3 text-right">
                    <button
                      onClick={() => handleOpenDetail(d.id)}
                      className="btn-secondary flex items-center gap-1.5 !px-3 !py-1.5 text-xs"
                      disabled={detailLoadingId === d.id}
                    >
                      {detailLoadingId === d.id && (
                        <span className="h-3 w-3 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                      )}
                      Görüntüle
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      <p className="mt-5 text-xs text-navy-400">
        Demo verileri MockGib sağlayıcısından gelir: gönderim ~30 saniyede işlenir, yaklaşık %10
        ihtimalle şema hatası döner (Yeniden Gönder ile tekrar denenebilir). Gerçek GİB e-Beyanname
        web servisi, SMMM e-Beyanname kimliği + GİB token&apos;ı iletildikten sonra etkinleştirilecektir.
      </p>
    </div>
  );
}
