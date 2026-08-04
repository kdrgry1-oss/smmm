"use client";

import { useState } from "react";
import { downloadLucaBulkExport, downloadLucaExport, LucaExportSource } from "@/lib/api";

/** İçinde bulunulan ayın ilk günü (yerel saat) — şeridin varsayılan başlangıcı. */
function monthStartInputValue(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-01`;
}

function todayInputValue(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(
    now.getDate()
  ).padStart(2, "0")}`;
}

/**
 * Faz 14 (C): Sayfa içi kompakt Luca dışa aktarma şeridi (Fişler / Faturalar sayfaları).
 * Tarih aralığı varsayılanı içinde bulunulan aydır. Tek mükellef seçiliyken MEVCUT tekil
 * XLSX indirme davranışı birebir korunur; allowBulk=true ile çoklu/`tümü` seçiminde her
 * mükellef için AYRI XLSX üretilip tek ZIP ("Luca_Fisleri_{tarih}.zip") indirilir.
 * Onaylanmış belge yoksa sunucunun Türkçe hatası hata bandında gösterilir.
 * Tam seçenekli dışa aktarma (CSV, kaynak seçimi, önizleme) Dışa Aktar sayfasındadır.
 */
export default function LucaExportStrip({
  clientIds,
  source,
  allowBulk = false,
}: {
  /** Seçili mükellefler; boş dizi = tüm mükellefler (yalnız allowBulk=true iken geçerli). */
  clientIds: string[];
  source: Exclude<LucaExportSource, "all">;
  /** Çoklu/tümü seçiminde ZIP indirmeye izin ver (Faturalar sayfası). */
  allowBulk?: boolean;
}) {
  const [fromDate, setFromDate] = useState(monthStartInputValue());
  const [toDate, setToDate] = useState(todayInputValue());
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  const label = source === "fis" ? "yalnız fişler" : "yalnız faturalar";
  const bulkMode = allowBulk && clientIds.length !== 1;

  async function handleDownload() {
    setError(null);
    setSuccess(null);

    if (!allowBulk && clientIds.length !== 1) {
      setError("Luca dosyası indirmek için önce bir mükellef seçin.");
      return;
    }
    if (!fromDate || !toDate) {
      setError("Başlangıç ve bitiş tarihlerini seçin.");
      return;
    }
    if (fromDate > toDate) {
      setError("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
      return;
    }

    setDownloading(true);
    try {
      // Tek mükellef → mevcut tekil XLSX ucu (davranış birebir korunur);
      // çoklu/tümü → mükellef başına ayrı XLSX içeren ZIP.
      const { blob, fileName } = bulkMode
        ? await downloadLucaBulkExport(clientIds, fromDate, toDate, source)
        : await downloadLucaExport(clientIds[0], fromDate, toDate, "xlsx", source);

      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download =
        fileName ??
        (bulkMode
          ? `Luca_Fisleri_${fromDate}-${toDate}.zip`
          : `luca-fis-aktarim-${source}-${fromDate}-${toDate}.xlsx`);
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);

      setSuccess(`Dosya indirildi: ${link.download}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "İndirme sırasında bir hata oluştu.");
    } finally {
      setDownloading(false);
    }
  }

  return (
    <div className="mt-6 rounded-xl border border-navy-100 bg-white px-4 py-3 shadow-sm">
      <div className="flex flex-wrap items-end gap-3">
        <span className="pb-2 text-xs font-semibold uppercase tracking-wide text-navy-500">
          Luca&apos;ya aktar
        </span>
        <div>
          <label htmlFor={`lucaStripFrom-${source}`} className="label !mb-1 text-xs">
            Başlangıç
          </label>
          <input
            id={`lucaStripFrom-${source}`}
            type="date"
            className="input !py-1.5 text-sm"
            value={fromDate}
            max={toDate}
            onChange={(e) => setFromDate(e.target.value)}
          />
        </div>
        <div>
          <label htmlFor={`lucaStripTo-${source}`} className="label !mb-1 text-xs">
            Bitiş
          </label>
          <input
            id={`lucaStripTo-${source}`}
            type="date"
            className="input !py-1.5 text-sm"
            value={toDate}
            min={fromDate}
            onChange={(e) => setToDate(e.target.value)}
          />
        </div>
        <button
          onClick={handleDownload}
          className="btn-secondary flex items-center gap-2 !px-4 !py-2 text-sm"
          disabled={downloading}
          title={
            bulkMode
              ? "Seçili (boşsa tüm) mükelleflerin ONAYLANMIŞ belgelerini mükellef başına ayrı XLSX olarak tek ZIP'te indirir."
              : "Seçilen aralıktaki ONAYLANMIŞ belgeleri Luca Fiş Aktarım Şablonu (.xlsx) olarak indirir."
          }
        >
          {downloading && (
            <span className="h-3.5 w-3.5 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
          )}
          {downloading
            ? "İndiriliyor..."
            : bulkMode
              ? "Excelleri İndir (ZIP)"
              : `Excel İndir (${label})`}
        </button>
        <span className="pb-2 text-xs text-navy-400">
          Yalnızca onaylanmış belgeler dahil edilir.
          {bulkMode && " Her mükellef için ayrı dosya üretilir."}{" "}
          Tüm seçenekler için Dışa Aktar sayfasını kullanın.
        </span>
      </div>

      {error && <div className="alert-error mt-3 !py-2 text-sm">{error}</div>}
      {success && (
        <div className="mt-3 rounded-lg border border-green-200 bg-green-50 px-4 py-2 text-sm text-green-800">
          {success}
        </div>
      )}
    </div>
  );
}
