using MaliMusavir.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// EF Core DbContext. Çok kiracılı (multi-tenant) izolasyon, ctor ile alınan
/// ICurrentUser üzerinden global query filter olarak uygulanır.
/// SuperAdmin için filtre devre dışıdır (tüm kayıtları görür).
/// </summary>
public class AppDbContext : DbContext
{
    // Query filter'lar DbContext instance alanları üzerinden parametrelenir;
    // her scoped context, o isteğin kullanıcısına göre filtre uygular.
    private readonly Guid? _tenantId;
    private readonly bool _isSuperAdmin;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        _tenantId = currentUser.TenantId;
        _isSuperAdmin = currentUser.IsSuperAdmin;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientIntegration> ClientIntegrations => Set<ClientIntegration>();

    // Faz 18 — Müşavir (SMMM) GİB kimliği: tenant başına tek kayıt.
    // DİKKAT: EnsureCreated mevcut DB'ye tablo EKLEMEZ — dağıtım SQL'i elle çalıştırılır.
    public DbSet<TenantGibCredential> TenantGibCredentials => Set<TenantGibCredential>();
    public DbSet<EInvoice> EInvoices => Set<EInvoice>();
    public DbSet<EInvoiceLine> EInvoiceLines => Set<EInvoiceLine>();

    // Faz 2 — Otomatik Hesap/Cari Kod Motoru
    public DbSet<ChartAccount> ChartAccounts => Set<ChartAccount>();
    public DbSet<AccountMemory> AccountMemories => Set<AccountMemory>();
    public DbSet<VendorDefault> VendorDefaults => Set<VendorDefault>();
    public DbSet<CodingRule> CodingRules => Set<CodingRule>();
    public DbSet<VendorCariMap> VendorCariMaps => Set<VendorCariMap>();
    public DbSet<ClientCodingSettings> ClientCodingSettings => Set<ClientCodingSettings>();

    // Faz 3 — Fiş OCR
    public DbSet<Receipt> Receipts => Set<Receipt>();

    // Faz 16 — Oran bazlı KDV kırılımı (çok oranlı fişler + Z raporları)
    public DbSet<ReceiptVatBreakdown> ReceiptVatBreakdowns => Set<ReceiptVatBreakdown>();

    // Faz 13 — Araç plakası / akaryakıt KKEG otomasyonu
    public DbSet<ClientVehicle> ClientVehicles => Set<ClientVehicle>();

    // Faz 4 — e-Tebligat
    public DbSet<ETebligatMessage> ETebligatMessages => Set<ETebligatMessage>();

    // Faz 5 — Abonelik / Ödeme
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<BillingInvoice> BillingInvoices => Set<BillingInvoice>();
    public DbSet<InvoiceNumberSequence> InvoiceNumberSequences => Set<InvoiceNumberSequence>();

    // Faz 5 — e-Beyanname
    public DbSet<Declaration> Declarations => Set<Declaration>();

    // Faz 7 — E1: Mükellef belge portalı
    public DbSet<ClientPortalAccess> ClientPortalAccesses => Set<ClientPortalAccess>();
    public DbSet<PortalUpload> PortalUploads => Set<PortalUpload>();
    public DbSet<DocumentRequest> DocumentRequests => Set<DocumentRequest>();

    // Faz 7 — E2: Vergi takvimi
    public DbSet<TaxObligation> TaxObligations => Set<TaxObligation>();

    // Faz 7 — E3: Sertifika süre takibi
    public DbSet<ClientCertificate> ClientCertificates => Set<ClientCertificate>();

    // Faz 7 — E4: Parametre merkezi (GLOBAL — tenant filtresi yok)
    public DbSet<TaxParameter> TaxParameters => Set<TaxParameter>();

    // Faz 8 — B2: Tevkifat kodu kuralları (GLOBAL — tenant filtresi yok)
    public DbSet<WithholdingRule> WithholdingRules => Set<WithholdingRule>();

    // Faz 8 — B5: Mutabakat "Yoksay" kayıtları (tenant filtreli)
    public DbSet<ReconciliationDismissal> ReconciliationDismissals => Set<ReconciliationDismissal>();

    // Faz 11 — E5: e-Defter berat dönem takibi
    public DbSet<EDefterPeriod> EDefterPeriods => Set<EDefterPeriod>();

    // Faz 11 — E6: Dışa gönderim kuyruğu (worker IgnoreQueryFilters ile tüm tenant'ları tarar)
    public DbSet<OutboundJob> OutboundJobs => Set<OutboundJob>();

    // Faz 11 — E7: SMMM ücret / tahsilat takibi
    public DbSet<ClientFee> ClientFees => Set<ClientFee>();
    public DbSet<FeeInvoice> FeeInvoices => Set<FeeInvoice>();

