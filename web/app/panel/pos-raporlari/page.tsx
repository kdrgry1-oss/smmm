"use client";

import { useEffect, useState } from "react";
import { api, ClientResponse } from "@/lib/api";

type Provider = { code: string; name: string; kind: string; devices: string; access: string; training: string };
type Connection = { id: string; provider: string; displayName: string; merchantId: string | null; terminalId: string | null; deviceSerialNumber: string | null; apiAccessEnabled: boolean; connectionStatus: string; lastSyncAt: string | null; lastError: string | null };

const apiGuides: Record<string, string[]> = {
  TokenBeko: ["Token/Beko satış sonrası veya kurumsal entegrasyon ekibine cihaz modeli, mali hafıza/seri no ve VKN ile başvurun.", "Cihaza yazılımevi/harici satış entegrasyonu aktivasyonu ve test terminali tanımlatın.", "Size verilen TSM/entegrasyon uç noktası, teknik kullanıcı veya sertifikayı bu bağlantıya ekleyin."],
  Ingenico: ["Ingenico iş ortağı veya cihazı sağlayan banka/ödeme kuruluşundan terminal uygulaması için entegrasyon yetkisi talep edin.", "Terminal ID, seri no ve üretim/test ortamı bilgilerini alın.", "Terminal API anahtarı/sertifikası varsa ekleyin; yalnız cihaz parolası API anahtarı değildir."],
  ProfiloVerifone: ["Profilo/VeriFone yetkili satış sonrası kanalına cihaz modeli, seri no, VKN ve yazılım entegrasyon talebini iletin.", "Üreticinin açtığı teknik kullanıcı, TSM veya raporlama erişimini kullanın.", "Sadece cihazdan alınan Z raporu varsa PDF/Fotoğrafı Fişler ekranında Z Raporu türüyle yükleyin."],
  Pavo: ["Pavo hesabınızdan veya teknik ekipten Harici Satış Entegrasyonu / raporlama erişimi isteyin.", "Cihaz seri no, üye işyeri VKN ve ilgili banka terminal tanımlarını paylaşın.", "Verilen teknik kullanıcı ya da API anahtarını buraya ekleyin."],
  Hugin: ["Hugin yetkili bayi veya yazılımevi üzerinden bulut/entegrasyon hizmetini aktifleştirin.", "Cihaz seri no, VKN ve entegrasyon uygulaması için test onayı alın.", "Resmi API yoksa günlük Z raporunu cihazdan PDF/fotoğraf olarak yükleyin."],
  Inpos: ["InPOS teknik destekten işletme hesabı ve harici yazılım entegrasyon yetkisi isteyin.", "Terminal ID, cihaz seri no ve teknik erişim bilgilerini alın.", "Raporlama API'si sağlanırsa anahtarı ekleyin; yoksa Z raporunu cihazdan dışa aktarın."],
  Paygo: ["Paygo teknik destek/işletici kuruluştan raporlama veya entegrasyon yetkisi isteyin.", "VKN, cihaz seri no ve terminal ID ile başvurun.", "Resmi erişim açılmadan hesabı bağlamayın; alternatif olarak Z raporunu belge olarak yükleyin."],
  Pax: ["POS'u veren banka veya ödeme kuruluşundan üye işyeri raporlama API'sini isteyin.", "PAX cihazın seri no/terminal ID'sini başvuruya ekleyin.", "Banka raporlama erişimini kullanın; cihaz üreticisi tek başına satış verisi yetkisi vermez."],
  BankaPos: ["Bankanızın üye işyeri/POS destek birimine kurumsal raporlama API veya günlük POS ekstresi talebi açın.", "VKN, üye işyeri no, terminal ID ve yetkili e-posta ile başvurun.", "API yoksa panelden günlük POS/Z raporunu PDF/Excel olarak indirin ve yükleyin."],
  PayTR: ["PayTR Mağaza Paneli > Destek & Kurulum > Entegrasyon Bilgileri sayfasına yetkili kullanıcıyla girin.", "merchant_id, merchant_key ve merchant_salt değerlerini alın. Bunlar sadece yetkili ana kullanıcı/teknik sorumluya gösterilir.", "Üç bilgiyi tek seferde API erişim bilgisi alanına girin; anahtarları e-posta ile paylaşmayın."],
  Iyzico: ["iyzico Merchant Panel > Ayarlar > API Anahtarları bölümüne yönetici rolüyle girin.", "API Key ve Secret Key oluşturun; canlı ve sandbox anahtarlarını birbirine karıştırmayın.", "Canlı anahtarları bu bağlantıya ekleyin ve raporlama yetkisini teknik hesabınızda doğrulayın."],
  Param: ["Param üye işyeri temsilcinizden API ve raporlama erişimi açılmasını isteyin.", "Üye işyeri no, VKN ve teknik IP/uygulama bilgilerini sağlayıcının talebine göre iletin.", "Verilen müşteri kodu/şifre veya imza anahtarını tek seferde kaydedin."],
  Sipay: ["Sipay üye işyeri portalı veya teknik destek üzerinden API erişimi talep edin.", "İşyeri numarası, VKN ve teknik sorumlu e-postasını tanımlatın.", "Size iletilen API kimlik bilgilerini kaydedin; üretim ve test bilgilerini ayırın."],
  Moka: ["Moka üye işyeri panelinden ya da müşteri temsilcinizden API erişimi isteyin.", "Dealer/üye işyeri bilgisi ve teknik kullanıcı yetkisini doğrulayın.", "Raporlama servisinin açık olduğunu onayladıktan sonra erişim bilgilerini ekleyin."],
  Paynet: ["Paynet panelinde yönetici/teknik kullanıcıyla API ve raporlama erişimini talep edin.", "Üye işyeri kodu ile teknik kullanıcı bilgilerini alın.", "Anahtarı kaydedin, ilk senkron öncesi Paynet'ten rapor kapsamını doğrulayın."],
};

