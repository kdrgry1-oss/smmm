"use client";

import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
import {
  ACCOUNT_CODE_REGEX,
  api,
  approveEInvoiceCoding,
  ApproveLine,
  bulkApproveEInvoices,
  bulkCodeEInvoices,
  bulkDeleteEInvoices,
  bulkUnapproveEInvoices,
  ClientResponse,
  CodeBulkResult,
  codeBulkPeriod,
  codeEInvoice,
  CodingStatusCode,
  EInvoiceDetail,
  EInvoiceListItem,
  EInvoicePage,
  EInvoiceStatusCode,
  getEInvoice,
  isGibManualVerificationMessage,
  listEInvoices,
  listWithholdingRules,
  setEInvoiceCodes,
  syncBulkEInvoices,
  syncClientEInvoices,
  unapproveEInvoice,
  WithholdingRuleItem,
} from "@/lib/api";
import LucaExportStrip from "@/components/LucaExportStrip";
import ClientMultiSelect from "@/components/ClientMultiSelect";
import AccountCodeText from "@/components/AccountCodeText";
import AccountCodeAutocomplete from "@/components/AccountCodeAutocomplete";
import DataImportModal from "@/components/DataImportModal";
import GibLoginModal from "@/components/GibLoginModal";

/** Tevkifat oranı biçimi (ör. "9/10") — backend aynı deseni doğrular. */
const WITHHOLDING_RATE_REGEX = /^\d{1,2}\/\d{1,2}$/;

/** "1.234,56" veya "1234.56" girişini sayıya çevirir; çözülemezse null. */
function parseAmountInput(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const normalized = trimmed.replace(/\./g, "").replace(",", ".");
  const direct = Number(trimmed.replace(",", "."));
  const parsed = Number(normalized);
  const result = !isNaN(direct) ? direct : parsed;
  return isNaN(result) ? null : result;
}

const PAGE_SIZE = 20;

const statusOptions: { value: string; label: string }[] = [
  { value: "", label: "Tüm durumlar" },
  { value: "New", label: "Yeni" },
  { value: "Processing", label: "İşleniyor" },
  { value: "Processed", label: "İşlendi" },
  { value: "Exported", label: "Aktarıldı" },
  { value: "Error", label: "Hata" },
];

const statusBadges: Record<EInvoiceStatusCode, { label: string; className: string }> = {
  New: { label: "Yeni", className: "bg-blue-100 text-blue-700" },
  Processing: { label: "İşleniyor", className: "bg-amber-100 text-amber-700" },
  Processed: { label: "İşlendi", className: "bg-green-100 text-green-700" },
  Exported: { label: "Aktarıldı", className: "bg-navy-100 text-navy-600" },
  Error: { label: "Hata", className: "bg-red-100 text-red-700" },
};

const codingBadges: Record<CodingStatusCode, { label: string; className: string }> = {
  Uncoded: { label: "Kodlanmadı", className: "bg-navy-100 text-navy-500" },
  AutoCoded: { label: "Otomatik", className: "bg-green-100 text-green-700" },
  NeedsReview: { label: "Onay Bekliyor", className: "bg-amber-100 text-amber-700" },
  Approved: { label: "Onaylandı", className: "bg-emerald-100 text-emerald-800" },
};

function formatDate(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

function formatMoney(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat("tr-TR", {
      style: "currency",
      currency: currency || "TRY",
      minimumFractionDigits: 2,
    }).format(value);
  } catch {
    return `${value.toFixed(2)} ${currency}`;
  }
}

