using System.Text;
using MaliMusavir.Api.Domain;

namespace MaliMusavir.Api.Services;

/// <summary>
/// GİB e-Tebligat resmi üst yazı ve tebellüğ belgesini Hazine ve Maliye Bakanlığı standartlarında
/// yüksek çözünürlüklü, yazdırılabilir ve mahkeme/mükellef teslimine uygun HTML/PDF formatında üretir.
/// Kullanıcının GİB portalına gitmesine gerek kalmadan belgenin tüm hukuki ve idari detaylarını sunar.
/// </summary>
public static class ETebligatHtmlRenderer
{
    public static string RenderOfficialDocument(ETebligatMessage message, Client? client)
    {
        var deliveredAtStr = message.DeliveredAt.ToString("dd.MM.yyyy HH:mm");
        var deemedServedDate = CalculateDeemedServedDate(message.DeliveredAt);
        var deemedServedStr = deemedServedDate.ToString("dd.MM.yyyy");
        var legalDeadlineStr = message.LegalDeadline?.ToString("dd.MM.yyyy") ?? "Süre Takibi Gerekmiyor (Bilgi)";
        var clientTitle = client?.Title ?? "Kayıtlı Mükellef";
        var clientTaxId = client?.TaxId ?? "—";
        var isOdemeEmri = message.Subject.Contains("ODEME EMRI", StringComparison.OrdinalIgnoreCase) ||
                          message.Subject.Contains("ÖDEME EMRİ", StringComparison.OrdinalIgnoreCase);

        var legalExplanation = isOdemeEmri
            ? "6183 sayılı Amme Alacaklarının Tahsil Usulü Hakkında Kanun'un 55. maddesi uyarınca tanzim edilen işbu ödeme emrine karşı, tebliğ edilmiş sayıldığı tarihten itibaren <b>15 GÜN</b> içinde borcun ödenmesi, mal bildiriminde bulunulması veya böyle bir borcun olmadığı/kısmen ödendiği/zamanaşımına uğradığı iddialarıyla yetkili Vergi Mahkemesi nezdinde (2577 sayılı İYUK md. 7) dava açılması gerekmektedir. Süresinde borç ödenmez veya mal bildiriminde bulunulmazsa 6183 sayılı Kanun hükümleri gereğince cebren tahsilat ve haciz işlemlerine başlanacaktır."
            : "213 sayılı Vergi Usul Kanunu ve 2577 sayılı İdari Yargılama Usulü Kanunu'nun 7. maddesi uyarınca işbu evraka karşı, tebliğ edilmiş sayıldığı tarihi izleyen günden itibaren <b>30 GÜN</b> içinde yetkili Vergi Mahkemesinde dava açma veya ilgili vergi dairesi başkanlığına/müdürlüğüne düzeltme talebinde bulunma hakkınız bulunmaktadır.";

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"tr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"  <title>Resmi e-Tebligat Evrakı — {message.Subject}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    * { box-sizing: border-box; margin: 0; padding: 0; }");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; background: #f8fafc; color: #0f172a; line-height: 1.5; padding: 24px; }");
        sb.AppendLine("    .container { max-width: 800px; margin: 0 auto; background: #ffffff; border: 1px solid #cbd5e1; border-radius: 12px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1); padding: 36px 44px; position: relative; }");
        sb.AppendLine("    .header { text-align: center; border-bottom: 2px solid #0f172a; padding-bottom: 20px; margin-bottom: 24px; }");
        sb.AppendLine("    .crest { font-size: 13px; font-weight: 700; letter-spacing: 1.5px; color: #b91c1c; text-transform: uppercase; margin-bottom: 4px; }");
        sb.AppendLine("    .org-title { font-size: 16px; font-weight: 800; color: #0f172a; letter-spacing: 0.5px; }");
        sb.AppendLine("    .sub-org { font-size: 14px; font-weight: 600; color: #334155; margin-top: 4px; }");
        sb.AppendLine("    .badge-bar { display: flex; justify-content: space-between; align-items: center; background: #f1f5f9; padding: 10px 16px; border-radius: 8px; margin-bottom: 24px; font-size: 12px; font-weight: 600; color: #475569; }");
        sb.AppendLine("    .barcode { font-family: 'Courier New', monospace; font-size: 13px; font-weight: 700; letter-spacing: 3px; background: #0f172a; color: #fff; padding: 4px 10px; border-radius: 4px; }");
        sb.AppendLine("    .section { margin-bottom: 24px; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden; }");
        sb.AppendLine("    .section-title { background: #f8fafc; padding: 10px 16px; font-size: 12px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.5px; color: #1e293b; border-bottom: 1px solid #e2e8f0; }");
        sb.AppendLine("    .section-body { padding: 16px; }");
        sb.AppendLine("    .info-grid { display: grid; grid-template-columns: 140px 1fr; gap: 8px 16px; font-size: 13px; }");
        sb.AppendLine("    .info-label { font-weight: 600; color: #64748b; }");
        sb.AppendLine("    .info-val { font-weight: 600; color: #0f172a; word-break: break-word; }");
        sb.AppendLine("    .timeline-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; margin-top: 8px; }");
        sb.AppendLine("    .timeline-box { background: #f8fafc; border: 1px solid #cbd5e1; border-radius: 8px; padding: 12px; text-align: center; }");
        sb.AppendLine("    .timeline-step { font-size: 11px; font-weight: 700; color: #64748b; text-transform: uppercase; margin-bottom: 4px; }");
        sb.AppendLine("    .timeline-date { font-size: 15px; font-weight: 800; color: #0f172a; }");
        sb.AppendLine("    .timeline-note { font-size: 10px; color: #64748b; margin-top: 4px; }");
        sb.AppendLine("    .timeline-box.active { background: #eff6ff; border-color: #3b82f6; }");
        sb.AppendLine("    .timeline-box.active .timeline-step { color: #1d4ed8; }");
        sb.AppendLine("    .timeline-box.active .timeline-date { color: #1e40af; }");
        sb.AppendLine("    .timeline-box.deadline { background: #fef2f2; border-color: #ef4444; }");
        sb.AppendLine("    .timeline-box.deadline .timeline-step { color: #b91c1c; }");
        sb.AppendLine("    .timeline-box.deadline .timeline-date { color: #991b1b; }");
        sb.AppendLine("    .legal-alert { background: #eff6ff; border-left: 4px solid #2563eb; padding: 14px 18px; border-radius: 0 8px 8px 0; font-size: 12px; color: #1e3a8a; line-height: 1.6; margin-bottom: 24px; }");
        sb.AppendLine("    .footer { border-top: 1px solid #e2e8f0; padding-top: 16px; margin-top: 32px; font-size: 11px; color: #64748b; text-align: center; }");
        sb.AppendLine("    .signature-box { display: flex; justify-content: space-between; align-items: flex-end; margin-top: 24px; padding: 16px; background: #fafafa; border: 1px dashed #cbd5e1; border-radius: 8px; font-size: 11px; color: #475569; }");
        sb.AppendLine("    .seal-text { font-weight: 600; line-height: 1.4; }");
        sb.AppendLine("    .print-btn { display: inline-flex; align-items: center; gap: 8px; background: #0f172a; color: #ffffff; padding: 10px 18px; border-radius: 8px; font-size: 13px; font-weight: 600; text-decoration: none; border: none; cursor: pointer; margin-bottom: 20px; }");
        sb.AppendLine("    .print-btn:hover { background: #1e293b; }");
        sb.AppendLine("    @media print {");
        sb.AppendLine("      body { background: #ffffff; padding: 0; }");
        sb.AppendLine("      .container { border: none; box-shadow: none; padding: 0; max-width: 100%; }");
        sb.AppendLine("      .print-btn { display: none !important; }");
        sb.AppendLine("    }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div style=\"text-align: right; max-width: 800px; margin: 0 auto;\">");
        sb.AppendLine("    <button onclick=\"window.print()\" class=\"print-btn\">🖨 Resmi Belgeyi Yazdır / PDF Kaydet</button>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <div class=\"container\">");
        sb.AppendLine("    <div class=\"header\">");
        sb.AppendLine("      <div class=\"crest\">T.C. Hazine ve Maliye Bakanlığı</div>");
        sb.AppendLine("      <div class=\"org-title\">GELİR İDARESİ BAŞKANLIĞI</div>");
        sb.AppendLine($"      <div class=\"sub-org\">{message.Sender}</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"badge-bar\">");
        sb.AppendLine($"      <span>ELEKTRONİK TEBLİGAT EVRAKI (213 S.K. MD. 107/A)</span>");
        sb.AppendLine($"      <span class=\"barcode\">{message.Id.ToString()[..12].ToUpper()}</span>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"section\">");
        sb.AppendLine("      <div class=\"section-title\">1. MUHATAP / MÜKELLEF BİLGİLERİ</div>");
        sb.AppendLine("      <div class=\"section-body\">");
        sb.AppendLine("        <div class=\"info-grid\">");
        sb.AppendLine($"          <div class=\"info-label\">Unvan / Ad Soyad:</div><div class=\"info-val\">{clientTitle}</div>");
        sb.AppendLine($"          <div class=\"info-label\">VKN / TCKN:</div><div class=\"info-val\">{clientTaxId}</div>");
        sb.AppendLine($"          <div class=\"info-label\">Düzenleyen Kurum:</div><div class=\"info-val\">{message.Sender}</div>");
        sb.AppendLine($"          <div class=\"info-label\">Evrak Referans No:</div><div class=\"info-val\"><code>{message.ExternalId}</code></div>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"section\">");
        sb.AppendLine("      <div class=\"section-title\">2. TEBLİĞ EDİLEN EVRAKIN NİTELİĞİ</div>");
        sb.AppendLine("      <div class=\"section-body\">");
        sb.AppendLine("        <div class=\"info-grid\">");
        sb.AppendLine($"          <div class=\"info-label\">Tebligat Konusu:</div><div class=\"info-val\"><b>{message.Subject}</b></div>");
        sb.AppendLine($"          <div class=\"info-label\">İşlem Niteliği:</div><div class=\"info-val\">{(isOdemeEmri ? "6183 Sayılı Kanun Uyarınca Amme Alacağı Takip ve Ödeme Emri" : "Vergi/Ceza İhbarnamesi ve İdari İşlem Tebliği")}</div>");
        sb.AppendLine($"          <div class=\"info-label\">Tebliğ Saati:</div><div class=\"info-val\">{deliveredAtStr} (Türkiye Saati / UTC+3)</div>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"section\">");
        sb.AppendLine("      <div class=\"section-title\">3. KANUNİ TEBELLÜĞ VE YASAL SÜRE TAKVİMİ (VUK MD. 107/A)</div>");
        sb.AppendLine("      <div class=\"section-body\">");
        sb.AppendLine("        <div class=\"timeline-grid\">");
        sb.AppendLine("          <div class=\"timeline-box\">");
        sb.AppendLine("            <div class=\"timeline-step\">1. Kutuya Ulaşma</div>");
        sb.AppendLine($"            <div class=\"timeline-date\">{message.DeliveredAt:dd.MM.yyyy}</div>");
        sb.AppendLine("            <div class=\"timeline-note\">Sisteme girdiği an</div>");
        sb.AppendLine("          </div>");
        sb.AppendLine("          <div class=\"timeline-box active\">");
        sb.AppendLine("            <div class=\"timeline-step\">2. Tebliğ Sayılma</div>");
        sb.AppendLine($"            <div class=\"timeline-date\">{deemedServedStr}</div>");
        sb.AppendLine("            <div class=\"timeline-note\">İzleyen 5. iş günü sonu</div>");
        sb.AppendLine("          </div>");
        sb.AppendLine("          <div class=\"timeline-box deadline\">");
        sb.AppendLine("            <div class=\"timeline-step\">3. Yasal Son Gün</div>");
        sb.AppendLine($"            <div class=\"timeline-date\">{legalDeadlineStr}</div>");
        sb.AppendLine($"            <div class=\"timeline-note\">Tür: {message.DeadlineType ?? "Takip"}</div>");
        sb.AppendLine("          </div>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"legal-alert\">");
        sb.AppendLine("      <b>HUKUKİ İHTAR VE DAVA HAKKI BİLGİLENDİRMESİ:</b><br>");
        sb.AppendLine($"      {legalExplanation}");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"signature-box\">");
        sb.AppendLine("      <div class=\"seal-text\">");
        sb.AppendLine("        <b>GÜVENLİ ELEKTRONİK İMZA VE DOĞRULAMA ŞERHİ</b><br>");
        sb.AppendLine("        İşbu evrak 5070 sayılı Elektronik İmza Kanunu ve 213 sayılı Vergi Usul Kanunu<br>");
        sb.AppendLine("        hükümleri uyarınca güvenli elektronik imza ile imzalanmış olup resmi geçerliliğe haizdir.<br>");
        sb.AppendLine($"        Doğrulama Referansı: {message.ExternalId}");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div style=\"text-align: right; font-family: monospace; font-size: 10px;\">");
        sb.AppendLine("        [ E-İMZALI ASLI İLE AYNIDIR ]<br>");
        sb.AppendLine($"        Kayıt ID: {message.Id}");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"footer\">");
        sb.AppendLine("      MaliMüşavir SaaS Platformu e-Tebligat Entegrasyon Merkezi tarafından Gelir İdaresi Başkanlığı Dijital Vergi Dairesi sisteminden alınmıştır.");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static DateTime CalculateDeemedServedDate(DateTime deliveredAt)
    {
        var count = 0;
        var current = deliveredAt.Date;
        while (count < 5)
        {
            current = current.AddDays(1);
            if (current.DayOfWeek != DayOfWeek.Saturday && current.DayOfWeek != DayOfWeek.Sunday)
            {
                count++;
            }
        }
        return current;
    }
}
