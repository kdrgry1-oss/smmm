"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import {
  api,
  CertificateItem,
  ClientResponse,
  ETebligatList,
  FeeList,
  listCertificates,
  listEInvoices,
  listETebligat,
  listFees,
  listReceipts,
  listTaxCalendar,
  TaxCalendarList,
} from "@/lib/api";

export default function DashboardPage() {
  const [clientCount, setClientCount] = useState<number | null>(null);
  const [invoiceCount, setInvoiceCount] = useState<number | null>(null);
  const [receiptCount, setReceiptCount] = useState<number | null>(null);
  const [receiptPendingCount, setReceiptPendingCount] = useState<number | null>(null);
  const [tebligat, setTebligat] = useState<ETebligatList | null>(null);
  const [calendar, setCalendar] = useState<TaxCalendarList | null>(null);
  const [expiringCerts, setExpiringCerts] = useState<CertificateItem[] | null>(null);
  const [fees, setFees] = useState<FeeList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        // Sayaçlar birlikte çekilir: mükellef, fatura, fiş (toplam + onay bekleyen),
        // tebligat özeti, vergi takvimi (geciken) ve 90 gün içinde biten sertifikalar (Faz 7).
        const [clients, invoicePage, receiptPage, pendingReceiptPage, tebligatList, calendarList, certs, feeList] =
          await Promise.all([
            api<ClientResponse[]>("/api/clients"),
            listEInvoices({ page: 1, pageSize: 1 }),
            listReceipts({ page: 1, pageSize: 1 }),
            listReceipts({ status: "NeedsReview", page: 1, pageSize: 1 }),
            listETebligat(),
            listTaxCalendar(),
            listCertificates(90),
            listFees(), // Faz 11 (E7): tüm dönemler — geciken tahsilat uyarısı
          ]);
        if (!cancelled) {
          setClientCount(clients.length);
          setInvoiceCount(invoicePage.totalCount);
          setReceiptCount(receiptPage.totalCount);
          setReceiptPendingCount(pendingReceiptPage.totalCount);
          setTebligat(tebligatList);
          setCalendar(calendarList);
          setExpiringCerts(certs);
          setFees(feeList);
        }
      } catch (err) {
        if (!cancelled)
          setError(err instanceof Error ? err.message : "Veriler yüklenirken bir hata oluştu.");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const urgentTebligat = tebligat?.urgentCount ?? 0;
  const overdueObligations = calendar?.overdueCount ?? 0;
  const expiringCertCount = expiringCerts?.length ?? 0;

  return (
    <div>
      <h1 className="text-2xl font-bold text-navy-900">Panel</h1>
      <p className="mt-1 text-sm text-navy-500">Ofisinizin genel görünümü.</p>

      {error && <div className="alert-error mt-4">{error}</div>}

      <div className="mt-6 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
        <Link href="/panel/mukellefler" className="card block transition hover:shadow-md">
          <p className="text-sm font-medium text-navy-500">Mükellef Sayısı</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-3xl font-extrabold text-navy-950">
              {clientCount ?? "—"}
            </p>
          )}
          <p className="mt-2 text-xs text-navy-400">Aktif (arşivlenmemiş) mükellefler</p>
        </Link>

        <Link href="/panel/faturalar" className="card block transition hover:shadow-md">
          <p className="text-sm font-medium text-navy-500">Gelen e-Fatura</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-3xl font-extrabold text-navy-950">
              {invoiceCount ?? "—"}
            </p>
          )}
          <p className="mt-2 text-xs text-navy-400">
            {!loading && invoiceCount === 0
              ? "Henüz fatura yok — senkronize edin"
              : "Senkronize edilen toplam fatura"}
          </p>
        </Link>

        <Link href="/panel/fisler" className="card block transition hover:shadow-md">
          <p className="text-sm font-medium text-navy-500">Fişler</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p className="mt-1 text-3xl font-extrabold text-navy-950">
              {receiptCount ?? "—"}
            </p>
          )}
          <p className="mt-2 text-xs text-navy-400">
            {!loading && receiptCount === 0
              ? "Fiş yükleyin — sistem gideri otomatik algılar"
              : `${receiptPendingCount ?? 0} fiş onay bekliyor`}
          </p>
        </Link>

        <Link
          href="/panel/tebligatlar"
          className={`card block transition hover:shadow-md ${
            urgentTebligat > 0 ? "!border-red-300 !bg-red-50" : ""
          }`}
        >
          <p className="text-sm font-medium text-navy-500">Süresi Yaklaşan Tebligat</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p
              className={`mt-1 text-3xl font-extrabold ${
                urgentTebligat > 0 ? "text-red-700" : "text-navy-950"
              }`}
            >
              {tebligat ? urgentTebligat : "—"}
            </p>
          )}
          <p className={`mt-2 text-xs ${urgentTebligat > 0 ? "font-semibold text-red-700" : "text-navy-400"}`}>
            {!loading && urgentTebligat > 0
              ? `⚠ ${urgentTebligat} tebligatta kalan süre ≤ 3 gün — hemen bakın`
              : !loading && (tebligat?.unreadCount ?? 0) > 0
                ? `${tebligat?.unreadCount} okunmamış tebligat var`
                : "Tebligat yok — hepsi kontrol altında ✓"}
          </p>
        </Link>

        {/* Faz 7 (E2): geciken yükümlülük kırmızı kartı */}
        <Link
          href="/panel/takvim"
          className={`card block transition hover:shadow-md ${
            overdueObligations > 0 ? "!border-red-300 !bg-red-50" : ""
          }`}
        >
          <p className="text-sm font-medium text-navy-500">Geciken Yükümlülük</p>
          {loading ? (
            <div className="mt-2 h-9 w-16 animate-pulse rounded bg-navy-100" />
          ) : (
            <p
              className={`mt-1 text-3xl font-extrabold ${
                overdueObligations > 0 ? "text-red-700" : "text-navy-950"
              }`}
            >
              {calendar ? overdueObligations : "—"}
            </p>
          )}
          <p
            className={`mt-2 text-xs ${
              overdueObligations > 0 ? "font-semibold text-red-700" : "text-navy-400"
            }`}
          >
            {!loading && overdueObligations > 0
              ? `⚠ ${overdueObligations} yükümlülüğün son günü geçti — takvime bakın`
              : !loading && (calendar?.pendingCount ?? 0) > 0
                ? `${calendar?.pendingCount} yükümlülük bekliyor`
                : "Geciken yükümlülük yok ✓"}
          </p>
        </Link>
      </div>

      {/* Faz 8 (B10): dönem kapanışı kısayol kartı */}
      <Link
        href="/panel/kapanis"
        className="card mt-6 flex flex-wrap items-center justify-between gap-3 transition hover:shadow-md"
      >
        <div>
          <p className="text-sm font-semibold text-navy-900">Dönem Kapanışı — &quot;Beyana hazır mıyım?&quot;</p>
          <p className="mt-1 text-xs text-navy-500">
            Kodlanmamış fatura, onay kuyruğu, mutabakat, tevkifat ve takvim kontrollerini tek
            listede görün; hazırsanız KDV taslağını tek tıkla hazırlayın.
          </p>
        </div>
        <span className="btn-secondary !px-4 !py-2 text-sm">Kontrol Listesini Aç</span>
      </Link>

      {/* Faz 11 (E7): geciken tahsilat uyarı kartı (varsa) */}
      {!loading && (fees?.overdueCount ?? 0) > 0 && (
        <Link
          href="/panel/tahsilat"
          className="mt-6 block rounded-xl border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800 transition hover:shadow-sm"
        >
          ⚠ {fees?.overdueCount} mükellefin ücret tahsilatı gecikti — toplam{" "}
          <strong>
            {(fees?.overdueTotal ?? 0).toLocaleString("tr-TR", {
              minimumFractionDigits: 2,
              maximumFractionDigits: 2,
            })}{" "}
            ₺
          </strong>
          . <span className="font-semibold underline">Tahsilat</span> sayfasından takip edin.
        </Link>
      )}

      {/* Faz 7 (E3): süresi yaklaşan sertifika uyarısı (varsa) */}
      {!loading && expiringCertCount > 0 && (
        <Link
          href="/panel/sertifikalar"
          className="alert-info mt-6 block transition hover:shadow-sm"
        >
          ⚠ {expiringCertCount} sertifikanın (e-İmza / mali mühür / KEP) süresi 90 gün içinde doluyor —{" "}
          <span className="font-semibold underline">Sertifikalar</span> sayfasından kontrol edin.
        </Link>
      )}

      {!loading && clientCount === 0 && !error && (
        <div className="card mt-8 flex flex-col items-center py-12 text-center">
          <h2 className="text-lg font-semibold text-navy-900">Henüz mükellef eklemediniz</h2>
          <p className="mt-2 max-w-md text-sm text-navy-500">
            Platformu kullanmaya başlamak için ilk mükellefinizi ekleyin.
          </p>
          <Link href="/panel/mukellefler" className="btn-primary mt-6">
            Mükellef Ekle
          </Link>
        </div>
      )}
    </div>
  );
}
