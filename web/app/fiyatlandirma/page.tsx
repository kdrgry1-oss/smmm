"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { api, getToken, PlanResponse } from "@/lib/api";

// API'ye ulaşılamadığında gösterilecek statik yedek içerik
const fallbackPlans: PlanResponse[] = [
  {
    id: "fallback-1",
    code: "baslangic",
    name: "Başlangıç",
    monthlyPrice: 499,
    maxClients: 10,
    maxUsers: 1,
    monthlyOcrQuota: 200,
    eTebligat: false,
    unlimitedClients: false,
  },
  {
    id: "fallback-2",
    code: "profesyonel",
    name: "Profesyonel",
    monthlyPrice: 999,
    maxClients: 50,
    maxUsers: 5,
    monthlyOcrQuota: 1000,
    eTebligat: true,
    unlimitedClients: false,
  },
  {
    id: "fallback-3",
    code: "ofis",
    name: "Ofis",
    monthlyPrice: 1999,
    maxClients: 0,
    maxUsers: 15,
    monthlyOcrQuota: 5000,
    eTebligat: true,
    unlimitedClients: true,
  },
];

function formatPrice(price: number) {
  return new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 0 }).format(price);
}

export default function PricingPage() {
  const [plans, setPlans] = useState<PlanResponse[] | null>(null);
  const [usingFallback, setUsingFallback] = useState(false);
  const [loading, setLoading] = useState(true);
  // Faz 5: kayıtlı kullanıcıların CTA'sı panel içi Abonelik bölümüne yönlenir.
  const [loggedIn, setLoggedIn] = useState(false);

  useEffect(() => {
    setLoggedIn(Boolean(getToken()));
  }, []);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const data = await api<PlanResponse[]>("/api/plans", { auth: false });
        if (!cancelled) {
          setPlans(data);
          setUsingFallback(false);
        }
      } catch {
        if (!cancelled) {
          setPlans(fallbackPlans);
          setUsingFallback(true);
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <main className="min-h-screen bg-white">
      <header className="border-b border-navy-100">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
          <Link href="/" className="text-lg font-bold text-navy-900">
            MaliMüşavir<span className="text-navy-400">SaaS</span>
          </Link>
          <nav className="flex items-center gap-3">
            <Link href="/giris" className="btn-secondary">
              Giriş Yap
            </Link>
            <Link href="/kayit" className="btn-primary">
              Ücretsiz Başla
            </Link>
          </nav>
        </div>
      </header>

      <section className="mx-auto max-w-6xl px-6 py-16">
        <h1 className="text-center text-3xl font-bold text-navy-900">Fiyatlandırma</h1>
        <p className="mt-3 text-center text-navy-600">
          Ofisinizin büyüklüğüne göre paket seçin. Tüm paketler ücretsiz deneme ile başlar.
        </p>

        {usingFallback && (
          <div className="alert-info mx-auto mt-8 max-w-2xl text-center">
            Güncel fiyat bilgisi şu anda alınamıyor; aşağıdaki bilgiler tanıtım amaçlıdır.
          </div>
        )}

        {loading ? (
          <div className="mt-12 grid gap-6 md:grid-cols-3">
            {[1, 2, 3].map((i) => (
              <div key={i} className="card animate-pulse">
                <div className="h-6 w-1/2 rounded bg-navy-100" />
                <div className="mt-4 h-10 w-2/3 rounded bg-navy-100" />
                <div className="mt-6 space-y-3">
                  <div className="h-4 rounded bg-navy-50" />
                  <div className="h-4 rounded bg-navy-50" />
                  <div className="h-4 rounded bg-navy-50" />
                </div>
              </div>
            ))}
          </div>
        ) : (
          <div className="mt-12 grid gap-6 md:grid-cols-3">
            {(plans ?? []).map((plan) => (
              <div
                key={plan.id}
                className={`card flex flex-col ${
                  plan.code === "profesyonel" ? "border-2 border-navy-900 shadow-md" : ""
                }`}
              >
                {plan.code === "profesyonel" && (
                  <span className="mb-3 inline-block w-fit rounded-full bg-navy-900 px-3 py-1 text-xs font-semibold text-white">
                    En Popüler
                  </span>
                )}
                <h2 className="text-xl font-bold text-navy-900">{plan.name}</h2>
                <p className="mt-3 text-3xl font-extrabold text-navy-950">
                  ₺{formatPrice(plan.monthlyPrice)}
                  <span className="text-base font-medium text-navy-400"> / ay</span>
                </p>
                <ul className="mt-6 flex-1 space-y-2.5 text-sm text-navy-700">
                  <li>
                    {plan.unlimitedClients ? "Sınırsız mükellef" : `${plan.maxClients} mükellef`}
                  </li>
                  <li>{plan.maxUsers} kullanıcı</li>
                  <li>Aylık {formatPrice(plan.monthlyOcrQuota)} fiş OCR</li>
                  <li className={plan.eTebligat ? "" : "text-navy-300 line-through"}>
                    e-Tebligat takibi
                  </li>
                  <li>Luca uyumlu aktarım dosyası (CSV/Excel)</li>
                </ul>
                <Link
                  href={loggedIn ? "/panel/ayarlar" : "/kayit"}
                  className="btn-primary mt-8 w-full"
                >
                  {loggedIn ? "Paketi Seç" : "Ücretsiz Dene"}
                </Link>
              </div>
            ))}
          </div>
        )}
      </section>
    </main>
  );
}
