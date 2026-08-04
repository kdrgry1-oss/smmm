/**
 * Tarayıcı tarafı fiş görseli küçültme (Faz 12): yüklemeden ÖNCE canvas ile uzun kenar
 * 1600 px'e indirilir ve JPEG kalite 0.75 ile yeniden kodlanır (sunucudaki ImageNormalizer
 * ile aynı parametreler — sunucu yine de son güvencedir). Böylece yükleme süresi kısalır,
 * bant genişliği ve OCR maliyeti düşer.
 *
 * Kurallar:
 * - PDF ve desteklenmeyen türler (HEIC dahil — tarayıcı decode edemez) OLDUĞU GİBİ geçer.
 * - Küçük görsel BÜYÜTÜLMEZ; sonuç orijinalden büyükse orijinal korunur.
 * - HERHANGİ bir hata durumunda orijinal dosya döner — küçültme yüklemeyi ASLA engellemez.
 */

const MAX_LONG_EDGE = 1600;
const JPEG_QUALITY = 0.75;

/** Tarayıcının canvas'a güvenle çizebildiği türler (HEIC/PDF vs. passthrough). */
const RESIZABLE_TYPES = ["image/jpeg", "image/png", "image/webp"];

/** Dosya boyutunu Türkçe biçimde gösterir (ör. "245 KB", "1,8 MB"). */
export function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 1 }).format(bytes / (1024 * 1024))} MB`;
}

/** Uzantıyı .jpg yapar (JPEG'e çevrilen dosyanın adı içerikle tutarlı kalsın). */
function toJpegName(name: string): string {
  const dot = name.lastIndexOf(".");
  return `${dot > 0 ? name.slice(0, dot) : name}.jpg`;
}

/** createImageBitmap yoksa (eski Safari) <img> ile decode eder. */
function loadImageElement(file: File): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      resolve(img);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error("Görsel çözümlenemedi."));
    };
    img.src = url;
  });
}

/** Kaynağı hedef boyutta JPEG blob'a çizer (OffscreenCanvas varsa onu, yoksa <canvas> kullanır). */
async function drawToJpegBlob(
  source: ImageBitmap | HTMLImageElement,
  width: number,
  height: number
): Promise<Blob> {
  if (typeof OffscreenCanvas !== "undefined") {
    const canvas = new OffscreenCanvas(width, height);
    const ctx = canvas.getContext("2d");
    if (!ctx) throw new Error("Canvas bağlamı alınamadı.");
    ctx.fillStyle = "#ffffff"; // PNG şeffaflığı JPEG'de beyaz zemine düşer
    ctx.fillRect(0, 0, width, height);
    ctx.drawImage(source, 0, 0, width, height);
    return canvas.convertToBlob({ type: "image/jpeg", quality: JPEG_QUALITY });
  }

  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("Canvas bağlamı alınamadı.");
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(0, 0, width, height);
  ctx.drawImage(source, 0, 0, width, height);
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(
      (blob) => (blob ? resolve(blob) : reject(new Error("JPEG üretilemedi."))),
      "image/jpeg",
      JPEG_QUALITY
    );
  });
}

/**
 * Tek görseli yükleme öncesi küçültür. Görsel değilse, küçültme kazanç sağlamıyorsa
 * veya herhangi bir adım hata verirse ORİJİNAL dosya döner (sessiz passthrough).
 */
export async function resizeImageForUpload(file: File): Promise<File> {
  if (!RESIZABLE_TYPES.includes(file.type)) return file;

  try {
    let bitmap: ImageBitmap | null = null;
    let imgEl: HTMLImageElement | null = null;

    if (typeof createImageBitmap === "function") {
      bitmap = await createImageBitmap(file);
    } else {
      imgEl = await loadImageElement(file);
    }

    const source = (bitmap ?? imgEl)!;
    const width = bitmap ? bitmap.width : imgEl!.naturalWidth;
    const height = bitmap ? bitmap.height : imgEl!.naturalHeight;
    if (!width || !height) return file;

    // En-boy oranı korunur; küçük görsel büyütülmez (scale en fazla 1).
    const scale = Math.min(1, MAX_LONG_EDGE / Math.max(width, height));
    const targetWidth = Math.max(1, Math.round(width * scale));
    const targetHeight = Math.max(1, Math.round(height * scale));

    const blob = await drawToJpegBlob(source, targetWidth, targetHeight);
    bitmap?.close();

    // Yeniden kodlama kazanç sağlamadıysa (zaten küçük/sıkışık dosya) orijinal korunur.
    if (blob.size >= file.size) return file;

    return new File([blob], toJpegName(file.name), { type: "image/jpeg" });
  } catch {
    // Küçültme asla yüklemeyi engellemez: hata durumunda orijinal dosya yüklenir,
    // sunucu tarafı normalizasyon (ImageNormalizer) son güvence olarak devrededir.
    return file;
  }
}

/** Çoklu dosya için sıralı küçültme (bellek baskısını sınırlamak için paralel değil). */
export async function resizeImagesForUpload(files: File[]): Promise<File[]> {
  const result: File[] = [];
  for (const file of files) {
    result.push(await resizeImageForUpload(file));
  }
  return result;
}
