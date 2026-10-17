using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Features.WhatsAppWebhook;

public class WhatsAppWebhookEndpointTests
{
    private readonly WhatsAppWebhookEndpoint endpoint = new();

    [Fact]
    public async Task VerifyChallenge_CorrectToken_ReturnsTheChallenge()
    {
        var result = endpoint.VerifyChallenge(
            "subscribe",
            "secret-token",
            "1158201444",
            Options.Create(new WhatsAppOptions { VerifyToken = "secret-token" }),
            NullLogger<WhatsAppWebhookEndpoint>.Instance);

        var (status, body) = Read(result);
        status.Should().Be(StatusCodes.Status200OK);
        body.Should().Be("1158201444");
    }

    [Fact]
    public async Task VerifyChallenge_WrongToken_Returns403()
    {
        var result = endpoint.VerifyChallenge(
            "subscribe",
            "wrong-token",
            "1158201444",
            Options.Create(new WhatsAppOptions { VerifyToken = "secret-token" }),
            NullLogger<WhatsAppWebhookEndpoint>.Instance);

        Read(result).Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task VerifyChallenge_EmptyConfiguredToken_Returns403(string configured)
    {
        var result = endpoint.VerifyChallenge(
            "subscribe",
            configured,
            "1158201444",
            Options.Create(new WhatsAppOptions { VerifyToken = configured }),
            NullLogger<WhatsAppWebhookEndpoint>.Instance);

        Read(result).Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    private static (int Status, string Body) Read(IResult result) => result switch
    {
        ContentHttpResult content => (content.StatusCode ?? StatusCodes.Status200OK, content.ResponseContent ?? string.Empty),
        StatusCodeHttpResult status => (status.StatusCode, string.Empty),
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}.")
    };
}
