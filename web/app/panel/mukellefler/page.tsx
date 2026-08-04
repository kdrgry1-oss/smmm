"use client";

import Link from "next/link";
import { FormEvent, useCallback, useEffect, useState } from "react";
import {
  addClientVehicle,
  api,
  ApiError,
  ClientCodingSettings,
  ClientResponse,
  ClientVehicleResponse,
  deleteClientIntegration,
  deleteClientVehicle,
  getClientIntegration,
  getCodingSettings,
  IntegrationProviderCode,
  IntegrationResponse,
  listClientIntegrations,
  listClientVehicles,
  normalizePlate,
  PLATE_REGEX,
  saveClientIntegration,
  testClientIntegration,
  updateCodingSettings,
} from "@/lib/api";
import DataImportModal from "@/components/DataImportModal";
import GibLoginModal from "@/components/GibLoginModal";

interface ClientFormState {
  title: string;
  taxId: string;
  taxOffice: string;
  email: string;
  phone: string;
  eInvoiceEnabled: boolean;
  eArchiveEnabled: boolean;
  integratorName: string;
}

const emptyForm: ClientFormState = {
  title: "",
  taxId: "",
  taxOffice: "",
  email: "",
  phone: "",
  eInvoiceEnabled: false,
  eArchiveEnabled: false,
  integratorName: "",
};

const integratorOptions = [
  // Faz 15: entegratörü olmayan mükellef de eklenebilir; bu seçim GİB Portalı robotuna yönlendirir.
  { value: "", label: "Entegratör kullanmıyor (GİB Portalı)" },
  { value: "Nilvera", label: "Nilvera" },
  { value: "Uyumsoft", label: "Uyumsoft" },
  { value: "Doğan Dönüşüm", label: "Doğan Dönüşüm" },
  { value: "eLogo", label: "eLogo (Logo)" },
  { value: "EDM Bilişim", label: "EDM Bilişim" },
  { value: "Paraşüt", label: "Paraşüt" },
  { value: "Diğer", label: "Diğer" },
];

/**
 * Kimliği kullanıcı adı + şifre (+opsiyonel servis adresi) olan SOAP sağlayıcıları.
 * Paraşüt DAHİL DEĞİLDİR — kimlik alanları farklıdır (Client ID/Secret + e-posta + Firma ID)
 * ve modalda ayrı form bloğuyla ele alınır.
 */
function isCredentialProvider(provider: IntegrationProviderCode): boolean {
  return (
    provider === "Uyumsoft" ||
    provider === "DoganDonusum" ||
    provider === "Elogo" ||
    provider === "Edm"
  );
}

/**
 * GİB portalları (Dijital Vergi Dairesi ve e-Arşiv): kimlik alanları kullanıcı kodu + şifredir
 * (servis adresi YOKTUR, portal adresleri sabittir) → ortak form bloğuyla ele alınırlar.
 */
function isGibPortal(provider: IntegrationProviderCode): boolean {
  return provider === "GibPortal" || provider === "EArsivPortal";
}

/** Kullanıcıya gösterilen Türkçe sağlayıcı adı. */
function providerLabel(provider: IntegrationProviderCode): string {
  if (provider === "DoganDonusum") return "Doğan Dönüşüm";
  if (provider === "Elogo") return "eLogo";
  if (provider === "Edm") return "EDM Bilişim";
  if (provider === "Parasut") return "Paraşüt";
  if (provider === "Mock") return "Mock (Demo)";
  if (provider === "GibPortal") return "GİB Portalı (entegratör yok)";
  if (provider === "EArsivPortal") return "GİB e-Arşiv Portalı";
  return provider;
}

/** Kimlik-tabanlı SOAP sağlayıcılarının modal metinleri (alan ipuçları + varsayılan uçlar). */
const credentialProviderTexts: Partial<
  Record<
    IntegrationProviderCode,
    {
      usernamePlaceholder: string;
      passwordPlaceholder: string;
      passwordHelpPrefix: string;
      endpointPlaceholder: string;
      endpointHelp: string;
      testEnvLabel: string;
    }
  >
> = {
  Uyumsoft: {
    usernamePlaceholder: "Uyumsoft web servis kullanıcı adınız",
    passwordPlaceholder: "Uyumsoft web servis şifreniz",
    passwordHelpPrefix: "",
    endpointPlaceholder: "https://edonusumapi.uyum.com.tr/Services/BasicIntegration",
    endpointHelp:
      'Uyumsoft’tan gelen "Web Servis URL" e-postasındaki adres. Boş bırakılırsa varsayılan kullanılır.',
    testEnvLabel: "Test ortamını kullan (efatura-test.uyumsoft.com.tr)",
  },
  DoganDonusum: {
    usernamePlaceholder: "Doğan e-Dönüşüm portal kullanıcı adınız",
    passwordPlaceholder: "Doğan e-Dönüşüm portal şifreniz",
    passwordHelpPrefix: "Doğan e-Dönüşüm portal kullanıcı adı ve şifrenizi girin. ",
    endpointPlaceholder: "https://connector.doganedonusum.com",
    endpointHelp:
      "Doğan Dönüşüm'ün bildirdiği taban servis adresi. Boş bırakılırsa varsayılan kullanılır.",
    testEnvLabel: "Test ortamını kullan (efaturatest.doganedonusum.com)",
  },
  Elogo: {
    usernamePlaceholder: "eLogo özel entegratör (PostBox) kullanıcı adınız",
    passwordPlaceholder: "eLogo özel entegratör (PostBox) şifreniz",
    passwordHelpPrefix: "eLogo PostBox web servis kullanıcı adı ve şifrenizi girin. ",
    endpointPlaceholder: "https://pb.elogo.com.tr/PostBoxService.svc",
    endpointHelp:
      "eLogo'nun bildirdiği PostBox servis adresi. Boş bırakılırsa varsayılan kullanılır.",
    testEnvLabel: "Test ortamını kullan (pbtest.elogo.com.tr)",
  },
  Edm: {
    usernamePlaceholder: "EDM Bilişim web servis kullanıcı adınız",
    passwordPlaceholder: "EDM Bilişim web servis şifreniz",
    passwordHelpPrefix: "EDM Bilişim web servis kullanıcı adı ve şifrenizi girin. ",
    endpointPlaceholder: "https://efatura.edmbilisim.com.tr/EFaturaEDM/EFaturaEDM.svc",
    endpointHelp:
      "EDM Bilişim'in bildirdiği servis adresi. Boş bırakılırsa varsayılan kullanılır.",
    testEnvLabel: "Test ortamını kullan (test.edmbilisim.com.tr)",
  },
};