    // ---------- Banka hesap hareketleri (ekstre içe aktarma + kodlama) ----------
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<BankStatementImport> BankStatementImports => Set<BankStatementImport>();
    public DbSet<BankCounterpartyMap> BankCounterpartyMaps => Set<BankCounterpartyMap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
            e.Property(t => t.Email).HasMaxLength(320).IsRequired();
            e.Property(t => t.TaxNumber).HasMaxLength(11);
            e.HasOne(t => t.Plan)
                .WithMany()
                .HasForeignKey(t => t.PlanId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(320).IsRequired();
            e.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            e.Property(u => u.PasswordHash).IsRequired();
            e.HasIndex(u => u.Email).IsUnique();
            e.HasOne(u => u.Tenant)
                .WithMany(t => t.Users)
                .HasForeignKey(u => u.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin tüm kullanıcıları görür,
            // diğer roller yalnızca kendi tenant'ının kullanıcılarını görür.
            e.HasQueryFilter(u => _isSuperAdmin || u.TenantId == _tenantId);
        });

        modelBuilder.Entity<Plan>(e =>
        {
            e.Property(p => p.Code).HasMaxLength(50).IsRequired();
            e.Property(p => p.Name).HasMaxLength(100).IsRequired();
            e.Property(p => p.MonthlyPrice).HasPrecision(18, 2);
            e.HasIndex(p => p.Code).IsUnique();
        });

        modelBuilder.Entity<Client>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(300).IsRequired();
            e.Property(c => c.TaxId).HasMaxLength(11).IsRequired();
            e.Property(c => c.TaxOffice).HasMaxLength(150);
            e.Property(c => c.Email).HasMaxLength(320);
            e.Property(c => c.Phone).HasMaxLength(30);
            e.Property(c => c.Address).HasMaxLength(500);
            e.Property(c => c.IntegratorName).HasMaxLength(100);

            // Aynı tenant içinde aynı VKN/TCKN yalnızca bir kez kayıt edilebilir.
            e.HasIndex(c => new { c.TenantId, c.TaxId }).IsUnique();

