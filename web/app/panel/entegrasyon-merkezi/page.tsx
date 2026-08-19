"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { api, ClientResponse, IntegrationProviderCode, IntegrationResponse, listClientIntegrations } from "@/lib/api";

type Capability = {
  provider: IntegrationProviderCode;
  title: string;
  documentTypes: string[];
  eTebligat: string;
  documentationUrl: string;
};

const capabilities: Capability[] = [
  { provider: "Uyumsoft", title: "Uyumsoft", documentTypes: ["e-Fatura / e-Arşiv", "e-İrsaliye", "e-SMM"], eTebligat: "Özel e-Tebligat servisi sözleşmesi gerekir; genel e-belge WSDL'si tek başına yeterli değildir.", documentationUrl: "https://www.uyumsoft.com/musavir-pro-mali-musavir-yazilimi" },
  { provider: "DoganDonusum", title: "Doğan Dönüşüm", documentTypes: ["e-Fatura / e-Arşiv", "e-İrsaliye", "e-SMM", "e-Müstahsil"], eTebligat: "Özel e-Tebligat/posta kutusu erişimi ve servis sözleşmesi doğrulanmalıdır.", documentationUrl: "https://www.doganedonusum.com/dev/" },
  { provider: "Nilvera", title: "Nilvera", documentTypes: ["e-Fatura / e-Arşiv", "e-İrsaliye", "e-SMM", "e-MM"], eTebligat: "Genel geliştirici API kapsamı e-Tebligat olarak doğrulanmadı.", documentationUrl: "https://developer.nilvera.com/" },
  { provider: "Elogo", title: "eLogo", documentTypes: ["e-Fatura / e-Arşiv", "e-İrsaliye"], eTebligat: "Sağlayıcı sözleşmesindeki e-Tebligat modülü ve posta kutusu yetkisi kontrol edilmelidir.", documentationUrl: "https://www.elogo.com.tr/" },
  { provider: "Edm", title: "EDM Bilişim", documentTypes: ["e-Fatura / e-Arşiv", "e-İrsaliye"], eTebligat: "Özel servis erişimi teyit edilmeden otomatik e-Tebligat çekimi başlatılmaz.", documentationUrl: "https://www.edmbilisim.com.tr/" },
  { provider: "Parasut", title: "Paraşüt", documentTypes: ["Satış / alış faturaları", "Cari veriler"], eTebligat: "Paraşüt API'si GİB e-Tebligat kaynağı değildir.", documentationUrl: "https://developers.parasut.com/" },
  { provider: "GibPortal", title: "GİB Dijital Vergi Dairesi", documentTypes: ["GİB belgeleri", "Şirket e-Tebligatı"], eTebligat: "Mükellef veya yetkili mali müşavir oturumu gerekir. GİB doğrulama istediğinde kod insan tarafından girilir.", documentationUrl: "https://dijital.gib.gov.tr/" },
  { provider: "EArsivPortal", title: "GİB e-Arşiv Portalı", documentTypes: ["e-Arşiv faturaları"], eTebligat: "e-Tebligat kapsamı yoktur.", documentationUrl: "https://ebelge.gib.gov.tr/earsivportal.html" },
  { provider: "Mock", title: "Demo", documentTypes: ["Test verisi"], eTebligat: "Gerçek veri kaynağı değildir.", documentationUrl: "https://gib.gov.tr/" },
];

function statusText(integration: IntegrationResponse | undefined) {
  if (!integration) return "Bağlantı tanımlı değil";
  if (integration.manualWarning) return "GİB oturumu yenilenmeli";
  if (!integration.lastSyncAt) return "İlk senkron bekliyor";
  return "Bağlı";
}

export default function IntegrationCenterPage() {
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [integrations, setIntegrations] = useState<IntegrationResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    Promise.all([api<ClientResponse[]>("/api/clients"), listClientIntegrations()])
      .then(([clientRows, integrationRows]) => {
        setClients(clientRows);
        setIntegrations(integrationRows);
      })
      .catch((err: unknown) => setError(err instanceof Error ? err.message : "Entegrasyonlar yüklenemedi."))
      .finally(() => setLoading(false));
  }, []);

  const byClient = useMemo(() => Object.fromEntries(integrations.map((item) => [item.clientId, item])), [integrations]);
  const configured = integrations.filter((item) => item.provider !== "Mock").length;

  return <div className="space-y-6">
    <div className="flex flex-col justify-between gap-3 lg:flex-row lg:items-end">
      <div><h1 className="text-2xl font-bold text-navy-900">Entegrasyon Merkezi</h1><p className="mt-1 text-sm text-navy-500">Mükellef bağlantıları, belge kapsamı ve e-Tebligat kaynak durumu.</p></div>
      <div className="flex gap-3 text-sm"><div className="rounded border border-navy-200 bg-white px-4 py-2 text-navy-700"><b>{configured}</b> kayıtlı bağlantı</div><Link className="btn-primary" href="/panel/mukellefler">Bağlantı Yönet</Link></div>
    </div>
    {error && <div className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</div>}
    <section className="overflow-hidden rounded-lg border border-navy-100 bg-white shadow-sm">
      <div className="border-b border-navy-100 px-5 py-4"><h2 className="font-bold text-navy-900">Mükellef bağlantı durumu</h2></div>
      {loading ? <div className="p-5 text-sm text-navy-500">Yükleniyor...</div> : clients.length === 0 ? <div className="p-5 text-sm text-navy-500">Kayıtlı mükellef yok.</div> : <div className="divide-y divide-navy-100">{clients.map((client) => { const integration = byClient[client.id]; return <div key={client.id} className="grid gap-2 px-5 py-4 text-sm md:grid-cols-[1.4fr_1fr_1fr_auto] md:items-center"><div><div className="font-semibold text-navy-900">{client.title}</div><div className="text-navy-500">{client.taxId}</div></div><div className="text-navy-700">{integration ? capabilities.find((item) => item.provider === integration.provider)?.title ?? integration.provider : "Bağlantı yok"}</div><div className={integration?.manualWarning ? "text-amber-700" : "text-navy-600"}>{statusText(integration)}</div><Link href="/panel/mukellefler" className="text-right font-semibold text-navy-700 hover:text-navy-950">Düzenle</Link></div>; })}</div>}
    </section>
    <section className="grid gap-4 lg:grid-cols-2">
      {capabilities.filter((item) => item.provider !== "Mock").map((item) => <article key={item.provider} className="rounded-lg border border-navy-100 bg-white p-5 shadow-sm"><div className="flex items-start justify-between gap-4"><h2 className="font-bold text-navy-900">{item.title}</h2><a className="text-sm font-semibold text-navy-700 hover:text-navy-950" href={item.documentationUrl} target="_blank" rel="noreferrer">Doküman</a></div><div className="mt-3 flex flex-wrap gap-2">{item.documentTypes.map((type) => <span key={type} className="rounded bg-navy-50 px-2 py-1 text-xs font-medium text-navy-700">{type}</span>)}</div><p className="mt-4 text-sm leading-6 text-navy-600"><b className="text-navy-800">e-Tebligat:</b> {item.eTebligat}</p></article>)}
    </section>
    <section className="rounded-lg border border-amber-200 bg-amber-50 p-5 text-sm leading-6 text-amber-950"><b>Kaynak ayrımı:</b> Fatura/e-belge servisi ile e-Tebligat servisi ayrı yetkilerdir. Sistem yalnız resmi olarak açılmış sağlayıcı erişiminden veri çeker; e-Tebligat servisi doğrulanmamışsa bağlantıyı GİB kaynağı gibi göstermez.</section>
  </div>;
}
