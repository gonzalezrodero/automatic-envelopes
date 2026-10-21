using System.Security.Cryptography;
using System.Text;
using AutomaticEnvelopes.Api.Features.Usage;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Usage;

public class UsageRulesTests
{
    private const string Secret = "integration-test-phone-hash-key";

    [Fact]
    public void MonthKey_UsesMadridMidnight_IncludingDaylightSaving()
    {
        UsageMonth.Key(new DateTimeOffset(2026, 1, 31, 22, 30, 0, TimeSpan.Zero)).Should().Be("2026-01");
        UsageMonth.Key(new DateTimeOffset(2026, 1, 31, 23, 30, 0, TimeSpan.Zero)).Should().Be("2026-02");
        UsageMonth.Key(new DateTimeOffset(2026, 3, 31, 21, 30, 0, TimeSpan.Zero)).Should().Be("2026-03");
        UsageMonth.Key(new DateTimeOffset(2026, 3, 31, 22, 30, 0, TimeSpan.Zero)).Should().Be("2026-04");
    }

    [Fact]
    public void PhoneRateKey_IsAnHmacOfTenantAndPhone()
    {
        PhoneRateKey.TryHash("club-a", "34600111222", Secret, out var first).Should().BeTrue();
        PhoneRateKey.TryHash("club-a", "34600111222", Secret, out var again).Should().BeTrue();
        PhoneRateKey.TryHash("club-b", "34600111222", Secret, out var otherTenant).Should().BeTrue();

        again.Should().Be(first);
        otherTenant.Should().NotBe(first);
        first.Should().NotBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("34600111222"))).ToLowerInvariant());
        PhoneRateKey.TryHash("club-a", "34600111222", "short", out _).Should().BeFalse();
    }
}
