"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import {
  ACCOUNT_CODE_REGEX,
  api,
  approveReceipt,
  ApproveReceiptRequest,
  bulkApproveReceipts,
  bulkDeleteReceipts,
  bulkUnapproveReceipts,
  ClientResponse,
  deleteReceipt,
  fetchReceiptFileUrl,
  getKkegParameters,
  getReceipt,
  getReceiptQuota,
  listReceipts,
  ReceiptDocumentKind,
  ReceiptPage,
  ReceiptQuota,
  ReceiptResponse,
  ReceiptStatusCode,
  ReceiptUploadResult,
  ReceiptVatBreakdownRow,
  rejectReceipt,
  unapproveReceipt,
  uploadReceipts,
} from "@/lib/api";
import { resizeImagesForUpload } from "@/lib/imageResize";
import LucaExportStrip from "@/components/LucaExportStrip";
import AccountCodeAutocomplete from "@/components/AccountCodeAutocomplete";
import DataImportModal from "@/components/DataImportModal";

const PAGE_SIZE = 20;
const TAX_ID_REGEX = /^\d{10}(\d)?$/;

/**
 * Faz 14/16: listede "İşleniyor" fiş varken otomatik yenileme aralığı — işlenen fiş varken
 * 2 sn'ye indirildi (A2 — OCR hızı); işleme bitince yoklama tamamen durur (eski davranış).
 */
const PROCESSING_POLL_MS = 2000;

/** Faz 15: KKEG kısıt oranı — backend'den okunamazsa bu varsayılan kullanılır (%30). */
const DEFAULT_KKEG_RATE = 30;

/**
 * Faz 15: akaryakıt fişi tespiti (satıcı adı üzerinden) — KKEG gerekçesi otomatiği için.
 * Backend'deki KkegAnalyzer.FuelKeywords ile uyumlu set; karşılaştırma tr-TR küçük harfle yapılır.
 */
const FUEL_KEYWORDS = ["motorin", "benzin", "lpg", "akaryakıt", "petrol"];

function isFuelText(text: string): boolean {
  const normalized = text.toLocaleLowerCase("tr-TR");
  return FUEL_KEYWORDS.some((k) => normalized.includes(k));
}

const statusOptions: { value: string; label: string }[] = [
  { value: "", label: "Tüm durumlar" },
  { value: "Processing", label: "İşleniyor" },
  { value: "NeedsReview", label: "Onay Bekliyor" },
  { value: "Approved", label: "Onaylandı" },
  { value: "Rejected", label: "Reddedildi" },
];

const statusBadges: Record<ReceiptStatusCode, { label: string; className: string }> = {
  Uploaded: { label: "Yüklendi", className: "bg-blue-100 text-blue-700" },
  // Faz 14: kuyrukta okunan fiş — mavi rozet + spinner (StatusBadge bileşeni ekler).
  Processing: { label: "İşleniyor", className: "bg-blue-100 text-blue-700" },
  NeedsReview: { label: "Onay Bekliyor", className: "bg-amber-100 text-amber-700" },
  Approved: { label: "Onaylandı", className: "bg-emerald-100 text-emerald-800" },
  Rejected: { label: "Reddedildi", className: "bg-red-100 text-red-700" },
};

