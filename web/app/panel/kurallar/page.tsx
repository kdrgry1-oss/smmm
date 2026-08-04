"use client";

import { useCallback, useEffect, useState } from "react";
import {
  ACCOUNT_CODE_REGEX,
  api,
  ClientResponse,
  CodingRule,
  createCodingRule,
  deleteCodingRule,
  listCodingRules,
} from "@/lib/api";
import AccountCodeAutocomplete from "@/components/AccountCodeAutocomplete";

/**
 * Kurallar (Katman 2): SMMM tanımlı anahtar kelime/regex kuralları.
 * Desen ";" içeriyorsa anahtar kelime listesi (ör. "akaryakıt;benzin;motorin"),
 * içermiyorsa regex olarak değerlendirilir. Büyük öncelik önce denenir.
 */
export default function CodingRulesPage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const [rules, setRules] = useState<CodingRule[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Form
  const [pattern, setPattern] = useState("");
  const [accountCode, setAccountCode] = useState("");
  const [priority, setPriority] = useState("10");
  const [clientId, setClientId] = useState("");
  const [isActive, setIsActive] = useState(true);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [formSuccess, setFormSuccess] = useState<string | null>(null);

  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  // Mükellefleri yükle (kapsam seçici için)
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

  const loadRules = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await listCodingRules();
      setRules(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Kurallar yüklenirken bir hata oluştu.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadRules();
  }, [loadRules]);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setFormError(null);
    setFormSuccess(null);

    // UI tarafı doğrulama (backend aynı kuralları ikinci kez doğrular).
    const trimmedPattern = pattern.trim();
    const trimmedAccount = accountCode.trim();
    const parsedPriority = Number(priority);

    if (!trimmedPattern) {
      setFormError("Desen zorunludur.");
      return;
    }
    if (trimmedPattern.length > 500) {
      setFormError("Desen en fazla 500 karakter olabilir.");
      return;
    }
    if (!trimmedPattern.includes(";")) {
      // Regex sözdizimi kontrolü (";" içermeyen desenler regex sayılır).
      try {
        new RegExp(trimmedPattern);
      } catch {
        setFormError("Geçersiz desen.");
        return;
      }
    }
    if (!ACCOUNT_CODE_REGEX.test(trimmedAccount)) {
      setFormError("Geçerli bir hesap kodu girin (ör. 770.01).");
      return;
    }
    if (!Number.isInteger(parsedPriority) || parsedPriority < 0 || parsedPriority > 1000) {
      setFormError("Öncelik 0 ile 1000 arasında bir tam sayı olmalıdır.");
      return;
    }

    setSaving(true);
    try {
      await createCodingRule({
        pattern: trimmedPattern,
        accountCode: trimmedAccount,
        priority: parsedPriority,
        clientId: clientId || null,
        isActive,
      });
      setFormSuccess("Kural eklendi.");
      setPattern("");
      setAccountCode("");
      setPriority("10");
      setClientId("");
      setIsActive(true);
      await loadRules();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : "Kural kaydedilirken bir hata oluştu.");
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(rule: CodingRule) {
    if (!window.confirm(`"${rule.pattern}" kuralını silmek istediğinize emin misiniz?`)) return;

    setDeleteError(null);
    setDeletingId(rule.id);
    try {
      await deleteCodingRule(rule.id);
      await loadRules();
    } catch (err) {
      setDeleteError(err instanceof Error ? err.message : "Kural silinirken bir hata oluştu.");
    } finally {
      setDeletingId(null);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Kodlama Kuralları</h1>
          <p className="mt-1 text-sm text-navy-500">
            Anahtar kelime veya regex desenleriyle kalemleri hesaplara yönlendirin. Hafıza ve
            satıcı varsayılanı eşleşmezse bu kurallar devreye girer.
          </p>
        </div>
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}

      {/* Yeni kural formu */}
      <form onSubmit={handleSubmit} className="card mt-6">
        <h2 className="text-sm font-semibold text-navy-900">Yeni Kural</h2>
        <div className="mt-4 flex flex-wrap items-end gap-4">
          <div className="min-w-[260px] flex-1">
            <label htmlFor="rulePattern" className="label">
              Desen
            </label>
            <input
              id="rulePattern"
              type="text"
              className="input"
              placeholder="akaryakıt;benzin;motorin (veya regex)"
              value={pattern}
              onChange={(e) => setPattern(e.target.value)}
              maxLength={500}
            />
            <p className="mt-1 text-xs text-navy-400">
              &quot;;&quot; ile ayrılmış anahtar kelimeler ya da regex. Kalem açıklaması
              küçük harfe normalize edilerek eşleştirilir.
            </p>
          </div>
          <div>
            <label className="label">Hesap Kodu</label>
            {/* Autocomplete: "7" → 7 ile başlayan kodlar, "kira" → adı kira içeren hesaplar.
                Kapsamda mükellef seçiliyse onun planında, değilse tüm mükelleflerde aranır. */}
            <AccountCodeAutocomplete
              clientId={clientId || undefined}
              value={accountCode}
              onChange={setAccountCode}
              placeholder="770.01"
              inputClassName="input w-32 font-mono"
            />
          </div>
          <div>
            <label htmlFor="rulePriority" className="label">
              Öncelik
            </label>
            <input
              id="rulePriority"
              type="number"
              className="input w-24"
              min={0}
              max={1000}
              value={priority}
              onChange={(e) => setPriority(e.target.value)}
            />
          </div>
          <div>
            <label htmlFor="ruleClient" className="label">
              Kapsam
            </label>
            <select
              id="ruleClient"
              className="input min-w-[200px]"
              value={clientId}
              onChange={(e) => setClientId(e.target.value)}
              disabled={clientsLoading}
            >
              <option value="">Tüm mükellefler</option>
              {clients.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </select>
          </div>
          <label className="flex items-center gap-2 pb-2.5 text-sm text-navy-700">
            <input
              type="checkbox"
              checked={isActive}
              onChange={(e) => setIsActive(e.target.checked)}
            />
            Aktif
          </label>
          <button type="submit" className="btn-primary flex items-center gap-2" disabled={saving}>
            {saving && (
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {saving ? "Kaydediliyor..." : "Kural Ekle"}
          </button>
        </div>

        {formError && <div className="alert-error mt-4">{formError}</div>}
        {formSuccess && (
          <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
            {formSuccess}
          </div>
        )}
      </form>

      {error && (
        <div className="alert-error mt-6 flex items-center justify-between gap-4">
          <span>{error}</span>
          <button className="btn-secondary !px-3 !py-1.5 text-xs" onClick={loadRules}>
            Tekrar dene
          </button>
        </div>
      )}
      {deleteError && <div className="alert-error mt-4">{deleteError}</div>}

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
      ) : rules.length === 0 && !error ? (
        /* Boş durum */
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          <h2 className="text-lg font-semibold text-navy-900">Henüz kural yok</h2>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            Örnek: &quot;akaryakıt;benzin;motorin&quot; desenini 770.01 hesabına yönlendirin.
            Kural, hafızada eşleşme olmayan tüm yeni kalemlere uygulanır.
          </p>
        </div>
      ) : rules.length > 0 ? (
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[760px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Desen</th>
                <th className="px-4 py-3">Hesap Kodu</th>
                <th className="px-4 py-3 text-right">Öncelik</th>
                <th className="px-4 py-3">Kapsam</th>
                <th className="px-4 py-3">Durum</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {rules.map((rule) => (
                <tr key={rule.id} className="transition hover:bg-navy-50/60">
                  <td className="px-4 py-3 font-mono text-xs text-navy-900">{rule.pattern}</td>
                  <td className="px-4 py-3 font-mono text-xs">{rule.accountCode}</td>
                  <td className="px-4 py-3 text-right">{rule.priority}</td>
                  <td className="px-4 py-3">
                    {rule.clientTitle ?? (
                      <span className="text-navy-500">Tüm mükellefler</span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    <span
                      className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                        rule.isActive
                          ? "bg-green-100 text-green-700"
                          : "bg-navy-100 text-navy-500"
                      }`}
                    >
                      {rule.isActive ? "Aktif" : "Pasif"}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-right">
                    <button
                      className="btn-secondary !px-3 !py-1.5 text-xs"
                      onClick={() => handleDelete(rule)}
                      disabled={deletingId === rule.id}
                    >
                      {deletingId === rule.id ? "Siliniyor..." : "Sil"}
                    </button>
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