export default function ClientsPage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [listError, setListError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [showArchived, setShowArchived] = useState(false);

  // Modal durumu
  const [modalOpen, setModalOpen] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<ClientFormState>(emptyForm);
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [rowBusy, setRowBusy] = useState<string | null>(null);

  // Faz 8 (B3): "Binek araç var" ayarı (ClientCodingSettings.HasCompanyVehicle) —
  // yalnızca DÜZENLEME modunda gösterilir; ayarlar arka planda yüklenir.
  const [codingSettings, setCodingSettings] = useState<ClientCodingSettings | null>(null);
  const [hasVehicle, setHasVehicle] = useState(false);

  // Entegrasyon modalı durumu
  const [integrationClient, setIntegrationClient] = useState<ClientResponse | null>(null);
  const [integration, setIntegration] = useState<IntegrationResponse | null>(null);
  const [integrationLoading, setIntegrationLoading] = useState(false);
  const [integrationError, setIntegrationError] = useState<string | null>(null);
  const [integrationSaving, setIntegrationSaving] = useState(false);
  const [integrationDeleting, setIntegrationDeleting] = useState(false);
  const [providerChoice, setProviderChoice] = useState<IntegrationProviderCode>("Mock");
  const [apiKeyInput, setApiKeyInput] = useState("");
  // Uyumsoft kimlik alanları (API anahtarı yerine kullanıcı adı + şifre kullanılır).
  const [usernameInput, setUsernameInput] = useState("");
  const [passwordInput, setPasswordInput] = useState("");
  // Uyumsoft opsiyonel servis adresi (boş = varsayılan uç).
  const [endpointInput, setEndpointInput] = useState("");
  // Uyumsoft belge türü bayrakları: e-Fatura/e-Arşiv (varsayılan) ve/veya e-SMM makbuzu.
  // İkisi birden seçilebilir (her iki servisten çekilir); en az biri seçili olmalıdır.
  const [uyumsoftEinvoice, setUyumsoftEinvoice] = useState(true);
  const [uyumsoftEsmm, setUyumsoftEsmm] = useState(false);
  // Paraşüt'e özgü kimlik alanları (Client ID/Secret + Firma ID; e-posta/şifre üstteki
  // username/password alanlarını paylaşır).
  const [parasutClientIdInput, setParasutClientIdInput] = useState("");
  const [parasutClientSecretInput, setParasutClientSecretInput] = useState("");
  const [parasutCompanyIdInput, setParasutCompanyIdInput] = useState("");
  const [useTestEnv, setUseTestEnv] = useState(true);

  // Faz 15 — GİB Portalı: bağlantı testi durumu + mükellef listesindeki uyarı rozetleri.
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<{ ok: boolean; manual: boolean; message: string } | null>(
    null
  );
  /** clientId → entegrasyon özeti (sağlayıcı rozeti + "Elle doğrulama gerekli" uyarısı). */
  const [integrationsByClient, setIntegrationsByClient] = useState<
    Record<string, IntegrationResponse>
  >({});

  // Faz 17 — "GİB Oturumu Aç" modalı: kullanıcı GİB giriş ekranındaki doğrulama kodunu
  // okuyup girer; oturum 12 saat saklanır (sistem captcha'yı ÇÖZMEZ).
  const [gibLoginClient, setGibLoginClient] = useState<ClientResponse | null>(null);

  // Faz 13: Araçlar modalı durumu (plaka kayıtları — akaryakıt KKEG eşleştirmesi için).
  const [vehiclesClient, setVehiclesClient] = useState<ClientResponse | null>(null);
  const [vehicles, setVehicles] = useState<ClientVehicleResponse[]>([]);
  const [vehiclesLoading, setVehiclesLoading] = useState(false);
  const [vehiclesError, setVehiclesError] = useState<string | null>(null);
  const [vehicleSaving, setVehicleSaving] = useState(false);
  const [vehicleDeletingId, setVehicleDeletingId] = useState<string | null>(null);
  const [plateInput, setPlateInput] = useState("");
  const [isPassengerCarInput, setIsPassengerCarInput] = useState(true);
  const [vehicleDescriptionInput, setVehicleDescriptionInput] = useState("");

  // Veri İçe Aktar modalı: ortak DataImportModal bileşeni satır bağlamındaki mükellefle
  // açılır (mizan / geçmiş fiş listesi / cari şablonu / hesap planı CSV + mükellef listesi
  // ve araç plakaları sekmeleri bileşenin içindedir).
  const [importClient, setImportClient] = useState<ClientResponse | null>(null);

  const loadClients = useCallback(async (q: string, includeArchived: boolean) => {
    setLoading(true);
    setListError(null);
    try {
      const params = new URLSearchParams();
      if (q.trim()) params.set("q", q.trim());
      if (includeArchived) params.set("include", "archived");
      const qs = params.toString();
      const data = await api<ClientResponse[]>(`/api/clients${qs ? `?${qs}` : ""}`);
      setClients(data);

      // Faz 15: entegrasyon özetleri TEK istekte (satır rozetleri + GİB uyarısı).
      // Hata modalı/list akışını bozmaz — rozetler görünmez, liste çalışmaya devam eder.
      try {
        const integrations = await listClientIntegrations();
        setIntegrationsByClient(
          Object.fromEntries(integrations.map((i) => [i.clientId, i]))
        );
      } catch {
        setIntegrationsByClient({});
      }
    } catch (err) {
      setListError(
        err instanceof Error ? err.message : "Mükellefler yüklenirken bir hata oluştu."
      );
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const timer = setTimeout(() => loadClients(search, showArchived), search ? 300 : 0);
    return () => clearTimeout(timer);
  }, [search, showArchived, loadClients]);

  function openCreateModal() {
    setEditingId(null);
    setForm(emptyForm);
    setFormError(null);
    setModalOpen(true);
  }

  function openEditModal(client: ClientResponse) {
    setEditingId(client.id);
    setForm({
      title: client.title,
      taxId: client.taxId,
      taxOffice: client.taxOffice ?? "",
      email: client.email ?? "",
      phone: client.phone ?? "",
      eInvoiceEnabled: client.eInvoiceEnabled,
      eArchiveEnabled: client.eArchiveEnabled,
      integratorName: client.integratorName ?? "",
    });
    setFormError(null);
    setModalOpen(true);

    // Faz 8 (B3): kodlama ayarları arka planda yüklenir; hata modalı engellemez.
    setCodingSettings(null);
    setHasVehicle(false);
    getCodingSettings(client.id)
      .then((s) => {
        setCodingSettings(s);
        setHasVehicle(s.hasCompanyVehicle);
      })
      .catch(() => {
        // ayarlar yüklenemezse onay kutusu devre dışı kalır
      });
  }

  function validateForm(): string | null {
    if (!form.title.trim()) return "Unvan zorunludur.";
    if (!form.taxId.trim()) return "Vergi/TC kimlik numarası zorunludur.";
    if (!/^\d{10}(\d)?$/.test(form.taxId.trim()))
      return "Vergi/TC kimlik numarası 10 veya 11 haneli olmalıdır.";
    if (form.email.trim() && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.email.trim()))
      return "Geçerli bir e-posta adresi girin.";
    return null;
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setFormError(null);

    const validationError = validateForm();
    if (validationError) {
      setFormError(validationError);
      return;
    }

    const payload = {
      title: form.title.trim(),
      taxId: form.taxId.trim(),
      taxOffice: form.taxOffice.trim() || null,
      email: form.email.trim() || null,
      phone: form.phone.trim() || null,
      address: null,
      eInvoiceEnabled: form.eInvoiceEnabled,
      eArchiveEnabled: form.eArchiveEnabled,
      integratorName: form.integratorName || null,
    };

    setSaving(true);
    try {
      if (editingId) {
        await api(`/api/clients/${editingId}`, { method: "PUT", body: payload });

        // Faz 8 (B3): "Binek araç var" değiştiyse kodlama ayarına yazılır
        // (diğer ayar alanları korunur; ayar yüklenemediyse dokunulmaz).
        if (codingSettings && hasVehicle !== codingSettings.hasCompanyVehicle) {
          await updateCodingSettings(editingId, {
            defaultExpenseAccount: codingSettings.defaultExpenseAccount,
            cariPrefix: codingSettings.cariPrefix,
            cariNextNumber: codingSettings.cariNextNumber,
            autoApplyThreshold: codingSettings.autoApplyThreshold,
            hasCompanyVehicle: hasVehicle,
          });
        }
      } else {
        await api("/api/clients", { method: "POST", body: payload });
      }
      setModalOpen(false);
      await loadClients(search, showArchived);
    } catch (err) {
      setFormError(err instanceof Error ? err.message : "Kaydetme sırasında bir hata oluştu.");
    } finally {
      setSaving(false);
    }
  }

  async function openIntegrationModal(client: ClientResponse) {
    setIntegrationClient(client);
    setIntegration(null);
    setIntegrationError(null);
    setProviderChoice("Mock");
    setApiKeyInput("");
    setUsernameInput("");
    setPasswordInput("");
    setEndpointInput("");
    setUyumsoftEinvoice(true);
    setUyumsoftEsmm(false);
    setParasutClientIdInput("");
    setParasutClientSecretInput("");
    setParasutCompanyIdInput("");
    setUseTestEnv(true);
    setTestResult(null);
    setIntegrationLoading(true);
    try {
      const data = await getClientIntegration(client.id);
      setIntegration(data);
      setProviderChoice(data.provider);
      setUseTestEnv(data.useTestEnvironment);
      setEndpointInput(data.endpoint ?? "");
      setUyumsoftEinvoice(data.uyumsoftEinvoice ?? true);
      setUyumsoftEsmm(data.uyumsoftEsmm ?? false);
    } catch (err) {
      // 404 = entegrasyon henüz tanımlı değil → boş form gösterilir, hata değildir.
      if (!(err instanceof ApiError && err.status === 404)) {
        setIntegrationError(
          err instanceof Error ? err.message : "Entegrasyon bilgisi yüklenirken bir hata oluştu."
        );
      }
    } finally {
      setIntegrationLoading(false);
    }
  }

  function closeIntegrationModal() {
    if (integrationSaving || integrationDeleting || testing) return;
    setIntegrationClient(null);
    setIntegration(null);
    setIntegrationError(null);
    setTestResult(null);
    setApiKeyInput("");
    setUsernameInput("");
    setPasswordInput("");
    setEndpointInput("");
    setUyumsoftEinvoice(true);
    setUyumsoftEsmm(false);
    setParasutClientIdInput("");
    setParasutClientSecretInput("");
    setParasutCompanyIdInput("");
  }

  async function handleIntegrationSave(e: FormEvent) {
    e.preventDefault();
    if (!integrationClient) return;
    setIntegrationError(null);

    const key = apiKeyInput.trim();
    const username = usernameInput.trim();
    const password = passwordInput;
    const endpoint = endpointInput.trim();
    const parasutClientId = parasutClientIdInput.trim();
    const parasutClientSecret = parasutClientSecretInput.trim();
    const parasutCompanyId = parasutCompanyIdInput.trim();
    const credProvider = isCredentialProvider(providerChoice);
    // Kayıtlı sır yalnızca AYNI sağlayıcıya aitse korunabilir (backend de aynı kuralı uygular).
    const hasStoredNilveraKey =
      integration?.provider === "Nilvera" && !!integration.maskedApiKey;
    const hasStoredCredentials =
      integration?.provider === providerChoice && !!integration.maskedUsername;

    // UI doğrulaması (backend aynı kuralı ayrıca uygular — çift validasyon).
    if (providerChoice === "Nilvera" && !hasStoredNilveraKey && !key) {
      setIntegrationError("Nilvera için API anahtarı zorunludur.");
      return;
    }
    if (providerChoice === "Nilvera" && key && key.length < 10) {
      setIntegrationError("API anahtarı geçersiz görünüyor (en az 10 karakter olmalı).");
      return;
    }
    if (credProvider) {
      if (!hasStoredCredentials && (!username || !password)) {
        setIntegrationError(`${providerLabel(providerChoice)} için kullanıcı adı ve şifre zorunludur.`);
        return;
      }
      // Kimlik değiştirilecekse iki alan birlikte girilmelidir.
      if ((username && !password) || (!username && password)) {
        setIntegrationError(`${providerLabel(providerChoice)} için kullanıcı adı ve şifre birlikte girilmelidir.`);
        return;
      }
      // Servis adresi opsiyoneldir; girildiyse http/https URL olmalıdır (backend de doğrular).
      if (endpoint && !/^https?:\/\//i.test(endpoint)) {
        setIntegrationError("Geçerli bir servis adresi girin (https://... ).");
        return;
      }
      // Uyumsoft belge türü: en az biri seçili olmalıdır (backend de doğrular).
      if (providerChoice === "Uyumsoft" && !uyumsoftEinvoice && !uyumsoftEsmm) {
        setIntegrationError(
          "Uyumsoft için en az bir belge türü seçin (e-Fatura / e-Arşiv veya e-SMM)."
        );
        return;
      }
    }
    if (isGibPortal(providerChoice)) {
      // GİB portalları: kullanıcı kodu/TCKN + şifre (backend aynı kuralı ayrıca uygular).
      const portalLabel = providerLabel(providerChoice);
      if (!hasStoredCredentials && (!username || !password)) {
        setIntegrationError(`${portalLabel} için kullanıcı kodu / TCKN ve şifre zorunludur.`);
        return;
      }
      if ((username && !password) || (!username && password)) {
        setIntegrationError(
          `${portalLabel} için kullanıcı kodu ve şifre birlikte girilmelidir.`
        );
        return;
      }
    }
    if (providerChoice === "Parasut") {
      // Paraşüt kimliği beş alandan oluşur; yenisi girilecekse beşi birlikte girilmelidir
      // (backend aynı kuralı ayrıca uygular).
      const anyProvided =
        !!parasutClientId || !!parasutClientSecret || !!username || !!password || !!parasutCompanyId;
      const allProvided =
        !!parasutClientId && !!parasutClientSecret && !!username && !!password && !!parasutCompanyId;
      if (!anyProvided && !hasStoredCredentials) {
        setIntegrationError(
          "Paraşüt için Client ID, Client Secret, e-posta, şifre ve Firma ID zorunludur."
        );
        return;
      }
      if (anyProvided && !allProvided) {
        setIntegrationError(
          "Paraşüt kimliğini kaydetmek için Client ID, Client Secret, e-posta, şifre ve Firma ID alanlarını birlikte girin."
        );
        return;
      }
    }

    const isParasut = providerChoice === "Parasut";
    const isPortal = isGibPortal(providerChoice);
    setIntegrationSaving(true);
    setTestResult(null);
    try {
      const saved = await saveClientIntegration(integrationClient.id, {
        provider: providerChoice,
        apiKey: providerChoice === "Nilvera" ? key || null : null,
        username: credProvider || isParasut || isPortal ? username || null : null,
        password: credProvider || isParasut || isPortal ? password || null : null,
        endpoint: credProvider ? endpoint || null : null,
        uyumsoftEsmm: providerChoice === "Uyumsoft" ? uyumsoftEsmm : null,
        uyumsoftEinvoice: providerChoice === "Uyumsoft" ? uyumsoftEinvoice : null,
        parasutClientId: isParasut ? parasutClientId || null : null,
        parasutClientSecret: isParasut ? parasutClientSecret || null : null,
        parasutCompanyId: isParasut ? parasutCompanyId || null : null,
        useTestEnvironment: useTestEnv,
      });
      setIntegration(saved);
      setApiKeyInput("");
      setUsernameInput("");
      setPasswordInput("");
      setEndpointInput(saved.endpoint ?? "");
      setUyumsoftEinvoice(saved.uyumsoftEinvoice ?? true);
      setUyumsoftEsmm(saved.uyumsoftEsmm ?? false);
      setParasutClientIdInput("");
      setParasutClientSecretInput("");
      setParasutCompanyIdInput("");
      await loadClients(search, showArchived);
    } catch (err) {
      setIntegrationError(
        err instanceof Error ? err.message : "Entegrasyon kaydedilirken bir hata oluştu."
      );
    } finally {
      setIntegrationSaving(false);
    }
  }

  /**
   * Faz 15 — "Bağlantıyı Test Et": formdaki kimlikle (boşsa kayıtlı kimlikle) gib-robot
   * üzerinden GİB portalına yalnız GİRİŞ denenir; belge çekilmez.
   * GİB captcha/SMS isterse sonuç "elle doğrulama gerekli" olarak gösterilir.
   */
  async function handleIntegrationTest() {
    if (!integrationClient) return;
    setIntegrationError(null);
    setTestResult(null);

    const username = usernameInput.trim();
    const password = passwordInput;
    const hasStored =
      integration?.provider === providerChoice && !!integration.maskedUsername;

    if (!username && !password && !hasStored) {
      setIntegrationError(
        "Test için GİB kullanıcı kodu ve şifresini girin (veya önce entegrasyonu kaydedin)."
      );
      return;
    }
    if ((username && !password) || (!username && password)) {
      setIntegrationError("Test için kullanıcı kodu ve şifreyi birlikte girin.");
      return;
    }

    setTesting(true);
    try {
      const result = await testClientIntegration(integrationClient.id, {
        provider: providerChoice,
        username: username || null,
        password: password || null,
      });
      setTestResult({
        ok: result.success,
        manual: result.requiresManual,
        message: result.message,
      });
    } catch (err) {
      setTestResult({
        ok: false,
        manual: false,
        message: err instanceof Error ? err.message : "Bağlantı testi sırasında bir hata oluştu.",
      });
    } finally {
      setTesting(false);
    }
  }

  async function handleIntegrationDelete() {
    if (!integrationClient || !integration) return;
    setIntegrationError(null);
    setIntegrationDeleting(true);
    try {
      await deleteClientIntegration(integrationClient.id);
      setIntegration(null);
      setProviderChoice("Mock");
      setApiKeyInput("");
      setUsernameInput("");
      setPasswordInput("");
      setEndpointInput("");
      setUyumsoftEinvoice(true);
      setUyumsoftEsmm(false);
      setParasutClientIdInput("");
      setParasutClientSecretInput("");
      setParasutCompanyIdInput("");
      setUseTestEnv(true);
      await loadClients(search, showArchived);
    } catch (err) {
      setIntegrationError(
        err instanceof Error ? err.message : "Entegrasyon silinirken bir hata oluştu."
      );
    } finally {
      setIntegrationDeleting(false);
    }
  }

  // ---------- Faz 13: Araçlar (plaka kayıtları) ----------

  async function openVehiclesModal(client: ClientResponse) {
    setVehiclesClient(client);
    setVehicles([]);
    setVehiclesError(null);
    setPlateInput("");
    setIsPassengerCarInput(true);
    setVehicleDescriptionInput("");
    setVehiclesLoading(true);
    try {
      const data = await listClientVehicles(client.id);
      setVehicles(data);
    } catch (err) {
      setVehiclesError(
        err instanceof Error ? err.message : "Araçlar yüklenirken bir hata oluştu."
      );
    } finally {
      setVehiclesLoading(false);
    }
  }

  function closeVehiclesModal() {
    if (vehicleSaving || vehicleDeletingId) return;
    setVehiclesClient(null);
    setVehicles([]);
    setVehiclesError(null);
  }

  async function handleVehicleAdd(e: FormEvent) {
    e.preventDefault();
    if (!vehiclesClient) return;
    setVehiclesError(null);

    // UI ön kontrolü — backend aynı kuralı normalize edip ikinci kez doğrular.
    const plate = normalizePlate(plateInput);
    if (!PLATE_REGEX.test(plate)) {
      setVehiclesError("Geçerli bir Türk plakası girin (ör. 34ABC123).");
      return;
    }
    if (vehicles.some((v) => v.plate === plate)) {
      setVehiclesError("Bu plaka zaten kayıtlı.");
      return;
    }

    setVehicleSaving(true);
    try {
      const added = await addClientVehicle(vehiclesClient.id, {
        plate,
        isPassengerCar: isPassengerCarInput,
        description: vehicleDescriptionInput.trim() || null,
      });
      setVehicles((prev) =>
        [...prev, added].sort((a, b) => a.plate.localeCompare(b.plate, "tr"))
      );
      setPlateInput("");
      setIsPassengerCarInput(true);
      setVehicleDescriptionInput("");
    } catch (err) {
      setVehiclesError(err instanceof Error ? err.message : "Araç eklenirken bir hata oluştu.");
    } finally {
      setVehicleSaving(false);
    }
  }

  async function handleVehicleDelete(vehicle: ClientVehicleResponse) {
    if (!vehiclesClient) return;
    if (!window.confirm(`${vehicle.plate} plakalı aracı silmek istediğinize emin misiniz?`)) return;
    setVehiclesError(null);
    setVehicleDeletingId(vehicle.id);
    try {
      await deleteClientVehicle(vehiclesClient.id, vehicle.id);
      setVehicles((prev) => prev.filter((v) => v.id !== vehicle.id));
    } catch (err) {
      setVehiclesError(err instanceof Error ? err.message : "Araç silinirken bir hata oluştu.");
    } finally {
      setVehicleDeletingId(null);
    }
  }

  async function toggleArchive(client: ClientResponse) {
    setRowBusy(client.id);
    setListError(null);
    try {
      const action = client.isArchived ? "unarchive" : "archive";
      await api(`/api/clients/${client.id}/${action}`, { method: "POST" });
      await loadClients(search, showArchived);
    } catch (err) {
      setListError(err instanceof Error ? err.message : "İşlem sırasında bir hata oluştu.");
    } finally {
      setRowBusy(null);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-navy-900">Mükellefler</h1>
          <p className="mt-1 text-sm text-navy-500">
            Ofisinizin çalıştığı firmaları yönetin.
          </p>
        </div>
        <button onClick={openCreateModal} className="btn-primary">
          Mükellef Ekle
        </button>
      </div>

      <div className="mt-6 flex flex-wrap items-center gap-4">
        <input
          type="search"
          className="input max-w-xs"
          placeholder="Unvan veya VKN/TCKN ara..."
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <label className="flex items-center gap-2 text-sm text-navy-700">
          <input
            type="checkbox"
            checked={showArchived}
            onChange={(e) => setShowArchived(e.target.checked)}
            className="h-4 w-4 rounded border-navy-300"
          />
          Arşivlenmişleri de göster
        </label>
      </div>

      {listError && <div className="alert-error mt-4">{listError}</div>}

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
      ) : clients.length === 0 ? (
        /* Boş durum */
        <div className="card mt-6 flex flex-col items-center py-14 text-center">
          {search.trim() ? (
            <>
              <h2 className="text-lg font-semibold text-navy-900">Sonuç bulunamadı</h2>
              <p className="mt-2 text-sm text-navy-500">
                &quot;{search}&quot; aramasıyla eşleşen mükellef yok.
              </p>
            </>
          ) : (
            <>
              <h2 className="text-lg font-semibold text-navy-900">Henüz mükellef eklemediniz</h2>
              <p className="mt-2 max-w-md text-sm text-navy-500">
                İlk mükellefinizi ekleyerek başlayın. Unvan ve vergi numarası yeterli.
              </p>
              <button onClick={openCreateModal} className="btn-primary mt-6">
                Mükellef Ekle
              </button>
            </>
          )}
        </div>
      ) : (
        /* Liste */
        <div className="mt-6 overflow-x-auto rounded-xl border border-navy-100 bg-white shadow-sm">
          <table className="w-full min-w-[720px] text-left text-sm">
            <thead className="border-b border-navy-100 bg-navy-50 text-xs uppercase tracking-wide text-navy-500">
              <tr>
                <th className="px-4 py-3">Unvan</th>
                <th className="px-4 py-3">VKN / TCKN</th>
                <th className="px-4 py-3">Vergi Dairesi</th>
                <th className="px-4 py-3">e-Belge</th>
                <th className="px-4 py-3">Entegratör</th>
                <th className="px-4 py-3">Durum</th>
                <th className="px-4 py-3 text-right">İşlemler</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-navy-50">
              {clients.map((client) => (
                <tr key={client.id} className={client.isArchived ? "bg-navy-50/50 text-navy-400" : ""}>
                  <td className="px-4 py-3 font-medium text-navy-900">
                    <span className={client.isArchived ? "text-navy-400" : ""}>{client.title}</span>
                  </td>
                  <td className="px-4 py-3 font-mono">{client.taxId}</td>
                  <td className="px-4 py-3">{client.taxOffice ?? "—"}</td>
                  <td className="px-4 py-3">
                    {[client.eInvoiceEnabled && "e-Fatura", client.eArchiveEnabled && "e-Arşiv"]
                      .filter(Boolean)
                      .join(", ") || "—"}
                  </td>
                  <td className="px-4 py-3">
                    {integrationsByClient[client.id]?.provider === "GibPortal"
                      ? "GİB Portalı (entegratör yok)"
                      : client.integratorName ?? "—"}
                  </td>
                  <td className="px-4 py-3">
                    {/* Faz 15: GİB portal robotu insan gerektiren bir adıma takıldıysa sarı uyarı.
                        Faz 17: uyarının yanındaki düğmeyle kullanıcı doğrulama kodunu tek tıkla girer. */}
                    {integrationsByClient[client.id]?.manualWarning && (
                      <>
                        <span
                          className="mb-1 block w-fit rounded-full bg-amber-100 px-2.5 py-0.5 text-xs font-bold text-amber-800"
                          title={integrationsByClient[client.id]?.manualWarning ?? undefined}
                        >
                          ⚠ Elle doğrulama gerekli
                        </span>
                        <span className="mb-1 block max-w-[240px] text-xs text-amber-700">
                          GİB oturumu yenilenmeli; otomatik çekim durdu. Doğrulama kodunu girerek
                          oturumu tek tıkla açabilirsiniz.
                        </span>
                        <button
                          onClick={() => setGibLoginClient(client)}
                          className="mb-1 rounded-md bg-amber-600 px-2.5 py-1 text-xs font-semibold text-white hover:bg-amber-700"
                          title="GİB giriş ekranındaki doğrulama kodunu girin — oturum 12 saat saklanır."
                        >
                          GİB Oturumu Aç
                        </button>
                      </>
                    )}
                    {client.isArchived ? (
                      <span className="rounded-full bg-navy-100 px-2.5 py-0.5 text-xs font-semibold text-navy-500">
                        Arşivde
                      </span>
                    ) : (
                      <span className="rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-semibold text-green-700">
                        Aktif
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <div className="flex justify-end gap-2">
                      <button
                        onClick={() => openEditModal(client)}
                        className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50"
                      >
                        Düzenle
                      </button>
                      <button
                        onClick={() => openIntegrationModal(client)}
                        className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50"
                      >
                        Entegrasyon
                      </button>
                      {/* Faz 17: GİB Portalı mükelleflerinde insan destekli giriş — kullanıcı
                          doğrulama kodunu girer, oturum 12 saat saklanır. */}
                      {integrationsByClient[client.id]?.provider === "GibPortal" && (
                        <button
                          onClick={() => setGibLoginClient(client)}
                          className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50"
                          title="GİB giriş ekranındaki doğrulama kodunu girin — oturum 12 saat saklanır ve çekimler otomatik sürer."
                        >
                          GİB Oturumu Aç
                        </button>
                      )}
                      {/* Faz 13: plaka kayıtları — akaryakıt fişi KKEG eşleştirmesi için */}
                      <button
                        onClick={() => openVehiclesModal(client)}
                        className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50"
                        title="Kayıtlı araçlar (plaka) — akaryakıt fişlerinde binek/ticari ayrımı"
                      >
                        Araçlar
                      </button>
                      {/* Veri içe aktarma: Luca mizanı, geçmiş fiş dökümü veya cari şablonu
                          yüklenir — autocomplete önerileri ve kod motoru hafızası dolar. */}
                      <button
                        onClick={() => setImportClient(client)}
                        className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50"
                        title="Mizan, geçmiş fiş listesi veya cari kod şablonunu yükleyin — kod motoru geçmiş kodlamanızı öğrenir"
                      >
                        Veri İçe Aktar
                      </button>
                      {/* Faz 11 (E7): mükellef ücret tanımı Tahsilat sayfasındaki modaldan yapılır. */}
                      <Link
                        href="/panel/tahsilat"
                        className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50"
                        title="Aylık hizmet ücreti tanımı ve tahsilat takibi"
                      >
                        Ücret
                      </Link>
                      <button
                        onClick={() => toggleArchive(client)}
                        disabled={rowBusy === client.id}
                        className="rounded-md px-2.5 py-1 text-xs font-semibold text-navy-700 hover:bg-navy-50 disabled:opacity-50"
                      >
                        {rowBusy === client.id
                          ? "İşleniyor..."
                          : client.isArchived
                            ? "Arşivden Çıkar"
                            : "Arşivle"}
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Ekle / Düzenle modalı */}
      {modalOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/50 px-4"
          onClick={() => !saving && setModalOpen(false)}
        >
          <div
            className="max-h-[90vh] w-full max-w-lg overflow-y-auto rounded-xl bg-white p-6 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="text-lg font-bold text-navy-900">
              {editingId ? "Mükellefi Düzenle" : "Yeni Mükellef"}
            </h2>

            {formError && <div className="alert-error mt-4">{formError}</div>}

            <form onSubmit={handleSubmit} className="mt-5 space-y-4" noValidate>
              <div>
                <label htmlFor="title" className="label">
                  Unvan <span className="text-red-500">*</span>
                </label>
                <input
                  id="title"
                  type="text"
                  className="input"
                  placeholder="Örn: ABC Tekstil San. Tic. Ltd. Şti."
                  value={form.title}
                  onChange={(e) => setForm({ ...form, title: e.target.value })}
                />
              </div>
              <div>
                <label htmlFor="taxId" className="label">
                  VKN / TCKN <span className="text-red-500">*</span>
                </label>
                <input
                  id="taxId"
                  type="text"
                  inputMode="numeric"
                  maxLength={11}
                  className="input"
                  placeholder="10 haneli VKN veya 11 haneli TCKN"
                  value={form.taxId}
                  onChange={(e) =>
                    setForm({ ...form, taxId: e.target.value.replace(/\D/g, "") })
                  }
                />
              </div>
              <div className="grid gap-4 sm:grid-cols-2">
                <div>
                  <label htmlFor="taxOffice" className="label">
                    Vergi Dairesi
                  </label>
                  <input
                    id="taxOffice"
                    type="text"
                    className="input"
                    placeholder="Örn: Kadıköy VD"
                    value={form.taxOffice}
                    onChange={(e) => setForm({ ...form, taxOffice: e.target.value })}
                  />
                </div>
                <div>
                  <label htmlFor="clientPhone" className="label">
                    Telefon
                  </label>
                  <input
                    id="clientPhone"
                    type="tel"
                    className="input"
                    placeholder="05xx xxx xx xx"
                    value={form.phone}
                    onChange={(e) => setForm({ ...form, phone: e.target.value })}
                  />
                </div>
              </div>
              <div>
                <label htmlFor="clientEmail" className="label">
                  E-posta
                </label>
                <input
                  id="clientEmail"
                  type="email"
                  className="input"
                  placeholder="muhasebe@firma.com"
                  value={form.email}
                  onChange={(e) => setForm({ ...form, email: e.target.value })}
                />
              </div>
              <div className="flex flex-wrap gap-6">
                <label className="flex items-center gap-2 text-sm text-navy-800">
                  <input
                    type="checkbox"
                    className="h-4 w-4 rounded border-navy-300"
                    checked={form.eInvoiceEnabled}
                    onChange={(e) => setForm({ ...form, eInvoiceEnabled: e.target.checked })}
                  />
                  e-Fatura mükellefi
                </label>
                <label className="flex items-center gap-2 text-sm text-navy-800">
                  <input
                    type="checkbox"
                    className="h-4 w-4 rounded border-navy-300"
                    checked={form.eArchiveEnabled}
                    onChange={(e) => setForm({ ...form, eArchiveEnabled: e.target.checked })}
                  />
                  e-Arşiv mükellefi
                </label>
                {/* Faz 8 (B3): akaryakıt giderlerinin KKEG %30 kısıtına girmesi bu ayara bağlıdır. */}
                {editingId && (
                  <label
                    className="flex items-center gap-2 text-sm text-navy-800"
                    title="İşaretliyse akaryakıt giderleri de binek oto %30 KKEG kısıtına girer."
                  >
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-navy-300"
                      checked={hasVehicle}
                      disabled={!codingSettings}
                      onChange={(e) => setHasVehicle(e.target.checked)}
                    />
                    Binek araç var (KKEG)
                    {!codingSettings && <span className="text-xs text-navy-400">(yükleniyor...)</span>}
                  </label>
                )}
              </div>
              <div>
                <label htmlFor="integrator" className="label">
                  Entegratör
                </label>
                <select
                  id="integrator"
                  className="input"
                  value={form.integratorName}
                  onChange={(e) => setForm({ ...form, integratorName: e.target.value })}
                >
                  {integratorOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
                {!form.integratorName && (
                  <p className="mt-1 text-xs text-navy-400">
                    Entegratör zorunlu değildir. Entegratör kullanmayan mükelleflerde
                    mükellefi kaydettikten sonra <strong>Entegrasyon</strong> ekranından
                    &quot;GİB Portalı (entegratör yok)&quot; seçin: gelen belgeler ve
                    e-Tebligatlar mükellefin GİB kimliğiyle her gece otomatik çekilir.
                  </p>
                )}
              </div>

              <div className="flex justify-end gap-3 pt-2">
                <button
                  type="button"
                  className="btn-secondary"
                  onClick={() => setModalOpen(false)}
                  disabled={saving}
                >
                  Vazgeç
                </button>
                <button type="submit" className="btn-primary" disabled={saving}>
                  {saving ? "Kaydediliyor..." : editingId ? "Güncelle" : "Kaydet"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Faz 13: Araçlar modalı (plaka kayıtları) */}
      {vehiclesClient && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/50 px-4"
          onClick={closeVehiclesModal}
        >
          <div
            className="max-h-[90vh] w-full max-w-lg overflow-y-auto rounded-xl bg-white p-6 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="text-lg font-bold text-navy-900">Kayıtlı Araçlar</h2>
            <p className="mt-1 text-sm text-navy-500">{vehiclesClient.title}</p>
            <p className="mt-2 text-xs text-navy-400">
              Akaryakıt fişindeki plaka bu listeyle eşleştirilir: binek araçta giderin %30&apos;u
              otomatik KKEG işaretlenir, ticari araçta tam gider yazılır. Eşleşmeyen plaka
              fiş ekranında uyarı olarak gösterilir.
            </p>

            {vehiclesError && <div className="alert-error mt-4">{vehiclesError}</div>}

            {vehiclesLoading ? (
              <div className="mt-6 space-y-3">
                {[1, 2].map((i) => (
                  <div key={i} className="animate-pulse rounded-lg bg-navy-50 p-4">
                    <div className="h-4 w-1/2 rounded bg-navy-100" />
                  </div>
                ))}
              </div>
            ) : (
              <>
                {/* Araç listesi */}
                {vehicles.length === 0 ? (
                  <div className="mt-5 rounded-lg border border-dashed border-navy-200 bg-navy-50/40 px-4 py-6 text-center text-sm text-navy-500">
                    Henüz araç kaydı yok. Aşağıdan plaka ekleyin.
                  </div>
                ) : (
                  <ul className="mt-5 divide-y divide-navy-50 rounded-lg border border-navy-100">
                    {vehicles.map((vehicle) => (
                      <li key={vehicle.id} className="flex items-center justify-between gap-3 px-4 py-3">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="font-mono text-sm font-semibold text-navy-900">
                            {vehicle.plate}
                          </span>
                          {vehicle.isPassengerCar ? (
                            <span
                              className="rounded-full bg-orange-100 px-2 py-0.5 text-xs font-semibold text-orange-700"
                              title="Akaryakıt giderinin %30'u KKEG olarak işaretlenir"
                            >
                              Binek
                            </span>
                          ) : (
                            <span
                              className="rounded-full bg-green-100 px-2 py-0.5 text-xs font-semibold text-green-700"
                              title="Akaryakıt gideri tam gider yazılır (KKEG yok)"
                            >
                              Ticari
                            </span>
                          )}
                          {vehicle.description && (
                            <span className="text-xs text-navy-500">{vehicle.description}</span>
                          )}
                        </div>
                        <button
                          onClick={() => handleVehicleDelete(vehicle)}
                          disabled={vehicleDeletingId === vehicle.id || vehicleSaving}
                          className="rounded-md px-2 py-1 text-xs font-semibold text-red-600 hover:bg-red-50 disabled:opacity-50"
                        >
                          {vehicleDeletingId === vehicle.id ? "Siliniyor..." : "Sil"}
                        </button>
                      </li>
                    ))}
                  </ul>
                )}

                {/* Ekleme formu */}
                <form onSubmit={handleVehicleAdd} className="mt-5 space-y-4 border-t border-navy-100 pt-4" noValidate>
                  <div className="grid gap-4 sm:grid-cols-2">
                    <div>
                      <label htmlFor="plateInput" className="label">
                        Plaka <span className="text-red-500">*</span>
                      </label>
                      <input
                        id="plateInput"
                        type="text"
                        className="input font-mono uppercase"
                        placeholder="34ABC123"
                        maxLength={12}
                        value={plateInput}
                        onChange={(e) => setPlateInput(e.target.value)}
                      />
                      <p className="mt-1 text-xs text-navy-400">
                        Boşluklu yazabilirsiniz (34 ABC 123) — sistem normalize eder.
                      </p>
                    </div>
                    <div>
                      <label htmlFor="vehicleDescription" className="label">
                        Açıklama
                      </label>
                      <input
                        id="vehicleDescription"
                        type="text"
                        className="input"
                        placeholder="Ör. Beyaz Passat"
                        maxLength={200}
                        value={vehicleDescriptionInput}
                        onChange={(e) => setVehicleDescriptionInput(e.target.value)}
                      />
                    </div>
                  </div>
                  <label
                    className="flex items-center gap-2 text-sm text-navy-800"
                    title="İşaretliyse bu aracın akaryakıt giderinin %30'u KKEG olarak işaretlenir; ticari araçta tam gider."
                  >
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-navy-300"
                      checked={isPassengerCarInput}
                      onChange={(e) => setIsPassengerCarInput(e.target.checked)}
                    />
                    Binek araç (akaryakıt gideri %30 KKEG kısıtına girer)
                  </label>
                  <div className="flex justify-end gap-3">
                    <button
                      type="button"
                      className="btn-secondary"
                      onClick={closeVehiclesModal}
                      disabled={vehicleSaving || vehicleDeletingId !== null}
                    >
                      Kapat
                    </button>
                    <button
                      type="submit"
                      className="btn-primary"
                      disabled={vehicleSaving || vehicleDeletingId !== null}
                    >
                      {vehicleSaving ? "Ekleniyor..." : "Araç Ekle"}
                    </button>
                  </div>
                </form>
              </>
            )}
          </div>
        </div>
      )}

      {/* Veri İçe Aktar modalı: ortak bileşen — satır bağlamındaki mükellefe sabitlenir
          (mizan / fiş listesi / cari şablonu / hesap planı CSV + mükellef listesi + araç
          plakaları sekmeleri). */}
      {importClient && (
        <DataImportModal
          client={{ id: importClient.id, title: importClient.title }}
          onClose={() => setImportClient(null)}
        />
      )}

      {/* Faz 17 — GİB insan destekli giriş modalı: kullanıcı doğrulama kodunu girer;
          başarıda liste yenilenir (uyarı rozeti temizlenmiş görünür). */}
      {gibLoginClient && (
        <GibLoginModal
          clientId={gibLoginClient.id}
          clientTitle={gibLoginClient.title}
          onClose={() => setGibLoginClient(null)}
          onSuccess={() => loadClients(search, showArchived)}
        />
      )}

      {/* Entegrasyon modalı */}
      {integrationClient && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-navy-950/50 px-4"
          onClick={closeIntegrationModal}
        >
          <div
            className="max-h-[90vh] w-full max-w-lg overflow-y-auto rounded-xl bg-white p-6 shadow-xl"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="text-lg font-bold text-navy-900">Entegratör Bağlantısı</h2>
            <p className="mt-1 text-sm text-navy-500">{integrationClient.title}</p>

            {integrationError && <div className="alert-error mt-4">{integrationError}</div>}

            {integrationLoading ? (
              <div className="mt-6 space-y-3">
                {[1, 2].map((i) => (
                  <div key={i} className="animate-pulse rounded-lg bg-navy-50 p-4">
                    <div className="h-4 w-1/2 rounded bg-navy-100" />
                  </div>
                ))}
              </div>
            ) : (
              <form onSubmit={handleIntegrationSave} className="mt-5 space-y-4" noValidate>
                {integration && (
                  <div className="rounded-lg border border-navy-100 bg-navy-50 px-4 py-3 text-sm text-navy-700">
                    <p>
                      Kayıtlı sağlayıcı: <strong>{providerLabel(integration.provider)}</strong>
                      {integration.maskedApiKey && (
                        <>
                          {" "}· Anahtar: <span className="font-mono">{integration.maskedApiKey}</span>
                        </>
                      )}
                      {integration.maskedUsername && (
                        <>
                          {" "}· Kullanıcı adı: <span className="font-mono">{integration.maskedUsername}</span>
                        </>
                      )}
                    </p>
                    <p className="mt-1 text-xs text-navy-500">
                      {integration.lastSyncAt
                        ? `Son senkron: ${new Date(integration.lastSyncAt).toLocaleString("tr-TR")}`
                        : "Henüz senkronizasyon yapılmadı."}
                    </p>
                  </div>
                )}

                <div>
                  <label htmlFor="providerChoice" className="label">
                    Sağlayıcı <span className="text-red-500">*</span>
                  </label>
                  <select
                    id="providerChoice"
                    className="input"
                    value={providerChoice}
                    onChange={(e) =>
                      setProviderChoice(e.target.value as IntegrationProviderCode)
                    }
                  >
                    <option value="EArsivPortal">
                      GİB e-Arşiv Portalı (entegratör yok — fatura kesme dahil)
                    </option>
                    <option value="GibPortal">GİB Portalı (entegratör yok)</option>
                    <option value="Mock">Mock (Demo) — anahtar gerektirmez</option>
                    <option value="Nilvera">Nilvera</option>
                    <option value="Uyumsoft">Uyumsoft</option>
                    <option value="DoganDonusum">Doğan Dönüşüm</option>
                    <option value="Elogo">eLogo (Logo)</option>
                    <option value="Edm">EDM Bilişim</option>
                    <option value="Parasut">Paraşüt</option>
                  </select>
                </div>

                {providerChoice === "Nilvera" && (
                  <div>
                    <label htmlFor="apiKeyInput" className="label">
                      API Anahtarı{" "}
                      {!(integration?.provider === "Nilvera" && integration.maskedApiKey) && (
                        <span className="text-red-500">*</span>
                      )}
                    </label>
                    <input
                      id="apiKeyInput"
                      type="password"
                      className="input font-mono"
                      placeholder={
                        integration?.provider === "Nilvera" && integration.maskedApiKey
                          ? `Kayıtlı: ${integration.maskedApiKey} — değiştirmek için yeni anahtar girin`
                          : "Nilvera portalinden aldığınız API anahtarı"
                      }
                      value={apiKeyInput}
                      onChange={(e) => setApiKeyInput(e.target.value)}
                      autoComplete="off"
                    />
                    <p className="mt-1 text-xs text-navy-400">
                      Anahtarınız şifrelenerek saklanır; yalnızca son 4 hanesi görüntülenir.
                    </p>
                  </div>
                )}

                {isGibPortal(providerChoice) && (
                  <>
                    {providerChoice === "EArsivPortal" ? (
                      <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-900">
                        Mükellefin <strong>e-Arşiv Portalı</strong> giriş bilgileri
                        (earsivportal.efatura.gov.tr). Bu yol gerçek API üzerinden çalışır —{" "}
                        <strong>doğrulama kodu (captcha) istemez</strong> ve fatura{" "}
                        <strong>kesmeye</strong> de izin verir.
                        <span className="mt-1 block text-emerald-800">
                          Kapsam: yalnızca <strong>e-Arşiv belgeleri</strong>. Ticari e-Fatura ve
                          e-Tebligat bu portalda bulunmaz — onlar için mükellefe ayrıca
                          &quot;GİB Portalı&quot; entegrasyonu veya müşavir girişi tanımlayın.
                        </span>
                      </div>
                    ) : (
                      <div className="rounded-lg border border-sky-200 bg-sky-50 px-4 py-3 text-sm text-sky-900">
                        Mükellefin Dijital Vergi Dairesi giriş bilgileri.{" "}
                        <strong>Bir kez girilir</strong>; sistem gelen belgeleri ve e-Tebligatları
                        her gece otomatik çeker.
                        <span className="mt-1 block text-sky-800">
                          Not: <strong>Müşavir girişi tanımlıysa ayrıca mükellef şifresi gerekmez</strong> —
                          &quot;GİB Bağlantısı&quot; sayfasından müşavir kimliğinizle tek oturum açmanız
                          yeterlidir; bu alanlar yalnızca mükellefin kendi kimliğiyle çekim
                          yapmak isterseniz doldurulur.
                        </span>
                      </div>
                    )}

                    {integration?.provider === "GibPortal" && integration.manualWarning && (
                      <div className="rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
                        <strong>⚠ Elle doğrulama gerekli.</strong>
                        <span className="mt-1 block">{integration.manualWarning}</span>
                        {integration.manualWarningAt && (
                          <span className="mt-1 block text-xs text-amber-700">
                            {new Date(integration.manualWarningAt).toLocaleString("tr-TR")}
                          </span>
                        )}
                      </div>
                    )}

                    <div>
                      <label htmlFor="usernameInput" className="label">
                        GİB Kullanıcı Kodu / TCKN{" "}
                        {!(isGibPortal(integration?.provider ?? "Mock") && integration?.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="usernameInput"
                        type="text"
                        className="input font-mono"
                        placeholder={
                          isGibPortal(integration?.provider ?? "Mock") && integration?.maskedUsername
                            ? `Kayıtlı: ${integration.maskedUsername} — değiştirmek için yeni kimlik girin`
                            : "Mükellefin GİB kullanıcı kodu veya TC kimlik numarası"
                        }
                        value={usernameInput}
                        onChange={(e) => setUsernameInput(e.target.value)}
                        autoComplete="off"
                      />
                    </div>
                    <div>
                      <label htmlFor="passwordInput" className="label">
                        GİB Şifresi{" "}
                        {!(isGibPortal(integration?.provider ?? "Mock") && integration?.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="passwordInput"
                        type="password"
                        className="input font-mono"
                        placeholder={
                          isGibPortal(integration?.provider ?? "Mock") && integration?.maskedUsername
                            ? "Kimliği değiştirmek için yeni şifre girin"
                            : "Mükellefin Dijital Vergi Dairesi şifresi"
                        }
                        value={passwordInput}
                        onChange={(e) => setPasswordInput(e.target.value)}
                        autoComplete="new-password"
                      />
                      <p className="mt-1 text-xs text-navy-400">
                        Kimlik bilgileri şifrelenerek saklanır; kullanıcı kodunun yalnızca ilk 2 ve
                        son 2 karakteri görüntülenir, şifre hiçbir zaman görüntülenmez ve
                        günlüklere yazılmaz. GİB ek doğrulama (SMS kodu / görsel doğrulama)
                        isterse sistem bunu <strong>çözmeye çalışmaz</strong>; mükellef &quot;Elle
                        doğrulama gerekli&quot; olarak işaretlenir.
                      </p>
                    </div>

                    <div>
                      <button
                        type="button"
                        className="btn-secondary flex items-center gap-2"
                        onClick={handleIntegrationTest}
                        disabled={testing || integrationSaving || integrationDeleting}
                      >
                        {testing && (
                          <span className="h-4 w-4 animate-spin rounded-full border-2 border-navy-400 border-t-transparent" />
                        )}
                        {testing ? "Test ediliyor..." : "Bağlantıyı Test Et"}
                      </button>
                      {testResult && (
                        <div
                          className={`mt-3 rounded-lg border px-4 py-3 text-sm ${
                            testResult.ok
                              ? "border-green-200 bg-green-50 text-green-800"
                              : testResult.manual
                                ? "border-amber-300 bg-amber-50 text-amber-900"
                                : "border-red-200 bg-red-50 text-red-700"
                          }`}
                        >
                          {testResult.ok ? "✓ " : testResult.manual ? "⚠ " : "✕ "}
                          {testResult.message}
                        </div>
                      )}
                      <p className="mt-2 text-xs text-navy-400">
                        Test yalnızca giriş dener; belge çekmez. Kimliği kaydetmeden de test
                        edebilirsiniz.
                      </p>
                    </div>
                  </>
                )}

                {isCredentialProvider(providerChoice) && (
                  <>
                    <div>
                      <label htmlFor="usernameInput" className="label">
                        Kullanıcı Adı{" "}
                        {!(integration?.provider === providerChoice && integration.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="usernameInput"
                        type="text"
                        className="input font-mono"
                        placeholder={
                          integration?.provider === providerChoice && integration.maskedUsername
                            ? `Kayıtlı: ${integration.maskedUsername} — değiştirmek için yeni kimlik girin`
                            : credentialProviderTexts[providerChoice]?.usernamePlaceholder ??
                              "Web servis kullanıcı adınız"
                        }
                        value={usernameInput}
                        onChange={(e) => setUsernameInput(e.target.value)}
                        autoComplete="off"
                      />
                    </div>
                    <div>
                      <label htmlFor="passwordInput" className="label">
                        Şifre{" "}
                        {!(integration?.provider === providerChoice && integration.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="passwordInput"
                        type="password"
                        className="input font-mono"
                        placeholder={
                          integration?.provider === providerChoice && integration.maskedUsername
                            ? "Kimliği değiştirmek için yeni şifre girin"
                            : credentialProviderTexts[providerChoice]?.passwordPlaceholder ??
                              "Web servis şifreniz"
                        }
                        value={passwordInput}
                        onChange={(e) => setPasswordInput(e.target.value)}
                        autoComplete="new-password"
                      />
                      <p className="mt-1 text-xs text-navy-400">
                        {credentialProviderTexts[providerChoice]?.passwordHelpPrefix ?? ""}
                        Kimlik bilgileriniz şifrelenerek saklanır; kullanıcı adının yalnızca ilk 2 ve
                        son 2 karakteri görüntülenir, şifre hiçbir zaman görüntülenmez.
                      </p>
                    </div>
                    <div>
                      <label htmlFor="endpointInput" className="label">
                        Servis Adresi <span className="text-navy-400">(opsiyonel)</span>
                      </label>
                      <input
                        id="endpointInput"
                        type="url"
                        className="input font-mono"
                        placeholder={
                          credentialProviderTexts[providerChoice]?.endpointPlaceholder ??
                          "https://..."
                        }
                        value={endpointInput}
                        onChange={(e) => setEndpointInput(e.target.value)}
                        autoComplete="off"
                      />
                      <p className="mt-1 text-xs text-navy-400">
                        {credentialProviderTexts[providerChoice]?.endpointHelp ??
                          "Entegratörün bildirdiği servis adresi. Boş bırakılırsa varsayılan kullanılır."}
                      </p>
                    </div>
                    {providerChoice === "Uyumsoft" && (
                      <div>
                        <span className="label">Bu mükellef hangi belgeleri kullanıyor?</span>
                        <div className="mt-1 space-y-2">
                          <label className="flex items-center gap-2 text-sm text-navy-800">
                            <input
                              type="checkbox"
                              className="h-4 w-4 rounded border-navy-300"
                              checked={uyumsoftEinvoice}
                              onChange={(e) => setUyumsoftEinvoice(e.target.checked)}
                            />
                            e-Fatura / e-Arşiv
                          </label>
                          <label className="flex items-center gap-2 text-sm text-navy-800">
                            <input
                              type="checkbox"
                              className="h-4 w-4 rounded border-navy-300"
                              checked={uyumsoftEsmm}
                              onChange={(e) => setUyumsoftEsmm(e.target.checked)}
                            />
                            e-SMM (Serbest Meslek Makbuzu)
                          </label>
                        </div>
                        {uyumsoftEsmm && (
                          <p className="mt-1 text-xs text-navy-400">
                            Serbest meslek mensupları için makbuzlar Uyumsoft VoucherIntegration
                            servisinden çekilir.
                            {uyumsoftEinvoice
                              ? " İkisi de işaretliyse hem e-Fatura gelen kutusu hem e-SMM makbuzları alınır."
                              : " Yalnız e-SMM işaretli: e-Fatura gelen kutusu sorgulanmaz."}
                          </p>
                        )}
                      </div>
                    )}
                  </>
                )}

                {providerChoice === "Parasut" && (
                  <>
                    <div>
                      <label htmlFor="parasutClientIdInput" className="label">
                        Client ID{" "}
                        {!(integration?.provider === "Parasut" && integration.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="parasutClientIdInput"
                        type="text"
                        className="input font-mono"
                        placeholder="Paraşüt API uygulamanızın Client ID değeri"
                        value={parasutClientIdInput}
                        onChange={(e) => setParasutClientIdInput(e.target.value)}
                        autoComplete="off"
                      />
                    </div>
                    <div>
                      <label htmlFor="parasutClientSecretInput" className="label">
                        Client Secret{" "}
                        {!(integration?.provider === "Parasut" && integration.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="parasutClientSecretInput"
                        type="password"
                        className="input font-mono"
                        placeholder="Paraşüt API uygulamanızın Client Secret değeri"
                        value={parasutClientSecretInput}
                        onChange={(e) => setParasutClientSecretInput(e.target.value)}
                        autoComplete="off"
                      />
                    </div>
                    <div>
                      <label htmlFor="usernameInput" className="label">
                        E-posta (kullanıcı adı){" "}
                        {!(integration?.provider === "Parasut" && integration.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="usernameInput"
                        type="email"
                        className="input font-mono"
                        placeholder={
                          integration?.provider === "Parasut" && integration.maskedUsername
                            ? `Kayıtlı: ${integration.maskedUsername} — değiştirmek için yeni kimlik girin`
                            : "Paraşüt hesabınızın e-posta adresi"
                        }
                        value={usernameInput}
                        onChange={(e) => setUsernameInput(e.target.value)}
                        autoComplete="off"
                      />
                    </div>
                    <div>
                      <label htmlFor="passwordInput" className="label">
                        Şifre{" "}
                        {!(integration?.provider === "Parasut" && integration.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="passwordInput"
                        type="password"
                        className="input font-mono"
                        placeholder={
                          integration?.provider === "Parasut" && integration.maskedUsername
                            ? "Kimliği değiştirmek için yeni şifre girin"
                            : "Paraşüt hesabınızın şifresi"
                        }
                        value={passwordInput}
                        onChange={(e) => setPasswordInput(e.target.value)}
                        autoComplete="new-password"
                      />
                    </div>
                    <div>
                      <label htmlFor="parasutCompanyIdInput" className="label">
                        Firma ID (companyId){" "}
                        {!(integration?.provider === "Parasut" && integration.maskedUsername) && (
                          <span className="text-red-500">*</span>
                        )}
                      </label>
                      <input
                        id="parasutCompanyIdInput"
                        type="text"
                        className="input font-mono"
                        placeholder="ör. 123456"
                        value={parasutCompanyIdInput}
                        onChange={(e) => setParasutCompanyIdInput(e.target.value)}
                        autoComplete="off"
                      />
                      <p className="mt-1 text-xs text-navy-400">
                        Paraşüt hesabınızda Ayarlar → API&apos;den uygulama oluşturup Client ID/Secret
                        alın; Firma ID adres çubuğundaki sayıdır. Kimlik bilgileriniz şifrelenerek
                        saklanır; e-postanın yalnızca ilk 2 ve son 2 karakteri görüntülenir.
                      </p>
                    </div>
                  </>
                )}

                {(providerChoice === "Nilvera" || isCredentialProvider(providerChoice)) && (
                  <label className="flex items-center gap-2 text-sm text-navy-800">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-navy-300"
                      checked={useTestEnv}
                      onChange={(e) => setUseTestEnv(e.target.checked)}
                    />
                    {providerChoice === "Nilvera"
                      ? "Test ortamını kullan (apitest.nilvera.com)"
                      : credentialProviderTexts[providerChoice]?.testEnvLabel ??
                        "Test ortamını kullan"}
                  </label>
                )}

                <div className="flex items-center justify-between gap-3 pt-2">
                  {integration ? (
                    <button
                      type="button"
                      onClick={handleIntegrationDelete}
                      disabled={integrationDeleting || integrationSaving || testing}
                      className="rounded-md px-2.5 py-1.5 text-xs font-semibold text-red-600 hover:bg-red-50 disabled:opacity-50"
                    >
                      {integrationDeleting ? "Siliniyor..." : "Bağlantıyı Sil"}
                    </button>
                  ) : (
                    <span />
                  )}
                  <div className="flex gap-3">
                    <button
                      type="button"
                      className="btn-secondary"
                      onClick={closeIntegrationModal}
                      disabled={integrationSaving || integrationDeleting || testing}
                    >
                      Kapat
                    </button>
                    <button
                      type="submit"
                      className="btn-primary"
                      disabled={integrationSaving || integrationDeleting || testing}
                    >
                      {integrationSaving ? "Kaydediliyor..." : "Kaydet"}
                    </button>
                  </div>
                </div>
              </form>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
