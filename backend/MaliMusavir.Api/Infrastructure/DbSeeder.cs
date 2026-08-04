using MaliMusavir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Uygulama açılışında veritabanını oluşturur ve başlangıç verilerini (planlar + süper admin) ekler.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, IConfiguration configuration, ILogger logger)
    {
        // Faz 0'da migration yok; şema doğrudan modelden oluşturulur.
        await db.Database.EnsureCreatedAsync();

        await SeedPlansAsync(db, logger);
        await SeedSuperAdminAsync(db, configuration, logger);
        await SeedTaxParametersAsync(db, logger);
        await SeedWithholdingRulesAsync(db, logger);
    }

    /// <summary>
    /// Faz 8 — B2: GİB kısmi KDV tevkifat kodları (GLOBAL tablo). Yalnızca eksik kodlar eklenir;
    /// mevcut kayıtlar ASLA ezilmez (süper admin düzeltmeleri korunur).
    /// Oran kaynakları: KDV Genel Uygulama Tebliği kısmi tevkifat bölümü (35 Seri No'lu
    /// tebliğ sonrası oranlar). // TODO-VERIFY: TÜM oranları GİB güncel tebliğinden ve
    /// e-fatura UBL tevkifat kod listesinden doğrula (yıl içinde değişebilir).
    /// </summary>
    private static async Task SeedWithholdingRulesAsync(AppDbContext db, ILogger logger)
    {
        var rules = new List<WithholdingRule>
        {
            new()
            {
                Code = "601",
                Description = "Yapım işleri ile bu işlerle birlikte ifa edilen mühendislik-mimarlık ve etüt-proje hizmetleri",
                Fraction = "4/10",
                KeywordPattern = "yapım işi;inşaat;hafriyat;taahhüt işi;kaba inşaat"
            },
            new()
            {
                Code = "602",
                Description = "Etüt, plan-proje, danışmanlık, denetim ve benzeri hizmetler",
                Fraction = "9/10",
                KeywordPattern = "danışmanlık;müşavirlik;etüt;plan proje;denetim hizmeti"
            },
            new()
            {
                Code = "603",
                Description = "Makine, teçhizat, demirbaş ve taşıtlara ait tadil, bakım ve onarım hizmetleri",
                Fraction = "7/10",
                KeywordPattern = "bakım onarım;tadilat;makine bakım;araç bakım;servis bakım"
            },
            new()
            {
                Code = "604",
                Description = "Yemek servis hizmeti",
                Fraction = "5/10",
                KeywordPattern = "yemek servis;catering;tabldot;yemek hizmeti"
            },
            new()
            {
                Code = "605",
                Description = "Organizasyon hizmeti",
                Fraction = "5/10",
                KeywordPattern = "organizasyon;fuar organizasyon;kongre organizasyon;etkinlik organizasyon"
            },
            new()
            {
                Code = "606",
                // TODO-VERIFY: 606 kodunun kapsamı (işgücü temini) ve 9/10 oranı GİB güncel listesinden doğrulanmalı.
                Description = "İşgücü temin hizmetleri (özel güvenlik dahil) — GİB güncel tebliğden doğrulayın",
                Fraction = "9/10",
                KeywordPattern = "işgücü;personel temin;güvenlik hizmeti;bordrolama;geçici personel"
            },
            new()
            {
                Code = "612",
                // TODO-VERIFY: temizlik hizmeti tevkifat oranı (9/10) ve kod eşlemesi doğrulanmalı.
                Description = "Temizlik hizmeti — GİB güncel tebliğden doğrulayın",
                Fraction = "9/10",
                KeywordPattern = "temizlik hizmeti;temizlik;ilaçlama;haşere"
            },
            new()
            {
                Code = "613",
                // TODO-VERIFY: çevre ve bahçe bakım hizmetleri oranı (9/10) doğrulanmalı.
                Description = "Çevre ve bahçe bakım hizmetleri — GİB güncel tebliğden doğrulayın",
                Fraction = "9/10",
                KeywordPattern = "bahçe bakım;çevre bakım;peyzaj"
            },
            new()
            {
                Code = "615",
                // TODO-VERIFY: baskı-basım hizmetleri oranı (7/10) doğrulanmalı.
                Description = "Her türlü baskı ve basım hizmetleri — GİB güncel tebliğden doğrulayın",
                Fraction = "7/10",
                KeywordPattern = "baskı;basım;matbaa"
            },
            new()
            {
                Code = "625",
                // TODO-VERIFY: kara yolu yük taşımacılığı 2/10 oranı ve kod numarası doğrulanmalı.
                Description = "Kara yolu yük taşımacılığı hizmeti — GİB güncel tebliğden doğrulayın",
                Fraction = "2/10",
                KeywordPattern = "yük taşıma;kara yolu taşıma;karayolu taşımacılık;lojistik taşıma"
            },
        };

        var existingCodes = await db.WithholdingRules.Select(w => w.Code).ToListAsync();

        var added = 0;
        foreach (var rule in rules.Where(r => !existingCodes.Contains(r.Code)))
        {
            db.WithholdingRules.Add(rule);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("{Count} tevkifat kodu kuralı seed edildi (B2).", added);
        }
    }

    /// <summary>
    /// Faz 7 — E4: 2026 mevzuat parametreleri (GLOBAL tablo, süper admin günceller).
    /// Yalnızca eksik anahtarlar eklenir; mevcut değerler ASLA ezilmez (admin değişiklikleri korunur).
    /// </summary>
    private static async Task SeedTaxParametersAsync(AppDbContext db, ILogger logger)
    {
        var effective2026 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var parameters = new List<TaxParameter>
        {
            // KDV oranları
            new() { Key = "kdv.oran.standart", Value = "20", Description = "Standart KDV oranı (%)", EffectiveFrom = effective2026 },
            new() { Key = "kdv.oran.indirimli", Value = "10", Description = "İndirimli KDV oranı (%)", EffectiveFrom = effective2026 },
            new() { Key = "kdv.oran.dusuk", Value = "1", Description = "Düşük KDV oranı (%)", EffectiveFrom = effective2026 },

            // KDV oranı → indirilecek KDV hesabı eşlemesi (Luca yevmiye dışa aktarımı bu eşlemeyi okur)
            new() { Key = "kdv.hesap.20", Value = "191.01", Description = "%20 KDV için indirilecek KDV hesabı (Luca yevmiye)", EffectiveFrom = effective2026 },
            new() { Key = "kdv.hesap.10", Value = "191.02", Description = "%10 KDV için indirilecek KDV hesabı (Luca yevmiye)", EffectiveFrom = effective2026 },
            new() { Key = "kdv.hesap.1", Value = "191.03", Description = "%1 KDV için indirilecek KDV hesabı (Luca yevmiye)", EffectiveFrom = effective2026 },
            new() { Key = "kdv.hesap.diger", Value = "191", Description = "Listede olmayan oranlar için indirilecek KDV hesabı", EffectiveFrom = effective2026 },

            // Binek otomobil gider kısıtları (2026)
            new() { Key = "binek.gider.kisit.oran", Value = "70", Description = "Binek oto giderlerinin indirilebilir kısmı (%; kalanı KKEG)", EffectiveFrom = effective2026 },
            new() { Key = "binek.kira.aylik.sinir", Value = "46000", Description = "Binek oto aylık kira gider sınırı (TL, 2026)", EffectiveFrom = effective2026 },
            new() { Key = "binek.otv.kdv.gider.sinir", Value = "1200000", Description = "Binek oto ÖTV+KDV toplamının gider yazılabilir sınırı (TL, 2026)", EffectiveFrom = effective2026 },
            new() { Key = "binek.amortisman.sinir", Value = "2600000", Description = "Binek oto amortisman matrahı sınırı (TL, 2026)", EffectiveFrom = effective2026 },

            // Faz 8 — B3: KKEG hesabı (Luca yevmiye dışa aktarımında KKEG satırlarının borç hesabı)
            new() { Key = "kkeg.hesap", Value = "689", Description = "KKEG (kanunen kabul edilmeyen gider) hesabı — Luca yevmiye KKEG satırı", EffectiveFrom = effective2026 },

            // Faz 9 — Luca Fiş Aktarım Şablonu kolon değerleri.
            // CANLI DOĞRULANDI: kullanıcının Luca'ya girmiş gerçek dosyasında Belge Türü
            // "EF" (e-fatura) / "PS" (perakende satış fişi), Para Birimi boş.
            new() { Key = "luca.belge.turu.fatura", Value = "EF", Description = "Luca şablonu Belge Türü kolonu — e-fatura satırları (gerçek dosyada EF)", EffectiveFrom = effective2026 },
            new() { Key = "luca.belge.turu.fis", Value = "PS", Description = "Luca şablonu Belge Türü kolonu — fiş satırları (gerçek dosyada PS)", EffectiveFrom = effective2026 },
            new() { Key = "luca.para.birimi", Value = "", Description = "Luca şablonu Para Birimi kolonu (gerçek dosyada boş bırakılır)", EffectiveFrom = effective2026 },
            // TODO-VERIFY: Döviz Tutar şablonda zorunlu (sarı) görünse de TRY satırında boş kabul
            // edildiği varsayıldı; Luca 0 istiyorsa bu parametre "true" yapılır (0 yazılır).
            new() { Key = "luca.doviz.tutar.sifir", Value = "false", Description = "Luca şablonu Döviz Tutar: true ise TRY satırına 0 yazılır, false ise boş bırakılır", EffectiveFrom = effective2026 },

            // Faz 16 — Z raporu (yazar kasa günlük kapanış) gelir fişi hesapları.
            new() { Key = "zrapor.gelir.hesap", Value = "600", Description = "Z raporu gelir fişinde alacak yazılan satış geliri hesabı (oran bazlı satırlar)", EffectiveFrom = effective2026 },
            new() { Key = "zrapor.kart.hesap", Value = "108", Description = "Z raporu gelir fişinde kart tahsilatının borç hesabı (nakit her zaman 100 Kasa)", EffectiveFrom = effective2026 },
        };

        var existingKeys = await db.TaxParameters
            .Select(p => p.Key)
            .ToListAsync();

        var added = 0;
        foreach (var parameter in parameters.Where(p => !existingKeys.Contains(p.Key)))
        {
            db.TaxParameters.Add(parameter);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("{Count} vergi parametresi seed edildi (2026 değerleri).", added);
        }
    }

    private static async Task SeedPlansAsync(AppDbContext db, ILogger logger)
    {
        var plans = new List<Plan>
        {
            new()
            {
                Code = "baslangic",
                Name = "Başlangıç",
                MonthlyPrice = 499m,
                MaxClients = 10,
                MaxUsers = 1,
                MonthlyOcrQuota = 200,
                ETebligat = false,
                IsActive = true
            },
            new()
            {
                Code = "profesyonel",
                Name = "Profesyonel",
                MonthlyPrice = 999m,
                MaxClients = 50,
                MaxUsers = 5,
                MonthlyOcrQuota = 1000,
                ETebligat = true,
                IsActive = true
            },
            new()
            {
                Code = "ofis",
                Name = "Ofis",
                MonthlyPrice = 1999m,
                MaxClients = int.MaxValue, // Sınırsız
                MaxUsers = 15,
                MonthlyOcrQuota = 5000,
                ETebligat = true,
                IsActive = true
            }
        };

        foreach (var plan in plans)
        {
            var exists = await db.Plans.IgnoreQueryFilters().AnyAsync(p => p.Code == plan.Code);
            if (!exists)
            {
                db.Plans.Add(plan);
                logger.LogInformation("Plan eklendi: {Code}", plan.Code);
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedSuperAdminAsync(AppDbContext db, IConfiguration configuration, ILogger logger)
    {
        var email = configuration["SuperAdmin:Email"];
        var password = configuration["SuperAdmin:Password"];
        var fullName = configuration["SuperAdmin:FullName"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("SuperAdmin yapılandırması eksik; süper admin kullanıcı oluşturulmadı.");
            return;
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var exists = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == normalizedEmail);
        if (exists) return;

        db.Users.Add(new AppUser
        {
            TenantId = null,
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FullName = string.IsNullOrWhiteSpace(fullName) ? "Sistem Yöneticisi" : fullName,
            Role = UserRole.SuperAdmin,
            IsActive = true
        });

        await db.SaveChangesAsync();
        logger.LogInformation("Süper admin kullanıcı oluşturuldu: {Email}", normalizedEmail);
    }
}
