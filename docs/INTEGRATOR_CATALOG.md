# Entegrator Katalogu ve Teknik Sozlesme Kaydi

Son dogrulama: 19.08.2026

Bu dosya entegrasyon gelistirmesinin kaynak kaydidir. Bir saglayici ancak resmi
teknik sozlesmesi, hesap yetkisi ve hata davranisi incelendikten sonra aktif
cekime alinir. Kimlik bilgileri bu dosyada tutulmaz.

## Ortak kurallar

- Mükellef verisi, ilgili mükellefin saglayici hesabi veya kanuni yetkili
  kullanici ile okunur. API anahtari/parola AES ile sifrelenir; loglara,
  dokumantasyona ve API yanitina yazilmaz.
- e-Belge verisi ile GIB e-Tebligat ayni veri kaynagi degildir. Ozel
  entegratorun e-Fatura API yetkisi, Dijital Vergi Dairesindeki tebligatlara
  erisim yetkisi vermez.
- e-Tebligat icin kaynak GIB Dijital Vergi Dairesidir. VUK 107/A kapsamindaki
  erisim, mukellef veya ozel yetkili vekaletname ile yetkilendirilmis kisi
  uzerinden yapilmalidir. CAPTCHA veya ek kimlik dogrulama otomatik cozulmez.
- e-Defter imzalama/yukleme gibi temsil sonuc doguran islemler, ayri yetki ve
  muvafakat kontrolu olmadan otomatiklestirilmez.
- Her yeni adaptorde: sandbox testi, salt-okunur listeleme, idempotent
  kaydetme, denetlenebilir hata kaydi ve canli onay sirasi zorunludur.

## Aktif adaptörler

| Saglayici | Resmi sözlesme | Kimlik | Kod durumu | Okuma kapsami |
|---|---|---|---|---|
| Uyumsoft | `BasicIntegration` SOAP | Web servis kullanicisi + sifre | Aktif | Gelen e-Fatura; e-SMM icin ayri `VoucherIntegration` yolu |
| Dogan Donusum | `AuthenticationWS` + `EFaturaOIB` SOAP | Kullanici adi + sifre + oturum | Aktif | Gelen e-Fatura |
| Nilvera | REST/OAuth 2.0 | Scope'lu API anahtari | Aktif | Gelen e-Fatura |
| eLogo | PostBox SOAP | Kullanici adi + sifre | Aktif | Gelen e-Fatura |
| EDM | SOAP | Kullanici adi + sifre | Aktif | Gelen e-Fatura |
| Paraşüt | REST JSON:API | OAuth istemci ve kullanici kimligi | Aktif | Satis/satin alma faturalari |
| GIB Portal | Dahili GIB istemcisi | GIB kimligi veya yetkili musavir oturumu | Aktif | GIB e-Tebligat ve GIB belge ekrani |

## Dogan Donusum servis ailesi

Resmi geliştirici sayfasi, test servisleri icin su aileleri yayinlar:

- `AuthenticationWS`: Login, Logout, kullanici/yetki sorgulari.
- `EFaturaOIB`: e-Fatura gelen/giden kutusu ve uygulama yanitlari.
- `EIArchiveWS/EFaturaArchive`: e-Arsiv fatura ve arsiv raporlari.
- `EIrsaliyeWS/EIrsaliye`: e-Irsaliye ve yanitlari.
- `SmmWS`: e-Serbest Meslek Makbuzu.
- `ReconciliationWS`: e-Mutabakat.
- `ZReportWS`: Z Raporu servisi; test WSDL'i 19.08.2026 tarihinde 404, canli
  ucu 503 dondu. Canli implementasyon, Dogan tarafindan servis adresi ve hesap
  yetkisi teyit edildikten sonra yapilacak.

Yayinlanmis WSDL'lerde GIB e-Tebligat listeleme operasyonu bulunmadi. Bu nedenle
bu saglayicinin e-Belge kimligiyle tebligat cekimi varsayimla etkinlestirilmez.

## Uyumsoft servis ailesi

- `BasicIntegration`: Gelen/giden e-Fatura, XML/PDF, durum, taslak, e-Arsiv
  ve e-Irsaliye-fatura baglantili islemler.
- `VoucherIntegration`: e-SMM makbuz akislari.
- Kamuya acik `BasicIntegration` WSDL'inde e-Tebligat operasyonu YOKTUR.
  Bu saglayicinin e-belge kimligiyle tebligat cekimi yapilmaz.

## Sıradaki adaptörler

| Saglayici | Resmi kaynak | Beklenen kimlik | Ilk kapsam | Durum |
|---|---|---|---|---|
| QNB eSolutions | `qnbesolutions.com.tr/api-docs-tr-final.html` | SOAP Header veya Cookie Container | Gelen e-Fatura, e-Irsaliye, e-Defter | Sozlesme inceleniyor |
| NES | `apitest.nes.com.tr/einvoice/index.html` | Saglayicinin API anahtari/tokeni | e-Fatura, e-Arsiv, e-Irsaliye, e-SMM, e-MM | Sandbox sozlesmesi inceleniyor |
| Isnet | Saglayicidan resmi API dokumani ve sandbox gerekli | Saglayicinin verdigi kimlik | Once salt-okunur e-Fatura | Bekliyor |

## Kaynaklar

- GIB Elektronik Tebligat Tebligi: https://gib.gov.tr/mevzuat/kanun/434/teblig/7956
- GIB 509 sayili VUK Tebligi: https://cdn.gib.gov.tr/api/gibportal-file/file/getFile?objectKey=MEVZUAT_TEBLIGLER%2FUNIVERSAL%2F2026%2FMEVZUAT_TEBLIGLER_2026_VukTeb509_Guncel.pdf
- Dogan Donusum gelistirici sayfasi: https://www.doganedonusum.com/dev/
- Uyumsoft BasicIntegration WSDL: http://edonusumapi.uyum.com.tr/Services/BasicIntegration?wsdl
- Nilvera API: https://developer.nilvera.com/
- QNB eSolutions API dokumani: https://www.qnbesolutions.com.tr/api-docs-tr-final.html
- NES e-Fatura sandbox: https://apitest.nes.com.tr/einvoice/index.html
