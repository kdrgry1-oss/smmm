/**
 * Liste tablolarında hesap/cari kodu gösterimi (Faturalar + Onay Kuyruğu ortak).
 * approved = true → kod kesinleşmiş (normal mono görünüm);
 * approved = false → kod motor ÖNERİSİ (soluk/italik + isteğe bağlı "öneri" etiketi).
 * value yoksa "—" gösterilir.
 */
export default function AccountCodeText({
  value,
  approved,
  showSuggestionTag = false,
}: {
  value: string | null;
  approved: boolean;
  showSuggestionTag?: boolean;
}) {
  if (!value) {
    return <span className="text-xs text-navy-300">—</span>;
  }

  if (approved) {
    return (
      <span className="font-mono text-xs text-navy-900" title="Onaylanmış kod">
        {value}
      </span>
    );
  }

  return (
    <span
      className="font-mono text-xs italic text-navy-400"
      title="Motor önerisi — henüz onaylanmadı"
    >
      {value}
      {showSuggestionTag && (
        <span className="ml-1 rounded bg-amber-50 px-1 py-px text-[10px] font-semibold not-italic text-amber-700">
          öneri
        </span>
      )}
    </span>
  );
}
