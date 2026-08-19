using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Beyanname;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using MaliMusavir.Api.Ocr;
using MaliMusavir.Api.Payments;
using MaliMusavir.Api.Services;
using MaliMusavir.Api.Tebligat;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------- Servisler ----------

builder.Services.AddHttpContextAccessor();

// ICurrentUser: normalde HTTP isteğinden çözülür (CurrentUser). Faz 11 (E6) kuyruk işçisi
// HTTP'siz scope'ta çalıştığından, worker WorkerCurrentUser.ActAsTenant ile kimlik verir;
// fabrika, worker kimliği ETKİNSE onu, değilse CurrentUser'ı döndürür (mevcut davranış aynen korunur).
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<WorkerCurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp =>
{
    var worker = sp.GetRequiredService<WorkerCurrentUser>();
    return worker.IsActive ? worker : sp.GetRequiredService<CurrentUser>();
});
builder.Services.AddScoped<TokenService>();

// ---------- Faz 1: e-Fatura senkron modülü ----------
// API anahtarı şifreleme (AES-256, Security:SecretKey).
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();

// Entegratör adaptörleri: IEnumerable<IEInvoiceProvider> olarak enjekte edilir;
// EInvoiceSyncService, entegrasyonun Provider enum'una göre doğru adaptörü seçer.
builder.Services.AddHttpClient(NilveraProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30); // her dış çağrıda zaman aşımı (blueprint §0.5)
});
builder.Services.AddHttpClient(UyumsoftProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30); // her dış çağrıda zaman aşımı (blueprint §0.5)
});
builder.Services.AddHttpClient(DoganDonusumProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30); // her dış çağrıda zaman aşımı (blueprint §0.5)
});
builder.Services.AddHttpClient(ElogoProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30); // her dış çağrıda zaman aşımı (blueprint §0.5)
});
builder.Services.AddHttpClient(EdmProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30); // her dış çağrıda zaman aşımı (blueprint §0.5)
});
builder.Services.AddHttpClient(ParasutProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30); // her dış çağrıda zaman aşımı (blueprint §0.5)
});
builder.Services.AddScoped<IEInvoiceProvider, MockProvider>();
builder.Services.AddScoped<IEInvoiceProvider, NilveraProvider>();
builder.Services.AddScoped<IEInvoiceProvider, UyumsoftProvider>(); // SOAP adaptörü (faz10)
builder.Services.AddScoped<IEInvoiceProvider, DoganDonusumProvider>(); // Cybersoft connector SOAP adaptörü
builder.Services.AddScoped<IEInvoiceProvider, ElogoProvider>(); // eLogo PostBox SOAP adaptörü
builder.Services.AddScoped<IEInvoiceProvider, EdmProvider>(); // EDM Bilişim SOAP adaptörü (Cybersoft türevi)
builder.Services.AddScoped<IEInvoiceProvider, ParasutProvider>(); // Paraşüt REST (JSON:API) adaptörü

// Faz 15 — GİB Portal Robotu: entegratör KULLANMAYAN mükellefler için. Adaptör, iç ağdaki
// gib-robot konteynerini (Node + Playwright) çağırır; taban adres GIB_ROBOT_URL, paylaşılan
// sır ROBOT_TOKEN ortam değişkenlerinden okunur (compose'da api ve robot AYNI değeri alır).
// Zaman aşımı robotun kendi 90 sn bütçesinden BÜYÜK tutulur (yanıt kaybolmasın).
builder.Services.AddHttpClient(GibRobotClient.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddScoped<GibRobotClient>();
builder.Services.AddScoped<IEInvoiceProvider, GibPortalProvider>();

// GİB e-Arşiv Portalı (earsivportal.efatura.gov.tr) — entegratörsüz mükellefler için
// captcha'sız JSON API yolu. Robotun YERİNE GEÇMEZ, tamamlar: e-Arşiv belgeleri buradan,
// ticari e-Fatura ve e-Tebligat robot üzerinden gelir. Fatura kesme de bu istemciyi kullanır.
builder.Services.AddHttpClient(EArsivPortalClient.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60); // detay çekimi liste + N belge çağırır
});
builder.Services.AddScoped<EArsivPortalClient>();
builder.Services.AddScoped<EArsivPortalProvider>();
builder.Services.AddScoped<IEInvoiceProvider>(sp => sp.GetRequiredService<EArsivPortalProvider>());
builder.Services.AddScoped<EArsivInvoiceIssuer>();

builder.Services.AddScoped<EInvoiceSyncService>();

