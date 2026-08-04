import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "MaliMüşavir SaaS — Mali müşavirin dijital asistanı",
  description:
    "SMMM ofisleri için e-Fatura senkronizasyonu, fiş OCR, otomatik hesap kodu, Luca aktarımı, e-Tebligat ve beyanname otomasyonu.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="tr">
      <body>{children}</body>
    </html>
  );
}
