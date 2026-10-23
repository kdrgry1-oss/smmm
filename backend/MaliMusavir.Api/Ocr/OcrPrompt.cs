namespace MaliMusavir.Api.Ocr;

/// <summary>
/// Vision-LLM fiş okuma prompt'unun TEK kaynağı — LlmVisionOcrProvider (Anthropic),
/// OpenAiVisionOcrProvider (OpenAI) ve GeminiVisionOcrProvider aynı Türkçe fiş uzmanı
/// prompt'unu ve dolayısıyla aynı JSON şemasını (LlmReceiptExtraction) kullanır.
/// Şema değişirse yalnız burası ve LlmReceiptExtraction güncellenir; sağlayıcılar
/// davranışsal olarak eş kalır. Belge türüne göre prompt For(kind) ile seçilir:
/// Fis → ReceiptExtraction, ZRaporu → ZReport.
/// </summary>
public static class OcrPrompt
{
    /// <summary>Belge türüne göre doğru prompt'u döndürür (sağlayıcılar bunu çağırır).</summary>
    public static string For(OcrDocumentKind kind)
        => kind == OcrDocumentKind.ZRaporu ? ZReport : ReceiptExtraction;

    /// <summary>
    /// Türkçe fiş uzmanı prompt'u. Kurallar: yalnız geçerli JSON; Türkçe sayı biçimi
    /// (1.234,56) çözümlenip JSON'a NOKTA ondalıklı sayı yazılır; KDV oranı yalnız 0/1/10/20;
    /// VKN 10 / TCKN 11 hane; emin olunmayan alan null + düşük güven — UYDURMA YOK.
    /// Çok oranlı KDV: fişteki KDV kırılım tablosu (KDV %1 / %10 / %20 satırları, "KDV MATRAH"
    /// blokları) vatBreakdown dizisi olarak SATIR SATIR istenir; iskonto/indirim satırları
    /// discountTotal'a yazılır ve net tutarlar İSKONTO SONRASI olmalıdır.
    /// "SADECE geçerli JSON döndür" kuralı OpenAI'nin response_format=json_object gereksinimiyle
    /// de uyumludur (json_object modu prompt'ta "JSON" kelimesinin geçmesini şart koşar).
    /// </summary>
    public const string ReceiptExtraction =
        "Sen Türk perakende fişleri, yazar kasa fişleri ve faturalar konusunda uzman bir belge okuma asistanısın. " +
        "Görseldeki belgeyi dikkatle oku ve alanları çıkar.\n\n" +
        "YANIT KURALLARI:\n" +
        "1. SADECE geçerli JSON döndür — başka hiçbir metin, açıklama veya kod bloğu işareti yazma.\n" +
        "2. Şema (alan adları birebir böyle):\n" +
        "{\"vendorName\": string|null, \"vendorTaxId\": string|null, \"documentNumber\": string|null, " +
        "\"receiptDate\": string|null, \"totalAmount\": number|null, \"vatRate\": number|null, " +
        "\"vatAmount\": number|null, \"netAmount\": number|null, \"discountTotal\": number|null, " +
        "\"vatBreakdown\": [{\"rate\": number|null, \"net\": number|null, \"vat\": number|null, \"gross\": number|null}], " +
        "\"currency\": string|null, " +
        "\"plateNumber\": string|null, " +
        "\"items\": [{\"name\": string, \"amount\": number|null, \"vatRate\": number|null}], " +
        "\"documentType\": \"perakende_fis\"|\"fatura\"|\"yazar_kasa\"|\"diger\", " +
        "\"confidence\": {\"vendorName\": number, \"vendorTaxId\": number, \"documentNumber\": number, " +
        "\"receiptDate\": number, \"totalAmount\": number, \"vatRate\": number, \"vatAmount\": number, \"netAmount\": number}, " +
        "\"warnings\": [string]}\n" +
        "3. Türk sayı biçimi: fişlerde ondalık ayraç VİRGÜL, binlik ayraç NOKTA olabilir (ör. \"1.234,56\"). " +
        "Bunu çözümle ve JSON'a HER ZAMAN nokta ondalıklı düz sayı yaz (1234.56).\n" +
        "4. Tarihi ISO 8601 biçiminde yaz (\"2026-07-28\"). Türk fişlerinde tarih GG.AA.YYYY veya GG/AA/YYYY biçimindedir.\n" +
        "5. KDV oranı Türkiye'de YALNIZ 0, 1, 10 veya 20 olabilir (KDV/TOPKDV satırlarına bak). " +
        "Fişte %8 veya %18 görüyorsan belge eski tarihli olabilir — yine okuduğunu yaz ama warnings'e not düş.\n" +
        "6. vendorTaxId: VKN 10 hane, TCKN 11 hanedir (fişte \"VKN\", \"V.D.\", \"VD\", vergi dairesi satırında). " +
        "Hane sayısı uymuyorsa null bırak.\n" +
        "7. documentNumber: fiş no / belge no / Z no / sıra no ne varsa onu yaz (ör. \"FİŞ NO: 0042\" → \"0042\").\n" +
        "8. totalAmount KDV DAHİL genel toplamdır (\"TOPLAM\", \"TOP\", \"GENEL TOPLAM\"); " +
        "vatAmount TOPLAM KDV tutarı (\"KDV\", \"TOPKDV\"); netAmount TOPLAM matrah (toplam - KDV).\n" +
        "9. ÇOK ORANLI KDV — ÇOK ÖNEMLİ: fişte birden fazla KDV oranı olabilir (%0, %1, %10, %20). " +
        "Fişin KDV kırılım tablosunu (\"KDV %1\", \"KDV %10\", \"KDV %20\", \"TOPKDV\" satırları, " +
        "\"KDV MATRAH\" blokları, satır sonlarındaki *1/*8/*10/*20 vergi işaretleri) SATIR SATIR oku ve " +
        "vatBreakdown dizisine HER ORAN İÇİN AYRI bir kalem yaz: rate = KDV oranı (%), " +
        "net = o oranın matrahı (KDV hariç), vat = o oranın KDV tutarı, gross = o oranın KDV dahil iskonto sonrası toplamı. Basılı olmayan net/vat alanlarını null bırakabilirsin; gross ve açıkça okunan oranla yazılım hesaplar. %0 açıkça basılıysa dahil et; oran okunamadığında %0 yazma. " +
        "Tek oranlı fişte dizi tek elemanlıdır. Kırılım hiç okunamıyorsa diziyi boş bırak ([]).\n" +
        "10. İSKONTO/İNDİRİM: fişte \"İSKONTO\", \"İNDİRİM\", \"KAMPANYA\" gibi indirim satırları varsa " +
        "toplam indirim tutarını discountTotal alanına POZİTİF sayı olarak yaz; yoksa null bırak. " +
        "vatBreakdown içindeki net tutarlar ve netAmount HER ZAMAN İSKONTO SONRASI (fiilen ödenen matrah) olmalıdır — " +
        "iskontoyu matraha ekleme veya son tutardan ikinci kez düşme. Genel indirimin oranlara dağılımı belgede belirli değilse keyfi dağıtma; warnings alanına yaz.\n" +
        "11. TUTARLILIK: toplam = tüm kırılım netlerinin toplamı + tüm kırılım KDV'lerinin toplamı olmalıdır " +
        "(totalAmount = Σnet + Σvat). netAmount = Σnet, vatAmount = Σvat yaz. " +
        "Birden fazla oran varsa vatRate=null yaz; tek oranı tüm fişe uygulama. Basılı toplamları kırılıma uydurmak için değiştirme; uyuşmazlığı warnings alanına yaz.\n" +
        "12. items: okuyabildiğin ürünlerin adları, amount=iskonto sonrası KDV dahil SATIR toplamı, vatRate=üründe açıkça basılı KDV yüzdesi. Birim fiyatı miktarla çarpmadan satır toplamı sanma. Toplam/KDV/ödeme/para üstü satırlarını ürün olarak ekleme. Vergi grup harflerini veya yıldız yanındaki fiyatı oran sanma; açıklaması yoksa vatRate=null. İndirim dağılımı belirsizse miktarları uydurma, warnings alanına yaz.\n" +
        "13. EMİN OLMADIĞIN ALANI NULL BIRAK ve o alanın confidence değerini düşür (0-1 arası). " +
        "ASLA tahmin uydurma; bulanık/kesik alan için warnings'e Türkçe not ekle.\n" +
        "14. Akaryakıt fişlerinde araç plakası basılıdır (ör. 34FPC630, 06 ABC 123); " +
        "görüyorsan plateNumber alanına boşluksuz büyük harfle yaz (\"06 ABC 123\" → \"06ABC123\"), " +
        "görmüyorsan null bırak — UYDURMA.";

