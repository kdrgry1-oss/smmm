"use client";
import { useEffect, useState } from "react";
import { getToken, beginGibLogin } from "@/lib/api";
export default function TebligatDocument({ id }: { id: string }) {
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("Asıl belge yükleniyor…");
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    let objectUrl: string | null = null;
    setUrl(null); setError(""); setStatus("Asıl belge yükleniyor…");
    (async () => {
      try {
        const token = getToken();
        if (!token) throw new Error("Panel oturumunuz sona ermiş; yeniden giriş yapın.");
        const load = () => fetch(`/api-proxy/api/etebligat/${id}/document`, {
          headers: { Authorization: `Bearer ${token}` }, cache: "no-store", signal: controller.signal,
        });
        let res = await load();
        if (res.status === 409) {
          const failure = await res.clone().json().catch(() => ({}));
          if (failure.renewClientId && !controller.signal.aborted) {
            setStatus("GİB oturumu yenileniyor; işlem tamamlanınca belge otomatik açılacak…");
            await beginGibLogin(failure.renewClientId);
            if (controller.signal.aborted) return;
            setStatus("Asıl belge yeniden yükleniyor…");
            res = await load();
          }
        }
        if (!res.ok) {
          const data = await res.json().catch(() => ({}));
          throw new Error(data.message || (res.status === 401 ? "Panel oturumunuz sona ermiş; yeniden giriş yapın." : "Belge yüklenemedi."));
        }
        if (!(res.headers.get("content-type") || "").includes("application/pdf")) throw new Error("Sunucu asıl PDF yerine farklı bir yanıt döndürdü.");
        const blob = await res.blob();
        if (controller.signal.aborted) return;
        objectUrl = URL.createObjectURL(blob); setUrl(objectUrl);
      } catch (e) {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : "Belge yüklenemedi.");
      }
    })();
    return () => { controller.abort(); if(objectUrl) URL.revokeObjectURL(objectUrl); };
  }, [id, attempt]);
  return <section className="rounded-xl border border-navy-200 p-4" aria-label="Tebligat belgesi">
    <h3 className="font-bold">Tebligat İçeriği</h3>
    {error ? <div role="alert" className="mt-3 text-sm text-red-700">{error}<button className="ml-3 underline" onClick={() => setAttempt(x => x + 1)}>Tekrar dene</button></div>
      : !url ? <p role="status" className="mt-3 text-sm">{status}</p>
      : <><div className="my-3 flex gap-4 text-sm"><a href={url} target="_blank" rel="noopener noreferrer" className="underline">Yeni sekmede aç / yazdır</a><a href={url} download={`Tebligat_${id}.pdf`} className="underline">PDF indir</a></div><iframe src={url} title="Asıl e-Tebligat PDF" className="h-[540px] w-full border-0" /></>}
  </section>;
}
