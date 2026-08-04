"use client";

/**
 * Faz 18 — "GİB Bağlantısı" sayfası: MÜŞAVİR (SMMM) TEK GİRİŞ modeli.
 *
 * Mali müşavir KENDİ GİB kullanıcı kodu/şifresini BİR KEZ kaydeder ve "GİB Oturumu Aç"
 * ile (görseldeki doğrulama kodunu kendisi girerek) tek oturum açar. GİB'in
 * "müşavir/mükellef işlemleri" özelliğiyle bu TEK oturum üzerinden tüm mükelleflerin
 * belgeleri/tebligatları çekilir — mükellef başına ayrı şifre/captcha GEREKMEZ.
 *
 * Bölümler:
 *  (a) Müşavir GİB kimliği formu (kullanıcı kodu + şifre; şifre asla geri dönmez).
 *  (b) "GİB Oturumu Aç" → GibLoginModal (advisor akışı; kod kullanıcı tarafından girilir).
 *  (c) Oturum durumu rozeti: aktif / yenilenmeli / hiç girilmemiş + son görülme.
 *  (d) "Mükelleflerimi GİB'den Getir": eşleşenler işaretli; eşleşmeyenler tek tıkla
 *      mükellef olarak eklenebilir.
 */

import { FormEvent, useCallback, useEffect, useState } from "react";
import {
  api,
  fetchGibAdvisorClients,
  getGibAdvisorCredentials,
  getGibAdvisorSession,
  saveGibAdvisorCredentials,
  ClientResponse,
  GibAdvisorClientItem,
  GibAdvisorCredentialResult,
  GibAdvisorSessionStatus,
} from "@/lib/api";
import GibLoginModal from "@/components/GibLoginModal";

/** ISO tarihini Türkçe kısa biçimde gösterir. */
function formatDateTime(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "—";
  return d.toLocaleString("tr-TR", { dateStyle: "short", timeStyle: "short" });
}

