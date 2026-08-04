"use client";

import { useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  downloadLucaExport,
  getLucaExportPreview,
  LucaExportFormat,
  LucaExportPreview,
  LucaExportSource,
} from "@/lib/api";

function toDateInputValue(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function formatMoney(value: number): string {
  return new Intl.NumberFormat("tr-TR", {
    style: "currency",
    currency: "TRY",
    minimumFractionDigits: 2,
  }).format(value);
}

/**
 * Dışa Aktar: onaylanmış fatura + fişleri Luca "Fiş Aktarım Şablonu"na birebir uygun
 * dosya olarak indirir. Varsayılan biçim .xlsx (sayfa adı "Fiş Aktarım Şablon", 14 kolon A→N);
 * ikinci seçenek aynı kolonlarla ;-ayraçlı UTF-8 BOM'lu CSV. Zorunlu alanı eksik belge
 * varsa sunucu indirmeyi engeller — eksikler önizlemede Türkçe listelenir.
 */
export default function LucaExportPage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);

  const today = new Date();
  const thirtyDaysAgo = new Date(today.getTime() - 30 * 24 * 60 * 60 * 1000);
  const [clientId, setClientId] = useState("");
  const [fromDate, setFromDate] = useState(toDateInputValue(thirtyDaysAgo));
  const [toDate, setToDate] = useState(toDateInputValue(today));
  const [format, setFormat] = useState<LucaExportFormat>("xlsx");
  // Faz 14 (C): kaynak filtresi — varsayılan "Tümü" mevcut davranışı birebir korur.
  const [source, setSource] = useState<LucaExportSource>("all");

  // Önizleme: aralıktaki onaylanmış fatura sayısı + toplam tutar
  const [preview, setPreview] = useState<LucaExportPreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);

  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [downloadSuccess, setDownloadSuccess] = useState<string | null>(null);

  // Mükellefleri yükle
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

  // Seçim değişince önizlemeyi yenile
  useEffect(() => {
    setPreview(null);
    setPreviewError(null);
    setDownloadError(null);
    setDownloadSuccess(null);

    if (!clientId || !fromDate || !toDate) return;
    if (fromDate > toDate) {
      setPreviewError("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
      return;
    }

    let cancelled = false;
    setPreviewLoading(true);
    (async () => {
      try {
        const data = await getLucaExportPreview(clientId, fromDate, toDate, source);
        if (!cancelled) setPreview(data);
      } catch (err) {
        if (!cancelled)
          setPreviewError(
            err instanceof Error ? err.message : "Önizleme yüklenirken bir hata oluştu."
          );
      } finally {
        if (!cancelled) setPreviewLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [clientId, fromDate, toDate, source]);

  async function handleDownload() {
    setDownloadError(null);
    setDownloadSuccess(null);

    if (!clientId) {
      setDownloadError("Dışa aktarma için önce bir mükellef seçin.");
      return;
    }
    if (!fromDate || !toDate) {
      setDownloadError("Başlangıç ve bitiş tarihlerini seçin.");
      return;
    }
    if (fromDate > toDate) {
      setDownloadError("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
      return;
    }

    if (preview && preview.missingFields.length > 0) {
      setDownloadError(
        "Dışa aktarma engellendi: zorunlu alanları eksik belgeler var. Aşağıdaki listeyi giderin ve tekrar deneyin."
      );
      return;
    }

    setDownloading(true);
    try {
      const { blob, fileName } = await downloadLucaExport(clientId, fromDate, toDate, format, source);

      // Blob'u tarayıcı indirmesine dönüştür.
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = fileName ?? `luca-fis-aktarim-${fromDate}-${toDate}.${format}`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);

      setDownloadSuccess(`Dosya indirildi: ${link.download}`);
    } catch (err) {
      setDownloadError(err instanceof Error ? err.message : "İndirme sırasında bir hata oluştu.");
    } finally {
      setDownloading(false);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Dışa Aktar</h1>
          <p className="mt-1 text-sm text-navy-500">
            Onaylanmış fatura ve fişleri Luca Fiş Aktarım Şablonu&apos;na birebir uygun dosya
            olarak indirin (Excel .xlsx önerilir).
          </p>
        </div>
      </div>

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}

      <div className="card mt-6">
        <div className="flex flex-wrap items-end gap-4">
          <div>
            <label htmlFor="exportClient" className="label">
              Mükellef
            </label>
            <select
              id="exportClient"
              className="input min-w-[240px]"
              value={clientId}
              onChange={(e) => setClientId(e.target.value)}
              disabled={clientsLoading}
            >
              <option value="">
                {clientsLoading ? "Yükleniyor..." : "Mükellef seçin"}
              </option>
              {clients.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor="exportFrom" className="label">
              Başlangıç
            </label>
            <input
              id="exportFrom"
              type="date"
              className="input"
              value={fromDate}
              max={toDate}
              onChange={(e) => setFromDate(e.target.value)}
            />
          </div>
          <div>
            <label htmlFor="exportTo" className="label">
              Bitiş
            </label>
            <input
              id="exportTo"
              type="date"
              className="input"
              value={toDate}
              min={fromDate}
              onChange={(e) => setToDate(e.target.value)}
            />
          </div>
          <div>
            <label htmlFor="exportSource" className="label">
              Kaynak
            </label>
            <select
              id="exportSource"
              className="input min-w-[170px]"
              value={source}
              onChange={(e) => setSource(e.target.value as LucaExportSource)}
              title="Dosyaya dahil edilecek belge türü — Tümü mevcut davranıştır (fatura + fiş)."
            >
              <option value="all">Tümü (fatura + fiş)</option>
              <option value="fis">Yalnız fişler</option>
              <option value="fatura">Yalnız faturalar</option>
            </select>
          </div>
          <div>
            <label htmlFor="exportFormat" className="label">
              Dosya biçimi
            </label>
            <select
              id="exportFormat"
              className="input min-w-[220px]"
              value={format}
              onChange={(e) => setFormat(e.target.value as LucaExportFormat)}
            >
              <option value="xlsx">Excel (.xlsx) — önerilen</option>
              <option value="csv">CSV (aynı kolonlar)</option>
            </select>
          </div>
          <button
            onClick={handleDownload}
            className="btn-primary flex items-center gap-2"
            disabled={
              downloading ||
              clientsLoading ||
              !clientId ||
              (preview !== null && preview.missingFields.length > 0)
            }
          >
            {downloading && (
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {downloading
              ? "İndiriliyor..."
              : format === "xlsx"
                ? "Luca Şablonu İndir (.xlsx)"
                : "Luca Şablonu İndir (.csv)"}
          </button>
        </div>

        {!clientsLoading && clients.length === 0 && !clientsError && (
          <p className="mt-3 text-sm text-navy-500">
            Dışa aktarma için önce bir mükellef ekleyin, faturaları kodlayıp onaylayın.
          </p>
        )}

        {/* Önizleme bandı */}
        {clientId && (
          <div className="mt-5 rounded-lg bg-navy-50 px-4 py-3 text-sm">
            {previewLoading ? (
              <span className="text-navy-500">Önizleme yükleniyor...</span>
            ) : previewError ? (
              <span className="text-red-700">{previewError}</span>
            ) : preview ? (
              preview.approvedCount > 0 || preview.approvedReceiptCount > 0 ? (
                <span className="text-navy-800">
                  Seçilen aralıkta{" "}
                  <strong>{preview.approvedCount} onaylanmış fatura</strong> (toplam{" "}
                  {formatMoney(preview.totalAmount)}) ve{" "}
                  <strong>{preview.approvedReceiptCount} onaylanmış fiş</strong> (toplam{" "}
                  {formatMoney(preview.receiptTotalAmount)}) var. Dosya bu belgelerden üretilecek.
                </span>
              ) : (
                <span className="text-navy-600">
                  Seçilen aralıkta onaylanmış fatura veya fiş yok. Önce belgeleri kodlayıp onaylayın.
                </span>
              )
            ) : null}
          </div>
        )}

        {/* Zorunlu alan ön-kontrolü: eksik alanlı belgeler (indirme engellenir) */}
        {preview && preview.missingFields.length > 0 && (
          <div className="mt-4 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            <p className="font-semibold">
              Dışa aktarma engellendi — {preview.missingFields.length} belgede zorunlu alan eksik:
            </p>
            <ul className="mt-2 list-disc space-y-1 pl-5">
              {preview.missingFields.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
            <p className="mt-2 text-xs">
              Eksikleri Faturalar / Fişler sayfasındaki onay ekranından tamamlayıp bu sayfaya dönün.
            </p>
          </div>
        )}

        {downloadError && <div className="alert-error mt-4">{downloadError}</div>}
        {downloadSuccess && (
          <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
            {downloadSuccess}
          </div>
        )}

        <p className="mt-5 text-xs text-navy-400">
          Dosya, Luca Fiş Aktarım Şablonu&apos;na birebir uyar — sayfa adı &quot;Fiş Aktarım
          Şablon&quot;, kolonlar (A→N): Fiş No, Fiş Tarihi, Fiş Açıklama, Hesap Kodu, Evrak No,
          Evrak Tarihi, Detay Açıklama, Borç, Alacak, Miktar, Belge Türü, Para Birimi, Kur, Döviz
          Tutar. Fiş No boş bırakılır (Luca otomatik verir); tarihler GG.AA.YYYY, para birimi TRY,
          kur 1 yazılır. KDV satırları oran eşlemesiyle üretilir (%20→191.01, %10→191.02,
          %1→191.03); cari kodu olmayan fişlerde alacak 100 (Kasa) hesabına yazılır. CSV seçeneği
          aynı kolonlarla noktalı virgül ayraçlı, UTF-8 BOM&apos;lu dosya üretir.
        </p>
      </div>
    </div>
  );
}
