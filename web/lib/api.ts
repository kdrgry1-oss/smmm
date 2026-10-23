/**
 * API istemcisi: base URL env'den okunur, Authorization header'ı otomatik eklenir,
 * 401'de token temizlenip /giris'e yönlendirilir, hata gövdesindeki Türkçe mesaj fırlatılır.
 */

const API_URL = process.env.NEXT_PUBLIC_API_URL || "http://localhost:5000";

const TOKEN_KEY = "mm_token";

export function getToken(): string | null {
  if (typeof window === "undefined") return null;
  return localStorage.getItem(TOKEN_KEY);
}

export function setToken(token: string) {
  localStorage.setItem(TOKEN_KEY, token);
}

export function clearToken() {
  localStorage.removeItem(TOKEN_KEY);
}

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
    this.name = "ApiError";
  }
}

async function parseErrorMessage(res: Response): Promise<string> {
  try {
    const body = await res.json();
    if (body && typeof body.message === "string" && body.message.trim()) return body.message;
    if (body && typeof body.title === "string" && body.title.trim()) return body.title;
  } catch {
    // gövde JSON değilse aşağıdaki genel mesaja düş
  }
  if (res.status === 403) return "Bu işlem için yetkiniz yok.";
  if (res.status === 404) return "Kayıt bulunamadı.";
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

export async function api<T = unknown>(
  path: string,
  options: { method?: string; body?: unknown; auth?: boolean } = {}
): Promise<T> {
  const { method = "GET", body, auth = true } = options;

  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (auth) {
    const token = getToken();
    if (token) headers["Authorization"] = `Bearer ${token}`;
  }

  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      cache: "no-store",
    });
  } catch {
    throw new ApiError(0, "Sunucuya ulaşılamıyor. İnternet bağlantınızı ve API'nin çalıştığını kontrol edin.");
  }

  if (res.status === 401 && auth) {
    clearToken();
    if (typeof window !== "undefined" && !window.location.pathname.startsWith("/giris")) {
      window.location.href = "/giris";
    }
    throw new ApiError(401, "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.");
  }

  if (!res.ok) {
    throw new ApiError(res.status, await parseErrorMessage(res));
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

// ---------- Tipler ----------

export interface AuthResponse {
  token: string;
  role: string;
  tenantId: string | null;
  fullName: string;
}

export interface MeResponse {
  id: string;
  email: string;
  fullName: string;
  role: string;
  tenantId: string | null;
  tenantName: string | null;
  planCode: string | null;
  planName: string | null;
}

export interface PlanResponse {
  id: string;
  code: string;
  name: string;
  monthlyPrice: number;
  maxClients: number;
  maxUsers: number;
  monthlyOcrQuota: number;
  eTebligat: boolean;
  unlimitedClients: boolean;
}

export interface ClientResponse {
  id: string;
  title: string;
  taxId: string;
  taxOffice: string | null;
  email: string | null;
  phone: string | null;
  address: string | null;
  eInvoiceEnabled: boolean;
  eArchiveEnabled: boolean;
  integratorName: string | null;
  isArchived: boolean;
  createdAt: string;
}

// ---------- Faz 13: Mükellef araçları (plaka) ----------

/** UI ön kontrolü — normalize edilmiş (boşluksuz, büyük harf) plaka deseni; backend aynı kuralı uygular. */
export const PLATE_REGEX = /^[0-9]{2}[A-Z]{1,3}[0-9]{2,5}$/;

/** Plakayı UI tarafında normalize eder: boşluk/tire silinir, Türkçe i/ı dahil büyük harfe çevrilir. */
export function normalizePlate(plate: string): string {
  return plate
    .trim()
    .replace(/[\s\-.]/g, "")
    .replace(/i/g, "I")
    .replace(/ı/g, "I")
    .replace(/İ/g, "I")
    .toUpperCase();
}

export interface ClientVehicleResponse {
  id: string;
  clientId: string;
  /** Normalize plaka (ör. "34FPC630"). */
  plate: string;
  /** Binek otomobil mi? KKEG %30 kısıtı yalnız binek araçta uygulanır. */
  isPassengerCar: boolean;
  description: string | null;
  createdAt: string;
}

export interface ClientVehicleCreateRequest {
  plate: string;
  isPassengerCar: boolean;
  description?: string | null;
}

export function listClientVehicles(clientId: string) {
  return api<ClientVehicleResponse[]>(`/api/clients/${clientId}/vehicles`);
}

export function addClientVehicle(clientId: string, body: ClientVehicleCreateRequest) {
  return api<ClientVehicleResponse>(`/api/clients/${clientId}/vehicles`, { method: "POST", body });
}

export function deleteClientVehicle(clientId: string, vehicleId: string) {
  return api<void>(`/api/clients/${clientId}/vehicles/${vehicleId}`, { method: "DELETE" });
}

export interface TenantAdminResponse {
  id: string;
  name: string;
  email: string;
  planCode: string | null;
  planName: string | null;
  subscriptionStatus: string;
  userCount: number;
  clientCount: number;
  createdAt: string;
}

export interface AdminStatsResponse {
  totalTenants: number;
  totalUsers: number;
  totalClients: number;
  planDistribution: { planCode: string; planName: string; tenantCount: number }[];
}

// ---------- Faz 1: Entegrasyon + e-Fatura ----------

export type IntegrationProviderCode =
  | "Mock"
  | "Nilvera"
  | "Uyumsoft"
  | "DoganDonusum"
  | "Elogo"
  | "Edm"
  | "Parasut"
  /** Faz 15 — entegratör kullanmayan mükellef: belgeler ve e-Tebligatlar GİB portalından robotla çekilir. */
  | "GibPortal"
  /**
   * GİB e-Arşiv Portalı — entegratörsüz mükellef için TERCİH EDİLEN yol: ekran kazıma değil
   * gerçek JSON API, girişinde doğrulama kodu yok. Fatura ÇEKME ve KESME'yi destekler.
   * Yalnız e-Arşiv belgelerini kapsar; ticari e-Fatura ve e-Tebligat GibPortal yolundan gelir.
   */
  | "EArsivPortal";

export type EInvoiceStatusCode = "New" | "Processing" | "Processed" | "Exported" | "Error";

export type CodingStatusCode = "Uncoded" | "AutoCoded" | "NeedsReview" | "Approved";

export interface IntegrationResponse {
  id: string;
  clientId: string;
  provider: IntegrationProviderCode;
  /** Maskeli anahtar (yalnızca son 4 hane, ör. "••••3f7a"). Anahtar yoksa null. */
  maskedApiKey: string | null;
  /** Kimlik-tabanlı sağlayıcılar (Uyumsoft/Doğan/eLogo/EDM/Paraşüt): maskeli kullanıcı adı (ilk 2 + son 2 karakter). Diğerlerinde null. */
  maskedUsername: string | null;
  /** Kimlik-tabanlı SOAP sağlayıcıları: kayıtlı servis adresi (gizli değildir, maskesiz). Boşsa varsayılan uç kullanılır. */
  endpoint: string | null;
  /** Uyumsoft: e-SMM (serbest meslek makbuzu) bayrağı — makbuzlar VoucherIntegration servisinden çekilir. Diğer sağlayıcılarda null. */
  uyumsoftEsmm: boolean | null;
  /** Uyumsoft: e-Fatura / e-Arşiv gelen kutusu da çekilsin mi? Diğer sağlayıcılarda null. */
  uyumsoftEinvoice: boolean | null;
  /**
   * GİB Portalı: son otomatik çekimde captcha/SMS gibi insan gerektiren bir adım çıktıysa
   * Türkçe uyarı metni ("Elle doğrulama gerekli." ile başlar). Sorunsuzsa null.
   */
  manualWarning: string | null;
  /** Uyarının oluştuğu an (ISO). Uyarı yoksa null. */
  manualWarningAt: string | null;
  useTestEnvironment: boolean;
  createdAt: string;
  lastSyncAt: string | null;
}

export interface IntegrationSaveRequest {
  provider: IntegrationProviderCode;
  /** Nilvera için zorunlu (kayıtlı anahtar varsa boş bırakılabilir). */
  apiKey?: string | null;
  /** Uyumsoft/Doğan/eLogo/EDM: web servis kullanıcı adı; Paraşüt: hesap e-postası (kayıtlı kimlik varsa boş bırakılabilir). */
  username?: string | null;
  /** Kimlik-tabanlı sağlayıcılar için zorunlu şifre (kayıtlı kimlik varsa boş bırakılabilir). */
  password?: string | null;
  /** Uyumsoft/Doğan/eLogo/EDM için opsiyonel servis adresi (entegratörün bildirdiği uç; boş = varsayılan). */
  endpoint?: string | null;
  /** Paraşüt: OAuth uygulaması Client ID (Ayarlar → API'den oluşturulur). */
  parasutClientId?: string | null;
  /** Paraşüt: OAuth uygulaması Client Secret. */
  parasutClientSecret?: string | null;
  /** Paraşüt: Firma ID (adres çubuğundaki sayı). */
  parasutCompanyId?: string | null;
  /** Uyumsoft: mükellef e-SMM mükellefi mi? (null = kayıtlı bayrak korunur). */
  uyumsoftEsmm?: boolean | null;
  /** Uyumsoft: e-Fatura / e-Arşiv gelen kutusu da çekilsin mi? (null = kayıtlı bayrak korunur). */
  uyumsoftEinvoice?: boolean | null;
  useTestEnvironment: boolean;
}

export interface SyncResultResponse {
  added: number;
  skippedDuplicates: number;
  message: string;
}

export interface EInvoiceListItem {
  id: string;
  clientId: string;
  invoiceNumber: string;
  issueDate: string;
  senderTaxId: string;
  senderTitle: string;
  currencyCode: string;
  taxExclusiveAmount: number;
  taxTotal: number;
  payableAmount: number;
  status: EInvoiceStatusCode;
  codingStatus: CodingStatusCode;
  createdAt: string;
  /**
   * Satırların etkili hesap kodlarının (nihai ?? öneri) DISTINCT özeti — en fazla 3 kod
   * virgülle, fazlası " +N" (ör. "770.01, 153.01 +2"). Kod yoksa null.
   * codingStatus Approved değilse öneri kaynaklıdır (soluk/italik gösterilir).
   */
  accountCodeSummary: string | null;
  /** İlk satırın cari kodu (ör. "320.001"); yoksa null. */
  cariCode: string | null;
}

export interface EInvoiceLine {
  id: string;
  lineNumber: number;
  itemName: string;
  quantity: number;
  unitCode: string;
  unitPrice: number;
  lineAmount: number;
  vatRate: number;
  vatAmount: number;
  suggestedAccountCode: string | null;
  accountCode: string | null;
  /** Motor güven skoru 0-1 (Faz 2). Onaylı satırlarda 1.0. */
  confidence: number | null;
  /** Satıcının cari hesap kodu (ör. "320.001"). */
  cariCode: string | null;
  // ---------- Faz 8 — B2: Tevkifat / B3: KKEG ----------
  /** GİB kısmi tevkifat kodu (ör. "601"); yoksa null. */
  withholdingCode: string | null;
  /** Tevkifat oranı pay/payda (ör. "9/10"). */
  withholdingRate: string | null;
  /** Tevkif edilen KDV (KDV2'de sorumlu sıfatıyla beyan edilir). */
  withholdingVatAmount: number | null;
  /** Satırda KKEG kısmı var mı? */
  isKkeg: boolean;
  kkegReason: string | null;
  /** KKEG tutarı (matrahın indirilemeyen kısmı, KDV hariç). */
  kkegAmount: number | null;
}

export interface EInvoiceDetail extends EInvoiceListItem {
  externalUuid: string;
  direction: "Incoming" | "Outgoing";
  lines: EInvoiceLine[];
}

export interface EInvoicePage {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  items: EInvoiceListItem[];
}

// ---------- Faz 1 yardımcı fonksiyonları ----------

export function getClientIntegration(clientId: string) {
  return api<IntegrationResponse>(`/api/clients/${clientId}/integration`);
}

export function saveClientIntegration(clientId: string, body: IntegrationSaveRequest) {
  return api<IntegrationResponse>(`/api/clients/${clientId}/integration`, {
    method: "POST",
    body,
  });
}

export function deleteClientIntegration(clientId: string) {
  return api<void>(`/api/clients/${clientId}/integration`, { method: "DELETE" });
}

/**
 * Faz 15: tenant'ın TÜM mükellef entegrasyonları tek istekte (anahtarlar maskeli).
 * Mükellef listesinde sağlayıcı rozeti ve GİB Portalı "Elle doğrulama gerekli" uyarısı için.
 */
export function listClientIntegrations() {
  return api<IntegrationResponse[]>("/api/client-integrations");
}

/** Faz 15 — bağlantı testi sonucu (şu an yalnız GİB Portalı sağlayıcısında desteklenir). */
export interface IntegrationTestResult {
  success: boolean;
  /** true → GİB captcha/SMS istedi; sistem güvenlik gereği çözmez, elle doğrulama gerekir. */
  requiresManual: boolean;
  /** Faz 17: true → kayıtlı GİB oturumu yok/geçersiz; "GİB Oturumu Aç" ile doğrulama kodu girilmeli. */
  needsCaptchaLogin?: boolean;
  message: string;
}

/**
 * Bağlantıyı test eder. Kimlik gönderilirse onunla (henüz kaydedilmemiş bilgilerle),
 * gönderilmezse kayıtlı kimlikle denenir. Şifre yanıtta ASLA dönmez.
 */
export function testClientIntegration(
  clientId: string,
  body: { provider: IntegrationProviderCode; username?: string | null; password?: string | null }
) {
  return api<IntegrationTestResult>(`/api/clients/${clientId}/integration/test`, {
    method: "POST",
    body,
  });
}

// ---------- Faz 17: GİB insan destekli giriş (doğrulama kodu akışı) ----------

/**
 * Hata mesajı GİB "elle doğrulama / oturum yenileme" durumunu mu bildiriyor?
 * Senkron hata bantlarındaki "Doğrulama Kodunu Gir" düğmesi bu kontrole göre gösterilir.
 */
export function isGibManualVerificationMessage(message: string | null | undefined): boolean {
  if (!message) return false;
  return /Elle doğrulama gerekli|oturumu yenilenmeli|doğrulama kodu girilmeli/i.test(message);
}

/**
 * gib-login/begin yanıtı: robot GİB giriş ekranını açıp KAYITLI kimliği doldurdu;
 * kullanıcı görseldeki doğrulama kodunu okuyup complete ile girecek. Şifre ASLA dönmez.
 */
export interface GibLoginBeginResult {
  success: boolean;
  message: string;
  /** Bekleyen giriş oturumu kimliği (robotta 5 dk bekletilir). Başarısızsa null. */
  pendingId: string | null;
  /** Doğrulama kodu görseli — base64 PNG ("data:" öneki YOK). Yoksa null. */
  captchaImage: string | null;
  /** Kodun girilmesi için tanınan süre (saniye). */
  expiresInSeconds: number | null;
  /** true → kayıtlı GİB oturumu hâlâ geçerli; doğrulama koduna gerek yok. */
  alreadyLoggedIn: boolean;
}

/** gib-login/complete yanıtı: hatalı kodda AYNI pendingId ile YENİ captcha görseli döner. */
export interface GibLoginCompleteResult {
  success: boolean;
  message: string;
  /** Hatalı denemede YENİ doğrulama kodu görseli (base64 PNG); yoksa null. */
  captchaImage: string | null;
  /** Kalan deneme hakkı (en fazla 3); bilinmiyorsa null. */
  attemptsLeft: number | null;
  /** true → bekleyen oturum iptal/zaman aşımı; "Yeni Kod" ile baştan başlanmalı. */
  pendingExpired: boolean;
}

/** GİB girişini başlatır: captcha görseli + pendingId döner (kayıtlı kimlik kullanılır). */
export async function beginGibLogin(clientId: string, manual = false): Promise<GibLoginBeginResult> {
  if (manual) return api<GibLoginBeginResult>(`/api/clients/${clientId}/integration/gib-login/begin?manual=true`, {method:"POST"});
  const {jobId} = await api<{jobId:string}>(`/api/clients/${clientId}/integration/gib-login/auto-start`, {method:"POST"});
  // Short status requests avoid gateway timeouts. The durable job survives closing the panel.
  for (let attempt=0; attempt<180; attempt++) {
    const result = await api<{status:string; message?:string}>(`/api/clients/${clientId}/integration/gib-login/auto-status/${jobId}`);
    if (result.status === "Basarili") return {success:true, alreadyLoggedIn:true, message:"GİB oturumu doğrulandı ve tebligat taraması tamamlandı.", pendingId:null,captchaImage:null,expiresInSeconds:null};
    if (result.status === "Basarisiz" || result.status === "Iptal") throw new Error(result.message || "Otomatik giriş tamamlanamadı. Doğrulama ekranını doğrudan açabilirsiniz.");
    await new Promise(resolve => setTimeout(resolve, 3000));
  }
  throw new Error("İşlem arka planda devam ediyor. Durumunu Gönderim Kuyruğu ekranından izleyebilirsiniz.");
}

/** Kullanıcının okuduğu doğrulama koduyla GİB girişini tamamlar (oturum 12 saat saklanır). */
export function completeGibLogin(clientId: string, pendingId: string, code: string) {
  return api<GibLoginCompleteResult>(`/api/clients/${clientId}/integration/gib-login/complete`, {
    method: "POST",
    body: { pendingId, code },
  });
}

// ---------- Faz 18: Müşavir (SMMM) tek GİB girişi ----------
//
// Mali müşavir KENDİ GİB kimliğiyle bir kez giriş yapar; GİB "müşavir/mükellef işlemleri"
// özelliğiyle aynı oturumdan tüm mükelleflerin ekranlarına geçilir — mükellef başına
// ayrı şifre/captcha GEREKMEZ. Şifre yanıtlarda ASLA dönmez.

/** Müşavir GİB kimliği durumu (şifre maskeli — hiç dönmez). */
export interface GibAdvisorCredentialResult {
  hasCredential: boolean;
  /** Maskeli kullanıcı kodu (ilk 2 + son 2 karakter). Kayıt yoksa null. */
  maskedUsername: string | null;
  updatedAt: string | null;
}

export function getGibAdvisorCredentials() {
  return api<GibAdvisorCredentialResult>("/api/gib-advisor/credentials");
}

/** Müşavir GİB kullanıcı kodu + şifresini kaydeder (tenant başına tek kayıt, AES şifreli). */
export function saveGibAdvisorCredentials(username: string, password: string) {
  return api<GibAdvisorCredentialResult>("/api/gib-advisor/credentials", {
    method: "POST",
    body: { username, password },
  });
}

/** Müşavir GİB girişini başlatır (kayıtlı müşavir kimliğiyle; captcha görseli döner). */
export function beginGibAdvisorLogin() {
  return api<GibLoginBeginResult>("/api/gib-advisor/login/begin", { method: "POST" });
}

/** Müşavir GİB girişini tamamlar; oturum advisor anahtarıyla saklanır (tüm mükellefler için). */
export function completeGibAdvisorLogin(pendingId: string, code: string) {
  return api<GibLoginCompleteResult>("/api/gib-advisor/login/complete", {
    method: "POST",
    body: { pendingId, code },
  });
}

/** Müşavir GİB oturum durumu rozeti (aktif / yenilenmeli + son görülme). */
export interface GibAdvisorSessionStatus {
  active: boolean;
  expired: boolean;
  exists: boolean;
  lastSeenAt: string | null;
  message: string | null;
}

export function getGibAdvisorSession() {
  return api<GibAdvisorSessionStatus>("/api/gib-advisor/session");
}

/** GİB'den gelen tek müşavir mükellefi (sistem eşleşmesiyle). */
export interface GibAdvisorClientItem {
  taxId: string;
  title: string;
  /** true → VKN sistemdeki bir mükellefle eşleşti. */
  matched: boolean;
  clientId: string | null;
  clientTitle: string | null;
}

/** "Mükelleflerimi GİB'den Getir" yanıtı. */
export interface GibAdvisorClientsResult {
  success: boolean;
  message: string;
  items: GibAdvisorClientItem[];
  /** true → müşavir GİB oturumu yok/düşmüş; önce "GİB Oturumu Aç". */
  needsCaptchaLogin: boolean;
}

/** Müşavir oturumuyla GİB'deki mükellef listesini çeker ve VKN'leri sistemle eşleştirir. */
export function fetchGibAdvisorClients() {
  return api<GibAdvisorClientsResult>("/api/gib-advisor/clients", { method: "POST" });
}

export function syncClientEInvoices(clientId: string, from?: string, to?: string) {
  const params = new URLSearchParams();
  if (from) params.set("from", from);
  if (to) params.set("to", to);
  const qs = params.toString();
  return api<SyncResultResponse>(
    `/api/clients/${clientId}/einvoices/sync${qs ? `?${qs}` : ""}`,
    { method: "POST" }
  );
}

export function listEInvoices(options: {
  clientId?: string;
  /** Çoklu mükellef filtresi (çoklu seçici) — clientId ile birlikte kullanılmaz. */
  clientIds?: string[];
  status?: string;
  q?: string;
  page?: number;
  pageSize?: number;
} = {}) {
  const params = new URLSearchParams();
  if (options.clientId) params.set("clientId", options.clientId);
  for (const id of options.clientIds ?? []) params.append("clientIds", id);
  if (options.status) params.set("status", options.status);
  if (options.q?.trim()) params.set("q", options.q.trim());
  if (options.page) params.set("page", String(options.page));
  if (options.pageSize) params.set("pageSize", String(options.pageSize));
  const qs = params.toString();
  return api<EInvoicePage>(`/api/einvoices${qs ? `?${qs}` : ""}`);
}

// ---------- Çoklu mükellef toplu senkron (sync-bulk — kuyruk tabanlı) ----------

/**
 * Toplu senkron özeti: senkronlar hemen çalıştırılmaz, mükellef başına "FaturaSync"
 * kuyruk işi açılır (Gönderim Kuyruğu sayfasından izlenebilir).
 */
export interface BulkSyncResult {
  queued: number;
  skippedNoIntegration: number;
  /** Türkçe özet, ör. "8 mükellef için senkron kuyruğa alındı, 2 mükellefte entegrasyon yok." */
  message: string;
}

/**
 * Seçili (boşsa TÜM aktif) mükellefler için gelen fatura senkronunu kuyruğa alır.
 * Entegrasyonu olmayanlar sessizce atlanır ve sayısı yanıtta döner.
 */
export function syncBulkEInvoices(clientIds: string[], from?: string, to?: string) {
  return api<BulkSyncResult>("/api/einvoices/sync-bulk", {
    method: "POST",
    body: { clientIds, from: from || null, to: to || null },
  });
}

export function getEInvoice(id: string) {
  return api<EInvoiceDetail>(`/api/einvoices/${id}`);
}

// ---------- Faz 2: Otomatik Hesap/Cari Kod Motoru ----------

export interface CodingLineSuggestion {
  lineId: string;
  lineNumber: number;
  itemName: string;
  suggestedAccountCode: string | null;
  confidence: number | null;
  cariCode: string | null;
  /** Öneri kaynağı katman: "Hafıza", "Satıcı varsayılanı", "Kural", "AI", "Varsayılan gider". */
  source: string;
}

export interface CodingResult {
  eInvoiceId: string;
  codingStatus: CodingStatusCode;
  message: string;
  lines: CodingLineSuggestion[];
}

export interface CodeBatchResult {
  total: number;
  autoCoded: number;
  needsReview: number;
  message: string;
}

export interface ApproveLine {
  lineId: string;
  accountCode?: string | null;
  cariCode?: string | null;
  // Faz 8 — B2: null/undefined = dokunma; "" = tevkifatı temizle; dolu = uygula.
  withholdingCode?: string | null;
  withholdingRate?: string | null;
  withholdingVatAmount?: number | null;
  // Faz 8 — B3: null/undefined = dokunma; false = işareti kaldır; true = işaretle.
  isKkeg?: boolean | null;
  kkegReason?: string | null;
  kkegAmount?: number | null;
}

/** Faz 8 — B2: Tevkifat kodu kuralı (onay çekmecesi açılır menüsü). */
export interface WithholdingRuleItem {
  code: string;
  description: string;
  /** Pay/payda (ör. "9/10"). */
  fraction: string;
}

export function listWithholdingRules() {
  return api<WithholdingRuleItem[]>("/api/coding/withholding-rules");
}

export interface CodingRule {
  id: string;
  clientId: string | null;
  clientTitle: string | null;
  pattern: string;
  accountCode: string;
  priority: number;
  isActive: boolean;
  createdAt: string;
}

export interface CodingRuleSaveRequest {
  pattern: string;
  accountCode: string;
  priority: number;
  clientId?: string | null;
  isActive: boolean;
}

export interface ClientCodingSettings {
  id: string;
  clientId: string;
  defaultExpenseAccount: string;
  cariPrefix: string;
  cariNextNumber: number;
  autoApplyThreshold: number;
  /** Faz 8 (B3): mükellefin binek aracı var mı? (akaryakıt KKEG kuralı anahtarı) */
  hasCompanyVehicle: boolean;
}

export interface LucaExportPreview {
  approvedCount: number;
  totalAmount: number;
  /** Aralıktaki onaylanmış fiş sayısı (Faz 3 — dosyaya fiş belge türüyle dahil edilir). */
  approvedReceiptCount: number;
  receiptTotalAmount: number;
  /**
   * Fiş Aktarım Şablonu zorunlu alan ön-kontrolü: eksik alanlı belgelerin Türkçe listesi.
   * Liste doluysa indirme sunucu tarafında ENGELLENİR — önce eksikler giderilmelidir.
   */
  missingFields: string[];
}

/** Hesap/cari kodu biçim kontrolü (UI tarafı; backend de aynı deseni doğrular). */
export const ACCOUNT_CODE_REGEX = /^\d{3}(\.\d{1,4}){0,3}$/;

export function codeEInvoice(id: string) {
  return api<CodingResult>(`/api/einvoices/${id}/code`, { method: "POST" });
}

export function codeBatch(clientId: string, from?: string, to?: string) {
  const params = new URLSearchParams({ clientId });
  if (from) params.set("from", from);
  if (to) params.set("to", to);
  return api<CodeBatchResult>(`/api/einvoices/code-batch?${params.toString()}`, {
    method: "POST",
  });
}

// ---------- Çoklu mükellef dönem kodlama (code-bulk) ----------

/** code-bulk: tek mükellefin kodlama kırılımı (yalnız faturası olan mükellefler döner). */
export interface CodeBulkClientResult {
  clientId: string;
  clientTitle: string;
  total: number;
  autoCoded: number;
  needsReview: number;
}

/** code-bulk özeti: mükellef başına kırılım + toplamlar. */
export interface CodeBulkResult {
  clients: CodeBulkClientResult[];
  total: number;
  autoCoded: number;
  needsReview: number;
  /** Türkçe özet, ör. "59 fatura kodlandı (47 otomatik, 12 onay bekliyor)." */
  message: string;
}

/**
 * Seçili (boşsa TÜM aktif) mükelleflerin aralıktaki kodlanmamış faturalarını mükellef
 * başına döngüyle motordan geçirir (kod motoru mükellef izolasyonlu — karışma yok).
 */
export function codeBulkPeriod(clientIds: string[], from?: string, to?: string) {
  return api<CodeBulkResult>("/api/coding/code-bulk", {
    method: "POST",
    body: { clientIds, from: from || null, to: to || null },
  });
}

export function approveEInvoiceCoding(id: string, lines: ApproveLine[]) {
  return api<EInvoiceDetail>(`/api/einvoices/${id}/approve`, {
    method: "POST",
    body: { lines },
  });
}

export function getReviewQueue(clientId?: string) {
  const qs = clientId ? `?clientId=${clientId}` : "";
  return api<EInvoiceListItem[]>(`/api/coding/review-queue${qs}`);
}

// ---------- Toplu onay / toplu kodlama (liste ekranı seçim kutuları) ----------

/** Toplu onay özeti: onaylanan / atlanan (zaten onaylı) / hata alan fatura sayıları. */
export interface BulkApproveResult {
  approved: number;
  skipped: number;
  failed: number;
  /** Türkçe özet, ör. "42 fatura onaylandı, 3 atlandı (zaten onaylı), 2 hata." */
  message: string;
}

/** Toplu kodlama (onaysız) özeti. */
export interface BulkCodeResult {
  autoCoded: number;
  needsReview: number;
  skipped: number;
  failed: number;
  message: string;
}

/**
 * Seçilen faturaların ÖNERİLEN kodlarını düzeltmesiz kesinleştirir (motor öğrenir).
 * Uncoded fatura önce kodlanır; zaten Approved olan atlanır.
 */
export function bulkApproveEInvoices(ids: string[]) {
  return api<BulkApproveResult>("/api/einvoices/bulk-approve", {
    method: "POST",
    body: { ids },
  });
}

/** Seçilen faturaları motordan geçirir (yalnız kodlar, onaylamaz). */
export function bulkCodeEInvoices(ids: string[]) {
  return api<BulkCodeResult>("/api/einvoices/bulk-code", {
    method: "POST",
    body: { ids },
  });
}

/**
 * Toplu silme özeti: silinen / durumu uygun olmadığı için atlanan / bulunamayan (başka
 * tenant'a ait ya da zaten silinmiş) kayıt sayıları. Faturada skipped hep 0'dır.
 */
export interface BulkDeleteResult {
  deleted: number;
  skipped: number;
  failed: number;
  /** Türkçe özet, ör. "5 fatura silindi, 1 hata." */
  message: string;
}

/** Seçilen faturaları kalem satırlarıyla birlikte KALICI olarak siler (geri alınamaz). */
export function bulkDeleteEInvoices(ids: string[]) {
  return api<BulkDeleteResult>("/api/einvoices/bulk-delete", {
    method: "POST",
    body: { ids },
  });
}

// ---------- Onay kaldırma (fatura + fiş) ----------

/**
 * Onay kaldırma özeti: reverted = Approved → Onay Bekliyor'a döndürülen; skipped = onaylı
 * olmadığı için atlanan; failed = bulunamayan kayıt sayısı. Kod değerleri ve motorun
 * öğrenme hafızası korunur (yeniden onayda üzerine yazılır).
 */
export interface BulkUnapproveResult {
  reverted: number;
  skipped: number;
  failed: number;
  /** Türkçe özet, ör. "5 faturanın onayı kaldırıldı, 2 atlandı (onaylı değil)." */
  message: string;
}

/** Tek faturanın onayını kaldırır (Approved → Onay Bekliyor); kodlar ve öğrenme korunur. */
export function unapproveEInvoice(id: string) {
  return api<EInvoiceDetail>(`/api/einvoices/${id}/unapprove`, { method: "POST" });
}

/** Seçilenlerden Approved olan faturaların onayını kaldırır; diğerleri atlanır (skipped). */
export function bulkUnapproveEInvoices(ids: string[]) {
  return api<BulkUnapproveResult>("/api/einvoices/bulk-unapprove", {
    method: "POST",
    body: { ids },
  });
}

export function listCodingRules() {
  return api<CodingRule[]>("/api/coding/rules");
}

export function createCodingRule(body: CodingRuleSaveRequest) {
  return api<CodingRule>("/api/coding/rules", { method: "POST", body });
}

export function deleteCodingRule(id: string) {
  return api<void>(`/api/coding/rules/${id}`, { method: "DELETE" });
}

export function getCodingSettings(clientId: string) {
  return api<ClientCodingSettings>(`/api/coding/settings/${clientId}`);
}

export function updateCodingSettings(
  clientId: string,
  body: Omit<ClientCodingSettings, "id" | "clientId">
) {
  return api<ClientCodingSettings>(`/api/coding/settings/${clientId}`, {
    method: "PUT",
    body,
  });
}

// ---------- Hesap kodu autocomplete + yerinde düzenleme + toplu içe aktarma ----------

/** Hesap kodu autocomplete öneri satırı (kod + varsa hesap adı / satıcı unvanı). */
export interface AccountSuggestion {
  code: string;
  /** Hesap adı (cari kodlarda satıcı unvanı); bilinmiyorsa null. */
  name: string | null;
}

/**
 * Hesap kodu araması: q rakam/nokta ile başlıyorsa KOD ÖN EKİ eşleşmesi ("700." → 700. ile
 * başlayanlar), aksi halde hesap ADINDA Türkçe duyarsız arama ("kira" → "KİRA GİDERİ").
 * clientId verilmezse tenant genelinde aranır (kurallar sayfası "tüm mükellefler" kapsamı).
 */
export function searchAccountCodes(
  q: string,
  options: { clientId?: string; limit?: number } = {}
) {
  const params = new URLSearchParams();
  if (options.clientId) params.set("clientId", options.clientId);
  if (q) params.set("q", q);
  params.set("limit", String(options.limit ?? 15));
  return api<AccountSuggestion[]>(`/api/coding/accounts?${params.toString()}`);
}

/**
 * Liste ekranı yerinde düzenleme: hesap ve/veya cari kodu faturanın TÜM satırlarına uygular.
 * Fatura onaylıysa sunucu 409 + "Önce onayı kaldırın." döner; değilse durum Onay Bekliyor
 * (NeedsReview) olur/kalır. Öğrenme yazılmaz (öğrenme yalnızca onay akışındadır).
 */
export function setEInvoiceCodes(
  id: string,
  body: { accountCode?: string | null; cariCode?: string | null }
) {
  return api<EInvoiceDetail>(`/api/einvoices/${id}/set-codes`, { method: "POST", body });
}

/** Toplu cari/hesap planı içe aktarma özeti (satır hataları Türkçe listelenir). */
export interface CodingImportResult {
  imported: number;
  updated: number;
  skipped: number;
  errors: string[];
  /** Türkçe özet, ör. "12 kayıt eklendi, 3 güncellendi, 2 satırda hata." */
  message: string;
}

/** Cari/hesap planı içe aktarma şablonunu (.xlsx) indirir. */
export function downloadCodingImportTemplate(clientId: string) {
  return apiDownload(`/api/coding/import-template?clientId=${clientId}`);
}

/**
 * Ortak multipart içe aktarma çağrısı (clientId + dosya: .xlsx veya .csv): cari şablonu,
 * mizan, geçmiş fiş listesi ve hesap planı (CSV) uçları aynı gövde biçimini kullanır.
 * clientId null ise gönderilmez (mükellef listesi / araç plakası uçları mükellef-bağımsızdır).
 */
async function postImportFile<T>(path: string, clientId: string | null, file: File): Promise<T> {
  const form = new FormData();
  if (clientId) form.append("clientId", clientId);
  form.append("file", file, file.name);

  const headers: Record<string, string> = {};
  const token = getToken();
  if (token) headers["Authorization"] = `Bearer ${token}`;

  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, {
      method: "POST",
      headers,
      body: form,
      cache: "no-store",
    });
  } catch {
    throw new ApiError(0, "Sunucuya ulaşılamıyor. İnternet bağlantınızı ve API'nin çalıştığını kontrol edin.");
  }

  if (res.status === 401) {
    clearToken();
    if (typeof window !== "undefined" && !window.location.pathname.startsWith("/giris")) {
      window.location.href = "/giris";
    }
    throw new ApiError(401, "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.");
  }

  if (!res.ok) {
    throw new ApiError(res.status, await parseErrorMessage(res));
  }

  return (await res.json()) as T;
}

