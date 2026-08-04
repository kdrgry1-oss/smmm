"use client";

import { useCallback, useEffect, useState } from "react";
import {
  api,
  BillingInvoiceItem,
  BillingSummary,
  cancelSubscription,
  getBillingSummary,
  listBillingInvoices,
  MeResponse,
  PlanResponse,
  SubscriptionStatusCode,
  subscribePlan,
} from "@/lib/api";

function formatTL(value: number | null | undefined): string {
  if (value === null || value === undefined) return "—";
  return new Intl.NumberFormat("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value) + " ₺";
}

function formatPrice(price: number) {
  return new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 0 }).format(price);
}

function formatDate(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

const statusLabels: Record<SubscriptionStatusCode, string> = {
  Trial: "Deneme",
  Active: "Aktif",
  PastDue: "Ödeme Gecikmiş",
  Canceled: "İptal Edildi",
};

function SubscriptionStatusBadge({ status }: { status: SubscriptionStatusCode }) {
  const styles: Record<SubscriptionStatusCode, string> = {
    Trial: "bg-blue-100 text-blue-700",
    Active: "bg-green-100 text-green-700",
    PastDue: "bg-red-100 text-red-700",
    Canceled: "bg-navy-100 text-navy-600",
  };
  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${styles[status]}`}>
      {statusLabels[status]}
    </span>
  );
}

export default function SettingsPage() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Abonelik
  const [summary, setSummary] = useState<BillingSummary | null>(null);
  const [plans, setPlans] = useState<PlanResponse[]>([]);
  const [invoices, setInvoices] = useState<BillingInvoiceItem[]>([]);
  const [billingLoading, setBillingLoading] = useState(true);
  const [billingError, setBillingError] = useState<string | null>(null);

  // Ödeme formu
  const [selectedPlanId, setSelectedPlanId] = useState<string>("");
  const [mockCard, setMockCard] = useState<string>("");
  const [paying, setPaying] = useState(false);
  const [canceling, setCanceling] = useState(false);
  const [billingMessage, setBillingMessage] = useState<string | null>(null);
  const [payError, setPayError] = useState<string | null>(null);

  const isOwner = me?.role === "TenantOwner";

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const data = await api<MeResponse>("/api/auth/me");
        if (!cancelled) setMe(data);
      } catch (err) {
        if (!cancelled)
          setError(err instanceof Error ? err.message : "Bilgiler yüklenirken bir hata oluştu.");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const loadBilling = useCallback(async () => {
    setBillingLoading(true);
    setBillingError(null);
    try {
      const [summaryData, plansData, invoicesData] = await Promise.all([
        getBillingSummary(),
        api<PlanResponse[]>("/api/plans", { auth: false }),
        listBillingInvoices(),
      ]);
      setSummary(summaryData);
      setPlans(plansData);
      setInvoices(invoicesData);
    } catch (err) {
      setBillingError(
        err instanceof Error ? err.message : "Abonelik bilgileri yüklenirken bir hata oluştu."
      );
    } finally {
      setBillingLoading(false);
    }
  }, []);

  useEffect(() => {
    loadBilling();
  }, [loadBilling]);

  async function handleSubscribe() {
    setBillingMessage(null);
    setPayError(null);

    // Çift validasyon (backend de aynı kuralları doğrular).
    if (!selectedPlanId) {
      setPayError("Lütfen bir paket seçin.");
      return;
    }
    const digits = mockCard.replace(/\s+/g, "");
    if (digits && !/^\d{12,19}$/.test(digits)) {
      setPayError("Kart numarası 12-19 haneli rakamlardan oluşmalıdır.");
      return;
    }

    setPaying(true);
    try {
      const result = await subscribePlan(selectedPlanId, digits || undefined);
      setBillingMessage(result.message);
      setSelectedPlanId("");
      setMockCard("");
      await loadBilling();
    } catch (err) {
      setPayError(err instanceof Error ? err.message : "Ödeme sırasında bir hata oluştu.");
    } finally {
      setPaying(false);
    }
  }

  async function handleCancel() {
    if (
      typeof window !== "undefined" &&
      !window.confirm("Aboneliğinizi iptal etmek istediğinize emin misiniz? Dönem sonuna kadar kullanmaya devam edebilirsiniz.")
    ) {
      return;
    }
    setBillingMessage(null);
    setPayError(null);
    setCanceling(true);
    try {
      const result = await cancelSubscription();
      setBillingMessage(result.message);
      await loadBilling();
    } catch (err) {
      setPayError(err instanceof Error ? err.message : "İptal sırasında bir hata oluştu.");
    } finally {
      setCanceling(false);
    }
  }

  return (
    <div>
      <h1 className="text-2xl font-bold text-navy-900">Ayarlar</h1>
      <p className="mt-1 text-sm text-navy-500">Hesap ve abonelik bilgileriniz.</p>

      {error && <div className="alert-error mt-4">{error}</div>}

      {/* Hesap bilgileri */}
      {loading ? (
        <div className="card mt-6 max-w-xl animate-pulse space-y-4">
          <div className="h-4 w-1/3 rounded bg-navy-100" />
          <div className="h-4 w-1/2 rounded bg-navy-100" />
          <div className="h-4 w-1/4 rounded bg-navy-100" />
        </div>
      ) : me ? (
        <div className="card mt-6 max-w-xl">
          <h2 className="text-sm font-semibold text-navy-900">Hesap</h2>
          <dl className="mt-2 divide-y divide-navy-50">
            <div className="flex justify-between py-3">
              <dt className="text-sm font-medium text-navy-500">Ad Soyad</dt>
              <dd className="text-sm font-semibold text-navy-900">{me.fullName}</dd>
            </div>
            <div className="flex justify-between py-3">
              <dt className="text-sm font-medium text-navy-500">E-posta</dt>
              <dd className="text-sm font-semibold text-navy-900">{me.email}</dd>
            </div>
            <div className="flex justify-between py-3">
              <dt className="text-sm font-medium text-navy-500">Firma</dt>
              <dd className="text-sm font-semibold text-navy-900">{me.tenantName ?? "—"}</dd>
            </div>
            <div className="flex justify-between py-3">
              <dt className="text-sm font-medium text-navy-500">Rol</dt>
              <dd className="text-sm font-semibold text-navy-900">
                {me.role === "TenantOwner" ? "Ofis Sahibi" : me.role === "TenantStaff" ? "Personel" : me.role}
              </dd>
            </div>
          </dl>
        </div>
      ) : null}

      {/* Abonelik */}
      <h2 className="mt-10 text-lg font-bold text-navy-900">Abonelik</h2>

      {billingMessage && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {billingMessage}
        </div>
      )}
      {payError && <div className="alert-error mt-4">{payError}</div>}
      {billingError && (
        <div className="alert-error mt-4 flex items-center justify-between gap-4">
          <span>{billingError}</span>
          <button className="btn-secondary !px-3 !py-1.5 text-xs" onClick={loadBilling}>
            Tekrar dene
          </button>
        </div>
      )}

      {billingLoading ? (
        <div className="card mt-4 max-w-xl animate-pulse space-y-4">
          <div className="h-4 w-1/3 rounded bg-navy-100" />
          <div className="h-4 w-1/2 rounded bg-navy-100" />
          <div className="h-4 w-2/3 rounded bg-navy-100" />
        </div>
      ) : summary ? (
        <>
          {/* Mevcut plan kartı */}
          <div className="card mt-4 max-w-xl">
            <div className="flex items-start justify-between gap-4">
              <div>
                <p className="text-sm font-medium text-navy-500">Mevcut Paket</p>
                <p className="mt-1 text-xl font-bold text-navy-950">
                  {summary.planName ?? "Paket seçilmedi"}
                  {summary.monthlyPrice != null && (
                    <span className="ml-2 text-sm font-medium text-navy-400">
                      ₺{formatPrice(summary.monthlyPrice)} / ay + KDV
                    </span>
                  )}
                </p>
              </div>
              <SubscriptionStatusBadge status={summary.status} />
            </div>

            <dl className="mt-4 divide-y divide-navy-50 text-sm">
              {summary.periodStart && (
                <div className="flex justify-between py-2">
                  <dt className="text-navy-500">Dönem</dt>
                  <dd className="font-medium text-navy-900">
                    {formatDate(summary.periodStart)} — {formatDate(summary.periodEnd)}
                  </dd>
                </div>
              )}
              {summary.status === "Trial" && summary.trialDaysRemaining != null && (
                <div className="flex justify-between py-2">
                  <dt className="text-navy-500">Deneme süresi</dt>
                  <dd
                    className={`font-semibold ${
                      summary.trialDaysRemaining <= 3 ? "text-red-700" : "text-navy-900"
                    }`}
                  >
                    {summary.trialDaysRemaining} gün kaldı
                  </dd>
                </div>
              )}
            </dl>

            {summary.status === "Trial" &&
              summary.trialDaysRemaining != null &&
              summary.trialDaysRemaining <= 3 && (
                <div className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                  ⚠ Deneme süreniz {summary.trialDaysRemaining === 0 ? "bugün doluyor" : `${summary.trialDaysRemaining} gün içinde doluyor`}.
                  Kesintisiz kullanım için aşağıdan bir paket satın alın.
                </div>
              )}

            {summary.status === "PastDue" && (
              <div className="alert-error mt-3">
                Aboneliğinizin süresi doldu. Kesintisiz kullanım için aşağıdan paketinizi yenileyin.
              </div>
            )}

            {summary.cancelsAtPeriodEnd && (
              <div className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                İptal talebiniz alındı; aboneliğiniz {formatDate(summary.periodEnd)} tarihinde kapanacak.
                Fikrinizi değiştirirseniz yeniden paket satın alabilirsiniz.
              </div>
            )}

            {isOwner &&
              (summary.status === "Active" || summary.status === "Trial") &&
              !summary.cancelsAtPeriodEnd &&
              summary.hasSubscription && (
                <button
                  onClick={handleCancel}
                  className="btn-secondary mt-4 flex items-center gap-2 !px-3 !py-1.5 text-xs"
                  disabled={canceling || paying}
                >
                  {canceling && (
                    <span className="h-3 w-3 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                  )}
                  Aboneliği İptal Et
                </button>
              )}
          </div>

          {/* Plan değiştirme */}
          <div className="card mt-6">
            <h3 className="text-sm font-semibold text-navy-900">Paket Satın Al / Değiştir</h3>
            <p className="mt-1 text-xs text-navy-500">
              Plan değişikliği anında geçerli olur; yeni dönem bugünden başlar (kalan gün oranlaması yapılmaz).
            </p>
            {me && !isOwner && (
              <div className="alert-error mt-3">
                Paket satın alma ve iptal işlemlerini yalnızca ofis sahibi yapabilir.
              </div>
            )}
            <div className="mt-4 grid gap-4 md:grid-cols-3">
              {plans.map((plan) => {
                const isCurrent = summary.planCode === plan.code && summary.status !== "PastDue" && summary.status !== "Canceled";
                const isSelected = selectedPlanId === plan.id;
                return (
                  <button
                    key={plan.id}
                    type="button"
                    onClick={() => isOwner && setSelectedPlanId(isSelected ? "" : plan.id)}
                    disabled={!isOwner}
                    className={`rounded-xl border p-4 text-left transition ${
                      isSelected
                        ? "border-2 border-navy-900 bg-navy-50/50 shadow-md"
                        : "border-navy-100 bg-white hover:border-navy-300"
                    } ${!isOwner ? "cursor-not-allowed opacity-60" : ""}`}
                  >
                    <div className="flex items-center justify-between">
                      <span className="font-bold text-navy-900">{plan.name}</span>
                      {isCurrent && (
                        <span className="rounded-full bg-navy-100 px-2 py-0.5 text-[10px] font-semibold text-navy-600">
                          Mevcut
                        </span>
                      )}
                    </div>
                    <p className="mt-2 text-2xl font-extrabold text-navy-950">
                      ₺{formatPrice(plan.monthlyPrice)}
                      <span className="text-xs font-medium text-navy-400"> / ay + KDV</span>
                    </p>
                    <ul className="mt-3 space-y-1 text-xs text-navy-600">
                      <li>{plan.unlimitedClients ? "Sınırsız mükellef" : `${plan.maxClients} mükellef`}</li>
                      <li>{plan.maxUsers} kullanıcı</li>
                      <li>Aylık {formatPrice(plan.monthlyOcrQuota)} fiş OCR</li>
                    </ul>
                  </button>
                );
              })}
            </div>

            {isOwner && (
              <div className="mt-5 flex flex-wrap items-end gap-4">
                <div>
                  <label className="mb-1 block text-xs font-medium text-navy-600">
                    Kart Numarası (test)
                  </label>
                  <input
                    className="input max-w-[260px]"
                    placeholder="4111 1111 1111 1111"
                    value={mockCard}
                    onChange={(e) => setMockCard(e.target.value)}
                    inputMode="numeric"
                  />
                </div>
                <button
                  onClick={handleSubscribe}
                  className="btn-primary flex items-center gap-2"
                  disabled={paying || canceling || !selectedPlanId}
                >
                  {paying && (
                    <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                  )}
                  {paying ? "Ödeme alınıyor..." : "Öde ve Paketi Etkinleştir"}
                </button>
              </div>
            )}
            <p className="mt-3 text-xs text-navy-400">
              Demo ödeme sağlayıcısı: <strong>4111</strong> ile başlayan kart başarılı,{" "}
              <strong>4000</strong> ile başlayan kart başarısız ödeme senaryosunu test eder
              (boş bırakılırsa başarılı sayılır). Kart bilgisi saklanmaz. Gerçek iyzico entegrasyonu
              API anahtarları iletilince etkinleştirilecektir.
            </p>
          </div>

          {/* Faturalar */}
          <div className="card mt-6">
            <h3 className="text-sm font-semibold text-navy-900">Abonelik Faturaları</h3>
            {invoices.length === 0 ? (
              <p className="mt-3 text-sm text-navy-500">
                Henüz fatura yok. İlk ödemenizle birlikte faturanız burada listelenecek.
              </p>
            ) : (
              <div className="mt-3 overflow-x-auto">
                <table className="w-full min-w-[560px] text-left text-sm">
                  <thead className="border-b border-navy-100 text-xs uppercase tracking-wide text-navy-500">
                    <tr>
                      <th className="py-2 pr-4">Fatura No</th>
                      <th className="py-2 pr-4">Tarih</th>
                      <th className="py-2 pr-4 text-right">Tutar</th>
                      <th className="py-2 pr-4 text-right">KDV (%20)</th>
                      <th className="py-2 text-right">Toplam</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-navy-50">
                    {invoices.map((inv) => (
                      <tr key={inv.id}>
                        <td className="py-2 pr-4 font-mono text-navy-800">{inv.number}</td>
                        <td className="py-2 pr-4">{formatDate(inv.issuedAt)}</td>
                        <td className="py-2 pr-4 text-right">{formatTL(inv.amount)}</td>
                        <td className="py-2 pr-4 text-right">{formatTL(inv.vatAmount)}</td>
                        <td className="py-2 text-right font-semibold">{formatTL(inv.totalAmount)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <p className="mt-3 text-xs text-navy-400">
                  Fatura PDF&apos;i (e-Arşiv) sonraki fazda eklenecektir.
                </p>
              </div>
            )}
          </div>
        </>
      ) : null}
    </div>
  );
}
