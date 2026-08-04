import Link from "next/link";

const features = [
  {
    title: "e-Fatura Senkron",
    desc: "Entegratörünüzdeki gelen/giden faturalar otomatik olarak platforma çekilir; elle indirme derdi biter.",
  },
  {
    title: "Fiş OCR",
    desc: "Fiş ve fatura görsellerini yükleyin; tutar, KDV ve satıcı bilgileri otomatik okunsun.",
  },
  {
    title: "Otomatik Cari / Hesap Kodu",
    desc: "Kod motoru; VKN ve unvana göre cari ile muhasebe hesap kodlarını kurallara göre otomatik atar.",
  },
  {
    title: "Luca'ya Aktarım",
    desc: "Hazırlanan yevmiye kayıtlarını Luca'ya aktarılabilir formatta tek tıkla dışa aktarın.",
  },
  {
    title: "e-Tebligat Tek Ekran",
    desc: "Tüm mükelleflerinizin e-Tebligat kutularını tek ekrandan takip edin, hiçbir tebligatı kaçırmayın.",
  },
  {
    title: "Beyanname Otomasyonu",
    desc: "KDV ve dönem kapanış süreçlerinde kontrol listeleri ve otomatik hazırlıkla zaman kazanın.",
  },
];

export default function LandingPage() {
  return (
    <main className="min-h-screen bg-white">
      {/* Üst bar */}
      <header className="border-b border-navy-100">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
          <Link href="/" className="text-lg font-bold text-navy-900">
            MaliMüşavir<span className="text-navy-400">SaaS</span>
          </Link>
          <nav className="flex items-center gap-3">
            <Link href="/fiyatlandirma" className="text-sm font-medium text-navy-700 hover:text-navy-900">
              Fiyatlandırma
            </Link>
            <Link href="/giris" className="btn-secondary">
              Giriş Yap
            </Link>
            <Link href="/kayit" className="btn-primary">
              Ücretsiz Başla
            </Link>
          </nav>
        </div>
      </header>

      {/* Hero */}
      <section className="bg-navy-950 text-white">
        <div className="mx-auto max-w-6xl px-6 py-24 text-center">
          <p className="mb-4 text-sm font-semibold uppercase tracking-widest text-navy-300">
            SMMM ofisleri için
          </p>
          <h1 className="mx-auto max-w-3xl text-4xl font-extrabold leading-tight sm:text-5xl">
            Mali müşavirin dijital asistanı
          </h1>
          <p className="mx-auto mt-6 max-w-2xl text-lg text-navy-200">
            Fatura toplama, fiş girişi, e-Tebligat takibi ve beyanname hazırlığındaki tekrar eden
            işleri otomatikleştirin. Ekibiniz danışmanlığa odaklansın.
          </p>
          <div className="mt-10 flex items-center justify-center gap-4">
            <Link href="/kayit" className="btn-primary bg-white !text-navy-950 hover:bg-navy-100">
              Ücretsiz Deneyin
            </Link>
            <Link
              href="/fiyatlandirma"
              className="btn-secondary border-navy-700 bg-transparent !text-white hover:bg-navy-900"
            >
              Paketleri İncele
            </Link>
          </div>
        </div>
      </section>

      {/* Özellikler */}
      <section className="mx-auto max-w-6xl px-6 py-20">
        <h2 className="text-center text-3xl font-bold text-navy-900">
          Ofisinizin tüm rutin işleri tek platformda
        </h2>
        <div className="mt-12 grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
          {features.map((f) => (
            <div key={f.title} className="card">
              <h3 className="text-lg font-semibold text-navy-900">{f.title}</h3>
              <p className="mt-2 text-sm leading-relaxed text-navy-600">{f.desc}</p>
            </div>
          ))}
        </div>
      </section>

      {/* CTA */}
      <section className="border-t border-navy-100 bg-navy-50">
        <div className="mx-auto max-w-6xl px-6 py-16 text-center">
          <h2 className="text-2xl font-bold text-navy-900">Bugün ücretsiz deneyin</h2>
          <p className="mt-3 text-navy-600">
            Kurulum gerekmez. Kayıt olun, mükelleflerinizi ekleyin, dakikalar içinde kullanmaya başlayın.
          </p>
          <Link href="/kayit" className="btn-primary mt-8">
            Hemen Kayıt Ol
          </Link>
        </div>
      </section>

      <footer className="border-t border-navy-100 py-8 text-center text-sm text-navy-400">
        © {new Date().getFullYear()} MaliMüşavirSaaS — Faz 0 önizleme
      </footer>
    </main>
  );
}