// ---------- Banka hesap hareketleri ----------
// Birincil yol dosya içe aktarmadır (MT940/CSV/Excel) — her banka çalışır, sözleşme gerekmez.
// Banka kurumsal API'si geldiğinde aynı ParsedBankLine sözleşmesini üreten adaptör eklenir.
builder.Services.AddScoped<BankStatementService>();

// ---------- Faz 2: Otomatik Hesap/Cari Kod Motoru ----------
// Katman 3 (AI sınıflandırıcı) genişleme noktası: bu fazda Noop — kademe
// hafıza → satıcı varsayılanı → kural → garantili doldurma ile her satırı yine de kodlar.
builder.Services.AddScoped<IAccountClassifier, NoopClassifier>();
builder.Services.AddScoped<CodingEngine>();
builder.Services.AddScoped<LucaExcelExporter>();

// ---------- Faz 3: Fiş OCR modülü ----------
// Dosyalar webroot dışında "storage/" altında tutulur (Storage:Root); S3 uyumlu depoya
// geçiş IFileStorage'ın yeni implementasyonuyla yapılır. OCR sağlayıcısı adapter arkasında
// ve "Ocr:Provider" yapılandırmasıyla seçilir:
//  - "Mock" (varsayılan): anahtar gerektirmez, deterministik demo fişler üretir.
//  - "LlmVision": Anthropic Messages API ile vision-LLM okuma (Ocr:Anthropic:ApiKey gerekir;
//    anahtar boşsa sağlayıcı çağrıda açıklayıcı Türkçe hata döner, uygulama yine açılır).
//  - "OpenAi": OpenAI Chat Completions (gpt-4o-mini) ile vision okuma (Ocr:OpenAi:ApiKey gerekir;
//    aynı prompt/şema/doğrulama — yalnız görüntü kabul eder, PDF için LlmVision/Gemini kullanın).
//  - "Gemini": Google Gemini generateContent (gemini-2.5-flash, kademeli fallback: gemini-2.5-pro)
//    ile vision okuma (Ocr:Gemini:ApiKey gerekir; görüntü VE PDF kabul eder).
// EnvVar override .NET config'te hazırdır: Ocr__Provider, Ocr__Anthropic__ApiKey,
// Ocr__OpenAi__ApiKey, Ocr__Gemini__ApiKey.
// Faz 12 — Fiş görseli küçültme (revize: "önce OCR, sonra sıkıştırma"): fiş önce temp/'e
// ORİJİNAL yazılır, OCR bu orijinalden çalışır; okuma bitince görüntü normalize edilip
// (uzun kenar ≤ ImageProcessing:MaxLongEdge, JPEG ImageProcessing:JpegQuality, EXIF/konum
// silinir — KVKK) kalıcı konuma sıkıştırılmış kaydedilir, temp silinir. HEIC decode
// edilemez → olduğu gibi saklanır (mobil katman zaten JPEG üretir).
// ImageProcessing:Enabled=false yalnızca DEPOLAMA sıkıştırmasını kapatır (kalıcı kopya
// orijinal kalır); OCR her durumda orijinal görüntüyü görür.
builder.Services.AddSingleton<IImageNormalizer, ImageSharpNormalizer>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
// Faz 16 (A2): istek başına zaman aşımı 90 → 60 sn (tek deneme; takılan çağrı kuyruğu bloklamasın).
builder.Services.AddHttpClient(LlmVisionOcrProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60); // vision çıkarımı uzun sürebilir (blueprint §0.5: her dış çağrıda zaman aşımı)
});
builder.Services.AddHttpClient(OpenAiVisionOcrProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60); // vision çıkarımı uzun sürebilir (blueprint §0.5: her dış çağrıda zaman aşımı)
});
builder.Services.AddHttpClient(GeminiVisionOcrProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60); // vision çıkarımı uzun sürebilir (blueprint §0.5: her dış çağrıda zaman aşımı)
});
builder.Services.AddScoped<OcrValidator>();

