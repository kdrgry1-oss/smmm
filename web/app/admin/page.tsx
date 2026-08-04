"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import {
  AdminBillingOverview,
  AdminStatsResponse,
  api,
  clearToken,
  getAdminBillingOverview,
  getToken,
  listTaxParameters,
  MeResponse,
  TaxParameterItem,
  TenantAdminResponse,
  updateTaxParameter,
} from "@/lib/api";

function formatTL(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return (
    new Intl.NumberFormat("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value) + " ₺"
  );
}

const statusLabels: Record<string, string> = {
  Trial: "Deneme",
  Active: "Aktif",
  PastDue: "Ödeme Gecikmiş",
  Canceled: "İptal",
};

const statusOptions = ["Trial", "Active", "PastDue", "Canceled"];

export default function AdminPage() {
  const router = useRouter();
  const [authorized, setAuthorized] = useState(false);
  const [checking, setChecking] = useState(true);
  const [stats, setStats] = useState<AdminStatsResponse | null>(null);
  const [billing, setBilling] = useState<AdminBillingOverview | null>(null);
  const [tenants, setTenants] = useState<TenantAdminResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyTenant, setBusyTenant] = useState<string | null>(null);

  // Faz 7 (E4): parametre merkezi
  const [parameters, setParameters] = useState<TaxParameterItem[]>([]);
  const [paramError, setParamError] = useState<string | null>(null);
  const [editingParamId, setEditingParamId] = useState<string | null>(null);
  const [editValue, setEditValue] = useState("");
  const [savingParam, setSavingParam] = useState(false);

  const loadData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [statsData, billingData, tenantsData, paramsData] = await Promise.all([
        api<AdminStatsResponse>("/api/admin/stats"),
        getAdminBillingOverview(),
        api<TenantAdminResponse[]>("/api/admin/tenants"),
        listTaxParameters(),
      ]);
      setStats(statsData);
      setBilling(billingData);
      setTenants(tenantsData);
      setParameters(paramsData);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Veriler yüklenirken bir hata oluştu.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!getToken()) {
      router.replace("/giris");
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const me = await api<MeResponse>("/api/auth/me");
        if (cancelled) return;
        if (me.role !== "SuperAdmin") {
          router.replace("/panel");
          return;
        }
        setAuthorized(true);
        setChecking(false);
        await loadData();
      } catch {
        if (!cancelled) setChecking(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [router, loadData]);

  async function changeStatus(tenantId: string, status: string) {
    setBusyTenant(tenantId);
    setError(null);
    try {
      await api(`/api/admin/tenants/${tenantId}/status`, {
        method: "POST",
        body: { status },
      });
      await loadData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Durum güncellenirken bir hata oluştu.");
    } finally {
      setBusyTenant(null);
    }
  }

  function startEditParam(p: TaxParameterItem) {
    setEditingParamId(p.id);
    setEditValue(p.value);
    setParamError(null);
  }

  async function saveParam(p: TaxParameterItem) {
    if (!editValue.trim()) {
      setParamError("Değer boş olamaz.");
      return;
    }
    setSavingParam(true);
    setParamError(null);
    try {
      await updateTaxParameter(p.id, { value: editValue.trim() });
      setEditingParamId(null);
      setParameters(await listTaxParameters());
    } catch (err) {
      setParamError(err instanceof Error ? err.message : "Parametre güncellenirken bir hata oluştu.");
    } finally {
      setSavingParam(false);
    }
  }

  function handleLogout() {
    clearToken();
    router.push("/giris");
  }

  if (checking || !authorized) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-navy-50">
        <p className="text-sm text-navy-500">Yetki kontrol ediliyor...</p>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-navy-50">
      <header className="flex items-center justify-between border-b border-navy-100 bg-navy-950 px-6 py-4 text-white">
        <Link href="/" className="text-lg font-bold">
          MaliMüşavir<span className="text-navy-400">SaaS</span>
          <span className="ml-3 rounded-full bg-navy-800 px-2.5 py-0.5 text-xs font-semibold text-navy-200">
            Süper Admin
          </span>
        </Link>
        <button
          onClick={handleLogout}
          className="rounded-lg border border-navy-700 px-4 py-1.5 text-sm font-semibold hover:bg-navy-900"
        >
          Çıkış
        </button>
      </header>

      <main className="mx-auto max-w-6xl px-6 py-8">
        <h1 className="text-2xl font-bold text-navy-900">Yönetim Paneli</h1>
        <p className="mt-1 text-sm text-navy-500">Platformdaki tüm firmalar ve genel istatistikler.</p>

        {error && <div className="alert-error mt-4">{error}</div>}

        {/* İstatistik kartları */}
        <div className="mt-6 grid gap-6 sm:grid-cols-3">
          {loading && !stats ? (
            [1, 2, 3].map((i) => (
              <div key={i} className="card animate-pulse">
                <div className="h-4 w-1/2 rounded bg-navy-100" />
                <div className="mt-3 h-9 w-16 rounded bg-navy-100" />
              </div>
            ))
          ) : (
            <>
              <div className="card">
                <p className="text-sm font-medium text-navy-500">Toplam Firma</p>
                <p className="mt-1 text-3xl font-extrabold text-navy-950">
                  {stats?.totalTenants ?? "—"}
                </p>
              </div>
              <div className="card">
                <p className="text-sm font-medium text-navy-500">Toplam Kullanıcı</p>
                <p className="mt-1 text-3xl font-extrabold text-navy-950">
                  {stats?.totalUsers ?? "—"}
                </p>
              </div>
              <div className="card">
                <p className="text-sm font-medium text-navy-500">Toplam Mükellef</p>
                <p className="mt-1 text-3xl font-extrabold text-navy-950">
                  {stats?.totalClients ?? "—"}
                </p>
              </div>
            </>
          )}
        </div>

        {/* Gelir özeti (Faz 5) */}
        <h2 className="mt-10 text-lg font-bold text-navy-900">Gelir Özeti</h2>
        <div className="mt-4 grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
          {loading && !billing ? (
            [1, 2, 3].map((i) => (
              <div key={i} className="card animate-pulse">
                <div className="h-4 w-1/2 rounded bg-navy-100" />
                <div className="mt-3 h-9 w-24 rounded bg-navy-100" />
              </div>
            ))
          ) : billing ? (
            <>
              <div className="card">
                <p className="text-sm font-medium text-navy-500">Bu Ay Tahsilat</p>
                <p className="mt-1 text-3xl font-extrabold text-navy-950">
                  {formatTL(billing.monthlyRevenue)}
                </p>
                <p className="mt-2 text-xs text-navy-400">
                  {billing.monthlyInvoiceCount} fatura kesildi (KDV dahil tutarlar)
                </p>
              </div>
              <div className="card">
                <p className="text-sm font-medium text-navy-500">Toplam Tahsilat</p>
                <p className="mt-1 text-3xl font-extrabold text-navy-950">
                  {formatTL(billing.totalRevenue)}
                </p>
                <p className="mt-2 text-xs text-navy-400">Tüm zamanlar, başarılı ödemeler</p>
              </div>
              <div className="card">
                <p className="text-sm font-medium text-navy-500">Abonelik Dağılımı</p>
                <div className="mt-2 flex flex-wrap gap-2 text-sm">
                  <span className="rounded-full bg-green-100 px-3 py-1 font-medium text-green-700">
                    Aktif: {billing.activeCount}
                  </span>
                  <span className="rounded-full bg-blue-100 px-3 py-1 font-medium text-blue-700">
                    Deneme: {billing.trialCount}
                  </span>
                  <span className="rounded-full bg-red-100 px-3 py-1 font-medium text-red-700">
                    Geciken: {billing.pastDueCount}
                  </span>
                  <span className="rounded-full bg-navy-100 px-3 py-1 font-medium text-navy-600">
                    İptal: {billing.canceledCount}
                  </span>
                </div>
              </div>
            </>
          ) : (
            <div className="card sm:col-span-2 lg:col-span-3">
              <p className="text-sm text-navy-500">Gelir verileri şu anda yüklenemedi.</p>
            </div>
          )}
        </div>

        {/* Plan dağılımı */}
        {stats && stats.planDistribution.length > 0 && (
          <div className="card mt-6">
            <h2 className="text-sm font-semibold text-navy-900">Plana Göre Dağılım</h2>
            <div className="mt-3 flex flex-wrap gap-3">
              {stats.planDistribution.map((p) => (
                <span
                  key={p.planCode}
                  className="rounded-full bg-navy-100 px-3 py-1 text-sm font-medium text-navy-700"
                >
                  {p.planName}: {p.tenantCount}
                </span>
              ))}
            </div>
          </div>
        )}

        {/* Tenant listesi */}
        <h2 className="mt-10 text-lg font-bold text-navy-900">Firmalar</h2>
        {loading ? (
          <div className="mt-4 space-y-3">
            {[1, 2, 3].map((i) => (
              <div key={i} className="card animate-pulse">
                <div className="h-5 w-1/3 rounded bg-navy-100" />
              </div>
            ))}
          </div>
        ) : tenants.length === 0 ? (
          <div className="card mt-4 py-12 text-center">
            <p className="text-sm text-navy-500">Henüz kayıtlı firma yok.</p>
          </div>
        ) : (
          <div className="mt-4 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
            <table className="w-full min-w-[760px] text-left text-sm">
              <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
                <tr>
                  <th className="px-4 py-3">Firma</th>
                  <th className="px-4 py-3">E-posta</th>
                  <th className="px-4 py-3">Plan</th>
                  <th className="px-4 py-3">Kullanıcı</th>
                  <th className="px-4 py-3">Mükellef</th>
                  <th className="px-4 py-3">Abonelik</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-navy-50">
                {tenants.map((t) => (
                  <tr key={t.id}>
                    <td className="px-4 py-3 font-medium text-navy-900">{t.name}</td>
                    <td className="px-4 py-3 text-navy-600">{t.email}</td>
                    <td className="px-4 py-3">{t.planName ?? "—"}</td>
                    <td className="px-4 py-3">{t.userCount}</td>
                    <td className="px-4 py-3">{t.clientCount}</td>
                    <td className="px-4 py-3">
                      <select
                        className="input !w-auto !px-2 !py-1 text-xs"
                        value={t.subscriptionStatus}
                        disabled={busyTenant === t.id}
                        onChange={(e) => changeStatus(t.id, e.target.value)}
                      >
                        {statusOptions.map((s) => (
                          <option key={s} value={s}>
                            {statusLabels[s] ?? s}
                          </option>
                        ))}
                      </select>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {/* Faz 7 (E4): Parametre merkezi */}
        <h2 className="mt-10 text-lg font-bold text-navy-900">Parametreler</h2>
        <div className="alert-info mt-3">
          ⚠ Dikkat: Bu değerler <span className="font-semibold">tüm hesaplamaları etkiler</span>{" "}
          (Luca KDV hesap eşlemesi, kod motoru güvenleri, binek gider kısıtları). Değişiklik anında
          tüm firmalar için geçerli olur.
        </div>

        {paramError && <div className="alert-error mt-3">{paramError}</div>}

        {loading ? (
          <div className="mt-4 space-y-3">
            {[1, 2, 3].map((i) => (
              <div key={i} className="card animate-pulse">
                <div className="h-5 w-1/2 rounded bg-navy-100" />
              </div>
            ))}
          </div>
        ) : parameters.length === 0 ? (
          <div className="card mt-4 py-10 text-center">
            <p className="text-sm text-navy-500">
              Parametre bulunamadı. API açılışında 2026 değerleri otomatik seed edilir.
            </p>
          </div>
        ) : (
          <div className="mt-4 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
            <table className="w-full min-w-[820px] text-left text-sm">
              <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
                <tr>
                  <th className="px-4 py-3">Anahtar</th>
                  <th className="px-4 py-3">Değer</th>
                  <th className="px-4 py-3">Açıklama</th>
                  <th className="px-4 py-3">Yürürlük</th>
                  <th className="px-4 py-3">İşlem</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-navy-50">
                {parameters.map((p) => (
                  <tr key={p.id} className="transition hover:bg-navy-50/60">
                    <td className="px-4 py-3">
                      <code className="rounded bg-navy-50 px-1.5 py-0.5 text-xs text-navy-700">
                        {p.key}
                      </code>
                    </td>
                    <td className="px-4 py-3">
                      {editingParamId === p.id ? (
                        <input
                          className="input !w-36 !px-2 !py-1 text-sm"
                          value={editValue}
                          onChange={(e) => setEditValue(e.target.value)}
                          maxLength={500}
                          autoFocus
                        />
                      ) : (
                        <span className="font-semibold text-navy-900">{p.value}</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-navy-600">{p.description || "—"}</td>
                    <td className="px-4 py-3 text-xs text-navy-400">
                      {new Date(p.effectiveFrom).toLocaleDateString("tr-TR")}
                    </td>
                    <td className="px-4 py-3">
                      {editingParamId === p.id ? (
                        <div className="flex items-center gap-2">
                          <button
                            className="text-xs font-semibold text-navy-900 hover:underline"
                            onClick={() => saveParam(p)}
                            disabled={savingParam}
                          >
                            {savingParam ? "Kaydediliyor..." : "Kaydet"}
                          </button>
                          <button
                            className="text-xs font-semibold text-navy-400 hover:underline"
                            onClick={() => setEditingParamId(null)}
                            disabled={savingParam}
                          >
                            Vazgeç
                          </button>
                        </div>
                      ) : (
                        <button
                          className="text-xs font-semibold text-navy-600 hover:underline"
                          onClick={() => startEditParam(p)}
                        >
                          Düzenle
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </main>
    </div>
  );
}
