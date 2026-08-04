"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { ClientResponse } from "@/lib/api";

/**
 * Aranabilir çoklu mükellef seçici: yazınca isim/VKN filtreler, her mükellef checkbox ile
 * seçilir; "Tümünü seç" ve "Temizle" kısayolları vardır. HİÇBİR ŞEY SEÇİLMEMİŞSE bu
 * "TÜM mükellefler" anlamına gelir (rozet "Tüm mükellefler" gösterir) — toplu senkron /
 * kodlama / export uçları boş listeyi "tümü" olarak yorumlar.
 */
export default function ClientMultiSelect({
  clients,
  loading = false,
  disabled = false,
  selectedIds,
  onChange,
}: {
  clients: ClientResponse[];
  loading?: boolean;
  disabled?: boolean;
  /** Seçili mükellef Id'leri; boş dizi = tüm mükellefler. */
  selectedIds: string[];
  onChange: (ids: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState("");
  const rootRef = useRef<HTMLDivElement | null>(null);

  const selectedSet = useMemo(() => new Set(selectedIds), [selectedIds]);

  // Dışarı tıklanınca kapan (Escape ile de kapanır).
  useEffect(() => {
    if (!open) return;
    function onDocClick(e: MouseEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDocClick);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDocClick);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const filtered = useMemo(() => {
    const term = search.trim().toLocaleLowerCase("tr-TR");
    if (!term) return clients;
    return clients.filter(
      (c) =>
        c.title.toLocaleLowerCase("tr-TR").includes(term) || c.taxId.includes(term)
    );
  }, [clients, search]);

  function toggle(id: string) {
    const next = new Set(selectedSet);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    onChange(Array.from(next));
  }

  /** "Tümünü seç": görünen (filtrelenmiş) mükelleflerin tamamını seçime ekler. */
  function selectAllVisible() {
    const next = new Set(selectedSet);
    for (const c of filtered) next.add(c.id);
    onChange(Array.from(next));
  }

  function clearAll() {
    onChange([]);
  }

  const summaryLabel = loading
    ? "Yükleniyor..."
    : selectedIds.length === 0
      ? "Tüm mükellefler"
      : selectedIds.length === 1
        ? clients.find((c) => c.id === selectedIds[0])?.title ?? "1 mükellef seçili"
        : `${selectedIds.length} mükellef seçili`;

  return (
    <div ref={rootRef} className="relative">
      <button
        type="button"
        className="input flex min-w-[240px] items-center justify-between gap-2 text-left"
        onClick={() => setOpen((o) => !o)}
        disabled={disabled || loading}
        aria-haspopup="listbox"
        aria-expanded={open}
      >
        <span
          className={`truncate ${
            selectedIds.length === 0 ? "text-navy-500" : "font-medium text-navy-900"
          }`}
        >
          {summaryLabel}
        </span>
        <span className="text-xs text-navy-400">▾</span>
      </button>

      {/* Seçim rozeti: kaç mükellef seçili / tümü */}
      {!loading && selectedIds.length > 0 && (
        <span className="absolute -top-2 right-2 rounded-full bg-navy-700 px-2 py-0.5 text-[10px] font-semibold text-white">
          {selectedIds.length}
        </span>
      )}

      {open && (
        <div className="absolute left-0 top-full z-40 mt-1 w-[320px] rounded-xl border border-navy-200 bg-white shadow-lg">
          <div className="border-b border-navy-100 p-2">
            <input
              type="search"
              className="input !py-1.5 text-sm"
              placeholder="Mükellef ara (unvan veya VKN)..."
              value={search}
              autoFocus
              onChange={(e) => setSearch(e.target.value)}
            />
            <div className="mt-2 flex items-center justify-between text-xs">
              <button
                type="button"
                className="font-semibold text-navy-600 hover:text-navy-800"
                onClick={selectAllVisible}
              >
                Tümünü seç
              </button>
              <button
                type="button"
                className="font-semibold text-navy-400 hover:text-navy-600"
                onClick={clearAll}
              >
                Temizle
              </button>
            </div>
          </div>

          <ul className="max-h-64 overflow-y-auto py-1" role="listbox" aria-multiselectable>
            {filtered.length === 0 ? (
              <li className="px-3 py-3 text-sm text-navy-400">Eşleşen mükellef yok.</li>
            ) : (
              filtered.map((c) => (
                <li key={c.id}>
                  <label className="flex cursor-pointer items-center gap-2 px-3 py-1.5 text-sm hover:bg-navy-50">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-navy-300"
                      checked={selectedSet.has(c.id)}
                      onChange={() => toggle(c.id)}
                    />
                    <span className="flex-1 truncate text-navy-900">{c.title}</span>
                    <span className="font-mono text-[10px] text-navy-400">{c.taxId}</span>
                  </label>
                </li>
              ))
            )}
          </ul>

          <div className="border-t border-navy-100 px-3 py-2 text-xs text-navy-500">
            {selectedIds.length === 0
              ? "Seçim yok — işlemler TÜM mükellefler için yapılır."
              : `${selectedIds.length} mükellef seçili.`}
          </div>
        </div>
      )}
    </div>
  );
}
