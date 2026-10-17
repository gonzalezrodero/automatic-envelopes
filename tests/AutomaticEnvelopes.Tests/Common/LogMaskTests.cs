using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Features.Portal;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Common;

public class LogMaskTests
{
    [Fact]
    public void Phone_IsStable_AndMatchesThePortalStreamId()
    {
        const string phone = "34666555444";

        var masked = LogMask.Phone(phone);

        masked.Should().Be(LogMask.Phone(phone));
        masked.Should().Be(PortalIds.ForStream(phone));
        masked.Should().HaveLength(32);
        masked.Should().MatchRegex("^[0-9a-f]{32}$");
        masked.Should().NotContain(phone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Phone_Blank_ReturnsEmpty(string? phone)
    {
        LogMask.Phone(phone).Should().BeEmpty();
    }

    [Fact]
    public void Phone_DifferentNumbers_ProduceDifferentHashes()
    {
        LogMask.Phone("34666555444").Should().NotBe(LogMask.Phone("34666555445"));
        LogMask.Phone("+34666555444").Should().NotBe(LogMask.Phone("34666555444"));
    }
}
