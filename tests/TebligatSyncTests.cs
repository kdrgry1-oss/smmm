using System.Net;
using System.Text;
using System.Text.Json;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Integrations;
using MaliMusavir.Api.Services;
using MaliMusavir.Api.Tebligat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TebligatSyncTests
{
    private sealed class User(Guid tenant) : ICurrentUser
    {
        public Guid? UserId => Guid.NewGuid();
        public Guid? TenantId => tenant;
        public string? Email => "test@example.test";
        public bool IsSuperAdmin => false;
        public bool IsAuthenticated => true;
        public bool IsPortal => false;
        public Guid? PortalClientId => null;
    }
    private sealed class Secrets : ISecretProtector
    {
        public string Protect(string text) => text;
        public string Unprotect(string text) => text;
    }
    private sealed class Robot : HttpMessageHandler, IHttpClientFactory
    {
        public List<JsonElement> Requests = new();
        public HashSet<string> FailKeys = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var payload = body.RootElement.Clone();
            Requests.Add(payload);
            bool failed = FailKeys.Contains(payload.GetProperty("sessionKey").GetString()!);
            var json = failed ? "{\"ok\":false,\"needsCaptchaLogin\":true,\"message\":\"Giriş gerekli\",\"items\":[]}"
                : "{\"ok\":true,\"items\":[{\"externalId\":\"test-document\",\"subject\":\"Bilgilendirme\",\"sentAt\":\"2026-10-20\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
    private static ETebligatSyncService Service(AppDbContext db, Robot transport)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ROBOT_TOKEN"] = "test-only" }).Build();
        var robot = new GibRobotClient(transport, config, NullLogger<GibRobotClient>.Instance);
        var provider = new GibPortalTebligatProvider(db, new Secrets(), robot, NullLogger<GibPortalTebligatProvider>.Instance);
        return new ETebligatSyncService(db, new[] { provider }, new Secrets(), NullLogger<ETebligatSyncService>.Instance);
    }
    private static AppDbContext Database(Guid tenant) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new User(tenant));
    private static Client AddClient(AppDbContext db, Guid tenant, string taxId, bool own = true)
    {
        var client = new Client { TenantId = tenant, Title = "Test company", TaxId = taxId };
        db.Clients.Add(client);
        if (own) db.ClientIntegrations.Add(new ClientIntegration {
            Client = client, ClientId = client.Id, Provider = IntegrationProvider.GibPortal,
            ApiKeyEncrypted = GibPortalProvider.PackCredentials("same-account", "test-password") });
        return client;
    }

    [Fact]
    public async Task BulkFailureIsVisibleAndDoesNotHideSuccessfulCompanyOrCrossTenants()
    {
        var tenant = Guid.NewGuid();
        await using var db = Database(tenant);
        var failed = AddClient(db, tenant, "0000000001");
        var good = AddClient(db, tenant, "0000000002");
        AddClient(db, Guid.NewGuid(), "0000000003");
        await db.SaveChangesAsync();
        using var transport = new Robot();
        transport.FailKeys.Add(GibRobotClient.ClientSessionKey(tenant, failed.Id, "same-account"));
        var result = await Service(db, transport).SyncAsync(null, default);
        Assert.True(result.Success);
        Assert.Equal(2, result.ClientCount);
        Assert.Equal(failed.Id, Assert.Single(result.Failures).ClientId);
        Assert.Equal(1, result.Added);
        Assert.Equal(good.Id, (await db.ETebligatMessages.SingleAsync()).ClientId);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(2, transport.Requests.Select(r => r.GetProperty("sessionKey").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task AdvisorFallbackUsesCurrentAccountAndSelectedCompany()
    {
        var tenant = Guid.NewGuid();
        await using var db = Database(tenant);
        var client = AddClient(db, tenant, "0000000001", own: false);
        db.TenantGibCredentials.Add(new TenantGibCredential { TenantId = tenant, Username = "advisor-new", PasswordEncrypted = "unused" });
        await db.SaveChangesAsync();
        using var transport = new Robot();
        var result = await Service(db, transport).SyncAsync(client.Id, default);
        Assert.True(result.Success);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(GibRobotClient.AdvisorSessionKey(tenant, "advisor-new"), request.GetProperty("sessionKey").GetString());
        Assert.Equal(client.TaxId, request.GetProperty("targetTaxId").GetString());
        Assert.False(request.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task RepeatedSyncDoesNotDuplicateMessagesOrResetReadState()
    {
        var tenant = Guid.NewGuid();
        await using var db = Database(tenant);
        var client = AddClient(db, tenant, "0000000001");
        await db.SaveChangesAsync();
        using var transport = new Robot();
        var service = Service(db, transport);
        Assert.Equal(1, (await service.SyncAsync(client.Id, default)).Added);
        var message = await db.ETebligatMessages.SingleAsync();
        message.IsRead = true;
        await db.SaveChangesAsync();
        var second = await service.SyncAsync(client.Id, default);
        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.SkippedDuplicates);
        Assert.True((await db.ETebligatMessages.SingleAsync()).IsRead);
    }
}