export default function GibBaglantisiPage() {
  // (a) Kimlik formu
  const [credential, setCredential] = useState<GibAdvisorCredentialResult | null>(null);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [saving, setSaving] = useState(false);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);

  // (c) Oturum durumu rozeti
  const [session, setSession] = useState<GibAdvisorSessionStatus | null>(null);
  const [sessionLoading, setSessionLoading] = useState(true);

  // (b) Giriş modalı
  const [loginOpen, setLoginOpen] = useState(false);

  // (d) Mükellef listesi
  const [fetching, setFetching] = useState(false);
  const [fetchMessage, setFetchMessage] = useState<string | null>(null);
  const [fetchError, setFetchError] = useState<string | null>(null);
  const [needsLogin, setNeedsLogin] = useState(false);
  const [gibClients, setGibClients] = useState<GibAdvisorClientItem[]>([]);
  const [addingTaxId, setAddingTaxId] = useState<string | null>(null);
  const [addedTaxIds, setAddedTaxIds] = useState<Set<string>>(new Set());

  const loadCredential = useCallback(async () => {
    try {
      const data = await getGibAdvisorCredentials();
      setCredential(data);
    } catch {
      // durum kartı bilgilendirmedir; hata formu engellemez
    }
  }, []);

  const loadSession = useCallback(async () => {
    setSessionLoading(true);
    try {
      const data = await getGibAdvisorSession();
      setSession(data);
    } catch {
      setSession(null);
    } finally {
      setSessionLoading(false);
    }
  }, []);

  useEffect(() => {
    loadCredential();
    loadSession();
  }, [loadCredential, loadSession]);

  /** (a) Kimlik kaydet: şifre yalnız bu istekte gider, yanıtta asla dönmez. */
  async function handleSaveCredentials(e: FormEvent) {
    e.preventDefault();
    setSaveMessage(null);
    setSaveError(null);

    if (!username.trim() || !password) {
      setSaveError("Müşavir GİB kullanıcı kodu ve şifresi zorunludur.");
      return;
    }

    setSaving(true);
    try {
      const result = await saveGibAdvisorCredentials(username.trim(), password);
      setCredential(result);
      setUsername("");
      setPassword("");
      setSaveMessage(
        'Müşavir GİB kimliği kaydedildi. Şimdi "GİB Oturumu Aç" ile doğrulama kodunu girerek oturumu başlatın.'
      );
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Kimlik kaydedilirken bir hata oluştu.");
    } finally {
      setSaving(false);
    }
  }

  /** (d) GİB'den mükellef listesini çeker; eşleşmeler sunucuda hesaplanır. */
  async function handleFetchClients() {
    setFetching(true);
    setFetchMessage(null);
    setFetchError(null);
    setNeedsLogin(false);
    try {
      const result = await fetchGibAdvisorClients();
      if (!result.success) {
        setFetchError(result.message);
        setNeedsLogin(result.needsCaptchaLogin);
        setGibClients([]);
        return;
      }
      setGibClients(result.items);
      setFetchMessage(result.message);
    } catch (err) {
      setFetchError(
        err instanceof Error ? err.message : "Mükellef listesi alınırken bir hata oluştu."
      );
    } finally {
      setFetching(false);
    }
  }

  /** (d) Eşleşmeyen GİB mükellefini sisteme ekler (unvan + VKN ile). */
  async function handleAddClient(item: GibAdvisorClientItem) {
    setAddingTaxId(item.taxId);
    setFetchError(null);
    try {
      await api<ClientResponse>("/api/clients", {
        method: "POST",
        body: {
          title: item.title,
          taxId: item.taxId,
          eInvoiceEnabled: false,
          eArchiveEnabled: false,
        },
      });
      setAddedTaxIds((prev) => new Set(prev).add(item.taxId));
    } catch (err) {
      setFetchError(err instanceof Error ? err.message : "Mükellef eklenirken bir hata oluştu.");
    } finally {
      setAddingTaxId(null);
    }
  }

  // (c) Rozet durumu
  const badge = sessionLoading
    ? { text: "Oturum durumu kontrol ediliyor...", cls: "bg-navy-100 text-navy-600" }
    : session?.active
      ? { text: "Oturum aktif", cls: "bg-green-100 text-green-800" }
      : session?.expired
        ? { text: "Oturum yenilenmeli", cls: "bg-amber-100 text-amber-800" }
        : { text: "Oturum açılmadı", cls: "bg-navy-100 text-navy-600" };

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">GİB Bağlantısı</h1>
          <p className="mt-1 text-sm text-navy-500">
            Müşavir kimliğinizle TEK GİB oturumu açın; mükellef işlemleri özelliğiyle tüm
            mükelleflerinizin belgeleri ve e-Tebligatları bu oturumla çekilir. Mükellef başına
            ayrıca şifre gerekmez.
          </p>
        </div>
        {/* (c) Oturum durumu rozeti */}
        <div className="flex items-center gap-3">
          <span className={`rounded-full px-3 py-1 text-xs font-semibold ${badge.cls}`}>
            {badge.text}
          </span>
          {session && session.lastSeenAt && (
            <span className="text-xs text-navy-400" title="Oturumun son başarılı hareketi">
              Son görülme: {formatDateTime(session.lastSeenAt)}
            </span>
          )}
        </div>
      </div>

      <div className="mt-6 grid gap-6 lg:grid-cols-2">
        {/* (a) Müşavir GİB kimliği */}
        <div className="card">
          <h2 className="text-lg font-semibold text-navy-900">Müşavir GİB Kimliği</h2>
          <p className="mt-1 text-sm text-navy-500">
            Dijital Vergi Dairesi kullanıcı kodunuz ve şifreniz bir kez kaydedilir; şifre
            şifreli saklanır, hiçbir yanıt veya günlükte görünmez.
          </p>

          {credential?.hasCredential && (
            <div className="mt-3 rounded-lg border border-navy-100 bg-navy-50 px-4 py-3 text-sm text-navy-700">
              Kayıtlı kimlik: <span className="font-mono">{credential.maskedUsername}</span>
              {credential.updatedAt && (
                <span className="ml-2 text-xs text-navy-400">
                  (güncellendi: {formatDateTime(credential.updatedAt)})
                </span>
              )}
            </div>
          )}

          <form onSubmit={handleSaveCredentials} className="mt-4 space-y-4" noValidate>
            <div>
              <label htmlFor="advisorUsername" className="label">
                GİB Kullanıcı Kodu / TCKN
              </label>
              <input
                id="advisorUsername"
                type="text"
                className="input"
                autoComplete="off"
                placeholder={
                  credential?.hasCredential
                    ? "Kayıtlı kimliği değiştirmek için yeni kullanıcı kodu"
                    : "Müşavirin GİB kullanıcı kodu veya TC kimlik numarası"
                }
                value={username}
                onChange={(e) => setUsername(e.target.value)}
              />
            </div>
            <div>
              <label htmlFor="advisorPassword" className="label">
                GİB Şifresi
              </label>
              <input
                id="advisorPassword"
                type="password"
                className="input"
                autoComplete="new-password"
                placeholder={
                  credential?.hasCredential ? "Değiştirmek için yeni şifre" : "GİB şifreniz"
                }
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </div>

            {saveMessage && (
              <div className="rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
                {saveMessage}
              </div>
            )}
            {saveError && <div className="alert-error">{saveError}</div>}

            <div className="flex flex-wrap items-center gap-2">
              <button type="submit" className="btn-primary flex items-center gap-2" disabled={saving}>
                {saving && (
                  <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                )}
                {saving ? "Kaydediliyor..." : "Kimliği Kaydet"}
              </button>
              {/* (b) GİB Oturumu Aç — advisor akışı (kod kullanıcı tarafından girilir) */}
              <button
                type="button"
                className="btn-secondary"
                onClick={() => setLoginOpen(true)}
                disabled={!credential?.hasCredential}
                title={
                  credential?.hasCredential
                    ? "GİB giriş ekranındaki doğrulama kodunu girin — tek oturum tüm mükellefler için yeterlidir."
                    : "Önce müşavir GİB kimliğini kaydedin."
                }
              >
                GİB Oturumu Aç
              </button>
            </div>
            <p className="text-xs text-navy-400">
              Güvenlik gereği doğrulama kodu (captcha) sistem tarafından ASLA otomatik çözülmez;
              kodu siz girersiniz. Başarılı girişte oturum sunucuda saklanır ve gece otomatik
              senkron dahil tüm çekimler kod istemeden sürer.
            </p>
          </form>
        </div>

        {/* (d) Mükelleflerimi GİB'den Getir */}
        <div className="card">
          <h2 className="text-lg font-semibold text-navy-900">Mükelleflerimi GİB&apos;den Getir</h2>
          <p className="mt-1 text-sm text-navy-500">
            Müşavir oturumunuzla GİB&apos;deki mükellef listeniz çekilir; VKN&apos;si sistemde kayıtlı
            olanlar işaretlenir, olmayanları tek tıkla mükellef olarak ekleyebilirsiniz.
          </p>

          <div className="mt-4">
            <button
              onClick={handleFetchClients}
              className="btn-primary flex items-center gap-2"
              disabled={fetching || !credential?.hasCredential}
              title={
                credential?.hasCredential
                  ? "GİB'deki mükellef listesini çeker (aktif müşavir oturumu gerekir)."
                  : "Önce müşavir GİB kimliğini kaydedin."
              }
            >
              {fetching && (
                <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
              )}
              {fetching ? "Getiriliyor..." : "Mükelleflerimi GİB'den Getir"}
            </button>
          </div>

          {fetchMessage && (
            <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
              {fetchMessage}
            </div>
          )}
          {fetchError && (
            <div className="alert-error mt-4 flex flex-wrap items-center justify-between gap-3">
              <span>{fetchError}</span>
              {needsLogin && (
                <button
                  className="rounded-md bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700"
                  onClick={() => setLoginOpen(true)}
                >
                  Doğrulama Kodunu Gir
                </button>
              )}
            </div>
          )}

          {gibClients.length > 0 && (
            <div className="mt-4 overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-navy-100 text-left text-xs uppercase text-navy-400">
                    <th className="py-2 pr-4">VKN / TCKN</th>
                    <th className="py-2 pr-4">Unvan (GİB)</th>
                    <th className="py-2 pr-4">Durum</th>
                    <th className="py-2"></th>
                  </tr>
                </thead>
                <tbody>
                  {gibClients.map((item) => {
                    const added = addedTaxIds.has(item.taxId);
                    return (
                      <tr key={item.taxId} className="border-b border-navy-50">
                        <td className="py-2 pr-4 font-mono">{item.taxId}</td>
                        <td className="py-2 pr-4">{item.title}</td>
                        <td className="py-2 pr-4">
                          {item.matched || added ? (
                            <span
                              className="rounded-full bg-green-100 px-2 py-0.5 text-xs font-semibold text-green-800"
                              title={item.clientTitle ? `Sistemdeki kayıt: ${item.clientTitle}` : undefined}
                            >
                              ✓ Kayıtlı
                            </span>
                          ) : (
                            <span className="rounded-full bg-navy-100 px-2 py-0.5 text-xs font-semibold text-navy-600">
                              Sisteme eklenebilir
                            </span>
                          )}
                        </td>
                        <td className="py-2 text-right">
                          {!item.matched && !added && (
                            <button
                              className="btn-secondary !px-3 !py-1 text-xs"
                              onClick={() => handleAddClient(item)}
                              disabled={addingTaxId === item.taxId}
                            >
                              {addingTaxId === item.taxId ? "Ekleniyor..." : "Mükellef olarak ekle"}
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
        </div>
      </div>

      {/* (b) Müşavir GİB girişi modalı (advisor akışı) */}
      {loginOpen && (
        <GibLoginModal
          mode="advisor"
          onClose={() => setLoginOpen(false)}
          onSuccess={() => {
            loadSession();
            setNeedsLogin(false);
          }}
        />
      )}
    </div>
  );
}
