"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { FormEvent, useState } from "react";
import { api, AuthResponse, setToken } from "@/lib/api";

export default function RegisterPage() {
  const router = useRouter();
  const [firmName, setFirmName] = useState("");
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  function validate(): string | null {
    if (!firmName.trim()) return "Firma adı zorunludur.";
    if (!fullName.trim()) return "Ad soyad zorunludur.";
    if (!email.trim()) return "E-posta adresi zorunludur.";
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim())) return "Geçerli bir e-posta adresi girin.";
    if (!password) return "Şifre zorunludur.";
    if (password.length < 8) return "Şifre en az 8 karakter olmalıdır.";
    return null;
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    const validationError = validate();
    if (validationError) {
      setError(validationError);
      return;
    }

    setSubmitting(true);
    try {
      const res = await api<AuthResponse>("/api/auth/register", {
        method: "POST",
        body: {
          firmName: firmName.trim(),
          fullName: fullName.trim(),
          email: email.trim(),
          password,
        },
        auth: false,
      });
      setToken(res.token);
      router.push("/panel");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="flex min-h-screen items-center justify-center bg-navy-50 px-6 py-12">
      <div className="w-full max-w-md">
        <Link href="/" className="mb-8 block text-center text-xl font-bold text-navy-900">
          MaliMüşavir<span className="text-navy-400">SaaS</span>
        </Link>
        <div className="card">
          <h1 className="text-xl font-bold text-navy-900">Ücretsiz Kayıt Ol</h1>
          <p className="mt-1 text-sm text-navy-500">
            Ofisiniz için hesap oluşturun. Deneme süreniz hemen başlar.
          </p>

          {error && <div className="alert-error mt-4">{error}</div>}

          <form onSubmit={handleSubmit} className="mt-6 space-y-4" noValidate>
            <div>
              <label htmlFor="firmName" className="label">
                Firma / Ofis Adı
              </label>
              <input
                id="firmName"
                type="text"
                className="input"
                placeholder="Örn: Yılmaz Mali Müşavirlik"
                value={firmName}
                onChange={(e) => setFirmName(e.target.value)}
              />
            </div>
            <div>
              <label htmlFor="fullName" className="label">
                Ad Soyad
              </label>
              <input
                id="fullName"
                type="text"
                className="input"
                placeholder="Adınız ve soyadınız"
                value={fullName}
                onChange={(e) => setFullName(e.target.value)}
                autoComplete="name"
              />
            </div>
            <div>
              <label htmlFor="email" className="label">
                E-posta
              </label>
              <input
                id="email"
                type="email"
                className="input"
                placeholder="ornek@ofisiniz.com"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                autoComplete="email"
              />
            </div>
            <div>
              <label htmlFor="password" className="label">
                Şifre
              </label>
              <input
                id="password"
                type="password"
                className="input"
                placeholder="En az 8 karakter"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="new-password"
              />
            </div>
            <button type="submit" className="btn-primary w-full" disabled={submitting}>
              {submitting ? "Hesap oluşturuluyor..." : "Hesap Oluştur"}
            </button>
          </form>

          <p className="mt-6 text-center text-sm text-navy-500">
            Zaten hesabınız var mı?{" "}
            <Link href="/giris" className="font-semibold text-navy-900 hover:underline">
              Giriş yapın
            </Link>
          </p>
        </div>
      </div>
    </main>
  );
}
