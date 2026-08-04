"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import {
  api,
  apiDownload,
  cancelDocumentRequest,
  ClientResponse,
  createDocumentRequest,
  createPortalAccess,
  deactivatePortalAccess,
  DocumentRequestItem,
  getPortalAccess,
  listDocumentRequests,
  listPortalUploads,
  PortalAccessCreated,
  PortalAccessStatus,
  PortalUploadListItem,
} from "@/lib/api";

const categoryLabels: Record<string, string> = {
  Fis: "Fiş",
  Fatura: "Fatura",
  BankaEkstresi: "Banka Ekstresi",
  Sozlesme: "Sözleşme",
  Diger: "Diğer",
};

const requestStatusLabels: Record<string, string> = {
  Bekliyor: "Bekliyor",
  Yuklendi: "Yüklendi",
  Iptal: "İptal",
};

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
    : `${d.toLocaleDateString("tr-TR")} ${d.toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" })}`;
}

function RequestStatusBadge({ status }: { status: string }) {
  const cls =
    status === "Yuklendi"
      ? "bg-green-100 text-green-700"
      : status === "Iptal"
        ? "bg-navy-100 text-navy-500"
        : "bg-amber-100 text-amber-700";
  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${cls}`}>
      {requestStatusLabels[status] ?? status}
    </span>
  );
}

export default function BelgelerPage() {
  // Mükellef seçimi
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);
  const [selectedClientId, setSelectedClientId] = useState("");

  // Portal durumu
  const [access, setAccess] = useState<PortalAccessStatus | null>(null);
  const [accessLoading, setAccessLoading] = useState(false);
  const [accessError, setAccessError] = useState<string | null>(null);
  const [accessBusy, setAccessBusy] = useState(false);
  /** PIN yalnızca üretim anında BİR KEZ gösterilir. */
  const [created, setCreated] = useState<PortalAccessCreated | null>(null);
  const [copied, setCopied] = useState(false);

  // Belge talepleri
  const [requests, setRequests] = useState<DocumentRequestItem[]>([]);
  const [requestsLoading, setRequestsLoading] = useState(false);
  const [requestsError, setRequestsError] = useState<string | null>(null);
  const [newTitle, setNewTitle] = useState("");
  const [newDueDate, setNewDueDate] = useState("");
  const [creatingRequest, setCreatingRequest] = useState(false);
  const [cancelingId, setCancelingId] = useState<string | null>(null);

  // Gelen belgeler
  const [uploads, setUploads] = useState<PortalUploadListItem[]>([]);
  const [uploadsLoading, setUploadsLoading] = useState(true);
  const [uploadsError, setUploadsError] = useState<string | null>(null);
  const [categoryFilter, setCategoryFilter] = useState("");
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  // Mükellefler
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api<ClientResponse[]>("/api/clients");
        if (!cancelled) setClients(list);
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

  // Portal durumu + talepler (mükellef seçilince)
  const loadClientData = useCallback(async (clientId: string) => {
    setCreated(null);
    setAccessError(null);
    setRequestsError(null);

    if (!clientId) {
      setAccess(null);
      setRequests([]);
      return;
    }

    setAccessLoading(true);
    setRequestsLoading(true);
    try {
      const [accessData, requestsData] = await Promise.all([
        getPortalAccess(clientId),
        listDocumentRequests(clientId),
      ]);
      setAccess(accessData);
      setRequests(requestsData);
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Portal bilgileri yüklenirken bir hata oluştu.";
      setAccessError(msg);
      setAccess(null);
      setRequests([]);
    } finally {
      setAccessLoading(false);
      setRequestsLoading(false);
    }
  }, []);

  useEffect(() => {
    loadClientData(selectedClientId);
  }, [selectedClientId, loadClientData]);

  // Gelen belgeler (mükellef + kategori filtresiyle)
  const loadUploads = useCallback(async (clientId: string, category: string) => {
    setUploadsLoading(true);
    setUploadsError(null);
    try {
      const list = await listPortalUploads({
        clientId: clientId || undefined,
        category: category || undefined,
      });
      setUploads(list);
    } catch (err) {
      setUploadsError(err instanceof Error ? err.message : "Gelen belgeler yüklenirken bir hata oluştu.");
    } finally {
      setUploadsLoading(false);
    }
  }, []);

  useEffect(() => {
    loadUploads(selectedClientId, categoryFilter);
  }, [selectedClientId, categoryFilter, loadUploads]);

  async function handleCreateOrRenew() {
    if (!selectedClientId) return;
    setAccessBusy(true);
    setAccessError(null);
    try {
      const res = await createPortalAccess(selectedClientId);
      setCreated(res);
      const accessData = await getPortalAccess(selectedClientId);
      setAccess(accessData);
    } catch (err) {
      setAccessError(err instanceof Error ? err.message : "Portal erişimi oluşturulurken bir hata oluştu.");
    } finally {
      setAccessBusy(false);
    }
  }

  async function handleDeactivate() {
    if (!selectedClientId) return;
    if (!window.confirm("Portal erişimi kapatılsın mı? Mükellef artık belge yükleyemez.")) return;
    setAccessBusy(true);
    setAccessError(null);
    try {
      await deactivatePortalAccess(selectedClientId);
      setCreated(null);
      const accessData = await getPortalAccess(selectedClientId);
      setAccess(accessData);
    } catch (err) {
      setAccessError(err instanceof Error ? err.message : "Portal erişimi kapatılırken bir hata oluştu.");
    } finally {
      setAccessBusy(false);
    }
  }

  async function handleCopyLink() {
    if (!access?.portalPath) return;
    const fullLink = `${window.location.origin}${access.portalPath}`;
    try {
      await navigator.clipboard.writeText(fullLink);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      window.prompt("Linki kopyalayın:", fullLink);
    }
  }

  async function handleCreateRequest() {
    if (!selectedClientId) return;
    setRequestsError(null);

    if (!newTitle.trim()) {
      setRequestsError("Talep başlığı zorunludur (ör. Haziran banka ekstresi).");
      return;
    }

    setCreatingRequest(true);
    try {
      await createDocumentRequest(selectedClientId, newTitle.trim(), newDueDate || null);
      setNewTitle("");
      setNewDueDate("");
      setRequests(await listDocumentRequests(selectedClientId));
    } catch (err) {
      setRequestsError(err instanceof Error ? err.message : "Belge talebi oluşturulurken bir hata oluştu.");
    } finally {
      setCreatingRequest(false);
    }
  }

  async function handleCancelRequest(requestId: string) {
    if (!selectedClientId) return;
    setCancelingId(requestId);
    setRequestsError(null);
    try {
      await cancelDocumentRequest(selectedClientId, requestId);
      setRequests(await listDocumentRequests(selectedClientId));
    } catch (err) {
      setRequestsError(err instanceof Error ? err.message : "Talep iptal edilirken bir hata oluştu.");
    } finally {
      setCancelingId(null);
    }
  }

  async function handleDownload(upload: PortalUploadListItem) {
    setDownloadingId(upload.id);
    setUploadsError(null);
    try {
      const { blob, fileName } = await apiDownload(`/api/portal-uploads/${upload.id}/file`);
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = fileName ?? upload.fileName;
      a.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      setUploadsError(err instanceof Error ? err.message : "Belge indirilirken bir hata oluştu.");
    } finally {
      setDownloadingId(null);
    }
  }

  return (
    <div>
      <h1 className="text-2xl font-bold text-navy-900">Belgeler</h1>
      <p className="mt-1 text-sm text-navy-500">
        Mükellef belge portalı: erişim yönetimi, eksik belge takibi ve portaldan gelen belgeler.
      </p>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}

      {/* Mükellef seçici */}
      <div className="mt-6">
        <select
          className="input max-w-[320px]"
          value={selectedClientId}
          onChange={(e) => setSelectedClientId(e.target.value)}
          disabled={clientsLoading}
        >
          <option value="">
            {clientsLoading ? "Yükleniyor..." : "Mükellef seçin (portal yönetimi için)"}
          </option>
          {clients.map((c) => (
            <option key={c.id} value={c.id}>
              {c.title}
            </option>
          ))}
        </select>
      </div>

      {!clientsLoading && clients.length === 0 && !clientsError && (
        <div className="card mt-6 flex flex-col items-center py-12 text-center">
          <h2 className="text-lg font-semibold text-navy-900">Henüz mükellef eklemediniz</h2>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            Belge portalı oluşturmak için önce bir mükellef ekleyin.
          </p>
          <Link href="/panel/mukellefler" className="btn-primary mt-6">
            Mükellef Ekle
          </Link>
        </div>
      )}

      {selectedClientId && (
        <div className="mt-6 grid gap-6 lg:grid-cols-2">
          {/* Portal durumu kartı */}
          <div className="card">
            <h2 className="text-base font-semibold text-navy-900">Portal Erişimi</h2>
            {accessError && <div className="alert-error mt-3">{accessError}</div>}

            {accessLoading ? (
              <div className="mt-4 space-y-3">
                <div className="h-5 w-2/3 animate-pulse rounded bg-navy-100" />
                <div className="h-9 w-1/2 animate-pulse rounded bg-navy-100" />
              </div>
            ) : !access?.exists ? (
              <>
                <p className="mt-2 text-sm text-navy-500">
                  Bu mükellef için henüz portal erişimi yok. Erişim oluşturun; size bir link ve
                  6 haneli PIN verilir — mükellefinize iletin, üyeliksiz belge yüklesin.
                </p>
                <button className="btn-primary mt-4" onClick={handleCreateOrRenew} disabled={accessBusy}>
                  {accessBusy ? "Oluşturuluyor..." : "Portal Erişimi Oluştur"}
                </button>
              </>
            ) : (
              <>
                <div className="mt-3 flex items-center gap-2">
                  <span
                    className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                      access.isActive ? "bg-green-100 text-green-700" : "bg-red-100 text-red-700"
                    }`}
                  >
                    {access.isActive ? "Aktif" : "Kapalı"}
                  </span>
                  <span className="text-xs text-navy-400">
                    Son giriş: {formatDateTime(access.lastAccessAt)}
                  </span>
                </div>

                <div className="mt-3 flex flex-wrap items-center gap-2">
                  <code className="max-w-full truncate rounded-lg bg-navy-50 px-3 py-2 text-xs text-navy-700">
                    {access.portalPath}
                  </code>
                  <button className="btn-secondary !px-3 !py-1.5 text-xs" onClick={handleCopyLink}>
                    {copied ? "✓ Kopyalandı" : "Linki Kopyala"}
                  </button>
                </div>

                <div className="mt-4 flex flex-wrap gap-2">
                  <button
                    className="btn-secondary !px-3 !py-1.5 text-xs"
                    onClick={handleCreateOrRenew}
                    disabled={accessBusy}
                    title="Link aynı kalır; yalnızca PIN değişir ve erişim yeniden aktifleşir."
                  >
                    {accessBusy ? "İşleniyor..." : "PIN Yenile"}
                  </button>
                  {access.isActive && (
                    <button
                      className="btn-secondary !border-red-200 !px-3 !py-1.5 text-xs !text-red-700 hover:!bg-red-50"
                      onClick={handleDeactivate}
                      disabled={accessBusy}
                    >
                      Erişimi Kapat
                    </button>
                  )}
                </div>
              </>
            )}

            {/* Tek seferlik PIN gösterimi */}
            {created && (
              <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3">
                <p className="text-sm font-semibold text-amber-800">
                  PIN: <span className="text-xl font-extrabold tracking-widest">{created.pin}</span>
                </p>
                <p className="mt-1 text-xs text-amber-800">{created.message}</p>
              </div>
            )}
          </div>

          {/* Belge talebi kartı */}
          <div className="card">
            <h2 className="text-base font-semibold text-navy-900">Eksik Belge Talepleri</h2>
            <p className="mt-1 text-xs text-navy-400">
              Talep açın; mükellef portalda kırmızı rozetle görür, yükleyince otomatik kapanır.
            </p>

            {requestsError && <div className="alert-error mt-3">{requestsError}</div>}

            <div className="mt-3 flex flex-wrap gap-2">
              <input
                type="text"
                className="input min-w-[180px] flex-1"
                placeholder="Örn: Haziran banka ekstresi"
                maxLength={300}
                value={newTitle}
                onChange={(e) => setNewTitle(e.target.value)}
              />
              <input
                type="date"
                className="input !w-auto"
                title="Son yükleme tarihi (isteğe bağlı)"
                value={newDueDate}
                onChange={(e) => setNewDueDate(e.target.value)}
              />
              <button className="btn-primary !px-4" onClick={handleCreateRequest} disabled={creatingRequest}>
                {creatingRequest ? "Ekleniyor..." : "Talep Aç"}
              </button>
            </div>

            {requestsLoading ? (
              <div className="mt-4 space-y-2">
                {[1, 2].map((i) => (
                  <div key={i} className="h-10 animate-pulse rounded-lg bg-navy-50" />
                ))}
              </div>
            ) : requests.length === 0 ? (
              <p className="mt-4 text-sm text-navy-400">Henüz belge talebi yok.</p>
            ) : (
              <ul className="mt-4 space-y-2">
                {requests.map((r) => (
                  <li
                    key={r.id}
                    className="flex items-center justify-between gap-3 rounded-lg border border-navy-100 px-3 py-2"
                  >
                    <div className="min-w-0">
                      <p className="truncate text-sm font-medium text-navy-900">{r.title}</p>
                      <p className="text-xs text-navy-400">
                        {r.dueDate ? `Son gün: ${formatDate(r.dueDate)}` : "Son gün belirtilmedi"}
                        {" · "}Açıldı: {formatDate(r.createdAt)}
                      </p>
                    </div>
                    <div className="flex shrink-0 items-center gap-2">
                      <RequestStatusBadge status={r.status} />
                      {r.status === "Bekliyor" && (
                        <button
                          className="text-xs font-semibold text-red-600 hover:underline"
                          onClick={() => handleCancelRequest(r.id)}
                          disabled={cancelingId === r.id}
                        >
                          {cancelingId === r.id ? "..." : "İptal"}
                        </button>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}

      {/* Gelen belgeler */}
      <div className="mt-8 flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-bold text-navy-900">Gelen Belgeler</h2>
        <select
          className="input !w-auto"
          value={categoryFilter}
          onChange={(e) => setCategoryFilter(e.target.value)}
        >
          <option value="">Tüm kategoriler</option>
          {Object.entries(categoryLabels).map(([code, label]) => (
            <option key={code} value={code}>
              {label}
            </option>
          ))}
        </select>
      </div>

      {uploadsError && <div className="alert-error mt-4">{uploadsError}</div>}

      {uploadsLoading ? (
        <div className="mt-4 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
            </div>
          ))}
        </div>
      ) : uploads.length === 0 && !uploadsError ? (
        <div className="card mt-4 flex flex-col items-center py-12 text-center">
          <h3 className="text-lg font-semibold text-navy-900">Henüz portaldan belge gelmedi</h3>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            Mükellefinize portal linki ve PIN'i iletin; yüklediği her belge burada görünür,
            fişler otomatik olarak OCR hattına girer.
          </p>
        </div>
      ) : uploads.length > 0 ? (
        <div className="mt-4 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[860px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Mükellef</th>
                <th className="px-4 py-3">Dosya</th>
                <th className="px-4 py-3">Kategori</th>
                <th className="px-4 py-3">Not</th>
                <th className="px-4 py-3">Tarih</th>
                <th className="px-4 py-3">İşlem</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {uploads.map((u) => (
                <tr key={u.id} className="transition hover:bg-navy-50/60">
                  <td className="px-4 py-3 font-medium text-navy-900">{u.clientTitle ?? "—"}</td>
                  <td className="px-4 py-3 text-navy-700">{u.fileName}</td>
                  <td className="px-4 py-3">
                    <span className="rounded-full bg-navy-100 px-2.5 py-0.5 text-xs font-semibold text-navy-600">
                      {categoryLabels[u.category] ?? u.category}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-navy-500">{u.note ?? "—"}</td>
                  <td className="px-4 py-3">{formatDateTime(u.uploadedAt)}</td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      {u.processedReceiptId && (
                        <Link
                          href="/panel/fisler"
                          className="text-xs font-semibold text-navy-900 hover:underline"
                          title="Bu belge fiş olarak işlendi; doğrulama kuyruğunda."
                        >
                          Fişlerde görüntüle
                        </Link>
                      )}
                      <button
                        className="text-xs font-semibold text-navy-600 hover:underline"
                        onClick={() => handleDownload(u)}
                        disabled={downloadingId === u.id}
                      >
                        {downloadingId === u.id ? "İndiriliyor..." : "İndir"}
                      </button>
                    </div>
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