function toDateInputValue(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/** Güven yüzdesi rozeti: yeşil ≥ %85, sarı ≥ %60, altı kırmızı. */
function ConfidenceBadge({ confidence }: { confidence: number | null }) {
  if (confidence === null || confidence === undefined) {
    return <span className="rounded-full bg-navy-100 px-2 py-0.5 text-xs font-semibold text-navy-500">—</span>;
  }
  const pct = Math.round(confidence * 100);
  const className =
    pct >= 85
      ? "bg-green-100 text-green-700"
      : pct >= 60
        ? "bg-amber-100 text-amber-700"
        : "bg-red-100 text-red-700";
  return (
    <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${className}`}>%{pct}</span>
  );
}

interface LineEdit {
  accountCode: string;
  cariCode: string;
  // Faz 8 — B2: tevkifat (boş kod = tevkifat yok)
  withholdingCode: string;
  withholdingRate: string;
  // Faz 8 — B3: KKEG
  isKkeg: boolean;
  kkegAmount: string;
  kkegReason: string;
}

export default function EInvoicesPage() {
  // Mükellef seçimi: çoklu seçim — BOŞ dizi "TÜM mükellefler" anlamına gelir.
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);
  const [selectedClientIds, setSelectedClientIds] = useState<string[]>([]);
  const clientMap = useMemo(() => new Map(clients.map((c) => [c.id, c])), [clients]);

  // Ortak "Veri İçe Aktar" modalı (mükellef seçici modal içindedir).
  const [importOpen, setImportOpen] = useState(false);

  // Tam olarak TEK mükellef seçiliyken mevcut tekil davranışlar (senkron vb.) korunur.
  const singleClientId = selectedClientIds.length === 1 ? selectedClientIds[0] : "";

  // Senkron
  const today = new Date();
  const thirtyDaysAgo = new Date(today.getTime() - 30 * 24 * 60 * 60 * 1000);
  const [fromDate, setFromDate] = useState(toDateInputValue(thirtyDaysAgo));
  const [toDate, setToDate] = useState(toDateInputValue(today));
  const [syncing, setSyncing] = useState(false);
  const [syncResult, setSyncResult] = useState<string | null>(null);
  const [syncError, setSyncError] = useState<string | null>(null);

  // Faz 17 — GİB Portalı mükellefinde senkron "GİB oturumu yenilenmeli" hatası verirse
  // kullanıcı doğrulama kodunu bu modalla girer (yalnız TEK mükellef seçiliyken anlamlı).
  const [gibLoginOpen, setGibLoginOpen] = useState(false);

  // Toplu kodlama (Faz 2 → çoklu mükellef code-bulk)
  const [batchCoding, setBatchCoding] = useState(false);
  const [batchResult, setBatchResult] = useState<CodeBulkResult | null>(null);
  const [batchError, setBatchError] = useState<string | null>(null);

  // Satır seçimi + toplu işlemler (toplu onay / toplu kodlama / onay kaldırma / toplu silme)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [bulkBusy, setBulkBusy] = useState<"approve" | "code" | "unapprove" | "delete" | null>(null);
  const [bulkResult, setBulkResult] = useState<string | null>(null);
  const [bulkError, setBulkError] = useState<string | null>(null);

  // Liste
  const [pageData, setPageData] = useState<EInvoicePage | null>(null);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  const [page, setPage] = useState(1);

  // Detay çekmecesi
  const [detailId, setDetailId] = useState<string | null>(null);
  const [detail, setDetail] = useState<EInvoiceDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);

  // Kodlama düzenleme durumu (satır Id → düzenlenen hesap/cari kodu)
  const [lineEdits, setLineEdits] = useState<Record<string, LineEdit>>({});
  const [coding, setCoding] = useState(false);
  const [approving, setApproving] = useState(false);
  const [codingError, setCodingError] = useState<string | null>(null);
  const [approveSuccess, setApproveSuccess] = useState(false);

  // Faz 8 — B2/B3: tevkifat kod listesi + satır bazlı "Tevkifat/KKEG" panel aç/kapa durumu.
  const [withholdingRules, setWithholdingRules] = useState<WithholdingRuleItem[]>([]);
  const [extraOpen, setExtraOpen] = useState<Record<string, boolean>>({});

  // Listede Hesap Kodu hücresi yerinde düzenleme (tıkla-düzenle): set-codes ucu faturanın
  // TÜM satırlarına uygular; onaylı faturada sunucu 409 + "Önce onayı kaldırın" döner.
  const [inlineEditId, setInlineEditId] = useState<string | null>(null);
  const [inlineValue, setInlineValue] = useState("");
  const [inlineSaving, setInlineSaving] = useState(false);
  const [inlineError, setInlineError] = useState<string | null>(null);

  // Tevkifat kodları bir kez yüklenir; hata rozet listesini etkilemez (elle kod girilebilir).
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const rules = await listWithholdingRules();
        if (!cancelled) setWithholdingRules(rules);
      } catch {
        // liste yüklenemezse açılır menü yerine serbest metin kullanılır
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

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

  const loadInvoices = useCallback(
    async (opts: { clientIds: string[]; status: string; q: string; page: number }) => {
      setListLoading(true);
      setListError(null);
      try {
        // Tek seçim → mevcut tekil clientId parametresi (davranış birebir korunur);
        // çoklu seçim → clientIds; boş seçim → filtre yok (tüm mükellefler birleşik).
        const data = await listEInvoices({
          clientId: opts.clientIds.length === 1 ? opts.clientIds[0] : undefined,
          clientIds: opts.clientIds.length > 1 ? opts.clientIds : undefined,
          status: opts.status || undefined,
          q: opts.q || undefined,
          page: opts.page,
          pageSize: PAGE_SIZE,
        });
        setPageData(data);
        // Liste her yenilendiğinde (sayfa/filtre değişimi dahil) seçim sıfırlanır.
        setSelectedIds(new Set());
        // Yerinde düzenleme de kapatılır (satır artık başka kaydı gösterebilir).
        setInlineEditId(null);
        setInlineValue("");
        setInlineError(null);
      } catch (err) {
        setListError(
          err instanceof Error ? err.message : "Faturalar yüklenirken bir hata oluştu."
        );
      } finally {
        setListLoading(false);
      }
    },
    []
  );

  // Filtre/arama/sayfa değişince listeyi yenile (aramada 300 ms debounce)
  useEffect(() => {
    const timer = setTimeout(
      () => loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page }),
      search ? 300 : 0
    );
    return () => clearTimeout(timer);
  }, [selectedClientIds, statusFilter, search, page, loadInvoices]);

  const applyDetail = useCallback((data: EInvoiceDetail) => {
    setDetail(data);
    // Satır düzenleme durumunu detaydan başlat: nihai kod > öneri > boş.
    const edits: Record<string, LineEdit> = {};
    const open: Record<string, boolean> = {};
    for (const line of data.lines) {
      edits[line.id] = {
        accountCode: line.accountCode ?? line.suggestedAccountCode ?? "",
        cariCode: line.cariCode ?? "",
        withholdingCode: line.withholdingCode ?? "",
        withholdingRate: line.withholdingRate ?? "",
        isKkeg: line.isKkeg,
        kkegAmount: line.kkegAmount != null ? String(line.kkegAmount).replace(".", ",") : "",
        kkegReason: line.kkegReason ?? "",
      };
      // Motor tevkifat/KKEG önerdiyse panel otomatik açık gelir (SMMM'nin gözünden kaçmasın).
      open[line.id] = Boolean(line.withholdingCode) || line.isKkeg;
    }
    setLineEdits(edits);
    setExtraOpen(open);
  }, []);

  const openDetailById = useCallback(
    async (id: string) => {
      setDetailId(id);
      setDetail(null);
      setDetailError(null);
      setCodingError(null);
      setApproveSuccess(false);
      setDetailLoading(true);
      try {
        const data = await getEInvoice(id);
        applyDetail(data);
      } catch (err) {
        setDetailError(
          err instanceof Error ? err.message : "Fatura detayı yüklenirken bir hata oluştu."
        );
      } finally {
        setDetailLoading(false);
      }
    },
    [applyDetail]
  );

  // Onay kuyruğundan "?fatura=<id>" ile gelindiyse çekmeceyi otomatik aç.
  useEffect(() => {
    if (typeof window === "undefined") return;
    const invoiceId = new URLSearchParams(window.location.search).get("fatura");
    if (invoiceId) openDetailById(invoiceId);
  }, [openDetailById]);

  /** Tarih aralığı ön kontrolü (senkron + kodlama ortak). Hata mesajı döner; yoksa null. */
  function validateRange(): string | null {
    if (!fromDate || !toDate) return "Başlangıç ve bitiş tarihlerini seçin.";
    if (fromDate > toDate) return "Başlangıç tarihi bitiş tarihinden sonra olamaz.";
    return null;
  }

  /**
   * "Senkronize Et" (yalnız TEK mükellef seçiliyken görünür): mevcut tekil senkron ucu —
   * davranış birebir korunur (anında çalışır, sonucu bekler).
   */
  async function handleSync() {
    setSyncError(null);
    setSyncResult(null);

    if (!singleClientId) {
      setSyncError("Senkronizasyon için önce bir mükellef seçin.");
      return;
    }
    const rangeError = validateRange();
    if (rangeError) {
      setSyncError(rangeError);
      return;
    }

    setSyncing(true);
    try {
      const result = await syncClientEInvoices(singleClientId, fromDate, toDate);
      setSyncResult(result.message);
      setPage(1);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page: 1 });
    } catch (err) {
      setSyncError(
        err instanceof Error ? err.message : "Senkronizasyon sırasında bir hata oluştu."
      );
    } finally {
      setSyncing(false);
    }
  }

  /**
   * "Faturaları Çek": seçili (boşsa TÜM) mükellefler için senkron KUYRUĞA alınır
   * (sync-bulk → OutboundJob "FaturaSync"); entegrasyonu olmayanlar sayıyla raporlanır.
   */
  async function handleSyncBulk() {
    setSyncError(null);
    setSyncResult(null);

    const rangeError = validateRange();
    if (rangeError) {
      setSyncError(rangeError);
      return;
    }

    setSyncing(true);
    try {
      const result = await syncBulkEInvoices(selectedClientIds, fromDate, toDate);
      setSyncResult(
        `${result.message} Senkron sonuçlarını Gönderim Kuyruğu sayfasından izleyebilirsiniz.`
      );
    } catch (err) {
      setSyncError(
        err instanceof Error ? err.message : "Toplu senkron sırasında bir hata oluştu."
      );
    } finally {
      setSyncing(false);
    }
  }

  /**
   * "Dönemi Kodla": seçili (boşsa TÜM) mükelleflerin aralıktaki kodlanmamış faturalarını
   * mükellef başına döngüyle motordan geçirir (code-bulk); sonuç bandında toplam + kırılım.
   */
  async function handleBatchCode() {
    setBatchError(null);
    setBatchResult(null);

    const rangeError = validateRange();
    if (rangeError) {
      setBatchError(rangeError);
      return;
    }

    setBatchCoding(true);
    try {
      const result = await codeBulkPeriod(selectedClientIds, fromDate, toDate);
      setBatchResult(result);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setBatchError(
        err instanceof Error ? err.message : "Toplu kodlama sırasında bir hata oluştu."
      );
    } finally {
      setBatchCoding(false);
    }
  }

  /** Tek satırın seçimini değiştirir (checkbox). */
  function toggleSelected(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  /** Başlıktaki checkbox: yalnız görünen sayfadaki faturaların tümünü seç/kaldır. */
  function toggleSelectAllVisible(visible: EInvoiceListItem[], allSelected: boolean) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      for (const inv of visible) {
        if (allSelected) next.delete(inv.id);
        else next.add(inv.id);
      }
      return next;
    });
  }

  /** "Toplu Onayla": onay penceresi → bulk-approve → sonuç bandı → liste yenile. */
  async function handleBulkApprove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    const confirmed = window.confirm(
      `${ids.length} fatura önerilen kodlarla onaylanacak. Motor bu eşlemeleri öğrenecek. Devam edilsin mi?`
    );
    if (!confirmed) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkBusy("approve");
    try {
      const result = await bulkApproveEInvoices(ids);
      setBulkResult(result.message);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Toplu onay sırasında bir hata oluştu.");
    } finally {
      setBulkBusy(null);
    }
  }

  /** "Toplu Kodla": seçilen faturaları motordan geçirir (onaylamadan). */
  async function handleBulkCode() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkBusy("code");
    try {
      const result = await bulkCodeEInvoices(ids);
      setBulkResult(result.message);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setBulkError(
        err instanceof Error ? err.message : "Toplu kodlama sırasında bir hata oluştu."
      );
    } finally {
      setBulkBusy(null);
    }
  }

  /**
   * "Onayı Kaldır": seçilenlerden Approved olan faturalar Onay Bekliyor'a döndürülür
   * (kodlar ve motorun öğrenme hafızası korunur); onaylı olmayanlar sunucuda atlanır.
   */
  async function handleBulkUnapprove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    const confirmed = window.confirm(
      `${ids.length} faturadan onaylı olanların onayı kaldırılacak (kodlar silinmez, motor hafızası korunur). Devam edilsin mi?`
    );
    if (!confirmed) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkBusy("unapprove");
    try {
      const result = await bulkUnapproveEInvoices(ids);
      setBulkResult(result.message);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setBulkError(
        err instanceof Error ? err.message : "Onay kaldırma sırasında bir hata oluştu."
      );
    } finally {
      setBulkBusy(null);
    }
  }

  /** "Seçilenleri Sil": onay penceresi → bulk-delete → sonuç bandı → liste yenile. */
  async function handleBulkDelete() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    const confirmed = window.confirm(`${ids.length} fatura silinecek. Emin misiniz?`);
    if (!confirmed) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkBusy("delete");
    try {
      const result = await bulkDeleteEInvoices(ids);
      setBulkResult(result.message);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Toplu silme sırasında bir hata oluştu.");
    } finally {
      setBulkBusy(null);
    }
  }

  // ---------- Listede Hesap Kodu hücresi: tıkla-düzenle ----------

  /** Hücreye tıklayınca düzenleme modunu açar (özet tek kodsa başlangıç değeri olur). */
  function startInlineEdit(invoice: EInvoiceListItem) {
    const summary = invoice.accountCodeSummary ?? "";
    // "770.01, 153.01 +2" gibi çoklu özet başlangıç değeri olamaz — boş başlanır.
    const initial = summary.includes(",") || summary.includes("+") ? "" : summary;
    setInlineEditId(invoice.id);
    setInlineValue(initial);
    setInlineError(null);
  }

  function cancelInlineEdit() {
    setInlineEditId(null);
    setInlineValue("");
    setInlineError(null);
  }

  /** Kaydet (✓): set-codes ucu kodu faturanın TÜM satırlarına uygular; listede değer güncellenir. */
  async function handleInlineSave() {
    if (!inlineEditId || inlineSaving) return;

    const code = inlineValue.trim();
    if (!code) {
      setInlineError("Hesap kodu boş olamaz.");
      return;
    }

    setInlineSaving(true);
    setInlineError(null);
    try {
      const updated = await setEInvoiceCodes(inlineEditId, { accountCode: code });
      // Liste yeniden çekilmez; güncellenen satır sunucu yanıtındaki değerlerle tazelenir.
      setPageData((prev) =>
        prev
          ? {
              ...prev,
              items: prev.items.map((item) =>
                item.id === updated.id
                  ? {
                      ...item,
                      accountCodeSummary: updated.accountCodeSummary,
                      cariCode: updated.cariCode,
                      codingStatus: updated.codingStatus,
                    }
                  : item
              ),
            }
          : prev
      );
      cancelInlineEdit();
    } catch (err) {
      // Onaylı faturada sunucu 409 + "Önce onayı kaldırın." Türkçe mesajı döner.
      setInlineError(err instanceof Error ? err.message : "Kod kaydedilirken bir hata oluştu.");
    } finally {
      setInlineSaving(false);
    }
  }

  function closeDetail() {
    setDetailId(null);
    setDetail(null);
    setDetailError(null);
    setCodingError(null);
    setApproveSuccess(false);
    setLineEdits({});
    setExtraOpen({});
  }

  /** Çekmecedeki "Kodla": tek faturayı motordan geçirir ve detayı yeniler. */
  async function handleCodeInvoice() {
    if (!detailId) return;
    setCodingError(null);
    setApproveSuccess(false);
    setCoding(true);
    try {
      await codeEInvoice(detailId);
      const data = await getEInvoice(detailId);
      applyDetail(data);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setCodingError(err instanceof Error ? err.message : "Fatura kodlanırken bir hata oluştu.");
    } finally {
      setCoding(false);
    }
  }

  /**
   * Çekmecedeki "Onayı Kaldır": Approved faturayı Onay Bekliyor'a döndürür; satır kodları
   * ve motorun öğrenme hafızası korunur (SMMM düzeltip yeniden onaylayabilir).
   */
  async function handleUnapprove() {
    if (!detailId) return;
    setCodingError(null);
    setApproveSuccess(false);
    setCoding(true);
    try {
      const data = await unapproveEInvoice(detailId);
      applyDetail(data);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setCodingError(
        err instanceof Error ? err.message : "Onay kaldırma sırasında bir hata oluştu."
      );
    } finally {
      setCoding(false);
    }
  }

  /** "Onayla": düzeltmelerle approve çağrısı; başarıda öğrenme bandı gösterilir. */
  async function handleApprove() {
    if (!detail || !detailId) return;
    setCodingError(null);
    setApproveSuccess(false);

    // UI tarafı doğrulama (backend aynı deseni ikinci kez doğrular).
    const lines: ApproveLine[] = [];
    for (const line of detail.lines) {
      const edit = lineEdits[line.id];
      const accountCode = edit?.accountCode.trim() ?? "";
      // Cari kod UI'da gösterilmez/düzenlenmez — motorun atadığı değer İÇSEL olarak aynen
      // geri gönderilir (regex kontrolü kaldırıldı: kullanıcı düzeltemeyeceği alan onayı engellemesin).
      const cariCode = edit?.cariCode.trim() ?? "";

      if (!accountCode) {
        setCodingError(`${line.lineNumber}. satır için hesap kodu boş olamaz.`);
        return;
      }
      if (!ACCOUNT_CODE_REGEX.test(accountCode)) {
        setCodingError(`${line.lineNumber}. satır için geçerli bir hesap kodu girin (ör. 770.01).`);
        return;
      }

      // Faz 8 — B2: tevkifat doğrulaması. Boş kod = "tevkifat yok" (backend'e "" gider, temizler).
      const withholdingCode = edit?.withholdingCode.trim() ?? "";
      const withholdingRate = edit?.withholdingRate.trim() ?? "";
      if (withholdingCode && !/^\d{3}$/.test(withholdingCode)) {
        setCodingError(`${line.lineNumber}. satır için tevkifat kodu 3 haneli olmalıdır (ör. 601).`);
        return;
      }
      if (withholdingCode && !WITHHOLDING_RATE_REGEX.test(withholdingRate)) {
        setCodingError(`${line.lineNumber}. satır için tevkifat oranını pay/payda biçiminde girin (ör. 9/10).`);
        return;
      }

      // Faz 8 — B3: KKEG doğrulaması.
      const isKkeg = edit?.isKkeg ?? false;
      let kkegAmount: number | null = null;
      if (isKkeg) {
        kkegAmount = parseAmountInput(edit?.kkegAmount ?? "");
        if (kkegAmount === null || kkegAmount <= 0) {
          setCodingError(`${line.lineNumber}. satır için geçerli bir KKEG tutarı girin (0'dan büyük).`);
          return;
        }
        if (kkegAmount > line.lineAmount) {
          setCodingError(`${line.lineNumber}. satır için KKEG tutarı satır matrahını (${line.lineAmount.toLocaleString("tr-TR")}) aşamaz.`);
          return;
        }
      }

      lines.push({
        lineId: line.id,
        accountCode,
        cariCode: cariCode || null,
        withholdingCode, // "" = temizle, dolu = uygula
        withholdingRate: withholdingCode ? withholdingRate : null,
        isKkeg,
        kkegAmount: isKkeg ? kkegAmount : null,
        kkegReason: isKkeg ? (edit?.kkegReason.trim() || null) : null,
      });
    }

    setApproving(true);
    try {
      const data = await approveEInvoiceCoding(detailId, lines);
      applyDetail(data);
      setApproveSuccess(true);
      await loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page });
    } catch (err) {
      setCodingError(err instanceof Error ? err.message : "Onay sırasında bir hata oluştu.");
    } finally {
      setApproving(false);
    }
  }

  const items = pageData?.items ?? [];
  const allVisibleSelected = items.length > 0 && items.every((i) => selectedIds.has(i.id));
  const detailApproved = detail?.codingStatus === "Approved";
  const detailUncoded = detail?.codingStatus === "Uncoded";

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Faturalar</h1>
          <p className="mt-1 text-sm text-navy-500">
            Gelen e-faturaları çekin, otomatik kod motoruyla kodlayın ve onaylayın.
          </p>
        </div>
        {/* Ortak içe aktarma modalı: mizan/fiş listesi/cari/hesap planı + mükellef listesi + plakalar */}
        <button
          onClick={() => setImportOpen(true)}
          className="btn-secondary"
          title="Excel'den toplu veri yükleyin — mizan, geçmiş fiş listesi, cari şablonu, hesap planı, mükellef listesi ve araç plakaları"
        >
          Veri İçe Aktar
        </button>
      </div>

      {importOpen && <DataImportModal onClose={() => setImportOpen(false)} />}

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}

      {/* Senkron + toplu kodlama paneli */}
      <div className="card mt-6">
        <div className="flex flex-wrap items-end gap-4">
          <div>
            <span className="label">Mükellef(ler)</span>
            {/* Aranabilir çoklu seçim: boş seçim = TÜM mükellefler. */}
            <ClientMultiSelect
              clients={clients}
              loading={clientsLoading}
              selectedIds={selectedClientIds}
              onChange={(ids) => {
                setSelectedClientIds(ids);
                setPage(1);
                setSyncResult(null);
                setSyncError(null);
                setBatchResult(null);
                setBatchError(null);
              }}
            />
          </div>
          <div>
            <label htmlFor="syncFrom" className="label">
              Başlangıç
            </label>
            <input
              id="syncFrom"
              type="date"
              className="input"
              value={fromDate}
              max={toDate}
              onChange={(e) => setFromDate(e.target.value)}
            />
          </div>
          <div>
            <label htmlFor="syncTo" className="label">
              Bitiş
            </label>
            <input
              id="syncTo"
              type="date"
              className="input"
              value={toDate}
              min={fromDate}
              max={toDateInputValue(new Date())}
              onChange={(e) => setToDate(e.target.value)}
            />
          </div>
          {/* Tek mükellef seçiliyken mevcut tekil senkron davranışı birebir korunur. */}
          {singleClientId ? (
            <button
              onClick={handleSync}
              className="btn-primary flex items-center gap-2"
              disabled={syncing || batchCoding || clientsLoading}
              title="Seçili mükellefin gelen faturalarını entegratörden hemen çeker."
            >
              {syncing && (
                <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
              )}
              {syncing ? "Senkronize ediliyor..." : "Senkronize Et"}
            </button>
          ) : (
            <button
              onClick={handleSyncBulk}
              className="btn-primary flex items-center gap-2"
              disabled={syncing || batchCoding || clientsLoading}
              title="Seçili (boşsa TÜM) mükellefler için senkronu kuyruğa alır; entegrasyonu olmayanlar atlanır."
            >
              {syncing && (
                <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
              )}
              {syncing ? "Kuyruğa alınıyor..." : "Faturaları Çek"}
            </button>
          )}
          <button
            onClick={handleBatchCode}
            className="btn-secondary flex items-center gap-2"
            disabled={syncing || batchCoding || clientsLoading}
            title="Seçili (boşsa TÜM) mükelleflerin dönemdeki kodlanmamış faturalarını otomatik kodlar."
          >
            {batchCoding && (
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
            )}
            {batchCoding ? "Kodlanıyor..." : "Dönemi Kodla"}
          </button>
        </div>

        {!clientsLoading && clients.length === 0 && !clientsError && (
          <p className="mt-3 text-sm text-navy-500">
            Senkronizasyon için önce bir mükellef ekleyin ve entegratör bağlayın.
          </p>
        )}

        {/* Senkron özet bandı: tekilde "5 yeni fatura alındı...", topluda
            "8 mükellef için senkron kuyruğa alındı, 2 mükellefte entegrasyon yok." */}
        {syncResult && (
          <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
            {syncResult}
          </div>
        )}
        {syncError && (
          <div className="alert-error mt-4 flex flex-wrap items-center justify-between gap-3">
            <span>{syncError}</span>
            {/* Faz 18: "GİB oturumu yenilenmeli" hatasında MÜŞAVİR oturumu yenilenir
                (advisor akışı — tek oturum tüm mükellefler için yeterlidir). */}
            {isGibManualVerificationMessage(syncError) && (
              <button
                className="rounded-md bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700"
                onClick={() => setGibLoginOpen(true)}
                title="Müşavir GİB girişindeki doğrulama kodunu girin — tek oturum tüm mükellefler için saklanır."
              >
                Doğrulama Kodunu Gir
              </button>
            )}
          </div>
        )}

        {/* Faz 18 — Müşavir GİB girişi modalı: başarıda senkron otomatik tekrarlanır. */}
        {gibLoginOpen && (
          <GibLoginModal
            mode="advisor"
            onClose={() => setGibLoginOpen(false)}
            onSuccess={() => {
              setSyncError(null);
              handleSync();
            }}
          />
        )}

        {/* Toplu kodlama sonuç bandı: toplam + mükellef kırılımı ("Velioğlu: 47, Aydın: 12"). */}
        {batchResult && (
          <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
            <p>{batchResult.message}</p>
            {batchResult.clients.length > 0 && (
              <p className="mt-1 text-xs text-green-700">
                {batchResult.clients
                  .map((c) => `${c.clientTitle}: ${c.autoCoded + c.needsReview}`)
                  .join(", ")}
              </p>
            )}
          </div>
        )}
        {batchError && <div className="alert-error mt-4">{batchError}</div>}
      </div>

      {/* Faz 14 (C): sayfa içi Luca dışa aktarma şeridi (yalnız faturalar).
          Tek mükellef → mevcut tek XLSX; çoklu/tümü → mükellef başına ayrı XLSX içeren ZIP. */}
      <LucaExportStrip clientIds={selectedClientIds} source="fatura" allowBulk />

      {/* Filtreler */}
      <div className="mt-6 flex flex-wrap items-center gap-4">
        <input
          type="search"
          className="input max-w-xs"
          placeholder="Fatura no veya satıcı ara..."
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            setPage(1);
          }}
        />
        <select
          className="input max-w-[180px]"
          value={statusFilter}
          onChange={(e) => {
            setStatusFilter(e.target.value);
            setPage(1);
          }}
        >
          {statusOptions.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </select>
        {pageData && pageData.totalCount > 0 && (
          <span className="text-sm text-navy-500">
            Toplam {pageData.totalCount} fatura
          </span>
        )}
      </div>

      {/* Toplu işlem sonuç bandı: "42 fatura onaylandı, 3 atlandı (zaten onaylı), 2 hata." */}
      {bulkResult && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {bulkResult}
        </div>
      )}
      {bulkError && <div className="alert-error mt-4">{bulkError}</div>}

      {/* Seçim varken yapışkan toplu işlem çubuğu */}
      {selectedIds.size > 0 && (
        <div className="sticky top-0 z-30 mt-4 flex flex-wrap items-center gap-3 rounded-xl border border-navy-200 bg-white px-4 py-3 shadow-md">
          <span className="text-sm font-semibold text-navy-900">
            {selectedIds.size} fatura seçili
          </span>
          <button
            onClick={handleBulkApprove}
            className="btn-primary flex items-center gap-2 !px-3 !py-1.5 text-xs"
            disabled={bulkBusy !== null}
            title="Seçilen faturaların önerilen kodlarını düzeltmesiz onaylar; motor bu eşlemeleri öğrenir."
          >
            {bulkBusy === "approve" && (
              <span className="h-3 w-3 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {bulkBusy === "approve" ? "Onaylanıyor..." : "Toplu Onayla"}
          </button>
          <button
            onClick={handleBulkCode}
            className="btn-secondary flex items-center gap-2 !px-3 !py-1.5 text-xs"
            disabled={bulkBusy !== null}
            title="Seçilen faturaları motordan geçirir (yalnız kodlar, onaylamaz)."
          >
            {bulkBusy === "code" && (
              <span className="h-3 w-3 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
            )}
            {bulkBusy === "code" ? "Kodlanıyor..." : "Toplu Kodla"}
          </button>
          <button
            onClick={handleBulkUnapprove}
            className="btn-secondary flex items-center gap-2 !px-3 !py-1.5 text-xs !text-amber-700"
            disabled={bulkBusy !== null}
            title="Seçilenlerden ONAYLI olanları Onay Bekliyor'a döndürür; kodlar silinmez, motor hafızası korunur."
          >
            {bulkBusy === "unapprove" && (
              <span className="h-3 w-3 animate-spin rounded-full border-2 border-amber-500 border-t-transparent" />
            )}
            {bulkBusy === "unapprove" ? "Kaldırılıyor..." : "Onayı Kaldır"}
          </button>
          <button
            onClick={handleBulkDelete}
            className="btn-primary !bg-red-700 hover:!bg-red-800 flex items-center gap-2 !px-3 !py-1.5 text-xs"
            disabled={bulkBusy !== null}
            title="Seçilen faturaları kalem satırlarıyla birlikte kalıcı olarak siler (geri alınamaz)."
          >
            {bulkBusy === "delete" && (
              <span className="h-3 w-3 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {bulkBusy === "delete" ? "Siliniyor..." : "Seçilenleri Sil"}
          </button>
          <button
            onClick={() => setSelectedIds(new Set())}
            className="text-xs font-semibold text-navy-500 hover:text-navy-700"
            disabled={bulkBusy !== null}
          >
            Vazgeç
          </button>
        </div>
      )}

      {listError && (
        <div className="alert-error mt-4 flex items-center justify-between gap-4">
          <span>{listError}</span>
          <button
            className="btn-secondary !px-3 !py-1.5 text-xs"
            onClick={() =>
              loadInvoices({ clientIds: selectedClientIds, status: statusFilter, q: search, page })
            }
          >
            Tekrar dene
          </button>
        </div>
      )}

      {/* Yükleniyor iskeleti */}
      {listLoading ? (
        <div className="mt-6 space-y-3">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="card animate-pulse">
              <div className="h-5 w-1/3 rounded bg-navy-100" />
              <div className="mt-3 h-4 w-1/4 rounded bg-navy-50" />
            </div>
          ))}
        </div>
      ) : items.length === 0 && !listError ? (
        /* Boş durum */
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          {search.trim() || statusFilter ? (
            <>
              <h2 className="text-lg font-semibold text-navy-900">Sonuç bulunamadı</h2>
              <p className="mt-2 text-sm text-navy-500">
                Arama veya filtre ölçütlerinizle eşleşen fatura yok.
              </p>
            </>
          ) : (
            <>
              <h2 className="text-lg font-semibold text-navy-900">
                Bu aralıkta fatura yok. Senkronize etmeyi deneyin.
              </h2>
              <p className="mt-2 max-w-md text-sm text-navy-500">
                Yukarıdan mükellef ve tarih aralığı seçip &quot;Senkronize Et&quot; düğmesine
                basarak entegratörünüzden gelen faturaları çekebilirsiniz.
              </p>
            </>
          )}
        </div>
      ) : items.length > 0 ? (
        /* Fatura tablosu */
        <>
          <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
            <table className="w-full min-w-[1180px] text-left text-sm">
              <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
                <tr>
                  <th className="w-10 px-4 py-3">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-navy-300"
                      aria-label="Sayfadaki tüm faturaları seç"
                      checked={allVisibleSelected}
                      onChange={() => toggleSelectAllVisible(items, allVisibleSelected)}
                    />
                  </th>
                  <th className="px-4 py-3">Tarih</th>
                  <th className="px-4 py-3">Fatura No</th>
                  <th className="px-4 py-3">Mükellef</th>
                  <th className="px-4 py-3">Satıcı</th>
                  <th className="px-4 py-3 text-right">Matrah</th>
                  <th className="px-4 py-3 text-right">KDV</th>
                  <th className="px-4 py-3 text-right">Toplam</th>
                  <th className="px-4 py-3">Durum</th>
                  <th className="px-4 py-3">Kodlama</th>
                  <th className="px-4 py-3">Hesap Kodu</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-navy-50">
                {items.map((invoice) => {
                  const badge = statusBadges[invoice.status] ?? statusBadges.New;
                  const codingBadge = codingBadges[invoice.codingStatus] ?? codingBadges.Uncoded;
                  const isApproved = invoice.codingStatus === "Approved";
                  const client = clientMap.get(invoice.clientId);
                  return (
                    <tr
                      key={invoice.id}
                      className="cursor-pointer transition hover:bg-navy-50/60"
                      onClick={() => openDetailById(invoice.id)}
                    >
                      {/* Seçim kutusu: satır tıklaması detay çekmecesini açar, checkbox açmaz. */}
                      <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="checkbox"
                          className="h-4 w-4 rounded border-navy-300"
                          aria-label={`${invoice.invoiceNumber} numaralı faturayı seç`}
                          checked={selectedIds.has(invoice.id)}
                          onChange={() => toggleSelected(invoice.id)}
                        />
                      </td>
                      <td className="px-4 py-3">{formatDate(invoice.issueDate)}</td>
                      <td className="px-4 py-3 font-mono text-xs">{invoice.invoiceNumber}</td>
                      <td className="px-4 py-3">
                        <span className="font-medium text-navy-900">{client?.title ?? "—"}</span>
                        {client?.taxId && (
                          <span className="block font-mono text-xs text-navy-400">
                            VKN: {client.taxId}
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-3">
                        <span className="font-medium text-navy-900">{invoice.senderTitle}</span>
                        <span className="block font-mono text-xs text-navy-400">
                          VKN: {invoice.senderTaxId}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-right">
                        {formatMoney(invoice.taxExclusiveAmount, invoice.currencyCode)}
                      </td>
                      <td className="px-4 py-3 text-right">
                        {formatMoney(invoice.taxTotal, invoice.currencyCode)}
                      </td>
                      <td className="px-4 py-3 text-right font-semibold text-navy-900">
                        {formatMoney(invoice.payableAmount, invoice.currencyCode)}
                      </td>
                      <td className="px-4 py-3">
                        <span
                          className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${badge.className}`}
                        >
                          {badge.label}
                        </span>
                      </td>
                      <td className="px-4 py-3">
                        <span
                          className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${codingBadge.className}`}
                        >
                          {codingBadge.label}
                        </span>
                      </td>
                      {/* Hesap kodları: Approved = kesin, diğer durumlar öneri görünümü (soluk/italik).
                          Hücreye tıklayınca yerinde düzenleme açılır (satır tıklaması çekmeceyi
                          açmaya devam eder — stopPropagation). Kaydet, kodu TÜM satırlara uygular. */}
                      <td
                        className="px-4 py-3"
                        onClick={(e) => {
                          e.stopPropagation();
                          if (inlineEditId !== invoice.id) startInlineEdit(invoice);
                        }}
                      >
                        {inlineEditId === invoice.id ? (
                          <div>
                            <div className="flex items-center gap-1">
                              <AccountCodeAutocomplete
                                clientId={invoice.clientId}
                                value={inlineValue}
                                onChange={setInlineValue}
                                placeholder="770.01"
                                autoFocus
                                disabled={inlineSaving}
                                onEnter={handleInlineSave}
                              />
                              <button
                                type="button"
                                className="rounded-md px-1.5 py-1 text-sm font-bold text-green-700 hover:bg-green-50 disabled:opacity-50"
                                onClick={handleInlineSave}
                                disabled={inlineSaving}
                                title="Kaydet — kod faturanın TÜM satırlarına uygulanır"
                                aria-label="Hesap kodunu kaydet"
                              >
                                {inlineSaving ? "…" : "✓"}
                              </button>
                              <button
                                type="button"
                                className="rounded-md px-1.5 py-1 text-sm font-bold text-navy-400 hover:bg-navy-50 disabled:opacity-50"
                                onClick={cancelInlineEdit}
                                disabled={inlineSaving}
                                title="Vazgeç"
                                aria-label="Düzenlemeyi iptal et"
                              >
                                ✗
                              </button>
                            </div>
                            {inlineError && (
                              <p className="mt-1 max-w-[220px] text-[10px] font-medium text-red-600">
                                {inlineError}
                              </p>
                            )}
                          </div>
                        ) : (
                          <span
                            className="cursor-text"
                            title="Tıklayıp hesap kodunu düzenleyin (faturanın tüm satırlarına uygulanır)"
                          >
                            <AccountCodeText
                              value={invoice.accountCodeSummary}
                              approved={isApproved}
                              showSuggestionTag
                            />
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* Sayfalama */}
          {pageData && pageData.totalPages > 1 && (
            <div className="mt-4 flex items-center justify-between text-sm text-navy-600">
              <button
                className="btn-secondary !px-3 !py-1.5 text-xs"
                disabled={page <= 1 || listLoading}
                onClick={() => setPage((p) => Math.max(1, p - 1))}
              >
                Önceki
              </button>
              <span>
                Sayfa {pageData.page} / {pageData.totalPages}
              </span>
              <button
                className="btn-secondary !px-3 !py-1.5 text-xs"
                disabled={page >= pageData.totalPages || listLoading}
                onClick={() => setPage((p) => p + 1)}
              >
                Sonraki
              </button>
            </div>
          )}
        </>
      ) : null}

      {/* Detay çekmecesi */}
      {detailId && (
        <div className="fixed inset-0 z-50 flex justify-end bg-navy-950/50" onClick={closeDetail}>
          <div
            className="h-full w-full max-w-3xl overflow-y-auto bg-white p-6 shadow-2xl"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-start justify-between gap-4">
              <h2 className="text-lg font-bold text-navy-900">Fatura Detayı</h2>
              <button
                onClick={closeDetail}
                className="rounded-md px-2.5 py-1 text-sm font-semibold text-navy-500 hover:bg-navy-50"
              >
                Kapat
              </button>
            </div>

            {detailError && <div className="alert-error mt-4">{detailError}</div>}

            {detailLoading ? (
              <div className="mt-6 space-y-3">
                {[1, 2, 3].map((i) => (
                  <div key={i} className="animate-pulse rounded-lg bg-navy-50 p-4">
                    <div className="h-4 w-1/2 rounded bg-navy-100" />
                  </div>
                ))}
              </div>
            ) : detail ? (
              <>
                {/* Başlık bilgileri */}
                <dl className="mt-5 grid grid-cols-2 gap-x-6 gap-y-4 text-sm">
                  <div className="col-span-2 rounded-lg bg-navy-50/80 p-3 border border-navy-100">
                    <dt className="text-xs font-semibold uppercase tracking-wide text-navy-500">
                      Mükellef (Faturanın Ait Olduğu Müşteri)
                    </dt>
                    <dd className="mt-0.5 text-sm font-bold text-navy-950">
                      {clientMap.get(detail.clientId)?.title ?? "—"}
                    </dd>
                    {clientMap.get(detail.clientId)?.taxId && (
                      <span className="font-mono text-xs text-navy-500">
                        VKN/TCKN: {clientMap.get(detail.clientId)?.taxId}
                      </span>
                    )}
                  </div>
                  <div>
                    <dt className="text-xs font-medium uppercase tracking-wide text-navy-400">
                      Satıcı
                    </dt>
                    <dd className="mt-0.5 font-medium text-navy-900">{detail.senderTitle}</dd>
                  </div>
                  <div>
                    <dt className="text-xs font-medium uppercase tracking-wide text-navy-400">
                      Satıcı VKN / TCKN
                    </dt>
                    <dd className="mt-0.5 font-mono">{detail.senderTaxId}</dd>
                  </div>
                  <div>
                    <dt className="text-xs font-medium uppercase tracking-wide text-navy-400">
                      Fatura No
                    </dt>
                    <dd className="mt-0.5 font-mono">{detail.invoiceNumber}</dd>
                  </div>
                  <div>
                    <dt className="text-xs font-medium uppercase tracking-wide text-navy-400">
                      Tarih
                    </dt>
                    <dd className="mt-0.5">{formatDate(detail.issueDate)}</dd>
                  </div>
                  <div>
                    <dt className="text-xs font-medium uppercase tracking-wide text-navy-400">
                      ETTN (UUID)
                    </dt>
                    <dd className="mt-0.5 break-all font-mono text-xs">{detail.externalUuid}</dd>
                  </div>
                  <div>
                    <dt className="text-xs font-medium uppercase tracking-wide text-navy-400">
                      Durum / Kodlama
                    </dt>
                    <dd className="mt-0.5 flex flex-wrap gap-2">
                      <span
                        className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                          (statusBadges[detail.status] ?? statusBadges.New).className
                        }`}
                      >
                        {(statusBadges[detail.status] ?? statusBadges.New).label}
                      </span>
                      <span
                        className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                          (codingBadges[detail.codingStatus] ?? codingBadges.Uncoded).className
                        }`}
                      >
                        {(codingBadges[detail.codingStatus] ?? codingBadges.Uncoded).label}
                      </span>
                    </dd>
                  </div>
                </dl>

                {/* Tutar özeti */}
                <div className="mt-6 grid grid-cols-3 gap-3">
                  <div className="rounded-lg bg-navy-50 p-3">
                    <p className="text-xs text-navy-500">Matrah</p>
                    <p className="mt-1 font-semibold text-navy-900">
                      {formatMoney(detail.taxExclusiveAmount, detail.currencyCode)}
                    </p>
                  </div>
                  <div className="rounded-lg bg-navy-50 p-3">
                    <p className="text-xs text-navy-500">KDV</p>
                    <p className="mt-1 font-semibold text-navy-900">
                      {formatMoney(detail.taxTotal, detail.currencyCode)}
                    </p>
                  </div>
                  <div className="rounded-lg bg-navy-50 p-3">
                    <p className="text-xs text-navy-500">Ödenecek</p>
                    <p className="mt-1 font-semibold text-navy-900">
                      {formatMoney(detail.payableAmount, detail.currencyCode)}
                    </p>
                  </div>
                </div>

                {/* Kodlama aksiyonları */}
                {codingError && <div className="alert-error mt-6">{codingError}</div>}
                {approveSuccess && (
                  <div className="mt-6 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
                    Onaylandı — sistem bu eşlemeyi öğrendi ✓
                  </div>
                )}

                {/* Kalem tablosu */}
                <div className="mt-8 flex flex-wrap items-center justify-between gap-3">
                  <h3 className="text-sm font-semibold text-navy-900">Kalemler</h3>
                  <div className="flex gap-2">
                    {detailUncoded && (
                      <button
                        onClick={handleCodeInvoice}
                        className="btn-secondary flex items-center gap-2 !px-3 !py-1.5 text-xs"
                        disabled={coding || approving}
                      >
                        {coding && (
                          <span className="h-3 w-3 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                        )}
                        {coding ? "Kodlanıyor..." : "Kodla"}
                      </button>
                    )}
                    {/* Onaylı faturada tekil "Onayı Kaldır": kodlar korunur, durum geri döner. */}
                    {detailApproved && (
                      <button
                        onClick={handleUnapprove}
                        className="btn-secondary flex items-center gap-2 !px-3 !py-1.5 text-xs !text-amber-700"
                        disabled={coding || approving}
                        title="Faturayı Onay Bekliyor durumuna döndürür; satır kodları silinmez, motor hafızası korunur."
                      >
                        {coding && (
                          <span className="h-3 w-3 animate-spin rounded-full border-2 border-amber-500 border-t-transparent" />
                        )}
                        {coding ? "Kaldırılıyor..." : "Onayı Kaldır"}
                      </button>
                    )}
                    {!detailApproved && detail.lines.length > 0 && (
                      <button
                        onClick={handleApprove}
                        className="btn-primary flex items-center gap-2 !px-3 !py-1.5 text-xs"
                        disabled={approving || coding}
                        title="Satırlardaki (düzeltilmiş) kodları onaylar; sistem bu eşlemeleri öğrenir."
                      >
                        {approving && (
                          <span className="h-3 w-3 animate-spin rounded-full border-2 border-white border-t-transparent" />
                        )}
                        {approving ? "Onaylanıyor..." : "Onayla"}
                      </button>
                    )}
                  </div>
                </div>

                {detail.lines.length === 0 ? (
                  <p className="mt-2 text-sm text-navy-500">
                    Bu fatura için kalem detayı bulunmuyor.
                  </p>
                ) : (
                  <div className="mt-2 overflow-x-auto rounded-lg border border-navy-100">
                    <table className="w-full min-w-[760px] text-left text-sm">
                      <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
                        <tr>
                          <th className="px-3 py-2">#</th>
                          <th className="px-3 py-2">Kalem</th>
                          <th className="px-3 py-2 text-right">KDV %</th>
                          <th className="px-3 py-2 text-right">Tutar</th>
                          <th className="px-3 py-2">Hesap Kodu</th>
                          {/* Cari kod kullanıcıya gösterilmez — motor arka planda atar,
                              Luca export'una otomatik girer (kullanıcı isteği). */}
                          <th className="px-3 py-2">Güven</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-navy-50">
                        {detail.lines.map((line) => {
                          const edit =
                            lineEdits[line.id] ??
                            ({
                              accountCode: "",
                              cariCode: "",
                              withholdingCode: "",
                              withholdingRate: "",
                              isKkeg: false,
                              kkegAmount: "",
                              kkegReason: "",
                            } as LineEdit);
                          const isOpen = extraOpen[line.id] ?? false;
                          const hasWithholding = detailApproved
                            ? Boolean(line.withholdingCode)
                            : Boolean(edit.withholdingCode.trim());
                          const hasKkeg = detailApproved ? line.isKkeg : edit.isKkeg;
                          return (
                            <Fragment key={line.id}>
                              <tr>
                                <td className="px-3 py-2 text-navy-400">{line.lineNumber}</td>
                                <td className="px-3 py-2 text-navy-900">
                                  {line.itemName}
                                  <span className="block text-xs text-navy-400">
                                    {line.quantity.toLocaleString("tr-TR")} {line.unitCode} ×{" "}
                                    {formatMoney(line.unitPrice, detail.currencyCode)}
                                  </span>
                                  {/* Faz 8: rozetler + panel aç/kapa */}
                                  <span className="mt-1 flex flex-wrap items-center gap-1.5">
                                    {hasWithholding && (
                                      <span
                                        className="rounded-full bg-purple-100 px-2 py-0.5 text-[10px] font-semibold text-purple-700"
                                        title="Tevkifatlı satır — KDV2 (sorumlu sıfatıyla) beyanına girer"
                                      >
                                        Tevkifat {edit.withholdingCode || line.withholdingCode}
                                        {(edit.withholdingRate || line.withholdingRate) &&
                                          ` (${edit.withholdingRate || line.withholdingRate})`}
                                      </span>
                                    )}
                                    {hasKkeg && (
                                      <span
                                        className="rounded-full bg-orange-100 px-2 py-0.5 text-[10px] font-semibold text-orange-700"
                                        title={line.kkegReason ?? "Kanunen kabul edilmeyen gider"}
                                      >
                                        KKEG
                                      </span>
                                    )}
                                    <button
                                      type="button"
                                      className="text-[10px] font-semibold text-navy-400 underline hover:text-navy-600"
                                      onClick={() =>
                                        setExtraOpen((prev) => ({ ...prev, [line.id]: !isOpen }))
                                      }
                                    >
                                      {isOpen ? "Tevkifat/KKEG gizle" : "Tevkifat/KKEG"}
                                    </button>
                                  </span>
                                </td>
                                <td className="px-3 py-2 text-right">%{line.vatRate}</td>
                                <td className="px-3 py-2 text-right font-medium">
                                  {formatMoney(line.lineAmount, detail.currencyCode)}
                                </td>
                                <td className="px-3 py-2">
                                  {detailApproved ? (
                                    <span className="font-mono text-xs text-navy-900">
                                      {line.accountCode ?? "—"}
                                    </span>
                                  ) : (
                                    /* Autocomplete: "7" → 7 ile başlayan kodlar, "kira" → adı
                                       kira içeren hesaplar; serbest metin girişi mümkün. */
                                    <AccountCodeAutocomplete
                                      clientId={detail.clientId}
                                      value={edit.accountCode}
                                      onChange={(value) =>
                                        setLineEdits((prev) => ({
                                          ...prev,
                                          [line.id]: { ...edit, accountCode: value },
                                        }))
                                      }
                                      placeholder="770.01"
                                    />
                                  )}
                                </td>
                                {/* Cari kod hücresi kaldırıldı: motor cariyi arka planda
                                    atamaya devam eder (lineEdits içinde İÇSEL taşınır),
                                    Luca export'una otomatik girer — UI'da gösterilmez. */}
                                <td className="px-3 py-2">
                                  <ConfidenceBadge confidence={line.confidence} />
                                </td>
                              </tr>

                              {/* Faz 8 — B2/B3: tevkifat + KKEG paneli (onaylı faturada salt okunur) */}
                              {isOpen && (
                                <tr className="bg-navy-50/40">
                                  <td colSpan={6} className="px-3 py-3">
                                    {detailApproved ? (
                                      <div className="flex flex-wrap gap-6 text-xs text-navy-700">
                                        <span>
                                          <strong>Tevkifat:</strong>{" "}
                                          {line.withholdingCode
                                            ? `${line.withholdingCode} (${line.withholdingRate ?? "—"}) — tevkif edilen KDV ${formatMoney(line.withholdingVatAmount ?? 0, detail.currencyCode)}`
                                            : "yok"}
                                        </span>
                                        <span>
                                          <strong>KKEG:</strong>{" "}
                                          {line.isKkeg
                                            ? `${formatMoney(line.kkegAmount ?? 0, detail.currencyCode)} — ${line.kkegReason ?? ""}`
                                            : "yok"}
                                        </span>
                                      </div>
                                    ) : (
                                      <div className="flex flex-wrap items-end gap-4">
                                        <div>
                                          <label className="mb-1 block text-[10px] font-semibold uppercase tracking-wide text-purple-700">
                                            Tevkifat Kodu
                                          </label>
                                          {withholdingRules.length > 0 ? (
                                            <select
                                              className="input !w-auto !px-2 !py-1 text-xs"
                                              value={edit.withholdingCode}
                                              onChange={(e) => {
                                                const code = e.target.value;
                                                const rule = withholdingRules.find((r) => r.code === code);
                                                setLineEdits((prev) => ({
                                                  ...prev,
                                                  [line.id]: {
                                                    ...edit,
                                                    withholdingCode: code,
                                                    withholdingRate: code
                                                      ? rule?.fraction ?? edit.withholdingRate
                                                      : "",
                                                  },
                                                }));
                                              }}
                                            >
                                              <option value="">Tevkifat yok</option>
                                              {withholdingRules.map((r) => (
                                                <option key={r.code} value={r.code}>
                                                  {r.code} — {r.description.length > 50 ? r.description.slice(0, 50) + "…" : r.description}
                                                </option>
                                              ))}
                                            </select>
                                          ) : (
                                            <input
                                              type="text"
                                              className="input w-20 !px-2 !py-1 font-mono text-xs"
                                              placeholder="601"
                                              value={edit.withholdingCode}
                                              onChange={(e) =>
                                                setLineEdits((prev) => ({
                                                  ...prev,
                                                  [line.id]: { ...edit, withholdingCode: e.target.value },
                                                }))
                                              }
                                            />
                                          )}
                                        </div>
                                        <div>
                                          <label className="mb-1 block text-[10px] font-semibold uppercase tracking-wide text-purple-700">
                                            Oran
                                          </label>
                                          <input
                                            type="text"
                                            className="input w-20 !px-2 !py-1 font-mono text-xs"
                                            placeholder="9/10"
                                            value={edit.withholdingRate}
                                            disabled={!edit.withholdingCode.trim()}
                                            onChange={(e) =>
                                              setLineEdits((prev) => ({
                                                ...prev,
                                                [line.id]: { ...edit, withholdingRate: e.target.value },
                                              }))
                                            }
                                          />
                                        </div>
                                        <div className="text-xs text-navy-500">
                                          Tevkif KDV ≈{" "}
                                          <strong>
                                            {(() => {
                                              const m = edit.withholdingRate.trim().match(/^(\d{1,2})\/(\d{1,2})$/);
                                              if (!edit.withholdingCode.trim() || !m) return "—";
                                              const value = (line.vatAmount * Number(m[1])) / Number(m[2]);
                                              return formatMoney(Math.round(value * 100) / 100, detail.currencyCode);
                                            })()}
                                          </strong>
                                        </div>

                                        <div className="ml-2 border-l border-navy-100 pl-4">
                                          <label className="flex items-center gap-2 text-xs font-semibold text-orange-700">
                                            <input
                                              type="checkbox"
                                              className="h-4 w-4 rounded border-navy-300"
                                              checked={edit.isKkeg}
                                              onChange={(e) =>
                                                setLineEdits((prev) => ({
                                                  ...prev,
                                                  [line.id]: {
                                                    ...edit,
                                                    isKkeg: e.target.checked,
                                                    kkegAmount:
                                                      e.target.checked && !edit.kkegAmount
                                                        ? String(line.kkegAmount ?? line.lineAmount).replace(".", ",")
                                                        : edit.kkegAmount,
                                                  },
                                                }))
                                              }
                                            />
                                            KKEG
                                          </label>
                                        </div>
                                        {edit.isKkeg && (
                                          <>
                                            <div>
                                              <label className="mb-1 block text-[10px] font-semibold uppercase tracking-wide text-orange-700">
                                                KKEG Tutarı (KDV hariç)
                                              </label>
                                              <input
                                                type="text"
                                                className="input w-28 !px-2 !py-1 text-xs"
                                                placeholder="0,00"
                                                value={edit.kkegAmount}
                                                onChange={(e) =>
                                                  setLineEdits((prev) => ({
                                                    ...prev,
                                                    [line.id]: { ...edit, kkegAmount: e.target.value },
                                                  }))
                                                }
                                              />
                                            </div>
                                            <div className="min-w-[200px] flex-1">
                                              <label className="mb-1 block text-[10px] font-semibold uppercase tracking-wide text-orange-700">
                                                Gerekçe
                                              </label>
                                              <input
                                                type="text"
                                                className="input !px-2 !py-1 text-xs"
                                                placeholder="Ör. Binek oto gideri %30 kısıt"
                                                value={edit.kkegReason}
                                                maxLength={300}
                                                onChange={(e) =>
                                                  setLineEdits((prev) => ({
                                                    ...prev,
                                                    [line.id]: { ...edit, kkegReason: e.target.value },
                                                  }))
                                                }
                                              />
                                            </div>
                                          </>
                                        )}
                                      </div>
                                    )}
                                  </td>
                                </tr>
                              )}
                            </Fragment>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}

                {detailUncoded && detail.lines.length > 0 && (
                  <p className="mt-3 text-xs text-navy-500">
                    Bu fatura henüz kodlanmadı. &quot;Kodla&quot; ile motor önerilerini alın ya da
                    kodları elle girip &quot;Onayla&quot;yın.
                  </p>
                )}
              </>
            ) : null}
          </div>
        </div>
      )}
    </div>
  );
}