/**
 * Doldurulmuş şablonu içe aktarır (multipart): VKN+Cari Kod satıcı→cari hafızasına,
 * cari/hesap kod + ad mükellef hesap planına (autocomplete kaynağı) yazılır.
 * Sınırlar: 2 MB, yalnızca .xlsx. Hatalı satırlar işlemi durdurmaz.
 */
export function importCodingData(clientId: string, file: File): Promise<CodingImportResult> {
  return postImportFile<CodingImportResult>("/api/coding/import", clientId, file);
}

/** Mizan içe aktarma özeti (api/coding/import-mizan). */
export interface MizanImportResult {
  /** Hesap planına işlenen YAPRAK hesap sayısı. */
  accounts: number;
  /** Cari hafızaya (unvan anahtarlı) yazılan 120/320 hesap sayısı. */
  cariCount: number;
  /** Türkçe özet. */
  message: string;
}

/**
 * Luca mizanını OLDUĞU GİBİ içe aktarır: yaprak hesaplar hesap planına, 120/320 carileri
 * kod motorunun unvan-bazlı cari hafızasına yazılır. Sınırlar: 5 MB, yalnızca .xlsx.
 */
export function importMizan(clientId: string, file: File): Promise<MizanImportResult> {
  return postImportFile<MizanImportResult>("/api/coding/import-mizan", clientId, file);
}

