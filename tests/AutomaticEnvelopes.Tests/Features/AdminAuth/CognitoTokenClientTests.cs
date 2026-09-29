using System.Net;
using System.Text;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using Microsoft.AspNetCore.Http;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

public class CognitoTokenClientTests
{
    private static readonly AdminAuthOptions Configured = new()
    {
        Region = "eu-west-1",
        UserPoolId = "eu-west-1_pool",
        ClientId = "client-123",
        Domain = "https://automatic-envelopes-admin-dev.auth.eu-west-1.amazoncognito.com/"
    };

    [Fact]
    public async Task ExchangeAsync_PostsAuthorizationCode_AndIgnoresRefreshToken()
    {
        string? body = null;
        Uri? uri = null;
        var handler = new StubHandler(async (request, ct) =>
        {
            uri = request.RequestUri;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            request.Headers.Authorization.Should().BeNull();
            const string json = """
                {"access_token":"access-value","id_token":"id-value","refresh_token":"refresh-value","expires_in":3600,"token_type":"Bearer"}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateClient(handler, Configured);
        var result = await sut.ExchangeAsync(
            "auth-code",
            "verifier",
            "http://localhost:5173/admin/auth/callback",
            CancellationToken.None);

        uri.Should().Be(new Uri("https://automatic-envelopes-admin-dev.auth.eu-west-1.amazoncognito.com/oauth2/token"));
        ParseForm(body!).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "client-123",
            ["code"] = "auth-code",
            ["redirect_uri"] = "http://localhost:5173/admin/auth/callback",
            ["code_verifier"] = "verifier"
        });
        result.Should().Be(new CognitoTokenSet("access-value", "id-value", 3600));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status401Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status502BadGateway)]
    public async Task ExchangeAsync_MapsCognitoErrors(HttpStatusCode cognitoStatus, int expectedStatus)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(cognitoStatus)
        {
            Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json")
        }));

        var sut = CreateClient(handler, Configured);
        var act = () => sut.ExchangeAsync("code", "verifier", "https://admin.core-webhook.eu/auth/callback", CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task ExchangeAsync_WhenCognitoIsUnreachable_Returns502()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("down"));
        var sut = CreateClient(handler, Configured);

        var act = () => sut.ExchangeAsync("code", "verifier", "https://admin.core-webhook.eu/auth/callback", CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task ExchangeAsync_WhenTokensAreMissing_Returns502()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"refresh_token":"refresh-value"}""", Encoding.UTF8, "application/json")
        }));
        var sut = CreateClient(handler, Configured);

        var act = () => sut.ExchangeAsync("code", "verifier", "https://admin.core-webhook.eu/auth/callback", CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task ExchangeAsync_WhenBodyIsNotJson_Returns502()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>nope</html>", Encoding.UTF8, "text/html")
        }));
        var sut = CreateClient(handler, Configured);

        var act = () => sut.ExchangeAsync("code", "verifier", "https://admin.core-webhook.eu/auth/callback", CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task ExchangeAsync_RethrowsWhenTheCallerCancels()
    {
        var cts = new CancellationTokenSource();
        var handler = new StubHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var sut = CreateClient(handler, Configured);

        var act = () => sut.ExchangeAsync("code", "verifier", "https://admin.core-webhook.eu/auth/callback", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExchangeAsync_WhenTheHttpCallTimesOut_Returns502()
    {
        var handler = new StubHandler((_, _) => throw new TaskCanceledException("timeout"));
        var sut = CreateClient(handler, Configured);

        var act = () => sut.ExchangeAsync("code", "verifier", "https://admin.core-webhook.eu/auth/callback", CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task ExchangeAsync_WhenCognitoIsNotConfigured_Returns500()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("should not be called"));
        var sut = CreateClient(handler, new AdminAuthOptions());

        var act = () => sut.ExchangeAsync("code", "verifier", "https://admin.core-webhook.eu/auth/callback", CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private static CognitoTokenClient CreateClient(HttpMessageHandler handler, AdminAuthOptions options) =>
        new(new StubFactory(handler), Options.Create(options), NullLogger<CognitoTokenClient>.Instance);

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
