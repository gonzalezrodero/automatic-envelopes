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

    [Theory]
    [InlineData("Quina és l'hora?", UsageMessages.Catalan)]
    [InlineData("Si us plau, l'horari", UsageMessages.Catalan)]
    [InlineData("What time do you open?", UsageMessages.English)]
    [InlineData("¿A qué hora abrís?", UsageMessages.Spanish)]
    [InlineData("", UsageMessages.Spanish)]
    public void LimitReached_UsesACheapLanguageHint(string text, string expected)
    {
        UsageMessages.LimitReached(text).Should().Be(expected);
    }

    [Fact]
    public void Record_Emails80And100Once_AndBlockedDoesNotRepeat()
    {
        var doc = new TenantMonthUsage { Id = "2026-10" };

        for (var i = 0; i < 799; i++)
        {
            UsageMath.Record(doc, 1, 1, 1000).Alert.Should().Be(UsageAlert.None);
        }

        UsageMath.Record(doc, 2, 3, 1000).Alert.Should().Be(UsageAlert.Warning);
        doc.Responses.Should().Be(800);
        doc.InputTokens.Should().Be(801);
        doc.Notified80.Should().BeTrue();
        UsageMath.Record(doc, 0, 0, 1000).Alert.Should().Be(UsageAlert.None);

        doc.Responses = 999;
        UsageMath.Record(doc, 4, 5, 1000).Alert.Should().Be(UsageAlert.Reached);
        doc.Notified100.Should().BeTrue();
        doc.Responses.Should().Be(1000);
        UsageMath.Record(doc, 1, 1, 1000).Alert.Should().Be(UsageAlert.None);

        var blocked = new TenantMonthUsage { Responses = 1000 };
        var first = UsageMath.MarkBlocked(blocked, 1000);
        first.Save.Should().BeTrue();
        first.Alert.Should().Be(UsageAlert.Reached);
        UsageMath.MarkBlocked(blocked, 1000).Save.Should().BeFalse();

        var stopped = UsageMath.MarkBlocked(new TenantMonthUsage(), 0);
        stopped.Save.Should().BeTrue();
        stopped.Alert.Should().Be(UsageAlert.Reached);
    }

    [Fact]
    public void Mail_IsShortSpanish()
    {
        UsageMail.Subject(UsageAlert.Warning).Should().Contain("80%");
        UsageMail.Subject(UsageAlert.Reached).Should().Contain("Límite");
        var body = UsageMail.Body("Club", "2026-10", 800, 1000, UsageAlert.Warning);
        body.Should().Contain("Club");
        body.Should().Contain("800");
        body.Should().Contain("2026-10");
        UsageMail.Body("Club", "2026-10", 1000, 1000, UsageAlert.Reached).Should().Contain("mes que viene");
    }
}
