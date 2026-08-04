"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { api, clearToken, getToken, listETebligat, listJobs, MeResponse } from "@/lib/api";

const navItems = [
  { href: "/panel", label: "Panel" },
  { href: "/panel/mukellefler", label: "Mükellefler" },
  { href: "/panel/gib-baglantisi", label: "GİB Bağlantısı" },
  { href: "/panel/belgeler", label: "Belgeler" },
  { href: "/panel/faturalar", label: "Faturalar" },
  { href: "/panel/fisler", label: "Fişler" },
  { href: "/panel/onay-kuyrugu", label: "Onay Kuyruğu" },
  { href: "/panel/mutabakat", label: "Mutabakat" },
  { href: "/panel/beyannameler", label: "Beyannameler" },
  { href: "/panel/kapanis", label: "Kapanış" },
  { href: "/panel/mizan", label: "Mizan" },
  { href: "/panel/edefter", label: "e-Defter" },
  { href: "/panel/gonderim-kuyrugu", label: "Gönderim Kuyruğu" },
  { href: "/panel/tahsilat", label: "Tahsilat" },
  { href: "/panel/takvim", label: "Takvim" },
  { href: "/panel/tebligatlar", label: "e-Tebligat" },
  { href: "/panel/sertifikalar", label: "Sertifikalar" },
  { href: "/panel/kurallar", label: "Kurallar" },
  { href: "/panel/disa-aktar", label: "Dışa Aktar" },
  { href: "/panel/ayarlar", label: "Ayarlar" },
];

export default function PanelLayout({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const [me, setMe] = useState<MeResponse | null>(null);
  const [checking, setChecking] = useState(true);
  const [unreadTebligat, setUnreadTebligat] = useState<number>(0);
  const [pendingJobs, setPendingJobs] = useState<number>(0);

  useEffect(() => {
    if (!getToken()) {
      router.replace("/giris");
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const data = await api<MeResponse>("/api/auth/me");
        if (!cancelled) setMe(data);
      } catch {
        // api() 401'de zaten /giris'e yönlendirir; diğer hatalarda kullanıcıyı içeride tutarız.
      } finally {
        if (!cancelled) setChecking(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [router]);

  // Okunmamış tebligat + bekleyen gönderim rozetleri: sayfa geçişlerinde tazelenir; hatası menüyü etkilemez.
  useEffect(() => {
    if (!getToken()) return;
    let cancelled = false;
    (async () => {
      try {
        const data = await listETebligat();
        if (!cancelled) setUnreadTebligat(data.unreadCount);
      } catch {
        // rozet yalnızca bilgilendirmedir; hata durumunda sessizce gizlenir
      }
      try {
        const jobs = await listJobs();
        if (!cancelled) setPendingJobs(jobs.pendingCount);
      } catch {
        // rozet yalnızca bilgilendirmedir; hata durumunda sessizce gizlenir
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [pathname]);

  function handleLogout() {
    clearToken();
    router.push("/giris");
  }

  if (checking) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-navy-50">
        <p className="text-sm text-navy-500">Yükleniyor...</p>
      </div>
    );
  }

  return (
    <div className="flex min-h-screen bg-navy-50">
      {/* Sidebar */}
      <aside className="hidden w-60 flex-col border-r border-navy-100 bg-navy-950 text-white sm:flex">
        <div className="px-6 py-5 text-lg font-bold">
          MaliMüşavir<span className="text-navy-400">SaaS</span>
        </div>
        <nav className="flex-1 space-y-1 px-3">
          {navItems.map((item) => {
            const active =
              item.href === "/panel" ? pathname === "/panel" : pathname.startsWith(item.href);
            return (
              <Link
                key={item.href}
                href={item.href}
                className={`flex items-center justify-between rounded-lg px-3 py-2.5 text-sm font-medium transition ${
                  active ? "bg-navy-800 text-white" : "text-navy-300 hover:bg-navy-900 hover:text-white"
                }`}
              >
                <span>{item.label}</span>
                {item.href === "/panel/tebligatlar" && unreadTebligat > 0 && (
                  <span
                    className="ml-2 rounded-full bg-red-600 px-2 py-0.5 text-xs font-bold text-white"
                    title={`${unreadTebligat} okunmamış tebligat`}
                  >
                    {unreadTebligat}
                  </span>
                )}
                {item.href === "/panel/gonderim-kuyrugu" && pendingJobs > 0 && (
                  <span
                    className="ml-2 rounded-full bg-amber-500 px-2 py-0.5 text-xs font-bold text-white"
                    title={`${pendingJobs} bekleyen gönderim`}
                  >
                    {pendingJobs}
                  </span>
                )}
              </Link>
            );
          })}
        </nav>
        <div className="px-6 py-4 text-xs text-navy-500">Faz 11 önizleme</div>
      </aside>

      {/* İçerik */}
      <div className="flex flex-1 flex-col">
        <header className="flex items-center justify-between border-b border-navy-100 bg-white px-6 py-4">
          {/* Mobil nav */}
          <nav className="flex flex-wrap gap-3 sm:hidden">
            {navItems.map((item) => (
              <Link key={item.href} href={item.href} className="text-sm font-medium text-navy-700">
                {item.label}
                {item.href === "/panel/tebligatlar" && unreadTebligat > 0 && (
                  <span className="ml-1 rounded-full bg-red-600 px-1.5 py-0.5 text-[10px] font-bold text-white">
                    {unreadTebligat}
                  </span>
                )}
                {item.href === "/panel/gonderim-kuyrugu" && pendingJobs > 0 && (
                  <span className="ml-1 rounded-full bg-amber-500 px-1.5 py-0.5 text-[10px] font-bold text-white">
                    {pendingJobs}
                  </span>
                )}
              </Link>
            ))}
          </nav>
          <div className="hidden text-sm text-navy-500 sm:block">
            {me?.tenantName ?? ""}
          </div>
          <div className="flex items-center gap-4">
            <span className="text-sm font-medium text-navy-800">{me?.fullName ?? "Kullanıcı"}</span>
            <button onClick={handleLogout} className="btn-secondary !px-3 !py-1.5 text-xs">
              Çıkış
            </button>
          </div>
        </header>
        <main className="flex-1 p-6">{children}</main>
      </div>
    </div>
  );
}