var ocrProviderName = builder.Configuration["Ocr:Provider"] ?? "Mock";
if (string.Equals(ocrProviderName, "LlmVision", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IReceiptOcrProvider, LlmVisionOcrProvider>();
else if (string.Equals(ocrProviderName, "OpenAi", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IReceiptOcrProvider, OpenAiVisionOcrProvider>();
else if (string.Equals(ocrProviderName, "Gemini", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IReceiptOcrProvider, GeminiVisionOcrProvider>();
else if (string.Equals(ocrProviderName, "Mock", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IReceiptOcrProvider, MockOcrProvider>();
else
    throw new InvalidOperationException(
        $"Geçersiz Ocr:Provider değeri: \"{ocrProviderName}\". Geçerli değerler: \"Mock\", \"LlmVision\", \"OpenAi\" veya \"Gemini\".");

builder.Services.AddScoped<ReceiptService>();

// Faz 16 (B5) — İLERİYE DÖNÜK Z raporu sağlayıcı arayüzü: TSM merkezi / POS-ÖKC merchant
// API'leri (Ödeal/Paycell/Token) ticari anlaşma gerektirdiğinden bugün BİRİNCİL yol OCR'dır;
// anlaşma gelince gerçek adaptör bu kayıtla değiştirilir. UI'da GÖSTERİLMEZ.
builder.Services.AddScoped<IZReportProvider, MockZReportProvider>();
builder.Services.AddHttpClient(PayTrReportProvider.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://www.paytr.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IPosReportProvider, PayTrReportProvider>();
builder.Services.AddScoped<PosReportSyncService>();

// Çoklu fiş yüklemesi: 20 dosya × 10 MB + form payı (varsayılan 128 MB sınırı yükseltilir).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 220 * 1024 * 1024;
});

// ---------- Faz 4: e-Tebligat tek ekran ----------
// Gerçek UETS/PTT web servisi, PTT kurumsal başvurusu sonrası IETebligatProvider'ı
// uygulayan tek sınıfla eklenecek; şimdilik MockUetsProvider.
builder.Services.AddScoped<IETebligatProvider, MockUetsProvider>();
// Faz 15: GİB Portalı entegrasyonu olan mükelleflerde tebligatlar gib-robot üzerinden
// GİB Dijital Vergi Dairesi'nden çekilir. Seçim MÜKELLEF BAZINDADIR (ETebligatSyncService).
builder.Services.AddScoped<GibPortalTebligatProvider>();
builder.Services.AddScoped<IETebligatProvider>(sp => sp.GetRequiredService<GibPortalTebligatProvider>());
builder.Services.AddScoped<ETebligatSyncService>();

// ---------- Faz 5: Abonelik / Ödeme ----------
// Ödeme sağlayıcısı adapter arkasında; Payments:Provider yapılandırmasıyla seçilir.
// iyzico API anahtarları iletilince "Iyzico" yapılır; anahtar yokken Mock uçtan uca çalışır.
builder.Services.AddHttpClient(IyzicoPaymentProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30); // her dış çağrıda zaman aşımı (blueprint §0.5)
});

var paymentProviderName = builder.Configuration["Payments:Provider"] ?? "Mock";
if (string.Equals(paymentProviderName, "Iyzico", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IPaymentProvider, IyzicoPaymentProvider>();
else
    builder.Services.AddScoped<IPaymentProvider, MockPaymentProvider>();

builder.Services.AddScoped<SubscriptionService>();

// ---------- Faz 5: e-Beyanname (GİB web servisi) ----------
// Gerçek GİB e-Beyanname servisi, SMMM kimliği + GİB token'ı iletilince IEBeyannameProvider'ı
// uygulayan tek sınıfla eklenecek; şimdilik MockGibProvider (30 sn işleme + %10 şema hatası).
builder.Services.AddScoped<IEBeyannameProvider, MockGibProvider>();
builder.Services.AddScoped<DeclarationService>();

// ---------- Faz 7: E-serisi hızlı kazanım modülleri ----------
// E1 — Mükellef belge portalı: PIN kaba kuvvet koruması bellek içi tek sayaçtır (singleton).
builder.Services.AddSingleton<PortalRateLimiter>();
// E2 — Vergi takvimi: dönem üretimi + beyanname senkronu.
builder.Services.AddScoped<TaxCalendarService>();
// E4 — Parametre merkezi: cache'li okuma (static ConcurrentDictionary; admin yazınca invalidate).
// LucaExcelExporter ve CodingEngine değerleri buradan okur — tablo boşsa eski sabitler geçerlidir.
builder.Services.AddScoped<ParameterService>();

// ---------- Faz 8: KDV/kapanış hızlandırma paketi (B2, B3, B5, B10) ----------
// B3 — KKEG analizörü: kodlama motoru + fiş hattı içinde çalışır (parametre merkezinden okur).
builder.Services.AddScoped<KkegAnalyzer>();
// B5 — Mükerrer/eksik fatura mutabakatı (bulgular anlık hesaplanır; "Yoksay" DB'de kalıcıdır).
builder.Services.AddScoped<ReconciliationService>();
// B10 — Dönem sonu kontrol listesi ("Beyana hazır" yüzdesi; B5 analizini de çağırır).
builder.Services.AddScoped<PeriodCloseService>();

// ---------- Faz 11: E5-E8 (e-Defter ön-kontrol, retry kuyruğu, tahsilat, mizan) ----------
// E8 — Mizan: dengeli fiş satırları LucaExcelExporter çekirdeğinden gelir (davranış birebir).
builder.Services.AddScoped<TrialBalanceService>();
// E5 — e-Defter berat ön-kontrolü (PeriodClose + TrialBalance + ClientCertificate'i birleştirir).
// Gerçek XBRL-GL üretimi/imza bu fazda YOK — dürüst kapsam notu serviste.
builder.Services.AddScoped<EDefterPrecheckService>();
// E6 — Gönderim kuyruğu: servis (enqueue/iptal/şimdi dene/işleme) + arka plan işçisi.
// TEK INSTANCE varsayımı: worker satır kilidi kullanmaz (bkz. OutboundJobWorker notu).
// Faz 16 (A2): uyandırma sinyali — yeni iş kuyruğa alınınca worker beklemeden tetiklenir
// (fiş yükleme → OCR başlangıcı arasındaki 15 sn'ye varan gecikme kalkar). Singleton: HTTP
// scope'ları ve worker aynı örneği paylaşır.
builder.Services.AddSingleton<OutboundJobSignal>();
builder.Services.AddScoped<OutboundJobService>();
builder.Services.AddHostedService<OutboundJobWorker>();
// Faz 15 — GİB portal robotu günlük tetikleyicisi: her gün 03:00'te (sunucu saati)
// GibPortal entegrasyonu olan TÜM mükellefler için son 7 günlük "GibPortalSync" işi
// kuyruğa alınır. Mükellef kimliği bir kez girilir; sonrası tamamen otomatiktir.
builder.Services.AddHostedService<DailyPortalSyncWorker>();
// E7 — SMMM ücret/tahsilat takibi.
builder.Services.AddScoped<FeeService>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Enum'lar JSON'da string olarak taşınır (ör. "Trial", "Active").
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// Model doğrulama hatalarında tutarlı { message } gövdesi (ilk hata mesajı, Türkçe attribute mesajları).
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var firstError = context.ModelState
            .Where(kv => kv.Value?.Errors.Count > 0)
            .SelectMany(kv => kv.Value!.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? "Girdiğiniz bilgiler geçersiz. Lütfen kontrol edip tekrar deneyin.";

        return new BadRequestObjectResult(new { message = firstError });
    };
});

// JWT kimlik doğrulama
var jwtKey = builder.Configuration["Jwt:Key"]
             ?? throw new InvalidOperationException("Jwt:Key yapılandırması eksik.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Faz 7 (E1): Portal token'ları (claim portal=true) kısa ömürlü ve DAR kapsamlıdır.
    // Varsayılan [Authorize] policy'si portal claim'ini REDDEDER → portal token'ı normal
    // API'lere (mükellef listesi, faturalar, ayarlar...) hiçbir koşulda erişemez.
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => !context.User.HasClaim("portal", "true"))
        .Build();

    // Portal uçları yalnızca bu policy ile açılır (PortalController'daki korumalı aksiyonlar).
    options.AddPolicy("PortalOnly", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim("portal", "true"));
});

// CORS (appsettings'ten)
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:3000" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// Swagger + Bearer şeması
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "MaliMusavir SaaS API",
        Version = "v1",
        Description = "Mali müşavirler için çok kiracılı SaaS platformu — Faz 0 API"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT token'ınızı girin. Örnek: eyJhbGciOi..."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ---------- Global hata yakalama middleware ----------
// Beklenmeyen tüm hatalarda tutarlı, Türkçe { message } gövdesi döner.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalExceptionHandler");
        logger.LogError(ex, "İşlenmeyen hata: {Path}", context.Request.Path);

        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";

            var body = JsonSerializer.Serialize(new
            {
                status = 500,
                title = "Sunucu Hatası",
                message = "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin."
            });

            await context.Response.WriteAsync(body);
        }
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Default");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ---------- Açılışta veritabanı seed ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");

    try
    {
        await DbSeeder.SeedAsync(db, config, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "Veritabanı seed işlemi başarısız. PostgreSQL'in çalıştığından emin olun (docker compose up -d).");
        throw;
    }
}

app.Run();