/** Geçmiş fiş listesi içe aktarma özeti (api/coding/import-fis-listesi). */
export interface FisListesiImportResult {
  /** İşlenen (satış hariç) fiş grubu sayısı. */
  vouchers: number;
  /** Unvan → gider hesabı / cari kodu öğrenmesi yazılan satıcı sayısı. */
  vendorMappings: number;
  /** Hesap planına eklenen YENİ hesap kodu sayısı. */
  accounts: number;
  /** "satış faturası" olduğu için atlanan fiş grubu sayısı. */
  skippedSales: number;
  /** Türkçe özet. */
  message: string;
}

/**
 * Luca geçmiş fiş dökümünü (14 kolonlu Fiş Aktarım Şablonu) OLDUĞU GİBİ içe aktarır:
 * satıcı unvanı → gider hesabı / cari kodu alışkanlıkları motorun hafızasına yazılır.
 */
export function importFisListesi(clientId: string, file: File): Promise<FisListesiImportResult> {
  return postImportFile<FisListesiImportResult>("/api/coding/import-fis-listesi", clientId, file);
}

/** Luca hesap planı (CSV) içe aktarma özeti (api/coding/import-hesap-plani). */
export interface HesapPlaniImportResult {
  /** Hesap planına işlenen ad dolu satır sayısı (grup başlıkları dahil). */
  accounts: number;
  /** Cari hafızaya (VKN + normalize unvan anahtarlı) yazılan 120/320 satır sayısı. */
  cariCount: number;
  /** Türkçe özet. */
  message: string;
}