            e.HasOne(c => c.Tenant)
                .WithMany(t => t.Clients)
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi mükelleflerini görür.
            e.HasQueryFilter(c => _isSuperAdmin || c.TenantId == _tenantId);
        });

        modelBuilder.Entity<ClientIntegration>(e =>
        {
            e.Property(ci => ci.ApiKeyEncrypted).HasMaxLength(2000);

            // Mükellef başına tek entegrasyon kaydı.
            e.HasIndex(ci => ci.ClientId).IsUnique();

            e.HasOne(ci => ci.Client)
                .WithMany()
                .HasForeignKey(ci => ci.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu Client üzerinden sağlanır (ClientIntegration'da TenantId kolonu yoktur).
            e.HasQueryFilter(ci => _isSuperAdmin || ci.Client!.TenantId == _tenantId);
        });

        // ---------- Faz 18: Müşavir (SMMM) GİB kimliği ----------

        modelBuilder.Entity<TenantGibCredential>(e =>
        {
            e.Property(t => t.Username).HasMaxLength(100).IsRequired();
            e.Property(t => t.PasswordEncrypted).HasMaxLength(2000).IsRequired();

            // Tenant başına TEK müşavir kimliği.
            e.HasIndex(t => t.TenantId).IsUnique();

            e.HasOne(t => t.Tenant)
                .WithMany()
                .HasForeignKey(t => t.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi kimliğini görür.
            e.HasQueryFilter(t => _isSuperAdmin || t.TenantId == _tenantId);
        });

        modelBuilder.Entity<EInvoice>(e =>
        {
            e.Property(i => i.ExternalUuid).HasMaxLength(64).IsRequired();
            e.Property(i => i.InvoiceNumber).HasMaxLength(30);
            e.Property(i => i.SenderTaxId).HasMaxLength(11);
            e.Property(i => i.SenderTitle).HasMaxLength(300);
            e.Property(i => i.CurrencyCode).HasMaxLength(3);
            e.Property(i => i.RawXmlPath).HasMaxLength(500);
            e.Property(i => i.TaxExclusiveAmount).HasPrecision(18, 2);
            e.Property(i => i.TaxTotal).HasPrecision(18, 2);
            e.Property(i => i.PayableAmount).HasPrecision(18, 2);

            // Mükerrer fatura koruması: aynı tenant içinde aynı entegratör UUID'i bir kez yazılır.
            e.HasIndex(i => new { i.TenantId, i.ExternalUuid }).IsUnique();
            e.HasIndex(i => new { i.ClientId, i.IssueDate });

            e.HasOne(i => i.Client)
                .WithMany()
                .HasForeignKey(i => i.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi faturalarını görür.
            e.HasQueryFilter(i => _isSuperAdmin || i.TenantId == _tenantId);
        });

        modelBuilder.Entity<EInvoiceLine>(e =>
        {
            e.Property(l => l.ItemName).HasMaxLength(500).IsRequired();
            e.Property(l => l.UnitCode).HasMaxLength(10);
            e.Property(l => l.SuggestedAccountCode).HasMaxLength(30);
            e.Property(l => l.AccountCode).HasMaxLength(30);
            e.Property(l => l.Quantity).HasPrecision(18, 4);
            e.Property(l => l.UnitPrice).HasPrecision(18, 4);
            e.Property(l => l.LineAmount).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 2);
            e.Property(l => l.VatAmount).HasPrecision(18, 2);

            // Faz 8 — B2/B3: tevkifat + KKEG alanları.
            e.Property(l => l.WithholdingCode).HasMaxLength(10);
            e.Property(l => l.WithholdingRate).HasMaxLength(10);
            e.Property(l => l.WithholdingVatAmount).HasPrecision(18, 2);
            e.Property(l => l.KkegReason).HasMaxLength(300);
            e.Property(l => l.KkegAmount).HasPrecision(18, 2);

            e.HasIndex(l => l.EInvoiceId);

            e.HasOne(l => l.EInvoice)
                .WithMany(i => i.Lines)
                .HasForeignKey(l => l.EInvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu fatura başlığı üzerinden sağlanır.
            e.HasQueryFilter(l => _isSuperAdmin || l.EInvoice!.TenantId == _tenantId);
        });

        // ---------- Faz 2: Otomatik Hesap/Cari Kod Motoru ----------

        modelBuilder.Entity<ChartAccount>(e =>
        {
            e.Property(a => a.Code).HasMaxLength(30).IsRequired();
            e.Property(a => a.Name).HasMaxLength(200).IsRequired();

            // Mükellef bazlı kanonik hesap planı: aynı kod bir kez tanımlanır.
            e.HasIndex(a => new { a.TenantId, a.ClientId, a.Code }).IsUnique();

            e.HasOne(a => a.Client)
                .WithMany()
                .HasForeignKey(a => a.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(a => _isSuperAdmin || a.TenantId == _tenantId);
        });

        modelBuilder.Entity<AccountMemory>(e =>
        {
            e.Property(m => m.SenderTaxId).HasMaxLength(11).IsRequired();
            e.Property(m => m.ItemSignature).HasMaxLength(200).IsRequired();
            e.Property(m => m.AccountCode).HasMaxLength(30).IsRequired();
            e.Property(m => m.VatRate).HasPrecision(5, 2);

            // Öğrenen hafızanın anahtarı (motor tasarımı §1 Katman 1).
            e.HasIndex(m => new { m.TenantId, m.ClientId, m.SenderTaxId, m.ItemSignature, m.VatRate }).IsUnique();

            e.HasOne(m => m.Client)
                .WithMany()
                .HasForeignKey(m => m.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(m => _isSuperAdmin || m.TenantId == _tenantId);
        });

        modelBuilder.Entity<VendorDefault>(e =>
        {
            e.Property(v => v.SenderTaxId).HasMaxLength(11).IsRequired();
            e.Property(v => v.VendorNameNormalized).HasMaxLength(300);
            e.Property(v => v.AccountCode).HasMaxLength(30).IsRequired();

            // Satıcı başına tek varsayılan hesap. VKN'siz (fiş listesi) kayıtlar unvan anahtarıyla
            // ayrışır — bu yüzden unique anahtar VKN + normalize unvan birleşimidir.
            // DİKKAT: mevcut DB'lerde EnsureCreated bu değişikliği UYGULAMAZ — deploy notundaki
            // ALTER/DROP INDEX/CREATE INDEX SQL'i elle çalıştırılmalıdır.
            // (Açık ad: Postgres 63 karakter kimlik sınırında sessiz kesilme olmasın.)
            e.HasIndex(v => new { v.TenantId, v.ClientId, v.SenderTaxId, v.VendorNameNormalized })
                .IsUnique()
                .HasDatabaseName("IX_VendorDefaults_Tenant_Client_TaxId_NameKey");
            // Unvan-bazlı ikincil eşleşme sorgusu için.
            e.HasIndex(v => new { v.ClientId, v.VendorNameNormalized })
                .HasDatabaseName("IX_VendorDefaults_Client_NameKey");

            e.HasOne(v => v.Client)
                .WithMany()
                .HasForeignKey(v => v.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(v => _isSuperAdmin || v.TenantId == _tenantId);
        });

        modelBuilder.Entity<CodingRule>(e =>
        {
            e.Property(r => r.Pattern).HasMaxLength(500).IsRequired();
            e.Property(r => r.AccountCode).HasMaxLength(30).IsRequired();

            // Kural değerlendirme sırası: aktif kurallar öncelikle çekilir.
            e.HasIndex(r => new { r.TenantId, r.IsActive, r.Priority });

            // ClientId null = tenant geneli; mükellef silinirse kural tenant geneline döner.
            e.HasOne(r => r.Client)
                .WithMany()
                .HasForeignKey(r => r.ClientId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasQueryFilter(r => _isSuperAdmin || r.TenantId == _tenantId);
        });

        modelBuilder.Entity<VendorCariMap>(e =>
        {
            e.Property(m => m.SenderTaxId).HasMaxLength(11).IsRequired();
            e.Property(m => m.SenderTitle).HasMaxLength(300);
            e.Property(m => m.VendorNameNormalized).HasMaxLength(300);
            e.Property(m => m.CariCode).HasMaxLength(30).IsRequired();

            // Satıcı VKN başına tek cari kod. Mizan içe aktarımı VKN'siz (unvan anahtarlı)
            // kayıt yazdığından unique anahtar VKN + normalize unvan birleşimidir.
            // DİKKAT: mevcut DB'lerde EnsureCreated bu değişikliği UYGULAMAZ — deploy notundaki
            // ALTER/DROP INDEX/CREATE INDEX SQL'i elle çalıştırılmalıdır.
            // (Açık ad: Postgres 63 karakter kimlik sınırında sessiz kesilme olmasın.)
            e.HasIndex(m => new { m.TenantId, m.ClientId, m.SenderTaxId, m.VendorNameNormalized })
                .IsUnique()
                .HasDatabaseName("IX_VendorCariMaps_Tenant_Client_TaxId_NameKey");
            // Unvan-bazlı ikincil cari eşleşme sorgusu için.
            e.HasIndex(m => new { m.ClientId, m.VendorNameNormalized })
                .HasDatabaseName("IX_VendorCariMaps_Client_NameKey");

            e.HasOne(m => m.Client)
                .WithMany()
                .HasForeignKey(m => m.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(m => _isSuperAdmin || m.TenantId == _tenantId);
        });

        modelBuilder.Entity<ClientCodingSettings>(e =>
        {
            e.Property(s => s.DefaultExpenseAccount).HasMaxLength(30).IsRequired();
            e.Property(s => s.CariPrefix).HasMaxLength(10).IsRequired();

            // Mükellef başına tek ayar kaydı.
            e.HasIndex(s => s.ClientId).IsUnique();

            e.HasOne(s => s.Client)
                .WithMany()
                .HasForeignKey(s => s.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu Client üzerinden sağlanır (ClientIntegration ile aynı desen).
            e.HasQueryFilter(s => _isSuperAdmin || s.Client!.TenantId == _tenantId);
        });

        // ---------- Faz 3: Fiş OCR ----------

        modelBuilder.Entity<Receipt>(e =>
        {
            e.Property(r => r.FileName).HasMaxLength(260).IsRequired();
            e.Property(r => r.StoredPath).HasMaxLength(500).IsRequired();
            e.Property(r => r.VendorName).HasMaxLength(300);
            e.Property(r => r.VendorTaxId).HasMaxLength(11);
            e.Property(r => r.DocumentNumber).HasMaxLength(60); // Evrak No (Luca şablonu)
            e.Property(r => r.SuggestedAccountCode).HasMaxLength(30);
            e.Property(r => r.AccountCode).HasMaxLength(30);
            e.Property(r => r.CariCode).HasMaxLength(30);
            e.Property(r => r.IneligibleReason).HasMaxLength(300);
            e.Property(r => r.RejectReason).HasMaxLength(300);
            e.Property(r => r.TotalAmount).HasPrecision(18, 2);
            e.Property(r => r.VatRate).HasPrecision(5, 2);
            e.Property(r => r.VatAmount).HasPrecision(18, 2);
            e.Property(r => r.NetAmount).HasPrecision(18, 2);

            // Faz 8 — B3: KKEG alanları.
            e.Property(r => r.KkegReason).HasMaxLength(300);
            e.Property(r => r.KkegAmount).HasPrecision(18, 2);

            // Faz 16: iskonto + Z raporu nakit/kart toplamları (DocumentKind int kolon olarak gider).
            e.Property(r => r.DiscountTotal).HasPrecision(18, 2);
            e.Property(r => r.CashTotal).HasPrecision(18, 2);
            e.Property(r => r.CardTotal).HasPrecision(18, 2);

            // Faz 13: fişten okunan plaka (normalize) + eşleşme durumu + plaka uyarısı.
            e.Property(r => r.PlateNumber).HasMaxLength(12);
            e.Property(r => r.PlateWarning).HasMaxLength(300);

            // Liste ekranı filtreleri (mükellef + durum) ve dönem sorguları (Luca export) için.
            e.HasIndex(r => new { r.TenantId, r.ClientId, r.Status });
            e.HasIndex(r => new { r.ClientId, r.ReceiptDate });

            e.HasOne(r => r.Client)
                .WithMany()
                .HasForeignKey(r => r.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi fişlerini görür.
            e.HasQueryFilter(r => _isSuperAdmin || r.TenantId == _tenantId);
        });

        // ---------- Faz 16: Oran bazlı KDV kırılımı ----------

        modelBuilder.Entity<ReceiptVatBreakdown>(e =>
        {
            e.Property(b => b.VatRate).HasPrecision(5, 2);
            e.Property(b => b.NetAmount).HasPrecision(18, 2);
            e.Property(b => b.VatAmount).HasPrecision(18, 2);

            // Fiş detay/export sorguları fiş kimliğinden okur.
            e.HasIndex(b => b.ReceiptId);
            e.HasIndex(b => new { b.TenantId, b.ReceiptId });

            e.HasOne(b => b.Receipt)
                .WithMany(r => r.VatBreakdowns)
                .HasForeignKey(b => b.ReceiptId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: mevcut kalıpla (SuperAdmin hariç yalnız kendi kayıtları).
            e.HasQueryFilter(b => _isSuperAdmin || b.TenantId == _tenantId);
        });

        // ---------- Faz 13: Mükellef araçları (plaka) ----------

        modelBuilder.Entity<ClientVehicle>(e =>
        {
            e.Property(v => v.Plate).HasMaxLength(12).IsRequired();
            e.Property(v => v.Description).HasMaxLength(200);

            // Aynı mükellefte aynı plaka bir kez kayıt edilir (normalize plaka üzerinden).
            e.HasIndex(v => new { v.TenantId, v.ClientId, v.Plate }).IsUnique();

            e.HasOne(v => v.Client)
                .WithMany()
                .HasForeignKey(v => v.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi araç kayıtlarını görür.
            e.HasQueryFilter(v => _isSuperAdmin || v.TenantId == _tenantId);
        });

        // ---------- Faz 4: e-Tebligat ----------

        modelBuilder.Entity<ETebligatMessage>(e =>
        {
            e.Property(m => m.ExternalId).HasMaxLength(100).IsRequired();
            e.Property(m => m.Sender).HasMaxLength(200).IsRequired();
            e.Property(m => m.Subject).HasMaxLength(500).IsRequired();
            e.Property(m => m.DeadlineType).HasMaxLength(20);

            // Mükerrer tebligat koruması: aynı tenant içinde aynı sağlayıcı kimliği bir kez yazılır.
            e.HasIndex(m => new { m.TenantId, m.ExternalId }).IsUnique();
            // Pano sayaçları (okunmamış) ve süre sıralaması için.
            e.HasIndex(m => new { m.TenantId, m.IsRead });
            e.HasIndex(m => new { m.ClientId, m.DeliveredAt });

            e.HasOne(m => m.Client)
                .WithMany()
                .HasForeignKey(m => m.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi tebligatlarını görür.
            e.HasQueryFilter(m => _isSuperAdmin || m.TenantId == _tenantId);
        });

        // ---------- Faz 5: Abonelik / Ödeme ----------

        modelBuilder.Entity<Subscription>(e =>
        {
            // Tenant başına TEK abonelik kaydı (tek gerçek kaynak; tarihçe transaction/fatura tablolarında).
            e.HasIndex(s => s.TenantId).IsUnique();

            e.HasOne(s => s.Tenant)
                .WithMany()
                .HasForeignKey(s => s.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(s => s.Plan)
                .WithMany()
                .HasForeignKey(s => s.PlanId)
                .OnDelete(DeleteBehavior.Restrict); // satıştan kalkan plan mevcut aboneliği bozamaz

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi aboneliğini görür.
            e.HasQueryFilter(s => _isSuperAdmin || s.TenantId == _tenantId);
        });

        modelBuilder.Entity<PaymentTransaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.Currency).HasMaxLength(3).IsRequired();
            e.Property(t => t.Provider).HasMaxLength(50).IsRequired();
            e.Property(t => t.ProviderRef).HasMaxLength(100);
            e.Property(t => t.FailureReason).HasMaxLength(500);

            // İşlem listesi ve admin gelir raporu sorguları için.
            e.HasIndex(t => new { t.TenantId, t.CreatedAt });
            e.HasIndex(t => new { t.Status, t.CreatedAt });

            e.HasOne(t => t.Subscription)
                .WithMany()
                .HasForeignKey(t => t.SubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasQueryFilter(t => _isSuperAdmin || t.TenantId == _tenantId);
        });

        modelBuilder.Entity<BillingInvoice>(e =>
        {
            e.Property(i => i.Number).HasMaxLength(20).IsRequired();
            e.Property(i => i.Amount).HasPrecision(18, 2);
            e.Property(i => i.VatAmount).HasPrecision(18, 2);
            e.Property(i => i.TotalAmount).HasPrecision(18, 2);
            e.Property(i => i.PdfPath).HasMaxLength(500);

            // Global sıra numarası tektir — sayaç yarışında son savunma hattı (BillingEntities notu).
            e.HasIndex(i => i.Number).IsUnique();
            e.HasIndex(i => new { i.TenantId, i.IssuedAt });

            e.HasQueryFilter(i => _isSuperAdmin || i.TenantId == _tenantId);
        });

        modelBuilder.Entity<InvoiceNumberSequence>(e =>
        {
            // Yıl bazlı GLOBAL sayaç: tenant filtresi YOKTUR (numara sırası tenant-bağımsızdır).
            e.HasKey(s => s.Year);
            // Yıl değeri uygulama tarafından verilir (identity/serial DEĞİL).
            e.Property(s => s.Year).ValueGeneratedNever();
        });

        // ---------- Faz 5: e-Beyanname ----------

        modelBuilder.Entity<Declaration>(e =>
        {
            e.Property(d => d.Period).HasMaxLength(7).IsRequired(); // "yyyy-MM"
            e.Property(d => d.PackageRef).HasMaxLength(50);
            e.Property(d => d.ErrorMessage).HasMaxLength(500);
            e.Property(d => d.TahakkukAmount).HasPrecision(18, 2);
            // KDV özeti JSON'u (kırılım listesiyle) 4000 karakteri aşmaz; sınır güvenlik payıyla konur.
            e.Property(d => d.SummaryJson).HasMaxLength(8000);

            // Aynı mükellef + tür + dönem için tek beyanname (mükerrer "hazırla" mevcut kaydı günceller).
            e.HasIndex(d => new { d.TenantId, d.ClientId, d.Type, d.Period }).IsUnique();
            // Durum panosu (dönem listesi) sorguları için.
            e.HasIndex(d => new { d.TenantId, d.Period });

            e.HasOne(d => d.Client)
                .WithMany()
                .HasForeignKey(d => d.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi beyannamelerini görür.
            e.HasQueryFilter(d => _isSuperAdmin || d.TenantId == _tenantId);
        });

        // ---------- Faz 7 — E1: Mükellef belge portalı ----------

        modelBuilder.Entity<ClientPortalAccess>(e =>
        {
            e.Property(p => p.Token).HasMaxLength(64).IsRequired();
            e.Property(p => p.PinHash).HasMaxLength(200).IsRequired();

            // Mükellef başına TEK portal erişimi; token URL'den benzersiz çözülür.
            e.HasIndex(p => p.ClientId).IsUnique();
            e.HasIndex(p => p.Token).IsUnique();

            e.HasOne(p => p.Client)
                .WithMany()
                .HasForeignKey(p => p.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu Client üzerinden (ClientIntegration deseni).
            // DİKKAT: anonim portal login/info sorguları IgnoreQueryFilters ile token'dan arar.
            e.HasQueryFilter(p => _isSuperAdmin || p.Client!.TenantId == _tenantId);
        });

        modelBuilder.Entity<PortalUpload>(e =>
        {
            e.Property(u => u.FileName).HasMaxLength(260).IsRequired();
            e.Property(u => u.StoredPath).HasMaxLength(500).IsRequired();
            e.Property(u => u.Note).HasMaxLength(500);

            // SMMM "gelen belgeler" listesi filtreleri (mükellef + kategori) için.
            e.HasIndex(u => new { u.TenantId, u.ClientId, u.Category });
            e.HasIndex(u => new { u.ClientId, u.UploadedAt });

            e.HasOne(u => u.Client)
                .WithMany()
                .HasForeignKey(u => u.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tenant izolasyonu: portal token'ı tenant_id claim'i taşıdığından filtre portalda da çalışır.
            e.HasQueryFilter(u => _isSuperAdmin || u.TenantId == _tenantId);
        });

        modelBuilder.Entity<DocumentRequest>(e =>
        {
            e.Property(r => r.Title).HasMaxLength(300).IsRequired();

            // Portal "bekleyen talepler" ve SMMM talep listesi sorguları için.
            e.HasIndex(r => new { r.ClientId, r.Status });

            e.HasOne(r => r.Client)
                .WithMany()
                .HasForeignKey(r => r.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(r => _isSuperAdmin || r.TenantId == _tenantId);
        });

        // ---------- Faz 7 — E2: Vergi takvimi ----------

        modelBuilder.Entity<TaxObligation>(e =>
        {
            e.Property(o => o.Type).HasMaxLength(100).IsRequired();
            e.Property(o => o.Period).HasMaxLength(7).IsRequired(); // "yyyy-MM"

            // Dönem panosu + mükerrer otomatik üretim kontrolü için
            // (unique DEĞİL: aynı türden ikinci MANUEL yükümlülük eklenebilir).
            e.HasIndex(o => new { o.TenantId, o.Period });
            e.HasIndex(o => new { o.ClientId, o.Period, o.Type });
            e.HasIndex(o => new { o.TenantId, o.Status, o.DueDate });

            e.HasOne(o => o.Client)
                .WithMany()
                .HasForeignKey(o => o.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(o => _isSuperAdmin || o.TenantId == _tenantId);
        });

        // ---------- Faz 7 — E3: Sertifika süre takibi ----------

        modelBuilder.Entity<ClientCertificate>(e =>
        {
            e.Property(c => c.Owner).HasMaxLength(200).IsRequired();
            e.Property(c => c.Note).HasMaxLength(500);

            // "Süresi yaklaşanlar" sorgusu için.
            e.HasIndex(c => new { c.TenantId, c.ExpiresAt });

            // Mükellef silinirse sertifikası da gider (null ClientId = SMMM'nin kendi sertifikası, etkilenmez).
            e.HasOne(c => c.Client)
                .WithMany()
                .HasForeignKey(c => c.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(c => _isSuperAdmin || c.TenantId == _tenantId);
        });

        // ---------- Faz 7 — E4: Parametre merkezi ----------

        modelBuilder.Entity<TaxParameter>(e =>
        {
            e.Property(p => p.Key).HasMaxLength(150).IsRequired();
            e.Property(p => p.Value).HasMaxLength(500).IsRequired();
            e.Property(p => p.Description).HasMaxLength(500);

            // GLOBAL tablo: tenant filtresi YOKTUR (süper admin yönetir, herkes okur).
            e.HasIndex(p => p.Key).IsUnique();
        });

        // ---------- Faz 8 — B2: Tevkifat kodu kuralları ----------

        modelBuilder.Entity<WithholdingRule>(e =>
        {
            e.Property(w => w.Code).HasMaxLength(10).IsRequired();
            e.Property(w => w.Description).HasMaxLength(300).IsRequired();
            e.Property(w => w.Fraction).HasMaxLength(10).IsRequired();
            e.Property(w => w.KeywordPattern).HasMaxLength(500).IsRequired();

            // GLOBAL tablo (TaxParameter deseni): tenant filtresi YOKTUR.
            e.HasIndex(w => w.Code).IsUnique();
        });

        // ---------- Faz 8 — B5: Mutabakat "Yoksay" kayıtları ----------

        modelBuilder.Entity<ReconciliationDismissal>(e =>
        {
            e.Property(d => d.FindingKey).HasMaxLength(64).IsRequired();

            // Aynı bulgu bir kez yok sayılır; analiz filtrelemesi bu index'ten okur.
            e.HasIndex(d => new { d.TenantId, d.FindingKey }).IsUnique();

            // Tenant izolasyonu: SuperAdmin hariç herkes yalnızca kendi kayıtlarını görür.
            e.HasQueryFilter(d => _isSuperAdmin || d.TenantId == _tenantId);
        });

        // ---------- Faz 11 — E5: e-Defter berat dönem takibi ----------

        modelBuilder.Entity<EDefterPeriod>(e =>
        {
            e.Property(p => p.Period).HasMaxLength(7).IsRequired(); // "yyyy-MM"
            e.Property(p => p.Note).HasMaxLength(500);

            // Aynı mükellef + dönem için TEK e-Defter kaydı (kontrol tekrarları upsert eder).
            e.HasIndex(p => new { p.TenantId, p.ClientId, p.Period }).IsUnique();
            e.HasIndex(p => new { p.TenantId, p.Period });

            e.HasOne(p => p.Client)
                .WithMany()
                .HasForeignKey(p => p.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(p => _isSuperAdmin || p.TenantId == _tenantId);
        });

        // ---------- Faz 11 — E6: Dışa gönderim kuyruğu ----------

        modelBuilder.Entity<OutboundJob>(e =>
        {
            e.Property(j => j.Type).HasMaxLength(50).IsRequired();
            e.Property(j => j.PayloadJson).HasMaxLength(2000).IsRequired();
            e.Property(j => j.LastError).HasMaxLength(1000);

            // Worker taraması: zamanı gelen bekleyen işler (IgnoreQueryFilters ile okunur).
            e.HasIndex(j => new { j.Status, j.NextAttemptAt });
            // Kuyruk ekranı listesi.
            e.HasIndex(j => new { j.TenantId, j.Status, j.CreatedAt });
            // Mükerrer kuyruk koruması sorgusu (tip + payload eşleşmesi).
            e.HasIndex(j => new { j.TenantId, j.Type });

            // API tarafında tenant izolasyonu; OutboundJobWorker sistem taramasında
            // IgnoreQueryFilters kullanır (kimliksiz arka plan scope'u).
            e.HasQueryFilter(j => _isSuperAdmin || j.TenantId == _tenantId);
        });

        // ---------- Faz 11 — E7: SMMM ücret / tahsilat takibi ----------

        // ---------- Banka hesap hareketleri ----------

        modelBuilder.Entity<BankAccount>(e =>
        {
            e.Property(a => a.BankName).HasMaxLength(150).IsRequired();
            e.Property(a => a.Iban).HasMaxLength(34).IsRequired();
            e.Property(a => a.AccountNumber).HasMaxLength(50);
            e.Property(a => a.CurrencyCode).HasMaxLength(3).IsRequired();
            e.Property(a => a.AccountCode).HasMaxLength(30);
            e.Property(a => a.LastBalance).HasPrecision(18, 2);

            // Aynı mükellefte aynı IBAN bir kez tanımlanır (ekstre tekrar yüklense de).
            e.HasIndex(a => new { a.ClientId, a.Iban }).IsUnique();

            e.HasOne(a => a.Client)
                .WithMany()
                .HasForeignKey(a => a.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(a => _isSuperAdmin || a.TenantId == _tenantId);
        });

        modelBuilder.Entity<BankTransaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.BalanceAfter).HasPrecision(18, 2);
            e.Property(t => t.CurrencyCode).HasMaxLength(3).IsRequired();
            e.Property(t => t.CounterpartyName).HasMaxLength(300);
            e.Property(t => t.CounterpartyIban).HasMaxLength(34);
            e.Property(t => t.CounterpartyTaxId).HasMaxLength(11);
            e.Property(t => t.Description).HasMaxLength(1000).IsRequired();
            e.Property(t => t.ReferenceNo).HasMaxLength(100);
            e.Property(t => t.TransactionType).HasMaxLength(50);
            e.Property(t => t.SuggestedAccountCode).HasMaxLength(30);
            e.Property(t => t.AccountCode).HasMaxLength(30);
            e.Property(t => t.CariCode).HasMaxLength(30);
            e.Property(t => t.DedupKey).HasMaxLength(64).IsRequired();

            // MÜKERRER KORUMASI: aynı hesapta aynı hareket iki kez yazılmaz.
            e.HasIndex(t => new { t.BankAccountId, t.DedupKey }).IsUnique();
            e.HasIndex(t => new { t.ClientId, t.TransactionDate });
            e.HasIndex(t => new { t.ClientId, t.CodingStatus });

            e.HasOne(t => t.BankAccount)
                .WithMany(a => a.Transactions)
                .HasForeignKey(t => t.BankAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(t => _isSuperAdmin || t.TenantId == _tenantId);
        });

        modelBuilder.Entity<BankStatementImport>(e =>
        {
            e.Property(i => i.FileName).HasMaxLength(300).IsRequired();
            e.Property(i => i.Format).HasMaxLength(20).IsRequired();
            e.Property(i => i.Warning).HasMaxLength(1000);

            e.HasIndex(i => new { i.ClientId, i.CreatedAt });

            e.HasQueryFilter(i => _isSuperAdmin || i.TenantId == _tenantId);
        });

        modelBuilder.Entity<BankCounterpartyMap>(e =>
        {
            e.Property(m => m.CounterpartyIban).HasMaxLength(34);
            e.Property(m => m.CounterpartyTaxId).HasMaxLength(11);
            e.Property(m => m.CounterpartyNameNormalized).HasMaxLength(300).IsRequired();
            e.Property(m => m.CariCode).HasMaxLength(30);
            e.Property(m => m.AccountCode).HasMaxLength(30);
            e.Property(m => m.DisplayName).HasMaxLength(300);

            // Eşleşme anahtarları — IBAN birincil, VKN ikincil, unvan üçüncül.
            e.HasIndex(m => new { m.ClientId, m.CounterpartyIban });
            e.HasIndex(m => new { m.ClientId, m.CounterpartyTaxId });
            e.HasIndex(m => new { m.ClientId, m.CounterpartyNameNormalized });

            e.HasQueryFilter(m => _isSuperAdmin || m.TenantId == _tenantId);
        });

        modelBuilder.Entity<ClientFee>(e =>
        {
            e.Property(f => f.MonthlyFee).HasPrecision(18, 2);
            e.Property(f => f.Currency).HasMaxLength(3).IsRequired();
            e.Property(f => f.Note).HasMaxLength(500);

            // Mükellef başına TEK ücret tanımı.
            e.HasIndex(f => new { f.TenantId, f.ClientId }).IsUnique();

            e.HasOne(f => f.Client)
                .WithMany()
                .HasForeignKey(f => f.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(f => _isSuperAdmin || f.TenantId == _tenantId);
        });

        modelBuilder.Entity<FeeInvoice>(e =>
        {
            e.Property(i => i.Period).HasMaxLength(7).IsRequired(); // "yyyy-MM"
            e.Property(i => i.Amount).HasPrecision(18, 2);
            e.Property(i => i.PaidAmount).HasPrecision(18, 2);
            e.Property(i => i.Note).HasMaxLength(500);

            // Aynı mükellef + dönem için TEK tahakkuk ("dönemi oluştur" mükerrer üretmez).
            e.HasIndex(i => new { i.TenantId, i.ClientId, i.Period }).IsUnique();
            // Dönem panosu + geciken taraması.
            e.HasIndex(i => new { i.TenantId, i.Period });
            e.HasIndex(i => new { i.TenantId, i.Status, i.DueDate });

            e.HasOne(i => i.Client)
                .WithMany()
                .HasForeignKey(i => i.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(i => _isSuperAdmin || i.TenantId == _tenantId);
        });
    }
}
