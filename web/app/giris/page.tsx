"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { FormEvent, useState } from "react";
import { api, AuthResponse, setToken } from "@/lib/api";

export default function LoginPage() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (!email.trim()) {
      setError("E-posta adresi zorunludur.");
      return;
    }
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim())) {
      setError("Geçerli bir e-posta adresi girin.");
      return;
    }
    if (!password) {
      setError("Şifre zorunludur.");
      return;
    }

    setSubmitting(true);
    try {
      const res = await api<AuthResponse>("/api/auth/login", {
        method: "POST",
        body: { email: email.trim(), password },
        auth: false,
      });
      setToken(res.token);
      router.push(res.role === "SuperAdmin" ? "/admin" : "/panel");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="flex min-h-screen items-center justify-center bg-navy-50 px-6">
      <div className="w-full max-w-md">
        <Link href="/" className="mb-8 block text-center text-xl font-bold text-navy-900">
          MaliMüşavir<span className="text-navy-400">SaaS</span>
        </Link>
        <div className="card">
          <h1 className="text-xl font-bold text-navy-900">Giriş Yap</h1>
          <p className="mt-1 text-sm text-navy-500">Hesabınıza erişmek için bilgilerinizi girin.</p>

          {error && <div className="alert-error mt-4">{error}</div>}

          <form onSubmit={handleSubmit} className="mt-6 space-y-4" noValidate>
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
                placeholder="••••••••"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
              />
            </div>
            <button type="submit" className="btn-primary w-full" disabled={submitting}>
              {submitting ? "Giriş yapılıyor..." : "Giriş Yap"}
            </button>
          </form>

          <p className="mt-6 text-center text-sm text-navy-500">
            Hesabınız yok mu?{" "}
            <Link href="/kayit" className="font-semibold text-navy-900 hover:underline">
              Ücretsiz kayıt olun
            </Link>
          </p>
        </div>
      </div>
    </main>
  );
}
