using System.Reflection;
using MaliMusavir.Api.Integrations;
using MaliMusavir.Api.Tebligat;
using Xunit;

public class GibSessionTests
{
    [Fact]
    public void ClientSessionsAreIsolatedByTenantClientAndAccount()
    {
        var tenant = Guid.NewGuid();
        var client = Guid.NewGuid();
        var key = GibRobotClient.ClientSessionKey(tenant, client, "account");
        Assert.Equal(key, GibRobotClient.ClientSessionKey(tenant, client, " account "));
        Assert.NotEqual(key, GibRobotClient.ClientSessionKey(Guid.NewGuid(), client, "account"));
        Assert.NotEqual(key, GibRobotClient.ClientSessionKey(tenant, Guid.NewGuid(), "account"));
        Assert.NotEqual(key, GibRobotClient.ClientSessionKey(tenant, client, "other"));
        Assert.DoesNotContain("account", key);
        Assert.True(key.Length <= 200);
        Assert.NotEqual(GibRobotClient.AdvisorSessionKey(tenant, "one"), GibRobotClient.AdvisorSessionKey(tenant, "two"));
        Assert.NotEqual(GibRobotClient.AdvisorSessionKey(tenant, "one"), GibRobotClient.AdvisorSessionKey(Guid.NewGuid(), "one"));
    }

    [Fact]
    public void DeemedServiceCountsCalendarDaysAcrossWeekend()
    {
        var calculate = typeof(GibPortalTebligatProvider).GetMethod("CalculateLegalDeadline", BindingFlags.NonPublic | BindingFlags.Static)!;
        // Friday + five calendar days = Wednesday; existing payment period follows.
        var deadline = (DateTime?)calculate.Invoke(null, new object[] { new DateTime(2026, 10, 16), "Ödeme" });
        Assert.Equal(new DateTime(2026, 11, 5), deadline);
    }
}