/**
 * Luca "Hesap Planı → Dışa Aktar" CSV dosyasını OLDUĞU GİBİ içe aktarır: ad dolu tüm satırlar
 * (grup başlıkları dahil) mükellef hesap planına, Vergi No/TCKN'li 120/320 satırları kod
 * motorunun cari hafızasına yazılır. Sınırlar: 2 MB, yalnızca .csv (ISO-8859-9 veya UTF-8 BOM).
 */
export function importHesapPlani(clientId: string, file: File): Promise<HesapPlaniImportResult> {
  return postImportFile<HesapPlaniImportResult>("/api/coding/import-hesap-plani", clientId, file);
}

// ---------- Mükellef listesi + araç plakası toplu içe aktarma (mükellef-bağımsız) ----------

/**
 * Mükellef listesi içe aktarma özeti (api/clients/import). VKN'si zaten kayıtlı mükellefler
 * DEĞİŞTİRİLMEZ (skipped); satır hataları Türkçe listelenir.
 */
export interface ClientImportResult {
  imported: number;
  skipped: number;
  errors: string[];
  /** Türkçe özet, ör. "12 mükellef eklendi, 3 satır atlandı (VKN zaten kayıtlı)." */
  message: string;
}

/** Mükellef listesi içe aktarma şablonunu (.xlsx) indirir — mükellef seçimi gerektirmez. */
export function downloadClientImportTemplate() {
  return apiDownload("/api/clients/import-template");
}

/**
 * Doldurulmuş mükellef listesi şablonunu içe aktarır: Unvan + VKN/TCKN zorunlu; VKN üzerinden
 * upsert — var olan mükellefe dokunulmaz, yeni olan plan kotası dahilinde eklenir.
 * Sınırlar: 2 MB, yalnızca .xlsx, en fazla 1000 satır. Hatalı satırlar işlemi durdurmaz.
 */
export function importClients(file: File): Promise<ClientImportResult> {
  return postImportFile<ClientImportResult>("/api/clients/import", null, file);
}

/** Araç plakası içe aktarma özeti (api/vehicles/import). */
export interface VehicleImportResult {
  imported: number;
  skipped: number;
  errors: string[];
  /** Türkçe özet, ör. "8 araç eklendi, 2 satır atlandı (plaka zaten kayıtlı)." */
  message: string;
}

/** Araç plakası içe aktarma şablonunu (.xlsx) indirir — mükellef VKN kolonuyla eşleşir. */
export function downloadVehicleImportTemplate() {
  return apiDownload("/api/vehicles/import-template");
}

/**
 * Doldurulmuş araç plakası şablonunu içe aktarır: her satır Mükellef VKN'den eşleşir
 * (bulunamayan satır hata), plaka normalize edilir, tip BINEK/TICARI olarak eşlenir,
 * kayıtlı plaka atlanır. Sınırlar: 2 MB, yalnızca .xlsx, en fazla 1000 satır.
 */
export function importVehicles(file: File): Promise<VehicleImportResult> {
  return postImportFile<VehicleImportResult>("/api/vehicles/import", null, file);
}

/** Faz 14 (C): Luca dışa aktarma kaynak filtresi — all: fatura+fiş (mevcut davranış), fis/fatura: tek kaynak. */
export type LucaExportSource = "all" | "fis" | "fatura";

export function getLucaExportPreview(
  clientId: string,
  from: string,
  to: string,
  source: LucaExportSource = "all"
) {
  const params = new URLSearchParams({ clientId, from, to, source });
  return api<LucaExportPreview>(`/api/export/luca/preview?${params.toString()}`);
}

/**
 * Dosya indirme (blob): Authorization header'ı ile GET atar, hata gövdesindeki
 * Türkçe mesajı fırlatır, Content-Disposition'daki dosya adını çözer.
 */
export async function apiDownload(path: string): Promise<{ blob: Blob; fileName: string | null }> {
  const headers: Record<string, string> = {};
  const token = getToken();
  if (token) headers["Authorization"] = `Bearer ${token}`;

  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, { headers, cache: "no-store" });
  } catch {
    throw new ApiError(0, "Sunucuya ulaşılamıyor. İnternet bağlantınızı ve API'nin çalıştığını kontrol edin.");
  }

  if (res.status === 401) {
    clearToken();
    if (typeof window !== "undefined" && !window.location.pathname.startsWith("/giris")) {
      window.location.href = "/giris";
    }
    throw new ApiError(401, "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.");
  }

  if (!res.ok) {
    throw new ApiError(res.status, await parseErrorMessage(res));
  }

  // Content-Disposition: attachment; filename=...; filename*=UTF-8''...
  let fileName: string | null = null;
  const disposition = res.headers.get("content-disposition");
  if (disposition) {
    const utf8Match = disposition.match(/filename\*=UTF-8''([^;]+)/i);
    const plainMatch = disposition.match(/filename="?([^";]+)"?/i);
    if (utf8Match) fileName = decodeURIComponent(utf8Match[1]);
    else if (plainMatch) fileName = plainMatch[1];
  }

  return { blob: await res.blob(), fileName };
}

/** Luca dışa aktarma dosya biçimi: xlsx (Fiş Aktarım Şablonu, önerilen) veya csv (aynı kolonlar). */
export type LucaExportFormat = "xlsx" | "csv";

