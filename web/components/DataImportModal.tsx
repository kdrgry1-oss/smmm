"use client";

import { useEffect, useState } from "react";
import {
  api,
  ClientResponse,
  downloadClientImportTemplate,
  downloadCodingImportTemplate,
  downloadVehicleImportTemplate,
  importClients,
  importCodingData,
  importFisListesi,
  importHesapPlani,
  importMizan,
  importVehicles,
} from "@/lib/api";

/**
 * Ortak "Veri İçe Aktar" modalı — Mükellefler, Faturalar ve Fişler sayfalarından aynı
 * bileşen açılır. Altı sekme:
 *  - Mükellef-bazlı (mizan / geçmiş fiş listesi / cari şablonu / hesap planı CSV): kod
 *    motorunun hafızasını ve mükellef hesap planını doldurur. `client` prop'u verilmişse
 *    (Mükellefler sayfası satır bağlamı) o mükellefe sabitlenir; verilmemişse modal içinde
 *    küçük mükellef seçici gösterilir (Faturalar/Fişler sayfaları).
 *  - Mükellef-bağımsız: "Mükellef Listesi" (VKN üzerinden upsert — var olana dokunulmaz)
 *    ve "Araç Plakaları" (satırdaki Mükellef VKN kolonundan eşleşir).
 */

type ImportTab = "mizan" | "fis" | "cari" | "plan" | "mukellef" | "arac";

/** Mükellef bağlamı GEREKTİRMEYEN sekmeler (mükellef listesi + araç plakaları). */
const CLIENT_FREE_TABS: ImportTab[] = ["mukellef", "arac"];

const TABS: { key: ImportTab; label: string }[] = [
  { key: "mizan", label: "Mizan (XLSX)" },
  { key: "fis", label: "Geçmiş Fiş Listesi (XLSX)" },
  { key: "cari", label: "Cari Kod Şablonu" },
  { key: "plan", label: "Hesap Planı (CSV)" },
  { key: "mukellef", label: "Mükellef Listesi" },
  { key: "arac", label: "Araç Plakaları" },
];

interface DataImportModalProps {
  /**
   * Sabit mükellef bağlamı (Mükellefler sayfasındaki satırdan). Verilmezse mükellef-bazlı
   * sekmelerde modal içi mükellef seçici gösterilir ve liste modal tarafından yüklenir.
   */
  client?: { id: string; title: string } | null;
  onClose: () => void;
}