/** Durum rozeti: "İşleniyor" durumunda dönen mini spinner ile (arka plan OCR hissi). */
function StatusBadge({ status }: { status: ReceiptStatusCode }) {
  const badge = statusBadges[status] ?? statusBadges.NeedsReview;
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-semibold ${badge.className}`}
    >
      {status === "Processing" && (
        <span className="h-2.5 w-2.5 animate-spin rounded-full border-2 border-blue-600 border-t-transparent" />
      )}
      {badge.label}
    </span>
  );
}

function formatDate(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "—" : d.toLocaleDateString("tr-TR");
}

function formatMoney(value: number | null): string {
  if (value === null || value === undefined) return "—";
  return new Intl.NumberFormat("tr-TR", {
    style: "currency",
    currency: "TRY",
    minimumFractionDigits: 2,
  }).format(value);
}

function toDateInputValue(iso: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  return isNaN(d.getTime()) ? "" : d.toISOString().slice(0, 10);
}

function isImageFile(fileName: string): boolean {
  return /\.(jpe?g|png)$/i.test(fileName);
}

/** Güven yüzdesi rozeti: yeşil ≥ %85, sarı ≥ %60, altı kırmızı (faturalar sayfasıyla aynı eşikler). */
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

/**
 * Faz 13: plaka eşleşme rozeti — yeşil "Kayıtlı araç", kırmızı "Kayıtlı değil",
 * gri "Okunamadı" (plaka hiç okunmadıysa yalnız detayda gösterilir).
 */
function PlateMatchBadge({ receipt }: { receipt: ReceiptResponse }) {
  if (!receipt.plateNumber) {
    return (
      <span className="rounded-full bg-navy-100 px-2 py-0.5 text-xs font-semibold text-navy-500">
        Okunamadı
      </span>
    );
  }
  return receipt.plateMatch === "Eslesti" ? (
    <span
      className="rounded-full bg-green-100 px-2 py-0.5 text-xs font-semibold text-green-700"
      title="Plaka mükellefin kayıtlı araçlarından biriyle eşleşti"
    >
      Kayıtlı araç
    </span>
  ) : (
    <span
      className="rounded-full bg-red-100 px-2 py-0.5 text-xs font-semibold text-red-700"
      title="Plaka mükellefin kayıtlı araçlarıyla eşleşmiyor — aracı Mükellefler sayfasından ekleyin"
    >
      Kayıtlı değil
    </span>
  );
}

/**
 * Küçük önizleme: görsel dosyalar Authorization'lı blob olarak çekilip object URL ile
 * gösterilir (<img src> token taşıyamaz); PDF/HEIC için tür etiketi gösterilir.
 */
function ReceiptThumb({ receipt }: { receipt: ReceiptResponse }) {
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!isImageFile(receipt.fileName)) return;
    let objectUrl: string | null = null;
    let cancelled = false;
    (async () => {
      try {
        const { url: u } = await fetchReceiptFileUrl(receipt.id);
        if (cancelled) {
          URL.revokeObjectURL(u);
          return;
        }
        objectUrl = u;
        setUrl(u);
      } catch {
        // önizleme yüklenemezse tür etiketi gösterilir; liste akışı bozulmaz
      }
    })();
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [receipt.id, receipt.fileName]);

  if (url) {
    // eslint-disable-next-line @next/next/no-img-element
    return <img src={url} alt={receipt.fileName} className="h-10 w-10 rounded-md border border-navy-100 object-cover" />;
  }

  const ext = receipt.fileName.split(".").pop()?.toUpperCase() ?? "DOSYA";
  return (
    <span className="flex h-10 w-10 items-center justify-center rounded-md border border-navy-100 bg-navy-50 text-[10px] font-bold text-navy-500">
      {ext}
    </span>
  );
}

/** Faz 16: düzenlenebilir KDV kırılım satırı (girdi alanları metin tutulur). */
interface BreakdownEditRow {
  vatRate: string;
  netAmount: string;
  vatAmount: string;
}

interface ReceiptEdit {
  vendorName: string;
  vendorTaxId: string;
  /** Fiş/belge numarası (Z no) — Luca şablonunda Evrak No kolonuna gider. */
  documentNumber: string;
  receiptDate: string;
  totalAmount: string;
  vatRate: string;
  vatAmount: string;
  netAmount: string;
  /** Faz 16: toplam iskonto/indirim (boş = yok). */
  discountTotal: string;
  /** Faz 16: oran bazlı KDV kırılımı (düzenlenebilir; satır ekle/sil). */
  vatBreakdown: BreakdownEditRow[];
  accountCode: string;
  cariCode: string;
  overrideEligibility: boolean;
  // Faz 8 — B3: KKEG düzeltmesi
  isKkeg: boolean;
  kkegAmount: string;
  kkegReason: string;
}

/** Türkçe ondalık (virgül) destekli sayı çözümleme; boş/geçersiz → null. */
function parseAmount(value: string): number | null {
  const t = value.trim().replace(",", ".");
  if (!t) return null;
  const n = parseFloat(t);
  return isNaN(n) ? null : n;
}

/** Faz 16: kırılım satırlarının Σmatrah / ΣKDV toplamları (geçersiz alanlar 0 sayılır). */
function breakdownTotals(rows: BreakdownEditRow[]): { net: number; vat: number } {
  let net = 0;
  let vat = 0;
  for (const row of rows) {
    net += parseAmount(row.netAmount) ?? 0;
    vat += parseAmount(row.vatAmount) ?? 0;
  }
  return { net: Math.round(net * 100) / 100, vat: Math.round(vat * 100) / 100 };
}

export default function ReceiptsPage() {
  // Mükellef seçimi
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientsLoading, setClientsLoading] = useState(true);
  const [clientsError, setClientsError] = useState<string | null>(null);
  const [selectedClientId, setSelectedClientId] = useState<string>("");

  // Ortak "Veri İçe Aktar" modalı (mükellef seçici modal içindedir).
  const [importOpen, setImportOpen] = useState(false);

  // Yükleme
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  // Faz 16: yüklenen belgenin türü — Gider Fişi (varsayılan) veya Z Raporu (gelir).
  const [uploadKind, setUploadKind] = useState<ReceiptDocumentKind>("Fis");
  const [dragActive, setDragActive] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState(0);
  const [uploadResult, setUploadResult] = useState<ReceiptUploadResult | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);

  // Liste
  const [pageData, setPageData] = useState<ReceiptPage | null>(null);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState("");
  const [page, setPage] = useState(1);

  // Satır seçimi + toplu işlemler (toplu onay / onay kaldırma / toplu silme — faturalar sayfasıyla aynı kalıp)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [bulkBusy, setBulkBusy] = useState<"approve" | "unapprove" | "delete" | null>(null);
  const [bulkResult, setBulkResult] = useState<string | null>(null);
  const [bulkError, setBulkError] = useState<string | null>(null);

  // Faz 14 (B): aylık kota göstergesi ("Bu ay: 12/200 fiş"; sınırsızsa gizli).
  const [quotaInfo, setQuotaInfo] = useState<ReceiptQuota | null>(null);

  // Detay çekmecesi
  const [detailId, setDetailId] = useState<string | null>(null);
  const [detail, setDetail] = useState<ReceiptResponse | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [edit, setEdit] = useState<ReceiptEdit | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [approving, setApproving] = useState(false);
  const [approveSuccess, setApproveSuccess] = useState(false);
  const [rejectMode, setRejectMode] = useState(false);
  const [rejectReason, setRejectReason] = useState("");
  const [rejecting, setRejecting] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [unapproving, setUnapproving] = useState(false);

  // Faz 15: KKEG kısıt oranı — bir kez çekilir; hata olursa %30 varsayılanıyla akış sürer.
  const [kkegRate, setKkegRate] = useState(DEFAULT_KKEG_RATE);

  // Faz 15: KKEG kutusu işaretlenince OTOMATİK doldurulan değerler (kutu kaldırılınca
  // yalnızca hâlâ bu değerlere eşit olan alanlar temizlenir — elle yazılana dokunulmaz).
  const autoKkegRef = useRef<{ amount: string | null; reason: string | null }>({
    amount: null,
    reason: null,
  });

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const params = await getKkegParameters();
        if (!cancelled && params.rate > 0) setKkegRate(params.rate);
      } catch {
        // oran ucu erişilemezse %30 varsayılanı kullanılır; akış engellenmez
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

  const loadReceipts = useCallback(
    async (
      opts: { clientId: string; status: string; page: number },
      options?: { silent?: boolean }
    ) => {
      // Faz 14: silent=true (5 sn'lik "İşleniyor" yoklaması) iskelet/hata bandı göstermez —
      // liste sessizce tazelenir; geçici yoklama hatası bir sonraki turda kendini toparlar.
      if (!options?.silent) {
        setListLoading(true);
        setListError(null);
      }
      try {
        const data = await listReceipts({
          clientId: opts.clientId || undefined,
          status: opts.status || undefined,
          page: opts.page,
          pageSize: PAGE_SIZE,
        });
        setPageData(data);
        // Liste elle yenilendiğinde (sayfa/filtre değişimi dahil) seçim sıfırlanır;
        // sessiz yoklama (5 sn "İşleniyor" turu) mevcut seçimi KORUR.
        if (!options?.silent) setSelectedIds(new Set());
        if (options?.silent) setListError(null);
      } catch (err) {
        if (!options?.silent)
          setListError(err instanceof Error ? err.message : "Fişler yüklenirken bir hata oluştu.");
      } finally {
        if (!options?.silent) setListLoading(false);
      }
    },
    []
  );

  useEffect(() => {
    loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
  }, [selectedClientId, statusFilter, page, loadReceipts]);

  // Faz 14 (B): kota göstergesi — sayfa açılışında ve her yükleme sonrasında tazelenir.
  const refreshQuota = useCallback(async () => {
    try {
      setQuotaInfo(await getReceiptQuota());
    } catch {
      // kota göstergesi süs niteliğindedir; hatası sayfayı bozmaz (backend yine de denetler)
    }
  }, []);

  useEffect(() => {
    refreshQuota();
  }, [refreshQuota]);

  // Faz 14 (A): listede "İşleniyor" fiş varken 5 sn'de bir sessiz yenile; hepsi bitince durur.
  const hasProcessing = (pageData?.items ?? []).some((r) => r.status === "Processing");
  useEffect(() => {
    if (!hasProcessing) return;
    const timer = setInterval(() => {
      loadReceipts({ clientId: selectedClientId, status: statusFilter, page }, { silent: true });
    }, PROCESSING_POLL_MS);
    return () => clearInterval(timer);
  }, [hasProcessing, selectedClientId, statusFilter, page, loadReceipts]);

  // ---------- Yükleme ----------

  async function handleFiles(fileList: FileList | File[]) {
    setUploadError(null);
    setUploadResult(null);

    if (!selectedClientId) {
      setUploadError("Fiş yüklemek için önce bir mükellef seçin.");
      return;
    }

    const files = Array.from(fileList);
    if (files.length === 0) return;

    setUploading(true);
    setUploadProgress(0);
    try {
      // Faz 12: görseller yüklemeden önce tarayıcıda SESSİZCE küçültülür (uzun kenar 1600 px,
      // JPEG q0.75). PDF/HEIC olduğu gibi geçer; küçültme hatası yüklemeyi asla engellemez.
      const prepared = await resizeImagesForUpload(files);
      const result = await uploadReceipts(selectedClientId, prepared, setUploadProgress, uploadKind);
      setUploadResult(result);
      setPage(1);
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page: 1 });
      await refreshQuota(); // Faz 14 (B): "Bu ay: X/Y fiş" göstergesi güncellenir
    } catch (err) {
      setUploadError(err instanceof Error ? err.message : "Yükleme sırasında bir hata oluştu.");
    } finally {
      setUploading(false);
    }
  }

  function handleDrop(e: React.DragEvent<HTMLDivElement>) {
    e.preventDefault();
    setDragActive(false);
    if (uploading) return;
    handleFiles(e.dataTransfer.files);
  }

  // ---------- Toplu seçim / toplu işlemler (faturalar sayfasıyla aynı kalıp) ----------

  /** Tek satırın seçimini değiştirir (checkbox). */
  function toggleSelected(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  /** Başlıktaki checkbox: yalnız görünen sayfadaki fişlerin tümünü seç/kaldır. */
  function toggleSelectAllVisible(visible: ReceiptResponse[], allSelected: boolean) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      for (const receipt of visible) {
        if (allSelected) next.delete(receipt.id);
        else next.add(receipt.id);
      }
      return next;
    });
  }

  /**
   * "Seçilenleri Onayla": onay penceresi → bulk-approve → Türkçe özet bandı → liste yenile.
   * Sunucu yalnız "Onay Bekliyor" durumundakileri onaylar; uygun olmayanlar sessizce atlanır
   * ve özet mesajda sayısı bildirilir.
   */
  async function handleBulkApprove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    const confirmed = window.confirm(
      `${ids.length} fiş okunan/önerilen bilgilerle onaylanacak. Motor bu eşlemeleri öğrenecek. Devam edilsin mi?`
    );
    if (!confirmed) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkBusy("approve");
    try {
      const result = await bulkApproveReceipts(ids);
      setBulkResult(result.message);
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Toplu onay sırasında bir hata oluştu.");
    } finally {
      setBulkBusy(null);
    }
  }

  /**
   * "Onayı Kaldır": seçilenlerden ONAYLI fişler Onay Bekliyor'a döndürülür (alanlar ve
   * motorun öğrenme hafızası korunur); onaylı olmayanlar sunucuda atlanır (skipped).
   */
  async function handleBulkUnapprove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    const confirmed = window.confirm(
      `${ids.length} fişten onaylı olanların onayı kaldırılacak (alanlar silinmez, motor hafızası korunur). Devam edilsin mi?`
    );
    if (!confirmed) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkBusy("unapprove");
    try {
      const result = await bulkUnapproveReceipts(ids);
      setBulkResult(result.message);
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Onay kaldırma sırasında bir hata oluştu.");
    } finally {
      setBulkBusy(null);
    }
  }

  /** "Seçilenleri Sil": onay penceresi → bulk-delete → Türkçe özet bandı → liste yenile. */
  async function handleBulkDelete() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    const confirmed = window.confirm(`${ids.length} fiş silinecek. Dosyaları da silinecek. Emin misiniz?`);
    if (!confirmed) return;

    setBulkError(null);
    setBulkResult(null);
    setBulkBusy("delete");
    try {
      const result = await bulkDeleteReceipts(ids);
      setBulkResult(result.message);
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
    } catch (err) {
      setBulkError(err instanceof Error ? err.message : "Toplu silme sırasında bir hata oluştu.");
    } finally {
      setBulkBusy(null);
    }
  }

  // ---------- Detay çekmecesi ----------

  const applyDetail = useCallback((data: ReceiptResponse) => {
    setDetail(data);
    // Faz 15: yeni detay yüklendi — önceki otomatik doldurma izleri sıfırlanır.
    autoKkegRef.current = { amount: null, reason: null };
    setEdit({
      vendorName: data.vendorName ?? "",
      vendorTaxId: data.vendorTaxId ?? "",
      documentNumber: data.documentNumber ?? "",
      receiptDate: toDateInputValue(data.receiptDate),
      totalAmount: data.totalAmount !== null ? String(data.totalAmount) : "",
      vatRate: data.vatRate !== null ? String(data.vatRate) : "",
      vatAmount: data.vatAmount !== null ? String(data.vatAmount) : "",
      netAmount: data.netAmount !== null ? String(data.netAmount) : "",
      discountTotal: data.discountTotal !== null ? String(data.discountTotal) : "",
      vatBreakdown: (data.vatBreakdown ?? []).map((b) => ({
        vatRate: String(b.vatRate),
        netAmount: String(b.netAmount),
        vatAmount: String(b.vatAmount),
      })),
      accountCode: data.accountCode ?? data.suggestedAccountCode ?? "",
      cariCode: data.cariCode ?? "",
      overrideEligibility: false,
      isKkeg: data.isKkeg,
      kkegAmount: data.kkegAmount !== null ? String(data.kkegAmount) : "",
      kkegReason: data.kkegReason ?? "",
    });
  }, []);

  const openDetailById = useCallback(
    async (id: string) => {
      setDetailId(id);
      setDetail(null);
      setDetailError(null);
      setActionError(null);
      setApproveSuccess(false);
      setRejectMode(false);
      setRejectReason("");
      setPreviewUrl(null);
      setDetailLoading(true);
      try {
        const data = await getReceipt(id);
        applyDetail(data);
        // Dosya önizlemesi ayrı çekilir; hatası detay akışını bozmaz.
        try {
          const { url } = await fetchReceiptFileUrl(id);
          setPreviewUrl(url);
        } catch {
          setPreviewUrl(null);
        }
      } catch (err) {
        setDetailError(err instanceof Error ? err.message : "Fiş detayı yüklenirken bir hata oluştu.");
      } finally {
        setDetailLoading(false);
      }
    },
    [applyDetail]
  );

  const closeDetail = useCallback(() => {
    setDetailId(null);
    setDetail(null);
    setDetailError(null);
    setActionError(null);
    setApproveSuccess(false);
    setRejectMode(false);
    setRejectReason("");
    setEdit(null);
    setPreviewUrl((prev) => {
      if (prev) URL.revokeObjectURL(prev);
      return null;
    });
  }, []);

  // Sayfadan ayrılırken object URL sızıntısı bırakma (ref, güncel URL'yi taşır).
  const previewUrlRef = useRef<string | null>(null);
  useEffect(() => {
    previewUrlRef.current = previewUrl;
  }, [previewUrl]);
  useEffect(() => {
    return () => {
      if (previewUrlRef.current) URL.revokeObjectURL(previewUrlRef.current);
    };
  }, []);

  /**
   * Faz 15: KKEG kutusu işaretlenince tutar + gerekçe OTOMATİK hesaplanıp doldurulur
   * (yalnızca alan boşsa — SMMM elle hesaplamaz, isterse değeri değiştirebilir):
   *  - Tutar = matrah × kısıt oranı (backend parametresi; varsayılan %30), 2 ondalık.
   *    Matrah boşsa toplam tutar esas alınır (geri düşüş).
   *  - Gerekçe: satıcı adı akaryakıt işareti taşıyorsa "Binek araç (plaka) akaryakıt gideri
   *    — %30 KKEG"; değilse "KKEG — %30 gider kısıtı".
   * Kutu kaldırılınca YALNIZCA hâlâ otomatik doldurulan değere eşit alanlar temizlenir
   * (elle yazılmış/düzeltilmiş değere dokunulmaz).
   */
  function handleKkegToggle(checked: boolean) {
    setEdit((p) => {
      if (!p) return p;

      if (!checked) {
        const auto = autoKkegRef.current;
        const next = { ...p, isKkeg: false };
        if (auto.amount !== null && p.kkegAmount === auto.amount) next.kkegAmount = "";
        if (auto.reason !== null && p.kkegReason === auto.reason) next.kkegReason = "";
        autoKkegRef.current = { amount: null, reason: null };
        return next;
      }

      let kkegAmount = p.kkegAmount;
      let kkegReason = p.kkegReason;

      if (!kkegAmount.trim()) {
        const base = parseFloat((p.netAmount || p.totalAmount).replace(",", "."));
        if (!isNaN(base) && base > 0) {
          kkegAmount = ((base * kkegRate) / 100).toFixed(2);
          autoKkegRef.current.amount = kkegAmount;
        }
      }

      if (!kkegReason.trim()) {
        const plate = detail?.plateNumber ?? null;
        kkegReason = isFuelText(`${p.vendorName} ${detail?.vendorName ?? ""}`)
          ? `Binek araç${plate ? ` (${plate})` : ""} akaryakıt gideri — %${kkegRate} KKEG`
          : `KKEG — %${kkegRate} gider kısıtı`;
        autoKkegRef.current.reason = kkegReason;
      }

      return { ...p, isKkeg: true, kkegAmount, kkegReason };
    });
  }

  // ---------- Faz 16: KDV kırılımı satır düzenleme (ekle / sil / alan değiştir) ----------
  // Satır değiştikçe Matrah ve KDV alanları kırılım toplamlarına otomatik eşitlenir
  // (tekil alanlar kırılımın türetilmiş görünümüdür; uyuşmazlık uyarısı toplam üzerinden verilir).
  function updateBreakdown(mutate: (rows: BreakdownEditRow[]) => BreakdownEditRow[]) {
    setEdit((p) => {
      if (!p) return p;
      const rows = mutate(p.vatBreakdown.map((r) => ({ ...r })));
      const next = { ...p, vatBreakdown: rows };
      if (rows.length > 0) {
        const totals = breakdownTotals(rows);
        next.netAmount = totals.net.toFixed(2);
        next.vatAmount = totals.vat.toFixed(2);
      }
      return next;
    });
  }

  async function handleApprove() {
    if (!detail || !detailId || !edit) return;
    setActionError(null);
    setApproveSuccess(false);

    // UI tarafı doğrulama (backend aynı kuralları ikinci kez doğrular).
    const accountCode = edit.accountCode.trim();
    if (!accountCode) {
      setActionError("Hesap kodu boş olamaz.");
      return;
    }
    if (!ACCOUNT_CODE_REGEX.test(accountCode)) {
      setActionError("Geçerli bir hesap kodu girin (ör. 770.01).");
      return;
    }
    // Cari kod UI'da gösterilmez/düzenlenmez — motorun atadığı değer İÇSEL olarak aynen geri
    // gönderilir (regex kontrolü kaldırıldı: kullanıcı düzeltemeyeceği alan onayı engellemesin).
    const cariCode = edit.cariCode.trim();
    const vendorTaxId = edit.vendorTaxId.trim();
    if (vendorTaxId && !TAX_ID_REGEX.test(vendorTaxId)) {
      setActionError("Vergi/TC kimlik numarası 10 veya 11 haneli olmalıdır.");
      return;
    }
    if (!edit.receiptDate) {
      setActionError("Belge tarihi boş olamaz.");
      return;
    }
    const totalAmount = parseFloat(edit.totalAmount.replace(",", "."));
    if (!edit.totalAmount || isNaN(totalAmount) || totalAmount <= 0) {
      setActionError("Toplam tutar 0'dan büyük olmalıdır.");
      return;
    }
    if (detail.expenseEligible === false && !edit.overrideEligibility) {
      setActionError(
        `Bu fiş gider gösterilemez: ${detail.ineligibleReason ?? "uygunluk kontrolünden geçmedi."} ` +
          'Yine de onaylamak için "Yine de gider göster" seçeneğini işaretleyin.'
      );
      return;
    }

    const parseOptional = (v: string): number | null => {
      const t = v.trim().replace(",", ".");
      if (!t) return null;
      const n = parseFloat(t);
      return isNaN(n) ? null : n;
    };

    // Faz 8 (B3): KKEG doğrulaması — işaretliyse tutar zorunlu ve pozitif.
    const kkegAmount = edit.isKkeg ? parseOptional(edit.kkegAmount) : null;
    if (edit.isKkeg && (kkegAmount === null || kkegAmount <= 0)) {
      setActionError("KKEG işaretli fiş için geçerli bir KKEG tutarı girin (0'dan büyük).");
      return;
    }

    // Faz 16: KDV kırılımı doğrulaması — tamamen boş satırlar atlanır, dolu satır geçerli olmalı.
    const breakdownRows: ReceiptVatBreakdownRow[] = [];
    for (const row of edit.vatBreakdown) {
      const isEmpty = !row.vatRate.trim() && !row.netAmount.trim() && !row.vatAmount.trim();
      if (isEmpty) continue;
      const rate = parseOptional(row.vatRate);
      const net = parseOptional(row.netAmount) ?? 0;
      const vat = parseOptional(row.vatAmount) ?? 0;
      if (rate === null || rate < 0 || rate > 100) {
        setActionError("KDV kırılımında geçerli bir oran girin (0-100).");
        return;
      }
      if (net < 0 || vat < 0) {
        setActionError("KDV kırılımında matrah ve KDV tutarları negatif olamaz.");
        return;
      }
      breakdownRows.push({ vatRate: rate, netAmount: net, vatAmount: vat });
    }

    // İskonto: alan boşaltıldıysa 0 gönderilir (backend alanı temizler); hiç yoksa dokunulmaz.
    const discountValue = parseOptional(edit.discountTotal);
    if (discountValue !== null && discountValue < 0) {
      setActionError("İskonto tutarı negatif olamaz.");
      return;
    }

    const body: ApproveReceiptRequest = {
      vendorName: edit.vendorName.trim() || null,
      vendorTaxId: vendorTaxId || null,
      documentNumber: edit.documentNumber.trim() || null,
      receiptDate: edit.receiptDate,
      totalAmount,
      vatRate: parseOptional(edit.vatRate),
      vatAmount: parseOptional(edit.vatAmount),
      netAmount: parseOptional(edit.netAmount),
      // Faz 16: iskonto (boşaltıldıysa 0 → backend temizler) + oran bazlı kırılım
      // (satır varsa birebir değiştirilir; boşsa dokunulmaz).
      discountTotal:
        discountValue !== null ? discountValue : detail.discountTotal !== null ? 0 : null,
      vatBreakdown: breakdownRows.length > 0 ? breakdownRows : null,
      accountCode,
      cariCode: cariCode || null,
      overrideEligibility: edit.overrideEligibility,
      isKkeg: edit.isKkeg,
      kkegAmount,
      kkegReason: edit.isKkeg ? (edit.kkegReason.trim() || null) : null,
    };

    setApproving(true);
    try {
      const data = await approveReceipt(detailId, body);
      applyDetail(data);
      setApproveSuccess(true);
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
    } catch (err) {
      setActionError(err instanceof Error ? err.message : "Onay sırasında bir hata oluştu.");
    } finally {
      setApproving(false);
    }
  }

  async function handleReject() {
    if (!detailId) return;
    setActionError(null);
    if (!rejectReason.trim()) {
      setActionError("Red gerekçesi zorunludur.");
      return;
    }
    setRejecting(true);
    try {
      const data = await rejectReceipt(detailId, rejectReason.trim());
      applyDetail(data);
      setRejectMode(false);
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
    } catch (err) {
      setActionError(err instanceof Error ? err.message : "Red sırasında bir hata oluştu.");
    } finally {
      setRejecting(false);
    }
  }

  /**
   * Çekmecedeki "Onayı Kaldır": Approved fişi Onay Bekliyor'a döndürür; alanlar/kodlar ve
   * motorun öğrenme hafızası korunur (SMMM düzeltip yeniden onaylayabilir).
   */
  async function handleUnapprove() {
    if (!detailId) return;
    setActionError(null);
    setApproveSuccess(false);
    setUnapproving(true);
    try {
      const data = await unapproveReceipt(detailId);
      applyDetail(data);
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
    } catch (err) {
      setActionError(
        err instanceof Error ? err.message : "Onay kaldırma sırasında bir hata oluştu."
      );
    } finally {
      setUnapproving(false);
    }
  }

  async function handleDelete() {
    if (!detailId) return;
    if (!window.confirm("Bu fişi silmek istediğinize emin misiniz? Dosya da silinecek.")) return;
    setActionError(null);
    setDeleting(true);
    try {
      await deleteReceipt(detailId);
      closeDetail();
      await loadReceipts({ clientId: selectedClientId, status: statusFilter, page });
    } catch (err) {
      setActionError(err instanceof Error ? err.message : "Silme sırasında bir hata oluştu.");
      setDeleting(false);
    }
  }

  const items = pageData?.items ?? [];
  const detailEditable = detail?.status === "NeedsReview";
  const allVisibleSelected = items.length > 0 && items.every((r) => selectedIds.has(r.id));

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Fişler</h1>
          <p className="mt-1 text-sm text-navy-500">
            Kağıt fiş ve masraf belgelerini yükleyin; OCR arka planda okur, motor gider hesabını önerir.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          {/* Faz 14 (B): küçük aylık kota göstergesi (sınırsız planda gizli) */}
          {quotaInfo && !quotaInfo.unlimited && (
            <span
              className={`rounded-full px-3 py-1 text-xs font-semibold ${
                quotaInfo.used >= quotaInfo.quota
                  ? "bg-red-100 text-red-700"
                  : "bg-navy-100 text-navy-600"
              }`}
              title="Aylık fiş okuma hakkınız (büro geneli, tüm mükellefler dahil)"
            >
              Bu ay: {quotaInfo.used}/{quotaInfo.quota} fiş
            </span>
          )}
          {/* Ortak içe aktarma modalı: mizan/fiş listesi/cari/hesap planı + mükellef listesi + plakalar */}
          <button
            onClick={() => setImportOpen(true)}
            className="btn-secondary"
            title="Excel'den toplu veri yükleyin — mizan, geçmiş fiş listesi, cari şablonu, hesap planı, mükellef listesi ve araç plakaları"
          >
            Veri İçe Aktar
          </button>
        </div>
      </div>

      {importOpen && <DataImportModal onClose={() => setImportOpen(false)} />}

      {clientsError && <div className="alert-error mt-4">{clientsError}</div>}

      {/* Yükleme paneli */}
      <div className="card mt-6">
        <div className="flex flex-wrap items-end gap-4">
          <div>
            <label htmlFor="receiptClient" className="label">
              Mükellef
            </label>
            <select
              id="receiptClient"
              className="input min-w-[240px]"
              value={selectedClientId}
              onChange={(e) => {
                setSelectedClientId(e.target.value);
                setPage(1);
                setUploadResult(null);
                setUploadError(null);
              }}
              disabled={clientsLoading || uploading}
            >
              <option value="">
                {clientsLoading ? "Yükleniyor..." : "Tüm mükellefler"}
              </option>
              {clients.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </select>
          </div>
          {/* Faz 16: belge türü seçimi — Z Raporu seçilirse OCR, Z raporu prompt'uyla okur. */}
          <div>
            <span className="label">Belge türü</span>
            <div className="flex items-center gap-4 rounded-lg border border-navy-200 bg-white px-3 py-2">
              <label className="flex cursor-pointer items-center gap-1.5 text-sm text-navy-800">
                <input
                  type="radio"
                  name="receiptDocKind"
                  checked={uploadKind === "Fis"}
                  onChange={() => setUploadKind("Fis")}
                  disabled={uploading}
                />
                Gider Fişi
              </label>
              <label
                className="flex cursor-pointer items-center gap-1.5 text-sm text-navy-800"
                title="Yazar kasa günlük kapanış (Z) raporu — gelir fişi olarak işlenir"
              >
                <input
                  type="radio"
                  name="receiptDocKind"
                  checked={uploadKind === "ZRaporu"}
                  onChange={() => setUploadKind("ZRaporu")}
                  disabled={uploading}
                />
                Z Raporu
              </label>
            </div>
          </div>
          <p className="text-xs text-navy-400">
            Yükleme için mükellef seçilmelidir. JPG, PNG, PDF veya HEIC; dosya başına en fazla 10 MB.
          </p>
        </div>

        {/* Sürükle-bırak alanı */}
        <div
          className={`mt-4 flex cursor-pointer flex-col items-center justify-center rounded-xl border-2 border-dashed px-6 py-10 text-center transition ${
            dragActive ? "border-navy-500 bg-navy-50" : "border-navy-200 bg-navy-50/40 hover:bg-navy-50"
          } ${uploading ? "pointer-events-none opacity-60" : ""}`}
          onClick={() => fileInputRef.current?.click()}
          onDragOver={(e) => {
            e.preventDefault();
            setDragActive(true);
          }}
          onDragLeave={() => setDragActive(false)}
          onDrop={handleDrop}
        >
          <p className="text-sm font-semibold text-navy-800">
            Fişleri buraya sürükleyin ya da tıklayıp seçin
          </p>
          <p className="mt-1 text-xs text-navy-500">
            Çoklu dosya desteklenir — fişler kuyruğa alınır, arka planda okunur; beklemenize gerek yok.
          </p>
          <input
            ref={fileInputRef}
            type="file"
            multiple
            accept=".jpg,.jpeg,.png,.pdf,.heic"
            className="hidden"
            onChange={(e) => {
              if (e.target.files) handleFiles(e.target.files);
              e.target.value = ""; // aynı dosya tekrar seçilebilsin
            }}
          />
        </div>

        {/* İlerleme göstergesi */}
        {uploading && (
          <div className="mt-4">
            <div className="flex items-center justify-between text-xs text-navy-500">
              <span>Yükleniyor — okuma arka planda yapılacak...</span>
              <span>%{uploadProgress}</span>
            </div>
            <div className="mt-1 h-2 overflow-hidden rounded-full bg-navy-100">
              <div
                className="h-full rounded-full bg-navy-700 transition-all"
                style={{ width: `${Math.max(uploadProgress, 4)}%` }}
              />
            </div>
          </div>
        )}

        {uploadError && <div className="alert-error mt-4">{uploadError}</div>}

        {/* Dosya bazlı sonuçlar */}
        {uploadResult && (
          <div className="mt-4 space-y-2">
            <div className="rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
              {uploadResult.message}
            </div>
            {uploadResult.items
              .filter((i) => !i.success)
              .map((i) => (
                <div key={i.fileName} className="alert-error !py-2 text-xs">
                  <strong>{i.fileName}:</strong> {i.message ?? "Yükleme başarısız."}
                </div>
              ))}
          </div>
        )}
      </div>

      {/* Faz 14 (C): sayfa içi Luca dışa aktarma şeridi (yalnız fişler — tekil davranış korunur) */}
      <LucaExportStrip clientIds={selectedClientId ? [selectedClientId] : []} source="fis" />

      {/* Filtreler */}
      <div className="mt-6 flex flex-wrap items-center gap-4">
        <select
          className="input max-w-[200px]"
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
          <span className="text-sm text-navy-500">Toplam {pageData.totalCount} fiş</span>
        )}
      </div>

      {/* Toplu işlem sonuç bandı: "5 fiş onaylandı, 2 atlandı (onay bekleyen durumda değil)." */}
      {bulkResult && (
        <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          {bulkResult}
        </div>
      )}
      {bulkError && <div className="alert-error mt-4">{bulkError}</div>}

      {/* Seçim varken yapışkan toplu işlem çubuğu (faturalar sayfasıyla aynı kalıp) */}
      {selectedIds.size > 0 && (
        <div className="sticky top-0 z-30 mt-4 flex flex-wrap items-center gap-3 rounded-xl border border-navy-200 bg-white px-4 py-3 shadow-md">
          <span className="text-sm font-semibold text-navy-900">
            {selectedIds.size} fiş seçili
          </span>
          <button
            onClick={handleBulkApprove}
            className="btn-primary flex items-center gap-2 !px-3 !py-1.5 text-xs"
            disabled={bulkBusy !== null}
            title="Seçilen fişleri okunan/önerilen bilgilerle düzeltmesiz onaylar; yalnız onay bekleyenler işlenir, diğerleri atlanır."
          >
            {bulkBusy === "approve" && (
              <span className="h-3 w-3 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {bulkBusy === "approve" ? "Onaylanıyor..." : "Seçilenleri Onayla"}
          </button>
          <button
            onClick={handleBulkUnapprove}
            className="btn-secondary flex items-center gap-2 !px-3 !py-1.5 text-xs !text-amber-700"
            disabled={bulkBusy !== null}
            title="Seçilenlerden ONAYLI fişleri Onay Bekliyor'a döndürür; alanlar silinmez, motor hafızası korunur."
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
            title="Seçilen fişleri dosyalarıyla birlikte kalıcı olarak siler; yalnız yüklenmiş/onay bekleyenler silinir."
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
            onClick={() => loadReceipts({ clientId: selectedClientId, status: statusFilter, page })}
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
          {statusFilter ? (
            <>
              <h2 className="text-lg font-semibold text-navy-900">Sonuç bulunamadı</h2>
              <p className="mt-2 text-sm text-navy-500">Filtre ölçütlerinizle eşleşen fiş yok.</p>
            </>
          ) : (
            <>
              <h2 className="text-lg font-semibold text-navy-900">
                Fiş yükleyin — sistem gideri otomatik algılar
              </h2>
              <p className="mt-2 max-w-md text-sm text-navy-500">
                Yukarıdan mükellef seçip fiş fotoğraflarını sürükleyin. OCR satıcı, tarih, tutar ve
                KDV&apos;yi okur; kod motoru gider hesabını önerir, siz yalnızca onaylarsınız.
              </p>
            </>
          )}
        </div>
      ) : items.length > 0 ? (
        <>
          <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
            <table className="w-full min-w-[1060px] text-left text-sm">
              <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
                <tr>
                  <th className="w-10 px-4 py-3">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-navy-300"
                      aria-label="Sayfadaki tüm fişleri seç"
                      checked={allVisibleSelected}
                      onChange={() => toggleSelectAllVisible(items, allVisibleSelected)}
                    />
                  </th>
                  <th className="px-4 py-3">Fiş</th>
                  <th className="px-4 py-3">Satıcı</th>
                  <th className="px-4 py-3">Mükellef</th>
                  <th className="px-4 py-3">Tarih</th>
                  <th className="px-4 py-3 text-right">Tutar</th>
                  <th className="px-4 py-3 text-right">KDV</th>
                  <th className="px-4 py-3">Güven</th>
                  <th className="px-4 py-3">Durum</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-navy-50">
                {items.map((receipt) => {
                  return (
                    <tr
                      key={receipt.id}
                      className="cursor-pointer transition hover:bg-navy-50/60"
                      onClick={() => openDetailById(receipt.id)}
                    >
                      {/* Seçim kutusu: satır tıklaması detay çekmecesini açar, checkbox açmaz. */}
                      <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="checkbox"
                          className="h-4 w-4 rounded border-navy-300"
                          aria-label={`${receipt.fileName} dosyalı fişi seç`}
                          checked={selectedIds.has(receipt.id)}
                          onChange={() => toggleSelected(receipt.id)}
                        />
                      </td>
                      <td className="px-4 py-3">
                        <div className="flex items-center gap-3">
                          <ReceiptThumb receipt={receipt} />
                          <span className="max-w-[140px] truncate font-mono text-xs text-navy-400">
                            {receipt.fileName}
                          </span>
                        </div>
                      </td>
                      <td className="px-4 py-3">
                        <span className="font-medium text-navy-900">{receipt.vendorName ?? "—"}</span>
                        {receipt.vendorTaxId && (
                          <span className="block font-mono text-xs text-navy-400">
                            VKN: {receipt.vendorTaxId}
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-navy-600">{receipt.clientTitle ?? "—"}</td>
                      <td className="px-4 py-3">
                        {formatDate(receipt.receiptDate)}
                        {receipt.isOldDocument && (
                          <span
                            className="ml-1.5 text-amber-600"
                            title="Belge tarihi 30 günden eski"
                          >
                            ⚠
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-right font-semibold text-navy-900">
                        {formatMoney(receipt.totalAmount)}
                      </td>
                      <td className="px-4 py-3 text-right">
                        {receipt.vatRate !== null ? `%${receipt.vatRate}` : "—"}
                        <span className="block text-xs text-navy-400">
                          {formatMoney(receipt.vatAmount)}
                        </span>
                      </td>
                      <td className="px-4 py-3">
                        <ConfidenceBadge confidence={receipt.confidence} />
                      </td>
                      <td className="px-4 py-3">
                        <div className="flex flex-wrap gap-1.5">
                          <StatusBadge status={receipt.status} />
                          {/* Faz 16: Z raporu rozeti — gelir belgesi ayırt edilir */}
                          {receipt.documentKind === "ZRaporu" && (
                            <span
                              className="rounded-full bg-purple-100 px-2.5 py-0.5 text-xs font-semibold text-purple-700"
                              title="Yazar kasa günlük kapanış (Z) raporu — Luca'ya gelir fişi olarak aktarılır"
                            >
                              Z Raporu
                            </span>
                          )}
                          {receipt.autoApproveReady && (
                            <span
                              className="rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-semibold text-green-700"
                              title="Güven eşiği üstünde — tek tıkla onaya hazır"
                            >
                              Otomatik onaya hazır
                            </span>
                          )}
                          {receipt.expenseEligible === false && (
                            <span
                              className="rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-semibold text-red-700"
                              title={receipt.ineligibleReason ?? undefined}
                            >
                              Gider gösterilemez
                            </span>
                          )}
                          {/* Faz 13: plaka rozeti (yalnız plaka okunan fişlerde) */}
                          {receipt.plateNumber && (
                            <span
                              className={`rounded-full px-2.5 py-0.5 font-mono text-xs font-semibold ${
                                receipt.plateMatch === "Eslesti"
                                  ? "bg-green-100 text-green-700"
                                  : "bg-red-100 text-red-700"
                              }`}
                              title={
                                receipt.plateMatch === "Eslesti"
                                  ? "Plaka kayıtlı araçla eşleşti"
                                  : "Plaka kayıtlı araçlarla eşleşmiyor"
                              }
                            >
                              {receipt.plateNumber}
                            </span>
                          )}
                          {/* Faz 8 (B3): KKEG rozeti */}
                          {receipt.isKkeg && (
                            <span
                              className="rounded-full bg-orange-100 px-2.5 py-0.5 text-xs font-semibold text-orange-700"
                              title={receipt.kkegReason ?? "Kanunen kabul edilmeyen gider"}
                            >
                              KKEG
                            </span>
                          )}
                        </div>
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
              <h2 className="text-lg font-bold text-navy-900">Fiş Detayı</h2>
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
            ) : detail && edit ? (
              <>
                {/* Durum + rozetler */}
                <div className="mt-4 flex flex-wrap gap-2">
                  <StatusBadge status={detail.status} />
                  {/* Faz 16: Z raporu rozeti */}
                  {detail.documentKind === "ZRaporu" && (
                    <span className="rounded-full bg-purple-100 px-2.5 py-0.5 text-xs font-semibold text-purple-700">
                      Z Raporu
                    </span>
                  )}
                  {detail.autoApproveReady && (
                    <span className="rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-semibold text-green-700">
                      Otomatik onaya hazır
                    </span>
                  )}
                  <ConfidenceBadge confidence={detail.confidence} />
                </div>

                {/* Faz 14: kuyrukta okunan fiş bilgisi */}
                {detail.status === "Processing" && (
                  <div className="alert-info mt-4">
                    Bu fiş arka planda okunuyor — alanlar okuma bitince otomatik dolacak. Liste,
                    okuma tamamlanana dek 2 saniyede bir kendini yeniler.
                  </div>
                )}

                {/* Uyarılar */}
                {detail.expenseEligible === false && (
                  <div className="alert-error mt-4">
                    <strong>Gider gösterilemez:</strong>{" "}
                    {detail.ineligibleReason ?? "uygunluk kontrolünden geçmedi."}
                    {detailEditable && (
                      <label className="mt-2 flex items-center gap-2 text-xs font-medium">
                        <input
                          type="checkbox"
                          checked={edit.overrideEligibility}
                          onChange={(e) =>
                            setEdit((prev) =>
                              prev ? { ...prev, overrideEligibility: e.target.checked } : prev
                            )
                          }
                        />
                        Yine de gider göster (sorumluluğu üstleniyorum)
                      </label>
                    )}
                  </div>
                )}
                {detail.isOldDocument && (
                  <div className="alert-info mt-4">
                    ⚠ Belge tarihi 30 günden eski. Gider gösterilebilir, ancak KDV indirimi için
                    dönem kontrolü yapmanız önerilir.
                  </div>
                )}
                {/* Faz 13: plaka uyarıları (eşleşmeyen plaka / akaryakıt fişinde okunamayan plaka) */}
                {detail.warnings.map((warning) => (
                  <div key={warning} className="alert-info mt-4 !border-amber-200 !bg-amber-50 !text-amber-800">
                    ⚠ {warning}
                  </div>
                ))}
                {detail.status === "Rejected" && detail.rejectReason && (
                  <div className="alert-error mt-4">
                    <strong>Red gerekçesi:</strong> {detail.rejectReason}
                  </div>
                )}

                {actionError && <div className="alert-error mt-4">{actionError}</div>}
                {approveSuccess && (
                  <div className="mt-4 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
                    Onaylandı — sistem bu eşlemeyi öğrendi ✓
                  </div>
                )}

                <div className="mt-6 grid gap-6 lg:grid-cols-2">
                  {/* Görsel önizleme */}
                  <div>
                    <h3 className="text-sm font-semibold text-navy-900">Belge Önizleme</h3>
                    <div className="mt-2 flex min-h-[240px] items-center justify-center rounded-lg border border-navy-100 bg-navy-50 p-2">
                      {previewUrl ? (
                        isImageFile(detail.fileName) ? (
                          // eslint-disable-next-line @next/next/no-img-element
                          <img
                            src={previewUrl}
                            alt={detail.fileName}
                            className="max-h-[420px] w-auto rounded-md object-contain"
                          />
                        ) : (
                          <a
                            href={previewUrl}
                            target="_blank"
                            rel="noreferrer"
                            className="btn-secondary !px-4 !py-2 text-sm"
                          >
                            Belgeyi yeni sekmede aç ({detail.fileName})
                          </a>
                        )
                      ) : (
                        <span className="text-sm text-navy-400">Önizleme yüklenemedi.</span>
                      )}
                    </div>
                    <p className="mt-2 font-mono text-xs text-navy-400">{detail.fileName}</p>
                  </div>

                  {/* Alanlar */}
                  <div>
                    <h3 className="text-sm font-semibold text-navy-900">OCR Alanları</h3>
                    <div className="mt-2 grid grid-cols-2 gap-3">
                      <div className="col-span-2">
                        <label className="label !mb-1 text-xs">Satıcı</label>
                        {detailEditable ? (
                          <input
                            type="text"
                            className="input !py-2 text-sm"
                            value={edit.vendorName}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, vendorName: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="text-sm font-medium text-navy-900">{detail.vendorName ?? "—"}</p>
                        )}
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">VKN / TCKN</label>
                        {detailEditable ? (
                          <input
                            type="text"
                            className="input !py-2 font-mono text-sm"
                            value={edit.vendorTaxId}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, vendorTaxId: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="font-mono text-sm">{detail.vendorTaxId ?? "—"}</p>
                        )}
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">Evrak No (fiş/Z no)</label>
                        {detailEditable ? (
                          <input
                            type="text"
                            className="input !py-2 font-mono text-sm"
                            placeholder="Ör. 0042"
                            maxLength={60}
                            value={edit.documentNumber}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, documentNumber: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="font-mono text-sm">{detail.documentNumber ?? "—"}</p>
                        )}
                        {detailEditable && !edit.documentNumber && (
                          <p className="mt-1 text-xs text-navy-400">
                            Boş bırakılırsa Luca dosyasında otomatik numara üretilir.
                          </p>
                        )}
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">Belge Tarihi</label>
                        {detailEditable ? (
                          <input
                            type="date"
                            className="input !py-2 text-sm"
                            value={edit.receiptDate}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, receiptDate: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="text-sm">{formatDate(detail.receiptDate)}</p>
                        )}
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">Toplam (KDV dahil)</label>
                        {detailEditable ? (
                          <input
                            type="number"
                            step="0.01"
                            min="0"
                            className="input !py-2 text-sm"
                            value={edit.totalAmount}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, totalAmount: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="text-sm font-semibold">{formatMoney(detail.totalAmount)}</p>
                        )}
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">KDV Oranı (%)</label>
                        {detailEditable ? (
                          <input
                            type="number"
                            step="1"
                            min="0"
                            max="100"
                            className="input !py-2 text-sm"
                            value={edit.vatRate}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, vatRate: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="text-sm">{detail.vatRate !== null ? `%${detail.vatRate}` : "—"}</p>
                        )}
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">KDV Tutarı</label>
                        {detailEditable ? (
                          <input
                            type="number"
                            step="0.01"
                            min="0"
                            className="input !py-2 text-sm"
                            value={edit.vatAmount}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, vatAmount: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="text-sm">{formatMoney(detail.vatAmount)}</p>
                        )}
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">Matrah</label>
                        {detailEditable ? (
                          <input
                            type="number"
                            step="0.01"
                            min="0"
                            className="input !py-2 text-sm"
                            value={edit.netAmount}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, netAmount: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="text-sm">{formatMoney(detail.netAmount)}</p>
                        )}
                      </div>
                      {/* Faz 16: iskonto / indirim */}
                      <div>
                        <label className="label !mb-1 text-xs">İskonto / İndirim</label>
                        {detailEditable ? (
                          <input
                            type="number"
                            step="0.01"
                            min="0"
                            className="input !py-2 text-sm"
                            placeholder="Yoksa boş bırakın"
                            value={edit.discountTotal}
                            onChange={(e) =>
                              setEdit((p) => (p ? { ...p, discountTotal: e.target.value } : p))
                            }
                          />
                        ) : (
                          <p className="text-sm">
                            {detail.discountTotal !== null ? formatMoney(detail.discountTotal) : "—"}
                          </p>
                        )}
                      </div>

                      {/* Faz 16: oran bazlı KDV kırılım tablosu (düzenlenebilir; satır ekle/sil) */}
                      <div className="col-span-2 rounded-lg border border-navy-100 bg-navy-50/40 p-3">
                        <div className="flex items-center justify-between">
                          <span className="text-xs font-semibold text-navy-900">
                            KDV Kırılımı (oran bazlı)
                          </span>
                          {detailEditable && (
                            <button
                              type="button"
                              className="text-xs font-semibold text-navy-600 hover:text-navy-800"
                              onClick={() =>
                                updateBreakdown((rows) => [
                                  ...rows,
                                  { vatRate: "", netAmount: "", vatAmount: "" },
                                ])
                              }
                            >
                              + Satır ekle
                            </button>
                          )}
                        </div>

                        {edit.vatBreakdown.length === 0 ? (
                          <p className="mt-2 text-xs text-navy-400">
                            Kırılım okunamadı
                            {detailEditable
                              ? " — satır ekleyerek oranları elle girebilirsiniz."
                              : "."}
                          </p>
                        ) : (
                          <table className="mt-2 w-full text-left text-xs">
                            <thead className="text-navy-500">
                              <tr>
                                <th className="py-1 pr-2 font-semibold">Oran (%)</th>
                                <th className="py-1 pr-2 font-semibold">Matrah</th>
                                <th className="py-1 pr-2 font-semibold">KDV</th>
                                {detailEditable && <th className="w-8" />}
                              </tr>
                            </thead>
                            <tbody>
                              {edit.vatBreakdown.map((row, idx) => (
                                <tr key={idx}>
                                  {detailEditable ? (
                                    <>
                                      <td className="py-1 pr-2">
                                        <input
                                          type="number"
                                          step="1"
                                          min="0"
                                          max="100"
                                          className="input !py-1.5 text-xs"
                                          value={row.vatRate}
                                          onChange={(e) =>
                                            updateBreakdown((rows) => {
                                              rows[idx] = { ...rows[idx], vatRate: e.target.value };
                                              return rows;
                                            })
                                          }
                                        />
                                      </td>
                                      <td className="py-1 pr-2">
                                        <input
                                          type="number"
                                          step="0.01"
                                          min="0"
                                          className="input !py-1.5 text-xs"
                                          value={row.netAmount}
                                          onChange={(e) =>
                                            updateBreakdown((rows) => {
                                              rows[idx] = { ...rows[idx], netAmount: e.target.value };
                                              return rows;
                                            })
                                          }
                                        />
                                      </td>
                                      <td className="py-1 pr-2">
                                        <input
                                          type="number"
                                          step="0.01"
                                          min="0"
                                          className="input !py-1.5 text-xs"
                                          value={row.vatAmount}
                                          onChange={(e) =>
                                            updateBreakdown((rows) => {
                                              rows[idx] = { ...rows[idx], vatAmount: e.target.value };
                                              return rows;
                                            })
                                          }
                                        />
                                      </td>
                                      <td className="py-1 text-right">
                                        <button
                                          type="button"
                                          className="rounded px-1.5 py-0.5 text-xs font-semibold text-red-600 hover:bg-red-50"
                                          title="Satırı sil"
                                          onClick={() =>
                                            updateBreakdown((rows) => rows.filter((_, i) => i !== idx))
                                          }
                                        >
                                          ✕
                                        </button>
                                      </td>
                                    </>
                                  ) : (
                                    <>
                                      <td className="py-1 pr-2">%{row.vatRate}</td>
                                      <td className="py-1 pr-2">
                                        {formatMoney(parseAmount(row.netAmount))}
                                      </td>
                                      <td className="py-1 pr-2">
                                        {formatMoney(parseAmount(row.vatAmount))}
                                      </td>
                                    </>
                                  )}
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        )}

                        {/* Toplam otomatik hesaplanır; fiş toplamıyla uyuşmazlıkta SARI uyarı. */}
                        {edit.vatBreakdown.length > 0 &&
                          (() => {
                            const totals = breakdownTotals(edit.vatBreakdown);
                            const grand = Math.round((totals.net + totals.vat) * 100) / 100;
                            const totalInput = parseAmount(edit.totalAmount);
                            const mismatch =
                              totalInput !== null && Math.abs(grand - totalInput) > 0.05;
                            return (
                              <div className="mt-2 text-xs text-navy-600">
                                Kırılım toplamı: matrah {formatMoney(totals.net)} + KDV{" "}
                                {formatMoney(totals.vat)} ={" "}
                                <strong>{formatMoney(grand)}</strong>
                                {mismatch && (
                                  <div className="mt-1 rounded border border-amber-200 bg-amber-50 px-2 py-1 text-amber-800">
                                    ⚠ KDV kırılımı toplamla uyuşmuyor: kırılım {formatMoney(grand)},
                                    fiş toplamı {formatMoney(totalInput)}. Satırları veya toplam
                                    tutarı düzeltin.
                                  </div>
                                )}
                              </div>
                            );
                          })()}
                      </div>

                      {/* Faz 16: Z raporu nakit/kart dağılımı (salt okunur — OCR alanı) */}
                      {detail.documentKind === "ZRaporu" && (
                        <div className="col-span-2 flex flex-wrap gap-6 rounded-lg border border-purple-100 bg-purple-50/40 p-3 text-sm text-navy-800">
                          <span>
                            <strong>Nakit:</strong>{" "}
                            {detail.cashTotal !== null ? formatMoney(detail.cashTotal) : "—"}
                          </span>
                          <span>
                            <strong>Kart:</strong>{" "}
                            {detail.cardTotal !== null ? formatMoney(detail.cardTotal) : "—"}
                          </span>
                          <span className="text-xs text-navy-500">
                            Luca aktarımında nakit 100 Kasa&apos;ya, kart tutarı parametredeki kart
                            hesabına (varsayılan 108) borç yazılır.
                          </span>
                        </div>
                      )}

                      {/* Faz 13: fişten okunan plaka + eşleşme rozeti (salt okunur — OCR alanı) */}
                      <div>
                        <label className="label !mb-1 text-xs">Plaka</label>
                        <div className="flex items-center gap-2">
                          <p className="font-mono text-sm">{detail.plateNumber ?? "—"}</p>
                          <PlateMatchBadge receipt={detail} />
                        </div>
                      </div>
                      <div>
                        <label className="label !mb-1 text-xs">Hesap Kodu</label>
                        {detailEditable ? (
                          /* Autocomplete: "7" → 7 ile başlayan kodlar, "kira" → adı kira
                             içeren hesaplar; serbest metin girişi mümkün. */
                          <AccountCodeAutocomplete
                            clientId={detail.clientId}
                            value={edit.accountCode}
                            onChange={(value) =>
                              setEdit((p) => (p ? { ...p, accountCode: value } : p))
                            }
                            placeholder="770.01"
                            inputClassName="input !py-2 font-mono text-sm"
                          />
                        ) : (
                          <p className="font-mono text-sm">{detail.accountCode ?? "—"}</p>
                        )}
                        {detail.suggestedAccountCode && detailEditable && (
                          <p className="mt-1 text-xs text-navy-400">
                            Öneri: <span className="font-mono">{detail.suggestedAccountCode}</span>
                          </p>
                        )}
                      </div>
                      {/* Cari kod alanı kaldırıldı: motor cariyi arka planda atamaya devam
                          eder (edit.cariCode İÇSEL taşınır), Luca export'una otomatik girer —
                          UI'da gösterilmez (kullanıcı isteği). */}

                      {/* Faz 8 (B3): KKEG alanları */}
                      <div className="col-span-2 rounded-lg border border-orange-200 bg-orange-50/40 p-3">
                        {detailEditable ? (
                          <>
                            <label className="flex items-center gap-2 text-sm font-semibold text-orange-700">
                              <input
                                type="checkbox"
                                className="h-4 w-4 rounded border-navy-300"
                                checked={edit.isKkeg}
                                // Faz 15: işaretlenince tutar + gerekçe otomatik hesaplanır (boşsa).
                                onChange={(e) => handleKkegToggle(e.target.checked)}
                              />
                              KKEG (kanunen kabul edilmeyen gider)
                            </label>
                            {edit.isKkeg && (
                              <div className="mt-3 grid grid-cols-2 gap-3">
                                <div>
                                  <label className="label !mb-1 text-xs">KKEG Tutarı (KDV hariç)</label>
                                  <input
                                    type="number"
                                    step="0.01"
                                    min="0"
                                    className="input !py-2 text-sm"
                                    value={edit.kkegAmount}
                                    onChange={(e) =>
                                      setEdit((p) => (p ? { ...p, kkegAmount: e.target.value } : p))
                                    }
                                  />
                                </div>
                                <div>
                                  <label className="label !mb-1 text-xs">Gerekçe</label>
                                  <input
                                    type="text"
                                    className="input !py-2 text-sm"
                                    placeholder="Ör. Binek oto gideri %30 kısıt"
                                    maxLength={300}
                                    value={edit.kkegReason}
                                    onChange={(e) =>
                                      setEdit((p) => (p ? { ...p, kkegReason: e.target.value } : p))
                                    }
                                  />
                                </div>
                              </div>
                            )}
                          </>
                        ) : (
                          <p className="text-sm text-navy-700">
                            <strong className="text-orange-700">KKEG:</strong>{" "}
                            {detail.isKkeg
                              ? `${formatMoney(detail.kkegAmount)} — ${detail.kkegReason ?? ""}`
                              : "yok"}
                          </p>
                        )}
                      </div>
                    </div>
                  </div>
                </div>

                {/* Aksiyonlar */}
                {detailEditable && (
                  <div className="mt-8 border-t border-navy-100 pt-5">
                    {rejectMode ? (
                      <div>
                        <label htmlFor="rejectReason" className="label">
                          Red gerekçesi (zorunlu)
                        </label>
                        <textarea
                          id="rejectReason"
                          className="input min-h-[80px]"
                          value={rejectReason}
                          onChange={(e) => setRejectReason(e.target.value)}
                          placeholder="Ör. Fiş okunaklı değil, işletme gideri değil..."
                        />
                        <div className="mt-3 flex gap-2">
                          <button
                            onClick={handleReject}
                            className="btn-primary !bg-red-700 hover:!bg-red-800 flex items-center gap-2 !px-4 !py-2 text-sm"
                            disabled={rejecting}
                          >
                            {rejecting && (
                              <span className="h-3 w-3 animate-spin rounded-full border-2 border-white border-t-transparent" />
                            )}
                            {rejecting ? "Reddediliyor..." : "Reddet"}
                          </button>
                          <button
                            onClick={() => {
                              setRejectMode(false);
                              setRejectReason("");
                            }}
                            className="btn-secondary !px-4 !py-2 text-sm"
                            disabled={rejecting}
                          >
                            Vazgeç
                          </button>
                        </div>
                      </div>
                    ) : (
                      <div className="flex flex-wrap gap-2">
                        <button
                          onClick={handleApprove}
                          className="btn-primary flex items-center gap-2"
                          disabled={approving || deleting}
                          title="Alanlardaki (düzeltilmiş) değerleri onaylar; sistem bu eşlemeyi öğrenir."
                        >
                          {approving && (
                            <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                          )}
                          {approving ? "Onaylanıyor..." : "Onayla"}
                        </button>
                        <button
                          onClick={() => setRejectMode(true)}
                          className="btn-secondary"
                          disabled={approving || deleting}
                        >
                          Reddet
                        </button>
                        <button
                          onClick={handleDelete}
                          className="btn-secondary !text-red-700"
                          disabled={approving || deleting}
                        >
                          {deleting ? "Siliniyor..." : "Sil"}
                        </button>
                      </div>
                    )}
                  </div>
                )}

                {/* Onaylı fişte tekil "Onayı Kaldır": alanlar korunur, durum Onay Bekliyor'a döner. */}
                {detail.status === "Approved" && (
                  <div className="mt-8 border-t border-navy-100 pt-5">
                    <button
                      onClick={handleUnapprove}
                      className="btn-secondary flex items-center gap-2 !text-amber-700"
                      disabled={unapproving}
                      title="Fişi Onay Bekliyor durumuna döndürür; alanlar/kodlar silinmez, motor hafızası korunur. Sonraki Luca export'larına dahil edilmez."
                    >
                      {unapproving && (
                        <span className="h-4 w-4 animate-spin rounded-full border-2 border-amber-500 border-t-transparent" />
                      )}
                      {unapproving ? "Kaldırılıyor..." : "Onayı Kaldır"}
                    </button>
                    <p className="mt-2 text-xs text-navy-500">
                      Onay kaldırıldığında fiş yeniden düzenlenebilir olur; öğrenilen eşlemeler
                      silinmez, yeniden onayda güncellenir.
                    </p>
                  </div>
                )}
              </>
            ) : null}
          </div>
        </div>
      )}
    </div>
  );
}