/**
 * Luca "Fiş Aktarım Şablonu"na birebir uygun dosyayı indirir.
 * format=xlsx (varsayılan): sayfa adı "Fiş Aktarım Şablon", 14 kolon (A→N);
 * format=csv: aynı kolon sırası/başlıklarla ;-ayraçlı UTF-8 BOM'lu CSV.
 * Faz 14 (C): source=fis yalnız onaylı fişleri, source=fatura yalnız onaylı faturaları
 * indirir; varsayılan all mevcut davranıştır. Zorunlu alanı eksik belge varsa sunucu
 * 400 + Türkçe eksik listesiyle engeller.
 */
export function downloadLucaExport(
  clientId: string,
  from: string,
  to: string,
  format: LucaExportFormat = "xlsx",
  source: LucaExportSource = "all"
) {
  const params = new URLSearchParams({ clientId, from, to, format, source });
  return apiDownload(`/api/export/luca?${params.toString()}`);
}

/**
 * Çoklu mükellef Luca export (ZIP): seçili (boşsa TÜM aktif) mükelleflerin her biri için
 * ayrı XLSX (şablon birebir aynı) üretilir ve "Luca_Fisleri_{tarih}.zip" olarak iner.
 * Verisi olmayan mükellef zip'e girmez; hiç veri yoksa sunucu 404 + Türkçe mesaj döner.
 * Tek mükellef seçiliyken mevcut tekil uç (downloadLucaExport) kullanılmalıdır.
 */
export function downloadLucaBulkExport(
  clientIds: string[],
  from: string,
  to: string,
  source: LucaExportSource = "all"
) {
  const params = new URLSearchParams({ from, to, source });
  for (const id of clientIds) params.append("clientIds", id);
  return apiDownload(`/api/export/luca-bulk?${params.toString()}`);
}

// ---------- Faz 3: Fiş OCR ----------

export type ReceiptStatusCode = "Uploaded" | "Processing" | "NeedsReview" | "Approved" | "Rejected";

/** Faz 16: fiş kaydının belge türü — Fis: gider fişi; ZRaporu: yazar kasa günlük kapanış (gelir). */
export type ReceiptDocumentKind = "Fis" | "ZRaporu";

/** Faz 16: oran bazlı KDV kırılım satırı (oran % / matrah / KDV). */
export interface ReceiptVatBreakdownRow {
  vatRate: number;
  netAmount: number;
  vatAmount: number;
}

/** Faz 13: plaka eşleşme durumu — Yok: plaka okunmadı; Eslesti: kayıtlı araç; Eslesmedi: kayıtlı değil. */
export type PlateMatchCode = "Yok" | "Eslesti" | "Eslesmedi";

export interface ReceiptResponse {
  id: string;
  clientId: string;
  clientTitle: string | null;
  fileName: string;
  status: ReceiptStatusCode;
  vendorName: string | null;
  vendorTaxId: string | null;
  /** Fiş/belge numarası (Z no) — Luca şablonunda Evrak No kolonuna gider. */
  documentNumber: string | null;
  receiptDate: string | null;
  totalAmount: number | null;
  vatRate: number | null;
  vatAmount: number | null;
  netAmount: number | null;
  /** Faz 16: "Fis" (gider fişi) | "ZRaporu" (yazar kasa günlük kapanış — gelir). */
  documentKind: ReceiptDocumentKind;
  /** Faz 16: fişteki toplam iskonto/indirim (pozitif); yoksa null. */
  discountTotal: number | null;
  /** Faz 16: oran bazlı KDV kırılımı (detay ucunda dolar; liste ucunda boş olabilir). */
  vatBreakdown: ReceiptVatBreakdownRow[];
  /** Faz 16 (Z raporu): nakit tahsilat toplamı; fişlerde null. */
  cashTotal: number | null;
  /** Faz 16 (Z raporu): kart tahsilat toplamı; fişlerde null. */
  cardTotal: number | null;
  suggestedAccountCode: string | null;
  accountCode: string | null;
  cariCode: string | null;
  /** Birleşik güven 0-1: min(OCR güveni, kod önerisi güveni). Onaylı fişte 1.0. */
  confidence: number | null;
  expenseEligible: boolean | null;
  ineligibleReason: string | null;
  rejectReason: string | null;
  /** Faz 8 (B3): fişte KKEG kısmı var mı? */
  isKkeg: boolean;
  kkegReason: string | null;
  /** KKEG tutarı (matrahın indirilemeyen kısmı, KDV hariç). */
  kkegAmount: number | null;
  /** Faz 13: fişten okunan normalize plaka (ör. "34FPC630"); okunamadıysa null. */
  plateNumber: string | null;
  /** Faz 13: plakanın kayıtlı araçlarla eşleşme durumu. */
  plateMatch: PlateMatchCode;
  /** Faz 13: fişe ait Türkçe uyarılar (plaka uyarıları; boş liste olabilir). */
  warnings: string[];
  /** Güven eşiği üstü + gider gösterilebilir → "otomatik onaya hazır" rozeti (oto-onay YOK). */
  autoApproveReady: boolean;
  /** Belge tarihi 30 günden eskiyse uyarı işareti (gider gösterilebilirlik etkilenmez). */
  isOldDocument: boolean;
  createdAt: string;
}

export interface ReceiptUploadItem {
  fileName: string;
  success: boolean;
  /** Başarısızsa dosya bazlı Türkçe hata mesajı. */
  message: string | null;
  receipt: ReceiptResponse | null;
}

export interface ReceiptUploadResult {
  message: string;
  items: ReceiptUploadItem[];
}

export interface ReceiptPage {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  items: ReceiptResponse[];
}

export interface ApproveReceiptRequest {
  vendorName?: string | null;
  vendorTaxId?: string | null;
  /** Fiş/belge numarası düzeltmesi (Luca Evrak No). Boş gönderilirse mevcut değer korunur. */
  documentNumber?: string | null;
  receiptDate?: string | null;
  totalAmount?: number | null;
  vatRate?: number | null;
  vatAmount?: number | null;
  netAmount?: number | null;
  /** Faz 16: iskonto düzeltmesi; 0 gönderilirse alan temizlenir, undefined = dokunma. */
  discountTotal?: number | null;
  /** Faz 16: KDV kırılımı düzeltmesi; null/undefined = dokunma, dolu liste = satırlar değiştirilir. */
  vatBreakdown?: ReceiptVatBreakdownRow[] | null;
  accountCode?: string | null;
  cariCode?: string | null;
  /** Gider gösterilemez işaretli fişi SMMM sorumluluğuyla yine de onaylar. */
  overrideEligibility?: boolean;
  // Faz 8 — B3: null/undefined = dokunma; false = KKEG işaretini kaldır; true = işaretle.
  isKkeg?: boolean | null;
  kkegReason?: string | null;
  kkegAmount?: number | null;
}

/**
 * Çoklu fiş yükleme (multipart). fetch upload ilerlemesi vermediği için XMLHttpRequest
 * kullanılır; onProgress 0-100 arası yüzde alır. Hata gövdesindeki Türkçe mesaj fırlatılır.
 */
