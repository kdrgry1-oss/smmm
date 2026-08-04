"use client";

import { useCallback, useEffect, useState } from "react";
import {
  api,
  CertificateItem,
  CertificateSaveBody,
  CertificateTypeCode,
  ClientResponse,
  createCertificate,
  deleteCertificate,
  listCertificates,
  updateCertificate,
} from "@/lib/api";

const typeLabels: Record<CertificateTypeCode, string> = {
  EImza: "e-İmza",
  MaliMuhur: "Mali Mühür",
  Kep: "KEP",
};

function formatDate(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

/** Kalan süre rozetleri: süresi dolmuş koyu kırmızı, <30 gün kırmızı, <90 gün sarı, diğerleri yeşil. */
function ExpiryBadge({ days }: { days: number }) {
  if (days <= 0) {
    return (
      <span className="rounded-full bg-red-800 px-2.5 py-0.5 text-xs font-bold text-white">
        Süresi doldu
      </span>
    );
  }
  if (days < 30) {
    return (
      <span className="rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-bold text-red-700">
        ⚠ {days} gün kaldı
      </span>
    );
  }
  if (days < 90) {
    return (
      <span className="rounded-full bg-amber-100 px-2.5 py-0.5 text-xs font-semibold text-amber-700">
        {days} gün kaldı
      </span>
    );
  }
  return (
    <span className="rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-semibold text-green-700">
      {days} gün kaldı
    </span>
  );
}

interface FormState {
  clientId: string; // "" = ofisin kendi sertifikası
  type: CertificateTypeCode;
  owner: string;
  expiresAt: string;
  note: string;
}

const emptyForm: FormState = { clientId: "", type: "EImza", owner: "", expiresAt: "", note: "" };

export default function SertifikalarPage() {
  const [certificates, setCertificates] = useState<CertificateItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);

  // Modal (ekle/düzenle)
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<CertificateItem | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  const [deletingId, setDeletingId] = useState<string | null>(null);

  const loadList = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setCertificates(await listCertificates());
    } catch (err) {
      setError(err instanceof Error ? err.message : "Sertifikalar yüklenirken bir hata oluştu.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadList();
    let cancelled = false;
    (async () => {
      try {
        const list = await api<ClientResponse[]>("/api/clients");
        if (!cancelled) setClients(list);
      } catch {
        // mükellef listesi form içindir; hatası listeyi engellemez
      } finally {
        if (!cancelled) setClientsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [loadList]);

  function openCreate() {
    setEditing(null);
    setForm(emptyForm);
    setFormError(null);
    setModalOpen(true);
  }

  function openEdit(cert: CertificateItem) {
    setEditing(cert);
    setForm({
      clientId: cert.clientId ?? "",
      type: cert.type,
      owner: cert.owner,
      expiresAt: cert.expiresAt.slice(0, 10),
      note: cert.note ?? "",
    });
    setFormError(null);
    setModalOpen(true);
  }

  async function handleSave() {
    setFormError(null);

    if (!form.owner.trim()) {
      setFormError("Sahip açıklaması zorunludur (ör. Ahmet Yılmaz — imza yetkilisi).");
      return;
    }
    if (!form.expiresAt) {
      setFormError("Geçerlilik sonu tarihini seçin.");
      return;
    }

    const body: CertificateSaveBody = {
      clientId: form.clientId || null,
      type: form.type,
      owner: form.owner.trim(),
      expiresAt: form.expiresAt,
      note: form.note.trim() || null,
    };

    setSaving(true);
    try {
      if (editing) await updateCertificate(editing.id, body);
      else await createCertificate(body);
      setModalOpen(false);
      await loadList();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : "Kayıt sırasında bir hata oluştu.");
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(cert: CertificateItem) {
    if (!window.confirm(`"${cert.owner}" sertifika kaydı silinsin mi?`)) return;
    setDeletingId(cert.id);
    setError(null);
    try {
      await deleteCertificate(cert.id);
      await loadList();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Silme sırasında bir hata oluştu.");
    } finally {
      setDeletingId(null);
    }
  }

  const expiringSoon = certificates.filter((c) => c.daysRemaining < 90).length;

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Sertifikalar</h1>
          <p className="mt-1 text-sm text-navy-500">
            e-İmza, mali mühür ve KEP sürelerini takip edin — süresi dolan sertifika işleri durdurur.
          </p>
        </div>
        <button className="btn-primary" onClick={openCreate}>
          Sertifika Ekle
        </button>
      </div>

      {error && (
        <div className="alert-error mt-4 flex items-center justify-between gap-4">
          <span>{error}</span>
          <button className="btn-secondary !px-3 !py-1.5 text-xs" onClick={loadList}>
            Tekrar dene
          </button>
        </div>
      )}

      {!loading && expiringSoon > 0 && (
        <div className="alert-info mt-4">
          ⚠ {expiringSoon} sertifikanın süresi 90 gün içinde doluyor. Yenileme başvurularını şimdiden yapın
          (e-Defter berat imzalama hatalarının sık sebebi süresi dolmuş sertifikadır).
        </div>
      )}

      {loading ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
              <div className="mt-3 h-4 w-1/4 rounded bg-navy-50" />
            </div>
          ))}
        </div>
      ) : certificates.length === 0 && !error ? (
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          <h2 className="text-lg font-semibold text-navy-900">Henüz sertifika kaydı yok</h2>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            Kendi e-imzanızı, mali mührünüzü ve mükelleflerinizin sertifikalarını ekleyin;
            süre yaklaştığında panelde uyarı görürsünüz.
          </p>
          <button className="btn-primary mt-6" onClick={openCreate}>
            İlk Sertifikayı Ekle
          </button>
        </div>
      ) : certificates.length > 0 ? (
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[860px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Ait Olduğu</th>
                <th className="px-4 py-3">Tür</th>
                <th className="px-4 py-3">Sahip</th>
                <th className="px-4 py-3">Bitiş Tarihi</th>
                <th className="px-4 py-3">Kalan Süre</th>
                <th className="px-4 py-3">Not</th>
                <th className="px-4 py-3">İşlem</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {certificates.map((cert) => (
                <tr
                  key={cert.id}
                  className={`transition hover:bg-navy-50/60 ${cert.daysRemaining < 30 ? "bg-red-50/40" : ""}`}
                >
                  <td className="px-4 py-3 font-medium text-navy-900">
                    {cert.clientId ? (cert.clientTitle ?? "—") : "Ofis (SMMM)"}
                  </td>
                  <td className="px-4 py-3">
                    <span className="rounded-full bg-navy-100 px-2.5 py-0.5 text-xs font-semibold text-navy-600">
                      {typeLabels[cert.type] ?? cert.type}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-navy-700">{cert.owner}</td>
                  <td className="px-4 py-3">{formatDate(cert.expiresAt)}</td>
                  <td className="px-4 py-3">
                    <ExpiryBadge days={cert.daysRemaining} />
                  </td>
                  <td className="px-4 py-3 text-navy-500">{cert.note ?? "—"}</td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <button
                        className="text-xs font-semibold text-navy-900 hover:underline"
                        onClick={() => openEdit(cert)}
                      >
                        Düzenle
                      </button>
                      <button
                        className="text-xs font-semibold text-red-600 hover:underline"
                        onClick={() => handleDelete(cert)}
                        disabled={deletingId === cert.id}
                      >
                        {deletingId === cert.id ? "Siliniyor..." : "Sil"}
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      {/* Ekle/düzenle modalı */}
      {modalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/40 px-4">
          <div className="card w-full max-w-md">
            <h2 className="text-lg font-bold text-navy-900">
              {editing ? "Sertifikayı Düzenle" : "Sertifika Ekle"}
            </h2>

            {formError && <div className="alert-error mt-4">{formError}</div>}

            <div className="mt-4 space-y-4">
              <div>
                <label className="label">Ait olduğu</label>
                <select
                  className="input"
                  value={form.clientId}
                  onChange={(e) => setForm((f) => ({ ...f, clientId: e.target.value }))}
                  disabled={clientsLoading}
                >
                  <option value="">Ofis (SMMM&apos;nin kendisi)</option>
                  {clients.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.title}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label className="label">Tür</label>
                <select
                  className="input"
                  value={form.type}
                  onChange={(e) =>
                    setForm((f) => ({ ...f, type: e.target.value as CertificateTypeCode }))
                  }
                >
                  <option value="EImza">e-İmza</option>
                  <option value="MaliMuhur">Mali Mühür</option>
                  <option value="Kep">KEP</option>
                </select>
              </div>
              <div>
                <label className="label">Sahip açıklaması</label>
                <input
                  type="text"
                  className="input"
                  maxLength={200}
                  placeholder="Örn: Ahmet Yılmaz — şirket imza yetkilisi"
                  value={form.owner}
                  onChange={(e) => setForm((f) => ({ ...f, owner: e.target.value }))}
                />
              </div>
              <div>
                <label className="label">Geçerlilik sonu</label>
                <input
                  type="date"
                  className="input"
                  value={form.expiresAt}
                  onChange={(e) => setForm((f) => ({ ...f, expiresAt: e.target.value }))}
                />
              </div>
              <div>
                <label className="label">Not (isteğe bağlı)</label>
                <input
                  type="text"
                  className="input"
                  maxLength={500}
                  placeholder="Örn: Kamu SM'den alındı, yenileme 2 hafta sürüyor"
                  value={form.note}
                  onChange={(e) => setForm((f) => ({ ...f, note: e.target.value }))}
                />
              </div>
            </div>

            <div className="mt-6 flex justify-end gap-2">
              <button className="btn-secondary" onClick={() => setModalOpen(false)} disabled={saving}>
                Vazgeç
              </button>
              <button className="btn-primary" onClick={handleSave} disabled={saving}>
                {saving ? "Kaydediliyor..." : "Kaydet"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