    /// <summary>
    /// Z RAPORU (yazar kasa günlük kapanış raporu) prompt'u. Fiş prompt'uyla aynı temel
    /// kurallar (yalnız JSON, Türk sayı biçimi, ISO tarih, uydurma yasak); alan seti Z raporuna
    /// özeldir: ÖKC seri no, Z no, rapor tarihi, oran bazlı KDV kırılımı, günlük toplam satış,
    /// toplam KDV ve nakit/kart kırılımı.
    /// </summary>
    public const string ZReport =
        "Sen Türk yazar kasa (ÖKC) Z raporları konusunda uzman bir belge okuma asistanısın. " +
        "Görseldeki belge bir yazar kasanın GÜNLÜK KAPANIŞ (Z) RAPORUDUR. Dikkatle oku ve alanları çıkar.\n\n" +
        "YANIT KURALLARI:\n" +
        "1. SADECE geçerli JSON döndür — başka hiçbir metin, açıklama veya kod bloğu işareti yazma.\n" +
        "2. Şema (alan adları birebir böyle):\n" +
        "{\"okcSerialNumber\": string|null, \"zNumber\": string|null, \"reportDate\": string|null, " +
        "\"vatBreakdown\": [{\"rate\": number|null, \"net\": number|null, \"vat\": number|null, \"gross\": number|null}], " +
        "\"totalSales\": number|null, \"totalVat\": number|null, " +
        "\"cashTotal\": number|null, \"cardTotal\": number|null, " +
        "\"confidence\": {\"okcSerialNumber\": number, \"zNumber\": number, \"reportDate\": number, " +
        "\"totalSales\": number, \"totalVat\": number}, " +
        "\"warnings\": [string]}\n" +
        "3. Türk sayı biçimi: ondalık ayraç VİRGÜL, binlik ayraç NOKTA olabilir (ör. \"1.234,56\"). " +
        "Bunu çözümle ve JSON'a HER ZAMAN nokta ondalıklı düz sayı yaz (1234.56).\n" +
        "4. reportDate: raporun tarihi, ISO 8601 (\"2026-07-28\"). Z raporlarında tarih GG.AA.YYYY biçimindedir.\n" +
        "5. okcSerialNumber: yazar kasanın sicil/seri numarası (\"SİCİL NO\", \"SERİ NO\", \"CİHAZ NO\" — " +
        "genelde 2 harf + rakamlar, ör. \"AB12345678\"). zNumber: Z raporu sayacı (\"Z NO\", \"Z SAYAÇ NO\").\n" +
        "6. KDV KIRILIMI — ÇOK ÖNEMLİ: Z raporunda KDV oran tablosu vardır (\"KDV ORANI %1/%10/%20\", " +
        "\"KÜMÜLATİF KDV\", \"TOPLAM KDV\" satırları). Bu tabloyu SATIR SATIR oku ve vatBreakdown dizisine " +
        "HER ORAN İÇİN AYRI kalem yaz: rate = oran (%), net = o oranın matrahı (KDV hariç satış), " +
        "vat = o oranın KDV tutarı. Türkiye'de oran yalnız 0/1/10/20 olabilir.\n" +
        "7. totalSales: günlük KDV DAHİL toplam satış (\"TOPLAM\", \"GÜNLÜK CİRO\", \"TOP SATIŞ\"); " +
        "totalVat: günlük toplam KDV. Tutarlılık: totalSales = Σnet + Σvat, totalVat = Σvat olmalıdır.\n" +
        "8. NAKİT/KART: raporda ödeme tipi kırılımı varsa (\"NAKİT\", \"KREDİ KARTI\", \"KART\") " +
        "cashTotal ve cardTotal alanlarına yaz (KDV dahil tutarlar); kırılım yoksa null bırak.\n" +
        "9. EMİN OLMADIĞIN ALANI NULL BIRAK ve o alanın confidence değerini düşür (0-1 arası). " +
        "ASLA tahmin uydurma; bulanık/kesik alan için warnings'e Türkçe not ekle.";
}
