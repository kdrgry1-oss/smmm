"use client";

/**
 * Hesap/cari kodu autocomplete girişi.
 *
 * - Yazarken 250 ms debounce ile öneri listesi açılır (kod + soluk hesap adı).
 *   Rakam/nokta ile başlayan giriş KOD ÖN EKİ araması ("700." → 700. ile başlayanlar),
 *   diğerleri hesap ADI araması ("kira" → "KİRA GİDERİ") yapar — mantık sunucudadır.
 * - Ok tuşları + Enter ile seçim, Esc kapatır, dışarı tıklayınca kapanır.
 * - Serbest metin girişi DAİMA mümkündür: öneri seçmek zorunlu değildir, değer olduğu
 *   gibi onChange ile yukarı akar (doğrulama çağıranın/backend'in işidir).
 * - prefixHint (ör. "320."): alan BOŞKEN odaklanınca bu ön ekle başlayan öneriler gelir
 *   (satıcı cari kodları pratikte hep 320 ile başlar); ayrıca dolu değer bu ön ekle
 *   başlamıyorsa engellemeden küçük sarı uyarı satırı gösterilir.
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { AccountSuggestion, searchAccountCodes } from "@/lib/api";

export default function AccountCodeAutocomplete({
  clientId,
  value,
  onChange,
  placeholder,
  prefixHint,
  inputClassName = "input w-28 !px-2 !py-1 font-mono text-xs",
  disabled = false,
  autoFocus = false,
  onEnter,
}: {
  /** Mükellef kimliği; boş/undefined ise arama tenant genelinde yapılır. */
  clientId?: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  /** Ör. "320." — alan boşken odaklanınca bu ön ekin önerileri listelenir + ön ek uyarısı. */
  prefixHint?: string;
  inputClassName?: string;
  disabled?: boolean;
  autoFocus?: boolean;
  /** Liste kapalıyken Enter'a basılınca çağrılır (yerinde düzenlemede "kaydet" kısayolu). */
  onEnter?: () => void;
}) {
  const [suggestions, setSuggestions] = useState<AccountSuggestion[]>([]);
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);

  const rootRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const requestSeqRef = useRef(0);
  // Öneri seçildiğinde value değişimi yeni arama tetiklememeli.
  const skipNextSearchRef = useRef(false);

  const runSearch = useCallback(
    (term: string) => {
      const seq = ++requestSeqRef.current;
      searchAccountCodes(term, { clientId: clientId || undefined, limit: 15 })
        .then((items) => {
          if (seq !== requestSeqRef.current) return; // eski yanıt — yok say
          setSuggestions(items);
          setOpen(items.length > 0);
          setActiveIndex(-1);
        })
        .catch(() => {
          // Öneri servisi erişilemezse sessiz kalınır — serbest metin girişi zaten mümkün.
          if (seq === requestSeqRef.current) {
            setSuggestions([]);
            setOpen(false);
          }
        });
    },
    [clientId]
  );

  // Yazım debounce'u (250 ms).
  useEffect(() => {
    if (skipNextSearchRef.current) {
      skipNextSearchRef.current = false;
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);

    const term = value.trim();
    if (!term) return; // boş alan: liste yalnız odak anında (prefixHint) açılır

    debounceRef.current = setTimeout(() => runSearch(term), 250);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [value, runSearch]);

  // Dışarı tıklayınca kapan.
  useEffect(() => {
    function handleMouseDown(e: MouseEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpen(false);
        setActiveIndex(-1);
      }
    }
    document.addEventListener("mousedown", handleMouseDown);
    return () => document.removeEventListener("mousedown", handleMouseDown);
  }, []);

  function select(suggestion: AccountSuggestion) {
    skipNextSearchRef.current = true;
    onChange(suggestion.code);
    setOpen(false);
    setActiveIndex(-1);
  }

  function handleFocus() {
    const term = value.trim();
    if (term) {
      runSearch(term);
    } else if (prefixHint) {
      // Boş cari alanı: odaklanınca "320." ile başlayan öneriler gelir.
      runSearch(prefixHint);
    }
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Escape") {
      if (open) {
        e.stopPropagation();
        setOpen(false);
        setActiveIndex(-1);
      }
      return;
    }
    if (e.key === "ArrowDown") {
      if (!open && suggestions.length > 0) {
        setOpen(true);
        return;
      }
      if (open) {
        e.preventDefault();
        setActiveIndex((i) => (i + 1 >= suggestions.length ? 0 : i + 1));
      }
      return;
    }
    if (e.key === "ArrowUp") {
      if (open) {
        e.preventDefault();
        setActiveIndex((i) => (i - 1 < 0 ? suggestions.length - 1 : i - 1));
      }
      return;
    }
    if (e.key === "Enter") {
      if (open && activeIndex >= 0 && activeIndex < suggestions.length) {
        e.preventDefault();
        select(suggestions[activeIndex]);
      } else if (onEnter) {
        e.preventDefault();
        onEnter();
      }
    }
  }

  // Ön ek uyarısı: "320." ipucu varken 320 ile başlamayan dolu değer (engelleme yok).
  const prefixDigits = prefixHint?.replace(/\.+$/, "") ?? "";
  const showPrefixWarning =
    prefixDigits.length > 0 && value.trim().length > 0 && !value.trim().startsWith(prefixDigits);

  return (
    <div ref={rootRef} className="relative">
      <input
        type="text"
        className={inputClassName}
        placeholder={placeholder}
        value={value}
        disabled={disabled}
        autoFocus={autoFocus}
        autoComplete="off"
        spellCheck={false}
        role="combobox"
        aria-expanded={open}
        aria-autocomplete="list"
        onChange={(e) => onChange(e.target.value)}
        onFocus={handleFocus}
        onKeyDown={handleKeyDown}
      />

      {open && suggestions.length > 0 && (
        <ul
          role="listbox"
          className="absolute left-0 top-full z-50 mt-1 max-h-56 w-64 overflow-y-auto rounded-lg border border-navy-200 bg-white py-1 shadow-lg"
        >
          {suggestions.map((s, index) => (
            <li key={`${s.code}-${index}`} role="option" aria-selected={index === activeIndex}>
              <button
                type="button"
                className={`flex w-full items-baseline gap-2 px-3 py-1.5 text-left text-xs ${
                  index === activeIndex ? "bg-navy-50" : "hover:bg-navy-50/60"
                }`}
                // onMouseDown: input blur'undan ÖNCE çalışır — seçim kaybolmaz.
                onMouseDown={(e) => {
                  e.preventDefault();
                  select(s);
                }}
                onMouseEnter={() => setActiveIndex(index)}
              >
                <span className="shrink-0 font-mono font-semibold text-navy-900">{s.code}</span>
                {s.name && <span className="truncate text-navy-400">{s.name}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}

      {showPrefixWarning && (
        <p className="mt-1 text-[10px] font-medium text-amber-700">
          Satıcı cari kodları genelde {prefixDigits} ile başlar.
        </p>
      )}
    </div>
  );
}