export default function DataImportModal({ client = null, onClose }: DataImportModalProps) {
  // Sabit bağlam varsa mevcut davranış korunur (mizan ile açılır); serbest açılışta
  // en genel sekme olan mükellef listesiyle başlanır.
  const [tab, setTab] = useState<ImportTab>(client ? "mizan" : "mukellef");
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState<"template" | "import" | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Sonuç özeti (Türkçe mesaj) + satır hataları (cari/mükellef/araç aktarımları üretir).
  const [summary, setSummary] = useState<string | null>(null);
  const [rowErrors, setRowErrors] = useState<string[]>([]);

  // Modal içi mükellef seçici (yalnız sabit bağlam yokken kullanılır).
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(false);
  const [selectedClientId, setSelectedClientId] = useState("");

  useEffect(() => {
    if (client) return; // sabit bağlam varsa liste gerekmez
    let cancelled = false;
    setClientsLoading(true);
    api<ClientResponse[]>("/api/clients")
      .then((data) => {
        if (!cancelled) setClients(data);
      })
      .catch(() => {
        // liste yüklenemezse seçici boş kalır; mükellef-bazlı içe aktarma denemesi
        // "Önce mükellef seçin." uyarısıyla durdurulur
      })
      .finally(() => {
        if (!cancelled) setClientsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [client]);

  const clientFree = CLIENT_FREE_TABS.includes(tab);
  const effectiveClientId = client ? client.id : selectedClientId;

  function close() {
    if (busy) return;
    onClose();
  }

  /** Sekme değişimi: dosya ve sonuç sıfırlanır (her sekme kendi ucuna gönderir). */
  function switchTab(next: ImportTab) {
    if (busy) return;
    setTab(next);
    setFile(null);
    setError(null);
    setSummary(null);
    setRowErrors([]);
  }

  /** "Şablonu İndir": sekmeye uygun .xlsx şablonunu indirir (örnek satırlar içerir). */
  async function handleTemplateDownload() {
    if (busy) return;
    if (tab === "cari" && !effectiveClientId) {
      setError("Şablonu indirmek için önce mükellef seçin.");
      return;
    }
    setBusy("template");
    setError(null);
    try {
      const { blob, fileName } =
        tab === "mukellef"
          ? await downloadClientImportTemplate()
          : tab === "arac"
            ? await downloadVehicleImportTemplate()
            : await downloadCodingImportTemplate(effectiveClientId);
      const fallback =
        tab === "mukellef"
          ? "mukellef_listesi_sablon.xlsx"
          : tab === "arac"
            ? "arac_plakalari_sablon.xlsx"
            : "cari-kod-sablonu.xlsx";
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = fileName ?? fallback;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Şablon indirilirken bir hata oluştu.");
    } finally {
      setBusy(null);
    }
  }

  /** "İçe Aktar": seçili sekmenin ucuna gönderir; Türkçe özet (+ varsa satır hataları) gösterilir. */
  async function handleImport() {
    if (busy) return;
    if (!clientFree && !effectiveClientId) {
      setError("Önce mükellef seçin.");
      return;
    }
    if (!file) {
      setError(tab === "plan" ? "Önce .csv dosyasını seçin." : "Önce .xlsx dosyasını seçin.");
      return;
    }

    setBusy("import");
    setError(null);
    setSummary(null);
    setRowErrors([]);
    try {
      if (tab === "mukellef") {
        const result = await importClients(file);
        setSummary(result.message);
        setRowErrors(result.errors);
      } else if (tab === "arac") {
        const result = await importVehicles(file);
        setSummary(result.message);
        setRowErrors(result.errors);
      } else if (tab === "mizan") {
        const result = await importMizan(effectiveClientId, file);
        setSummary(result.message);
      } else if (tab === "fis") {
        const result = await importFisListesi(effectiveClientId, file);
        setSummary(result.message);
      } else if (tab === "plan") {
        const result = await importHesapPlani(effectiveClientId, file);
        setSummary(result.message);
      } else {
        const result = await importCodingData(effectiveClientId, file);
        setSummary(result.message);
        setRowErrors(result.errors);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "İçe aktarma sırasında bir hata oluştu.");
    } finally {
      setBusy(null);
    }
  }

  const fileLabel =
    tab === "mizan"
      ? "Mizan dosyası (.xlsx, en fazla 5 MB)"
      : tab === "fis"
        ? "Fiş dökümü dosyası (.xlsx, en fazla 5 MB)"
        : tab === "plan"
          ? "Hesap planı dosyası (.csv, en fazla 2 MB)"
          : "Doldurulmuş şablon (.xlsx, en fazla 2 MB)";

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/50 px-4"
      onClick={close}
    >
      <div
        className="max-h-[90vh] w-full max-w-lg overflow-y-auto rounded-xl bg-white p-6 shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-lg font-bold text-navy-900">Veri İçe Aktar</h2>
        {client && <p className="mt-1 text-sm text-navy-500">{client.title}</p>}

        {/* Sekmeler: her biri kendi ucuna gönderir */}
        <div className="mt-4 grid grid-cols-2 gap-1 rounded-lg border border-navy-100 bg-navy-50 p-1 text-xs font-semibold sm:grid-cols-3">
          {TABS.map((t) => (
            <button
              key={t.key}
              type="button"
              onClick={() => switchTab(t.key)}
              disabled={busy !== null}
              className={`rounded-md px-2 py-1.5 transition ${
                tab === t.key
                  ? "bg-white text-navy-900 shadow-sm"
                  : "text-navy-500 hover:text-navy-700"
              }`}
            >
              {t.label}
            </button>
          ))}
        </div>

        {tab === "mizan" && (
          <p className="mt-3 text-xs text-navy-400">
            Luca&apos;dan aldığınız mizanı olduğu gibi yükleyin. Yaprak hesaplar mükellefin
            hesap planına (kod alanlarındaki otomatik önerilerin kaynağı), 120/320 cari
            hesapları unvanlarıyla birlikte kod motorunun cari hafızasına işlenir — motor
            faturadaki satıcı unvanını mizandaki cari kartla eşleştirir.
          </p>
        )}
        {tab === "fis" && (
          <p className="mt-3 text-xs text-navy-400">
            Luca fiş dökümünüzü (14 kolonlu Fiş Aktarım Şablonu biçimi) olduğu gibi yükleyin.
            Her evrak bir fiş sayılır: satıcı unvanı → gider hesabı ve satıcı unvanı → cari
            kodu alışkanlıklarınız kod motoruna öğretilir; satış faturası fişleri atlanır.
          </p>
        )}
        {tab === "cari" && (
          <p className="mt-3 text-xs text-navy-400">
            Geçmişte satıcılarınızı nasıl kodladıysanız toplu yükleyin: Satıcı VKN + Cari Kod
            satırları kod motorunun cari hafızasına, cari/hesap kodları ve adları mükellefin
            hesap planına işlenir. Şablondaki örnek satırları içe aktarmadan önce silmeyi
            unutmayın.
          </p>
        )}
        {tab === "plan" && (
          <p className="mt-3 text-xs text-navy-400">
            Luca → Hesap Planı → Dışa Aktar dosyasını olduğu gibi yükleyin (hesap adları ve
            VKN&apos;li cariler içe alınır).
          </p>
        )}
        {tab === "mukellef" && (
          <p className="mt-3 text-xs text-navy-400">
            Mükellef portföyünüzü tek seferde aktarın: Unvan ve VKN/TCKN zorunludur; Vergi
            Dairesi, E-posta, Telefon ve Adres opsiyoneldir. VKN&apos;si zaten kayıtlı olan
            mükellefe DOKUNULMAZ (atlanır), yeniler eklenir. Şablondaki örnek satırları içe
            aktarmadan önce silmeyi unutmayın.
          </p>
        )}
        {tab === "arac" && (
          <p className="mt-3 text-xs text-navy-400">
            Araç plakalarını toplu yükleyin: her satır &quot;Mükellef VKN&quot; kolonundan
            mükellefe bağlanır (mükellef seçimi gerekmez). Araç Tipi BINEK ise akaryakıt
            giderinin %30&apos;u KKEG işaretlenir, TICARI ise tam gider yazılır. Kayıtlı
            plakalar atlanır.
          </p>
        )}

        {/* Mükellef-bazlı sekmelerde, satır bağlamı yoksa modal içi mükellef seçici */}
        {!client && !clientFree && (
          <div className="mt-4">
            <label htmlFor="importClientSelect" className="label">
              Mükellef <span className="text-red-500">*</span>
            </label>
            <select
              id="importClientSelect"
              className="input"
              value={selectedClientId}
              disabled={clientsLoading || busy !== null}
              onChange={(e) => {
                setSelectedClientId(e.target.value);
                setError(null);
                setSummary(null);
                setRowErrors([]);
              }}
            >
              <option value="">
                {clientsLoading ? "Yükleniyor..." : "Mükellef seçin..."}
              </option>
              {clients.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </select>
          </div>
        )}

        {error && <div className="alert-error mt-4">{error}</div>}

        {/* Sonuç özeti: Türkçe mesaj + ilk 10 satır hatası */}
        {summary && (
          <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
            <p>{summary}</p>
            {rowErrors.length > 0 && (
              <ul className="mt-2 list-disc space-y-0.5 pl-5 text-xs text-amber-800">
                {rowErrors.slice(0, 10).map((rowError, index) => (
                  <li key={index}>{rowError}</li>
                ))}
                {rowErrors.length > 10 && <li>+{rowErrors.length - 10} satır hatası daha</li>}
              </ul>
            )}
          </div>
        )}

        {/* Kalıcı ipucu (kodlama sekmeleri): içe aktarım sonrası öneriler yeniden kodlamayla tazelenir */}
        {!clientFree && (
          <p className="mt-3 rounded-lg border border-sky-200 bg-sky-50 px-3 py-2 text-xs text-sky-800">
            İçe aktarmadan sonra Faturalar sayfasında &quot;Dönemi Kodla&quot; çalıştırın —
            öneriler yeni kodlarla güncellenir.
          </p>
        )}

        <div className="mt-5 space-y-4">
          {(tab === "cari" || tab === "mukellef" || tab === "arac") && (
            <div>
              <button
                type="button"
                onClick={handleTemplateDownload}
                className="btn-secondary flex items-center gap-2"
                disabled={busy !== null}
              >
                {busy === "template" && (
                  <span className="h-4 w-4 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                )}
                {busy === "template" ? "İndiriliyor..." : "Şablonu İndir"}
              </button>
              <p className="mt-1 text-xs text-navy-400">
                {tab === "cari"
                  ? "Kolonlar: Satıcı VKN, Satıcı Unvanı, Cari Kod, Hesap Kodu (ops.), Hesap Adı (ops.)"
                  : tab === "mukellef"
                    ? "Kolonlar: Unvan*, VKN/TCKN*, Vergi Dairesi, E-posta, Telefon, Adres"
                    : "Kolonlar: Mükellef VKN*, Plaka*, Araç Tipi (BINEK/TICARI)*, Açıklama (ops.)"}
              </p>
            </div>
          )}

          <div>
            <label htmlFor="dataImportFile" className="label">
              {fileLabel}
            </label>
            <input
              id="dataImportFile"
              key={tab} // sekme değişince dosya seçimi sıfırlanır
              type="file"
              accept={tab === "plan" ? ".csv" : ".xlsx"}
              className="mt-1 block w-full text-sm text-navy-700 file:mr-3 file:rounded-md file:border-0 file:bg-navy-100 file:px-3 file:py-1.5 file:text-xs file:font-semibold file:text-navy-700 hover:file:bg-navy-200"
              onChange={(e) => {
                setFile(e.target.files?.[0] ?? null);
                setError(null);
                setSummary(null);
                setRowErrors([]);
              }}
            />
          </div>

          <div className="flex justify-end gap-3 pt-2">
            <button
              type="button"
              className="btn-secondary"
              onClick={close}
              disabled={busy !== null}
            >
              Kapat
            </button>
            <button
              type="button"
              className="btn-primary flex items-center gap-2"
              onClick={handleImport}
              disabled={busy !== null || !file}
            >
              {busy === "import" && (
                <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
              )}
              {busy === "import" ? "Aktarılıyor..." : "İçe Aktar"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