export function uploadReceipts(
  clientId: string,
  files: File[],
  onProgress?: (percent: number) => void,
  documentKind: ReceiptDocumentKind = "Fis"
): Promise<ReceiptUploadResult> {
  return new Promise((resolve, reject) => {
    const form = new FormData();
    for (const file of files) form.append("files", file, file.name);
    // Faz 16: belge türü — "ZRaporu" seçilirse OCR, Z raporu prompt'uyla okur.
    form.append("documentKind", documentKind);

    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${API_URL}/api/clients/${clientId}/receipts`);

    const token = getToken();
    if (token) xhr.setRequestHeader("Authorization", `Bearer ${token}`);

    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && onProgress) onProgress(Math.round((e.loaded / e.total) * 100));
    };

    xhr.onload = () => {
      if (xhr.status === 401) {
        clearToken();
        if (typeof window !== "undefined" && !window.location.pathname.startsWith("/giris")) {
          window.location.href = "/giris";
        }
        reject(new ApiError(401, "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın."));
        return;
      }

      let body: unknown = null;
      try {
        body = JSON.parse(xhr.responseText);
      } catch {
        // gövde JSON değilse aşağıdaki genel mesaja düş
      }

      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(body as ReceiptUploadResult);
      } else {
        const message =
          body && typeof (body as { message?: unknown }).message === "string"
            ? ((body as { message: string }).message)
            : "Yükleme sırasında bir hata oluştu. Lütfen tekrar deneyin.";
        reject(new ApiError(xhr.status, message));
      }
    };

    xhr.onerror = () =>
      reject(new ApiError(0, "Sunucuya ulaşılamıyor. İnternet bağlantınızı ve API'nin çalıştığını kontrol edin."));

    xhr.send(form);
  });
}

export function listReceipts(options: {
  clientId?: string;
  status?: string;
  page?: number;
  pageSize?: number;
} = {}) {
  const params = new URLSearchParams();
  if (options.clientId) params.set("clientId", options.clientId);
  if (options.status) params.set("status", options.status);
  if (options.page) params.set("page", String(options.page));
  if (options.pageSize) params.set("pageSize", String(options.pageSize));
  const qs = params.toString();
  return api<ReceiptPage>(`/api/receipts${qs ? `?${qs}` : ""}`);
}

export function getReceipt(id: string) {
  return api<ReceiptResponse>(`/api/receipts/${id}`);
}

/**
 * Fiş dosyasını Authorization header'ıyla indirip tarayıcı içi önizleme için
 * object URL döner. Çağıran, işi bitince URL.revokeObjectURL ile temizlemelidir.
 */
export async function fetchReceiptFileUrl(id: string): Promise<{ url: string; fileName: string | null }> {
  const { blob, fileName } = await apiDownload(`/api/receipts/${id}/file`);
  return { url: URL.createObjectURL(blob), fileName };
}

export function approveReceipt(id: string, body: ApproveReceiptRequest) {
  return api<ReceiptResponse>(`/api/receipts/${id}/approve`, { method: "POST", body });
}

export function rejectReceipt(id: string, reason: string) {
  return api<ReceiptResponse>(`/api/receipts/${id}/reject`, {
    method: "POST",
    body: { reason },
  });
}

export function deleteReceipt(id: string) {
  return api<void>(`/api/receipts/${id}`, { method: "DELETE" });
}

// ---------- Fişlerde toplu onay / toplu silme (liste ekranı seçim kutuları) ----------

/**
 * Seçilen fişleri DÜZELTMESİZ onaylar (tekil onayla aynı iş mantığı — öğrenme dahil).
 * Yalnız "Onay Bekliyor" durumundakiler onaylanır; uygun olmayanlar sunucuda sessizce
 * atlanır ve sayısı skipped olarak döner (tarih/tutar/hesap kodu eksikler failed).
 */
export function bulkApproveReceipts(ids: string[]) {
  return api<BulkApproveResult>("/api/receipts/bulk-approve", {
    method: "POST",
    body: { ids },
  });
}

/**
 * Seçilen fişleri dosyalarıyla birlikte KALICI olarak siler (geri alınamaz).
 * Yalnız yüklenmiş/onay bekleyen fişler silinir; onaylı/reddedilmişler atlanır (skipped).
 */
export function bulkDeleteReceipts(ids: string[]) {
  return api<BulkDeleteResult>("/api/receipts/bulk-delete", {
    method: "POST",
    body: { ids },
  });
}

/** Tek fişin onayını kaldırır (Approved → Onay Bekliyor); alanlar ve öğrenme korunur. */
export function unapproveReceipt(id: string) {
  return api<ReceiptResponse>(`/api/receipts/${id}/unapprove`, { method: "POST" });
}

/** Seçilenlerden Approved olan fişlerin onayını kaldırır; diğerleri atlanır (skipped). */
export function bulkUnapproveReceipts(ids: string[]) {
  return api<BulkUnapproveResult>("/api/receipts/bulk-unapprove", {
    method: "POST",
    body: { ids },
  });
}

// ---------- Faz 14 (B): Aylık OCR kota durumu ----------

/**
 * Aylık fiş kota durumu: used = bu takvim ayında yüklenen fiş sayısı (büro geneli),
 * quota = plandaki aylık OCR hakkı. unlimited=true ise denetim yok (plansız kayıt
 * veya sınırsız kota) — gösterge gizlenebilir.
 */
export interface ReceiptQuota {
  used: number;
  quota: number;
  unlimited: boolean;
}

export function getReceiptQuota() {
  return api<ReceiptQuota>("/api/receipts/quota");
}

// ---------- Faz 15: KKEG oranı (fiş detayı otomatik doldurma) ----------

/**
 * KKEG (binek gider kısıtı) yüzdesi — backend, parametre merkezindeki
 * binek.gider.kisit.oran (indirilebilir %) değerinden türetir (varsayılan 30).
 * Fiş detayı bu değeri bir kez çekip "KKEG tutarı = matrah × oran" önerisini hesaplar;
 * uç erişilemezse çağıran 30'a düşmelidir (akış engellenmez).
 */
export interface KkegParameters {
  /** KKEG yüzdesi (0-100; varsayılan 30). */
  rate: number;
}

export function getKkegParameters() {
  return api<KkegParameters>("/api/coding/parameters/kkeg");
}

// ---------- Faz 4: e-Tebligat ----------

export interface ETebligatItem {
  id: string;
  clientId: string;
  clientTitle: string | null;
  externalId?: string;
  sender: string;
  subject: string;
  deliveredAt: string;
  legalDeadline: string | null;
  /** "İtiraz" / "Ödeme" / "Bilgi". */
  deadlineType: string | null;
  isRead: boolean;
  readAt: string | null;
  /** Son güne kalan gün; 0 ve altı = süre doldu, null = süresiz bilgilendirme. */
  daysRemaining: number | null;
  /** Kalan süre ≤ 3 gün (süresi geçmişler dahil). */
  isUrgent: boolean;
  createdAt: string;
  hasDocument?: boolean;
  documentFileName?: string | null;
  documentContent?: string | null;
}

export interface ETebligatList {
  totalCount: number;
  unreadCount: number;
  urgentCount: number;
  items: ETebligatItem[];
}

export interface ETebligatSyncResult {
  added: number;
  skippedDuplicates: number;
  clientCount: number;
  message: string;
}

export function listETebligat(options: {
  clientId?: string;
  onlyUnread?: boolean;
  onlyUrgent?: boolean;
} = {}) {
  const params = new URLSearchParams();
  if (options.clientId) params.set("clientId", options.clientId);
  if (options.onlyUnread) params.set("onlyUnread", "true");
  if (options.onlyUrgent) params.set("onlyUrgent", "true");
  const qs = params.toString();
  return api<ETebligatList>(`/api/etebligat${qs ? `?${qs}` : ""}`);
}

/**
 * clientId verilmezse tenant'ın tüm aktif mükellefleri taranır.
 *
 * Faz 15: opsiyonel from/to aralığı GİB Portalı mükelleflerinde robot'a iletilir
 * (portal ekranındaki tarih filtresi). Diğer sağlayıcılar aralığı yok sayar.
 */
export function syncETebligat(clientId?: string, from?: string, to?: string) {
  const params = new URLSearchParams();
  if (clientId) params.set("clientId", clientId);
  if (from) params.set("from", from);
  if (to) params.set("to", to);
  const qs = params.toString();
  return api<ETebligatSyncResult>(`/api/etebligat/sync${qs ? `?${qs}` : ""}`, { method: "POST" });
}

export function markETebligatRead(id: string) {
  return api<ETebligatItem>(`/api/etebligat/${id}/mark-read`, { method: "POST" });
}

export function getETebligatDocumentUrl(id: string): string {
  if (typeof window !== "undefined") {
    return `${window.location.origin}/api/etebligat/${id}/document`;
  }
  const base = process.env.NEXT_PUBLIC_API_URL || "";
  return `${base.replace(/\/api-proxy\/?$/, "")}/api/etebligat/${id}/document`;
}

export function getETebligatDownloadUrl(id: string): string {
  if (typeof window !== "undefined") {
    return `${window.location.origin}/api/etebligat/${id}/download`;
  }
  const base = process.env.NEXT_PUBLIC_API_URL || "";
  return `${base.replace(/\/api-proxy\/?$/, "")}/api/etebligat/${id}/download`;
}

// ---------- Faz 5: Abonelik / Ödeme ----------

export type SubscriptionStatusCode = "Trial" | "Active" | "PastDue" | "Canceled";

export interface BillingSummary {
  /** Abonelik kaydı henüz yoksa false (Faz 5 öncesi hesaplar). */
  hasSubscription: boolean;
  planCode: string | null;
  planName: string | null;
  monthlyPrice: number | null;
  status: SubscriptionStatusCode;
  periodStart: string | null;
  periodEnd: string | null;
  trialEndsAt: string | null;
  /** Deneme süresinden kalan gün (deneme değilse null). */
  trialDaysRemaining: number | null;
  canceledAt: string | null;
  /** İptal talebi verildi; abonelik dönem sonunda kapanacak. */
  cancelsAtPeriodEnd: boolean;
}

export interface SubscribeApiResult {
  message: string;
  /** Yönlendirmeli sağlayıcıda (iyzico) ödeme sayfası adresi; mock'ta null. */
  redirectUrl: string | null;
  summary: BillingSummary;
}

export interface BillingInvoiceItem {
  id: string;
  number: string;
  amount: number;
  vatAmount: number;
  totalAmount: number;
  issuedAt: string;
  /** PDF üretimi sonraki fazda; şimdilik hep null. */
  pdfPath: string | null;
}

export interface PaymentTransactionItem {
  id: string;
  amount: number;
  currency: string;
  status: "Pending" | "Succeeded" | "Failed" | "Refunded";
  provider: string;
  providerRef: string | null;
  failureReason: string | null;
  createdAt: string;
}

export interface AdminBillingOverview {
  monthlyRevenue: number;
  totalRevenue: number;
  activeCount: number;
  trialCount: number;
  pastDueCount: number;
  canceledCount: number;
  monthlyInvoiceCount: number;
}

export function getBillingSummary() {
  return api<BillingSummary>("/api/billing/summary");
}

/** Mock kart notu: "4111..." başarı, "4000..." başarısızlık senaryosu (kart saklanmaz). */
export function subscribePlan(planId: string, mockCardNumber?: string) {
  return api<SubscribeApiResult>("/api/billing/subscribe", {
    method: "POST",
    body: { planId, mockCardNumber: mockCardNumber?.trim() || undefined },
  });
}

export function cancelSubscription() {
  return api<SubscribeApiResult>("/api/billing/cancel", { method: "POST" });
}

export function listBillingInvoices() {
  return api<BillingInvoiceItem[]>("/api/billing/invoices");
}

export function listBillingTransactions() {
  return api<PaymentTransactionItem[]>("/api/billing/transactions");
}

export function getAdminBillingOverview() {
  return api<AdminBillingOverview>("/api/admin/billing/overview");
}

// ---------- Faz 5: e-Beyanname ----------

export type DeclarationTypeCode = "KDV1" | "KDV2" | "Muhtasar" | "Damga";

export type DeclarationStatusCode = "Draft" | "Ready" | "Sent" | "Approved" | "Error";

export interface KdvBreakdownItem {
  /** İndirilecek KDV hesabı: 191.01 (%1) / 191.02 (%10) / 191.03 (%20) / 191.99 (diğer). */
  hesapKodu: string;
  kdvOrani: number;
  matrah: number;
  kdv: number;
  belgeSayisi: number;
}

export interface KdvSummary {
  period: string;
  hesaplananMatrah: number;
  /** Hesaplanan KDV — giden (satış) faturalarından; satış senkronu yoksa 0. */
  hesaplananKdv: number;
  indirilecekKdvToplam: number;
  indirimKirilimlari: KdvBreakdownItem[];
  oncekiDonemdenDevredenKdv: number;
  odenecekKdv: number;
  sonrakiDonemeDevredenKdv: number;
  gidenFaturaSayisi: number;
  gelenFaturaSayisi: number;
  fisSayisi: number;
  // ---------- Faz 8 ----------
  /** B3: KKEG'e isabet ettiği için indirilecek KDV'den düşülen tutar. */
  kkegIndirilemeyenKdv: number;
  /** B3: dönemdeki KKEG matrah toplamı (bilgi). */
  kkegToplam: number;
  /** B2: onaylı alış faturalarında tevkif edilen KDV toplamı (KDV2 ile beyan edilir). */
  tevkifatKdvToplam: number;
}

/** Faz 8 — B2: KDV2 taslağı kod bazlı kırılım satırı. */
export interface Kdv2BreakdownItem {
  kod: string;
  aciklama: string;
  /** Pay/payda (ör. "9/10"). */
  oran: string;
  matrah: number;
  tevkifatKdv: number;
  belgeSayisi: number;
}

/** Faz 8 — B2: KDV2 (sorumlu sıfatıyla) taslak özeti. */
export interface Kdv2Summary {
  period: string;
  /** Sorumlu sıfatıyla ödenecek tevkifat KDV toplamı. */
  odenecekKdv: number;
  kirilimlar: Kdv2BreakdownItem[];
  faturaSayisi: number;
  satirSayisi: number;
}

export interface DeclarationItem {
  id: string;
  clientId: string;
  clientTitle: string | null;
  type: DeclarationTypeCode;
  period: string;
  status: DeclarationStatusCode;
  packageRef: string | null;
  tahakkukAmount: number | null;
  errorMessage: string | null;
  /** KDV1 kırılımlı taslak özeti (liste ucunda null; detay/işlem yanıtında dolu). */
  kdvSummary: KdvSummary | null;
  /** Faz 8 (B2): KDV2 taslak özeti (tür KDV2 ise; liste ucunda null). */
  kdv2Summary: Kdv2Summary | null;
  preparedAt: string;
  sentAt: string | null;
  approvedAt: string | null;
  createdAt: string;
}

export interface DeclarationActionResult {
  message: string;
  declaration: DeclarationItem;
}

/** Aynı (mükellef, tür, dönem) taslağı varsa yeniden hesaplanır. Period biçimi: "2026-06". */
export function prepareDeclaration(clientId: string, type: DeclarationTypeCode, period: string) {
  return api<DeclarationActionResult>("/api/declarations/prepare", {
    method: "POST",
    body: { clientId, type, period },
  });
}

export function listDeclarations(options: { clientId?: string; period?: string } = {}) {
  const params = new URLSearchParams();
  if (options.clientId) params.set("clientId", options.clientId);
  if (options.period) params.set("period", options.period);
  const qs = params.toString();
  return api<DeclarationItem[]>(`/api/declarations${qs ? `?${qs}` : ""}`);
}

export function getDeclaration(id: string) {
  return api<DeclarationItem>(`/api/declarations/${id}`);
}

/** Draft/Ready/Error → Sent. Error durumunda "Yeniden Gönder" de bu uçtur. */
export function sendDeclaration(id: string) {
  return api<DeclarationActionResult>(`/api/declarations/${id}/send`, { method: "POST" });
}

export function refreshDeclarationStatus(id: string) {
  return api<DeclarationActionResult>(`/api/declarations/${id}/refresh-status`, { method: "POST" });
}

// ---------- Faz 7 — E1: Mükellef belge portalı (SMMM tarafı) ----------

export type PortalUploadCategoryCode = "Fis" | "Fatura" | "BankaEkstresi" | "Sozlesme" | "Diger";

export type DocumentRequestStatusCode = "Bekliyor" | "Yuklendi" | "Iptal";

export interface PortalAccessStatus {
  exists: boolean;
  isActive: boolean;
  token: string | null;
  /** Web uygulamasındaki portal yolu (ör. "/portal/abc..."); tam link UI'da origin ile birleşir. */
  portalPath: string | null;
  createdAt: string | null;
  lastAccessAt: string | null;
}

export interface PortalAccessCreated {
  token: string;
  portalPath: string;
  /** 6 haneli PIN — YALNIZCA bu yanıtta döner, bir daha gösterilemez. */
  pin: string;
  message: string;
}

export interface DocumentRequestItem {
  id: string;
  clientId: string;
  title: string;
  dueDate: string | null;
  status: DocumentRequestStatusCode;
  createdAt: string;
  fulfilledUploadId: string | null;
}

export interface PortalUploadListItem {
  id: string;
  clientId: string;
  clientTitle: string | null;
  fileName: string;
  category: PortalUploadCategoryCode;
  note: string | null;
  uploadedAt: string;
  /** Fis kategorisi OCR hattına aktarıldıysa oluşan fişin Id'si ("Fişlerde görüntüle"). */
  processedReceiptId: string | null;
}

export function getPortalAccess(clientId: string) {
  return api<PortalAccessStatus>(`/api/clients/${clientId}/portal`);
}

/** Erişim üret/yenile — dönen PIN yalnızca bir kez gösterilir. */
export function createPortalAccess(clientId: string) {
  return api<PortalAccessCreated>(`/api/clients/${clientId}/portal`, { method: "POST" });
}

export function deactivatePortalAccess(clientId: string) {
  return api<void>(`/api/clients/${clientId}/portal`, { method: "DELETE" });
}

export function createDocumentRequest(clientId: string, title: string, dueDate?: string | null) {
  return api<DocumentRequestItem>(`/api/clients/${clientId}/document-requests`, {
    method: "POST",
    body: { title, dueDate: dueDate || null },
  });
}

export function listDocumentRequests(clientId: string) {
  return api<DocumentRequestItem[]>(`/api/clients/${clientId}/document-requests`);
}

export function cancelDocumentRequest(clientId: string, requestId: string) {
  return api<DocumentRequestItem>(`/api/clients/${clientId}/document-requests/${requestId}`, {
    method: "DELETE",
  });
}

export function listPortalUploads(options: { clientId?: string; category?: string } = {}) {
  const params = new URLSearchParams();
  if (options.clientId) params.set("clientId", options.clientId);
  if (options.category) params.set("category", options.category);
  const qs = params.toString();
  return api<PortalUploadListItem[]>(`/api/portal-uploads${qs ? `?${qs}` : ""}`);
}

// ---------- Faz 7 — E1: Portal (mükellef tarafı — PUBLIC, mm_token KULLANMAZ) ----------
// Portal oturumu kendi kısa ömürlü token'ını taşır; 401'de /giris'e YÖNLENDİRİLMEZ
// (mükellef SMMM girişine gönderilmez), Türkçe hata mesajı fırlatılır.

export interface PortalPendingRequest {
  id: string;
  title: string;
  dueDate: string | null;
  createdAt: string;
}

export interface PortalLoginResult {
  token: string;
  clientTitle: string;
  expiresAt: string;
  pendingRequests: PortalPendingRequest[];
}

export interface PortalUploadFileResult {
  fileName: string;
  success: boolean;
  message: string | null;
  category: string;
}

export interface PortalUploadResult {
  message: string;
  items: PortalUploadFileResult[];
}

async function portalFetch<T>(path: string, init: RequestInit, portalToken?: string): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, {
      ...init,
      headers: {
        ...(init.headers as Record<string, string> | undefined),
        ...(portalToken ? { Authorization: `Bearer ${portalToken}` } : {}),
      },
      cache: "no-store",
    });
  } catch {
    throw new ApiError(0, "Sunucuya ulaşılamıyor. İnternet bağlantınızı kontrol edip tekrar deneyin.");
  }

  if (res.status === 401) {
    throw new ApiError(401, "Oturumunuzun süresi doldu. Lütfen PIN ile tekrar giriş yapın.");
  }

  if (!res.ok) {
    throw new ApiError(res.status, await parseErrorMessage(res));
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

/** Login ÖNCESİ jenerik kontrol: link geçerli mi? (Unvan dönmez — bilinçli.) */
export function portalInfo(token: string) {
  return portalFetch<{ isActive: boolean }>(`/api/portal/${token}/info`, { method: "GET" });
}

export function portalLogin(token: string, pin: string) {
  return portalFetch<PortalLoginResult>(`/api/portal/${token}/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pin }),
  });
}