export default function PosReportsPage() {
  const [providers, setProviders] = useState<Provider[]>([]);
  const [clients, setClients] = useState<ClientResponse[]>([]);
  const [clientId, setClientId] = useState("");
  const [connections, setConnections] = useState<Connection[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState({ provider: "TokenBeko", displayName: "", merchantId: "", terminalId: "", serial: "", credential: "", apiAccessEnabled: false });

  useEffect(() => { Promise.all([api<Provider[]>("/api/pos/providers"), api<ClientResponse[]>("/api/clients")]).then(([p, c]) => { setProviders(p); setClients(c); if (c[0]) setClientId(c[0].id); }).catch(e => setError(e.message)); }, []);
  useEffect(() => { if (!clientId) return; api<Connection[]>(`/api/clients/${clientId}/pos-connections`).then(setConnections).catch(e => setError(e.message)); }, [clientId]);
  const selected = providers.find(p => p.code === form.provider);

  async function save() {
    if (!clientId) return setError("Önce mükellef seçin.");
    setSaving(true); setError(null); setMessage(null);
    try {
      await api(`/api/clients/${clientId}/pos-connections`, { method: "POST", body: { provider: form.provider, displayName: form.displayName || selected?.name, merchantId: form.merchantId || null, terminalId: form.terminalId || null, deviceSerialNumber: form.serial || null, apiCredential: form.credential || null, apiAccessEnabled: form.apiAccessEnabled } });
      setConnections(await api<Connection[]>(`/api/clients/${clientId}/pos-connections`));
      setForm(f => ({ ...f, displayName: "", merchantId: "", terminalId: "", serial: "", credential: "" })); setMessage("Bağlantı kaydedildi. Sağlayıcı yetkisi doğrulandığında otomatik çekim etkinleşir.");
    } catch (e) { setError(e instanceof Error ? e.message : "Bağlantı kaydedilemedi."); } finally { setSaving(false); }
  }

  async function syncConnection(id: string) {
    setError(null); setMessage(null);
    try {
      const result = await api<{ recordCount: number; message: string }>(`/api/pos-connections/${id}/sync`, { method: "POST" });
      setMessage(result.message);
      setConnections(await api<Connection[]>(`/api/clients/${clientId}/pos-connections`));
    } catch (e) { setError(e instanceof Error ? e.message : "Rapor çekilemedi."); }
  }

  return <div className="space-y-6">
    <div><h1 className="text-2xl font-bold text-navy-900">POS ve Z Raporları</h1><p className="mt-1 text-sm text-navy-500">Fiziki POS, YNÖKC ve sanal POS raporlarını tek muhasebe akışında toplayın.</p></div>
    {error && <div className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</div>}{message && <div className="rounded border border-green-200 bg-green-50 p-3 text-sm text-green-700">{message}</div>}
    <section className="grid gap-6 lg:grid-cols-[1fr_1.25fr]"><div className="rounded-lg border border-navy-100 bg-white p-5 shadow-sm space-y-3"><h2 className="font-bold text-navy-900">POS hesabı bağla</h2>
      <select className="input" value={clientId} onChange={e => setClientId(e.target.value)}>{clients.map(c => <option key={c.id} value={c.id}>{c.title}</option>)}</select>
      <select className="input" value={form.provider} onChange={e => setForm({ ...form, provider: e.target.value })}>{providers.map(p => <option key={p.code} value={p.code}>{p.name} - {p.kind}</option>)}</select>
      <input className="input" placeholder="Bağlantı adı (ör. Mağaza 1)" value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })}/>
      <div className="grid gap-3 sm:grid-cols-2"><input className="input" placeholder="Üye işyeri no" value={form.merchantId} onChange={e => setForm({ ...form, merchantId: e.target.value })}/><input className="input" placeholder="Terminal ID" value={form.terminalId} onChange={e => setForm({ ...form, terminalId: e.target.value })}/></div>
      <input className="input" placeholder="Cihaz seri no" value={form.serial} onChange={e => setForm({ ...form, serial: e.target.value })}/>
      <textarea className="input min-h-20" placeholder={form.provider === "PayTR" ? "PayTR: merchant_key|merchant_salt" : "API key / secret / teknik kullanıcı bilgisi"} value={form.credential} onChange={e => setForm({ ...form, credential: e.target.value })}/>
      <label className="flex items-center gap-2 text-sm text-navy-700"><input type="checkbox" checked={form.apiAccessEnabled} onChange={e => setForm({ ...form, apiAccessEnabled: e.target.checked })}/> Sağlayıcı bu hesap için resmi API/raporlama erişimini açtı</label>
      <button className="btn-primary" disabled={saving} onClick={save}>{saving ? "Kaydediliyor..." : "Bağlantıyı Kaydet"}</button></div>
      <div className="rounded-lg border border-navy-100 bg-white p-5 shadow-sm"><h2 className="font-bold text-navy-900">Kayıtlı bağlantılar</h2><div className="mt-3 space-y-3">{connections.length === 0 ? <p className="text-sm text-navy-500">Bu mükellefte henüz POS bağlantısı yok.</p> : connections.map(c => <div key={c.id} className="border-b border-navy-100 pb-3 text-sm"><div className="font-semibold text-navy-800">{c.displayName} <span className="font-normal text-navy-500">({c.provider})</span></div><div className="mt-1 text-navy-500">{c.apiAccessEnabled ? "API erişimi bildirildi" : "API yetkisi bekleniyor"} · {c.connectionStatus}</div>{c.lastError && <div className="mt-1 text-amber-700">{c.lastError}</div>}{c.apiAccessEnabled && <button className="mt-2 text-sm font-semibold text-navy-700 hover:text-navy-900" onClick={() => syncConnection(c.id)}>Raporu Çek</button>}</div>)}</div></div></section>
    <section className="rounded-lg border border-navy-100 bg-white p-5 shadow-sm"><h2 className="font-bold text-navy-900">Sağlayıcı eğitimi ve API yetkisi</h2><p className="mt-1 text-sm text-navy-500">Fiziki cihazda Z raporu cihazdan alınır; otomatik çekim yalnız üretici, banka veya ödeme kuruluşunun resmi erişimiyle yapılır.</p><div className="mt-4 grid gap-4 lg:grid-cols-2">{providers.map(p => <details key={p.code} className="rounded border border-navy-100 p-4"><summary className="cursor-pointer font-semibold text-navy-800">{p.name} <span className="font-normal text-navy-500">- {p.kind}</span></summary><p className="mt-3 text-sm text-navy-600">{p.devices}</p><p className="mt-2 text-sm text-navy-700">Gerekli erişim: {p.access}</p><ol className="mt-3 list-decimal space-y-2 pl-5 text-sm text-navy-600">{(apiGuides[p.code] || [p.training]).map(step => <li key={step}>{step}</li>)}</ol><p className="mt-3 text-xs text-navy-500">Manuel yol: {p.training}</p></details>)}</div></section>
    <section className="rounded-lg border border-blue-200 bg-blue-50 p-5 text-sm text-blue-900"><b>Muhasebeleştirme:</b> Z raporu gelir belgesidir. Sistem, onaylanan raporu Luca aktarımında nakit için <b>100</b>, kartlı tahsilat için <b>108</b> borç; satış için <b>600</b> ve hesaplanan KDV için <b>391</b> alacak olarak oluşturur. Komisyon ve banka kesintileri POS sağlayıcısının hareket raporundan ayrıca gider/mahsup fişi olarak işlenir.</section>
  </div>;
}
