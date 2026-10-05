using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

public class PrivacyPolicyUrlTests
{
    [Theory]
    [InlineData("https://example.com/privacy", "https://example.com/privacy")]
    [InlineData("  https://example.com/p  ", "https://example.com/p")]
    [InlineData("HTTPS://Example.COM/Privacy", "https://example.com/Privacy")]
    [InlineData("http://localhost/privacy", "http://localhost/privacy")]
    [InlineData("http://localhost:5173/privacy", "http://localhost:5173/privacy")]
    [InlineData("http://127.0.0.1/privacy", "http://127.0.0.1/privacy")]
    [InlineData("http://127.0.0.1:9/privacy", "http://127.0.0.1:9/privacy")]
    [InlineData("http://[::1]/privacy", "http://[::1]/privacy")]
    [InlineData("https://localhost/privacy", "https://localhost/privacy")]
    public void TryNormalize_AcceptsHttpsAndLoopbackHttp(string input, string expected)
    {
        PrivacyPolicyUrls.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com/privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://localhost/p")]
    [InlineData("https://user:pass@example.com/p")]
    [InlineData("https://user@example.com/p")]
    [InlineData("/privacy")]
    [InlineData("//example.com/privacy")]
    [InlineData("http://localhost.example.com/p")]
    [InlineData("file:///etc/passwd")]
    public void TryNormalize_RejectsAnythingThePortalWouldNotLink(string? input)
    {
        PrivacyPolicyUrls.TryNormalize(input, out var normalized).Should().BeFalse();
        normalized.Should().BeEmpty();
    }

    [Fact]
    public void TryNormalize_RejectsAnOverlongValue()
    {
        var input = "https://example.com/" + new string('a', 2048);

        PrivacyPolicyUrls.TryNormalize(input, out _).Should().BeFalse();
    }
}