export function portalPendingRequests(portalToken: string) {
  return portalFetch<PortalPendingRequest[]>(`/api/portal/requests`, { method: "GET" }, portalToken);
}

/** Çoklu belge yükleme (multipart). Fiş kategorisi OCR hattına otomatik girer. */
export function portalUpload(
  portalToken: string,
  files: File[],
  category: PortalUploadCategoryCode,
  note?: string,
  documentRequestId?: string
) {
  const form = new FormData();
  for (const file of files) form.append("files", file, file.name);
  form.append("category", category);
  if (note?.trim()) form.append("note", note.trim());
  if (documentRequestId) form.append("documentRequestId", documentRequestId);

  return portalFetch<PortalUploadResult>(`/api/portal/upload`, { method: "POST", body: form }, portalToken);
}

// ---------- Faz 7 — E2: Vergi takvimi ----------

export type ObligationStatusCode = "Bekliyor" | "Tamamlandi";

export interface TaxObligationItem {
  id: string;
  clientId: string;
  clientTitle: string | null;
  type: string;
  period: string;
  dueDate: string;
  status: ObligationStatusCode;
  completedAt: string | null;
  source: "Otomatik" | "Manuel";
  /** Son güne kalan gün (0 ve altı = geçti). */
  daysRemaining: number;
  /** Bekliyor + son günü geçmiş → geciken (kırmızı). */
  isOverdue: boolean;
  createdAt: string;
}

export interface TaxCalendarList {
  totalCount: number;
  pendingCount: number;
  overdueCount: number;
  items: TaxObligationItem[];
}

export interface GenerateCalendarResult {
  message: string;
  added: number;
  skippedDuplicates: number;
  autoCompleted: number;
}

/** Seçilen ay için tüm aktif mükelleflere standart seti üretir (mükerrer üretmez). */
export function generateTaxCalendar(period: string) {
  return api<GenerateCalendarResult>("/api/tax-calendar/generate", {
    method: "POST",
    body: { period },
  });
}

export function listTaxCalendar(options: {
  period?: string;
  clientId?: string;
  onlyPending?: boolean;
  onlyOverdue?: boolean;
} = {}) {
  const params = new URLSearchParams();
  if (options.period) params.set("period", options.period);
  if (options.clientId) params.set("clientId", options.clientId);
  if (options.onlyPending) params.set("onlyPending", "true");
  if (options.onlyOverdue) params.set("onlyOverdue", "true");
  const qs = params.toString();
  return api<TaxCalendarList>(`/api/tax-calendar${qs ? `?${qs}` : ""}`);
}

export function completeObligation(id: string) {
  return api<TaxObligationItem>(`/api/tax-calendar/${id}/complete`, { method: "POST" });
}

export function reopenObligation(id: string) {
  return api<TaxObligationItem>(`/api/tax-calendar/${id}/reopen`, { method: "POST" });
}

export function createManualObligation(body: {
  clientId: string;
  type: string;
  period: string;
  dueDate: string;
}) {
  return api<TaxObligationItem>("/api/tax-calendar", { method: "POST", body });
}

// ---------- Faz 7 — E3: Sertifika süre takibi ----------

export type CertificateTypeCode = "EImza" | "MaliMuhur" | "Kep";

export interface CertificateItem {
  id: string;
  /** null = SMMM'nin (ofisin) kendi sertifikası. */
  clientId: string | null;
  clientTitle: string | null;
  type: CertificateTypeCode;
  owner: string;
  expiresAt: string;
  note: string | null;
  /** Bitişe kalan gün (0 ve altı = süresi doldu). */
  daysRemaining: number;
  createdAt: string;
}

export interface CertificateSaveBody {
  clientId?: string | null;
  type: CertificateTypeCode;
  owner: string;
  expiresAt: string;
  note?: string | null;
}

export function listCertificates(expiringInDays?: number) {
  const qs = expiringInDays !== undefined ? `?expiringInDays=${expiringInDays}` : "";
  return api<CertificateItem[]>(`/api/certificates${qs}`);
}

export function createCertificate(body: CertificateSaveBody) {
  return api<CertificateItem>("/api/certificates", { method: "POST", body });
}

export function updateCertificate(id: string, body: CertificateSaveBody) {
  return api<CertificateItem>(`/api/certificates/${id}`, { method: "PUT", body });
}

export function deleteCertificate(id: string) {
  return api<void>(`/api/certificates/${id}`, { method: "DELETE" });
}

// ---------- Faz 7 — E4: Parametre merkezi ----------

export interface TaxParameterItem {
  id: string;
  key: string;
  value: string;
  description: string;
  effectiveFrom: string;
  updatedAt: string;
}

/** GET tüm oturum açmış kullanıcılara açık; yazma uçları yalnızca SuperAdmin. */
export function listTaxParameters() {
  return api<TaxParameterItem[]>("/api/parameters");
}

export function updateTaxParameter(
  id: string,
  body: { value: string; description?: string | null; effectiveFrom?: string | null }
) {
  return api<TaxParameterItem>(`/api/parameters/${id}`, { method: "PUT", body });
}

export function createTaxParameter(body: {
  key: string;
  value: string;
  description?: string | null;
  effectiveFrom?: string | null;
}) {
  return api<TaxParameterItem>("/api/parameters", { method: "POST", body });
}

export function deleteTaxParameter(id: string) {
  return api<void>(`/api/parameters/${id}`, { method: "DELETE" });
}

// ---------- Faz 8 — B5: Mükerrer / eksik fatura mutabakatı ----------

export interface ReconciliationDocRef {
  id: string;
  /** "Fatura" veya "Fiş". */
  docType: string;
  label: string;
  date: string;
  amount: number;
}

export interface ReconciliationFinding {
  /** Deterministik bulgu anahtarı — "Yoksay" bu anahtarla kalıcı kaydedilir. */
  key: string;
  kind: "Mukerrer" | "Eksik" | "Tutarsiz";
  title: string;
  /** Türkçe öneri metni. */
  suggestion: string;
  documents: ReconciliationDocRef[];
}

export interface ReconciliationReport {
  clientId: string;
  period: string;
  mukerrer: ReconciliationFinding[];
  eksik: ReconciliationFinding[];
  tutarsiz: ReconciliationFinding[];
  /** Daha önce yok sayılıp bu analizde gizlenen bulgu sayısı. */
  dismissedCount: number;
  invoiceCount: number;
  receiptCount: number;
}

export function analyzeReconciliation(clientId: string, period: string) {
  const params = new URLSearchParams({ clientId, period });
  return api<ReconciliationReport>(`/api/reconciliation?${params.toString()}`);
}

export function dismissReconciliationFinding(findingKey: string) {
  return api<{ message: string }>("/api/reconciliation/dismiss", {
    method: "POST",
    body: { findingKey },
  });
}

// ---------- Faz 8 — B10: Dönem sonu kontrol listesi ----------

export type PeriodCheckStatusCode = "Ok" | "Uyari" | "Hata";

export interface PeriodCheckItem {
  key: string;
  name: string;
  status: PeriodCheckStatusCode;
  detail: string;
  /** İlgili panel sayfası (ör. "/panel/faturalar"). */
  link: string;
}

export interface PeriodCloseChecklist {
  clientId: string;
  period: string;
  /** "Beyana hazır" yüzdesi (Ok=1, Uyari=0.5, Hata=0). */
  percent: number;
  /** En az bir Hata (beyana engel) var mı? */
  hasBlocker: boolean;
  readyForDeclaration: boolean;
  items: PeriodCheckItem[];
}

export function getPeriodCloseChecklist(clientId: string, period: string) {
  const params = new URLSearchParams({ clientId, period });
  return api<PeriodCloseChecklist>(`/api/period-close/checklist?${params.toString()}`);
}

// ---------- Faz 11 — E5: e-Defter berat ön-kontrol ----------

export type EDefterPeriodStatusCode = "Hazirlaniyor" | "KontrolTamam" | "BeratYuklendi" | "Hata";

export interface EDefterCheckItem {
  key: string;
  ad: string;
  /** Ok / Uyari / Hata (kapanış kontrol listesiyle aynı sözleşme). */
  durum: PeriodCheckStatusCode;
  detay: string;
  /** Türkçe çözüm önerisi. */
  oneri: string;
}

export interface EDefterPrecheckResult {
  clientId: string;
  period: string;
  items: EDefterCheckItem[];
  /** En az bir kırmızı (Hata) kontrol var mı? */
  hasError: boolean;
  status: EDefterPeriodStatusCode;
  checkedAt: string;
}

export interface EDefterPeriodItem {
  id: string;
  clientId: string;
  clientTitle: string | null;
  period: string;
  status: EDefterPeriodStatusCode;
  /** Türkçe durum etiketi (rozet). */
  statusLabel: string;
  checkedAt: string | null;
  markedUploadedAt: string | null;
  note: string | null;
  createdAt: string;
}

/** Kontrol setini çalıştırır (dönem kaydını da günceller). */
export function runEDefterPrecheck(clientId: string, period: string) {
  const params = new URLSearchParams({ clientId, period });
  return api<EDefterPrecheckResult>(`/api/edefter/precheck?${params.toString()}`);
}

/** SMMM, beratı GİB portalında yükledikten SONRA elle işaretler. */
export function markEDefterUploaded(clientId: string, period: string, note?: string) {
  return api<EDefterPeriodItem>(`/api/edefter/${clientId}/${period}/mark-uploaded`, {
    method: "POST",
    body: { note: note?.trim() || null },
  });
}

export function listEDefterPeriods(clientId?: string) {
  const qs = clientId ? `?clientId=${clientId}` : "";
  return api<EDefterPeriodItem[]>(`/api/edefter${qs}`);
}

// ---------- Faz 11 — E6: Gönderim kuyruğu ----------

export type OutboundJobStatusCode = "Bekliyor" | "Calisiyor" | "Basarili" | "Basarisiz" | "Iptal";

export interface OutboundJobItem {
  id: string;
  /** "BeyannameGonder" / "TebligatSync" / "FaturaSync" / "ReceiptOcr" (Faz 14). */
  type: string;
  /** Türkçe tip etiketi. */
  typeLabel: string;
  status: OutboundJobStatusCode;
  attemptCount: number;
  maxAttempts: number;
  /** Bir sonraki otomatik deneme zamanı (yalnızca Bekliyor'da anlamlı). */
  nextAttemptAt: string;
  lastError: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface OutboundJobList {
  /** Bekleyen + çalışan iş sayısı (sidebar rozeti — filtreden bağımsız). */
  pendingCount: number;
  items: OutboundJobItem[];
}

export function listJobs(status?: OutboundJobStatusCode) {
  const qs = status ? `?status=${status}` : "";
  return api<OutboundJobList>(`/api/jobs${qs}`);
}

export function cancelJob(id: string) {
  return api<OutboundJobItem>(`/api/jobs/${id}/cancel`, { method: "POST" });
}

export function retryJobNow(id: string) {
  return api<OutboundJobItem>(`/api/jobs/${id}/retry-now`, { method: "POST" });
}

/**
 * Beyanname gönderimini kuyruğa atar (E6): anında göndermek yerine sistem müsait olduğunda
 * otomatik gönderilir (GİB çökmelerinde SMMM'yi ekran başında tutmaz).
 */
export function queueDeclarationSend(id: string) {
  return api<DeclarationActionResult>(`/api/declarations/${id}/send?queue=true`, { method: "POST" });
}

// ---------- Faz 11 — E7: SMMM ücret / tahsilat takibi ----------

export type FeeInvoiceStatusCode = "Bekliyor" | "Odendi" | "Gecikti";

export interface ClientFeeItem {
  id: string;
  clientId: string;
  monthlyFee: number;
  currency: string;
  startDate: string;
  note: string | null;
  createdAt: string;
}

export interface FeeInvoiceItem {
  id: string;
  clientId: string;
  clientTitle: string | null;
  period: string;
  amount: number;
  status: FeeInvoiceStatusCode;
  dueDate: string;
  paidAt: string | null;
  paidAmount: number | null;
  note: string | null;
  /** Vadeye kalan gün (0 ve altı = geçti; ödenmişse anlamsız). */
  daysRemaining: number;
  createdAt: string;
}

/** Özet kartlar status filtresinden bağımsızdır (tebligat deseni). */
export interface FeeList {
  expectedTotal: number;
  paidTotal: number;
  overdueTotal: number;
  totalCount: number;
  paidCount: number;
  overdueCount: number;
  pendingCount: number;
  items: FeeInvoiceItem[];
}

export interface FeeGenerateResult {
  added: number;
  skippedDuplicates: number;
  overdueMarked: number;
  message: string;
}

/** Ücret tanımı yoksa 404 döner (ApiError.status === 404 → boş form). */
export function getClientFee(clientId: string) {
  return api<ClientFeeItem>(`/api/clients/${clientId}/fee`);
}

export function saveClientFee(
  clientId: string,
  body: { monthlyFee: number; startDate?: string | null; note?: string | null }
) {
  return api<ClientFeeItem>(`/api/clients/${clientId}/fee`, { method: "PUT", body });
}

export function deleteClientFee(clientId: string) {
  return api<void>(`/api/clients/${clientId}/fee`, { method: "DELETE" });
}

/** Seçilen ay için ücret tanımlı tüm mükelleflere tahakkuk üretir (mükerrersiz). */
export function generateFees(period: string) {
  return api<FeeGenerateResult>("/api/fees/generate", { method: "POST", body: { period } });
}

export function listFees(options: { period?: string; status?: FeeInvoiceStatusCode } = {}) {
  const params = new URLSearchParams();
  if (options.period) params.set("period", options.period);
  if (options.status) params.set("status", options.status);
  const qs = params.toString();
  return api<FeeList>(`/api/fees${qs ? `?${qs}` : ""}`);
}

/** paidAmount boşsa tahakkuk tutarı tahsil edilmiş sayılır. */
export function markFeePaid(id: string, paidAmount?: number) {
  return api<FeeInvoiceItem>(`/api/fees/${id}/mark-paid`, {
    method: "POST",
    body: { paidAmount: paidAmount ?? null },
  });
}

export function reopenFee(id: string) {
  return api<FeeInvoiceItem>(`/api/fees/${id}/reopen`, { method: "POST" });
}

// ---------- Faz 11 — E8: Mizan & uyum kontrolü ----------

export interface TrialBalanceRow {
  hesapKodu: string;
  hesapAdi: string;
  borc: number;
  alacak: number;
  borcBakiye: number;
  alacakBakiye: number;
}

export interface TrialBalanceCheck {
  ad: string;
  /** Ok / Uyari (mizan uyum kontrolleri Hata üretmez). */
  durum: PeriodCheckStatusCode;
  detay: string;
}

export interface TrialBalanceResult {
  clientId: string;
  period: string;
  rows: TrialBalanceRow[];
  toplamBorc: number;
  toplamAlacak: number;
  /** |Borç − Alacak| ≤ 0,01 TL → denk. */
  denk: boolean;
  fark: number;
  uyumKontrolleri: TrialBalanceCheck[];
  faturaSayisi: number;
  fisSayisi: number;
  /** Şablon üretimi uyarıları (hesapsız satır vb. — bilgi). */
  uyarilar: string[];
}

export function getTrialBalance(clientId: string, period: string) {
  const params = new URLSearchParams({ clientId, period });
  return api<TrialBalanceResult>(`/api/trial-balance?${params.toString()}`);
}

/** Mizanı .xlsx olarak indirir (MinimalXlsxWriter — Luca fiş çekirdeğiyle aynı dağılım). */
export function downloadTrialBalanceXlsx(clientId: string, period: string) {
  const params = new URLSearchParams({ clientId, period, format: "xlsx" });
  return apiDownload(`/api/trial-balance?${params.toString()}`);
}
