using System.Security.Claims;
using System.Text.Json;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

public class AuthEndpointsTests
{
    private const string Verifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";
    private static readonly IOptions<AdminAuthOptions> PortalOptions = Options.Create(
        AdminAuthOptions.FromConfiguration(
            new ConfigurationBuilder().AddJsonFile(AdminAuthAppSettings.Path()).Build()));
    private static readonly string RedirectUri = PortalOptions.Value.AllowedRedirectUris[0];

    [Fact]
    public async Task Exchange_SetsHostOnlyCookies_AndOmitsTokensFromJson()
    {
        var cognito = new Mock<ICognitoTokenClient>();
        cognito.Setup(client => client.ExchangeAsync("auth-code", Verifier, RedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CognitoTokenSet("access-value", "id-value", 1200));
        var reader = new Mock<ICognitoIdTokenReader>();
        reader.Setup(tokenReader => tokenReader.EnsureAccessTokenAsync("access-value", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        reader.Setup(tokenReader => tokenReader.ReadAsync("id-value", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminUserProfile("admin@core-webhook.eu", "Daniel González", ["admin", "club-basquet-sama"]));

        var context = NewContext();
        var result = await AuthEndpoints.Exchange(
            new TokenExchangeRequest { Code = "auth-code", CodeVerifier = Verifier, RedirectUri = RedirectUri },
            context,
            cognito.Object,
            reader.Object,
            PortalOptions,
            NullLogger<AuthEndpoints>.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        var cookies = CookieHeaders(context);
        var accessCookie = cookies.Single(cookie => cookie.StartsWith("ae_access=", StringComparison.Ordinal));
        var idCookie = cookies.Single(cookie => cookie.StartsWith("ae_id=", StringComparison.Ordinal));
        AssertSessionCookie(accessCookie, "ae_access=access-value", "path=/");
        AssertSessionCookie(idCookie, "ae_id=id-value", "path=/me");
        accessCookie.ToLowerInvariant().Should().Contain("max-age=1200");

        var body = await ReadBody(context);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("email").GetString().Should().Be("admin@core-webhook.eu");
        json.RootElement.GetProperty("name").GetString().Should().Be("Daniel González");
        json.RootElement.GetProperty("groups").EnumerateArray().Select(group => group.GetString())
            .Should().Equal("admin", "club-basquet-sama");
        json.RootElement.TryGetProperty("accessToken", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("refreshToken", out _).Should().BeFalse();
        body.Should().NotContain("access-value");
        body.Should().NotContain("id-value");
        body.Should().NotContain("refresh");
    }

    [Fact]
    public async Task Exchange_RejectsRedirectUriThatIsNotAllowListed()
    {
        var cognito = new Mock<ICognitoTokenClient>();
        var context = NewContext();

        var result = await AuthEndpoints.Exchange(
            new TokenExchangeRequest
            {
                Code = "auth-code",
                CodeVerifier = Verifier,
                RedirectUri = "https://evil.example/auth/callback"
            },
            context,
            cognito.Object,
            Mock.Of<ICognitoIdTokenReader>(),
            PortalOptions,
            NullLogger<AuthEndpoints>.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        SetCookieHeader(context).Should().BeEmpty();
        cognito.Verify(
            client => client.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Exchange_RejectsShortCodeVerifier()
    {
        var cognito = new Mock<ICognitoTokenClient>();
        var context = NewContext();

        var result = await AuthEndpoints.Exchange(
            new TokenExchangeRequest { Code = "auth-code", CodeVerifier = "too-short", RedirectUri = RedirectUri },
            context,
            cognito.Object,
            Mock.Of<ICognitoIdTokenReader>(),
            PortalOptions,
            NullLogger<AuthEndpoints>.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        cognito.Verify(
            client => client.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status502BadGateway)]
    public async Task Exchange_MapsCognitoFailures_AndDoesNotSetCookies(int statusCode)
    {
        var cognito = new Mock<ICognitoTokenClient>();
        cognito.Setup(client => client.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CognitoAuthException(statusCode));
        var context = NewContext();

        var result = await AuthEndpoints.Exchange(
            new TokenExchangeRequest { Code = "auth-code", CodeVerifier = Verifier, RedirectUri = RedirectUri },
            context,
            cognito.Object,
            Mock.Of<ICognitoIdTokenReader>(),
            PortalOptions,
            NullLogger<AuthEndpoints>.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(statusCode);
        SetCookieHeader(context).Should().BeEmpty();
    }

    [Fact]
    public async Task Exchange_WhenTokenValidationFails_Returns401WithoutCookies()
    {
        var cognito = new Mock<ICognitoTokenClient>();
        cognito.Setup(client => client.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CognitoTokenSet("access-value", "id-value", 3600));
        var reader = new Mock<ICognitoIdTokenReader>();
        reader.Setup(tokenReader => tokenReader.EnsureAccessTokenAsync("access-value", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SecurityTokenException("bad signature"));
        var context = NewContext();

        var result = await AuthEndpoints.Exchange(
            new TokenExchangeRequest { Code = "auth-code", CodeVerifier = Verifier, RedirectUri = RedirectUri },
            context,
            cognito.Object,
            reader.Object,
            PortalOptions,
            NullLogger<AuthEndpoints>.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        SetCookieHeader(context).Should().BeEmpty();
    }

    [Fact]
    public async Task Me_ReadsProfileFromIdTokenCookie()
    {
        var reader = new Mock<ICognitoIdTokenReader>();
        reader.Setup(tokenReader => tokenReader.ReadAsync("id-value", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminUserProfile("admin@core-webhook.eu", "Daniel González", ["admin"]));
        var context = NewContext();
        context.Request.Headers.Cookie = "ae_id=id-value";

        var result = await AuthEndpoints.Me(
            Authenticated(new Claim("email", "other@example.com")),
            context,
            reader.Object,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var body = await ReadBody(context);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("email").GetString().Should().Be("admin@core-webhook.eu");
        json.RootElement.GetProperty("groups")[0].GetString().Should().Be("admin");
    }

    [Fact]
    public async Task Me_FallsBackToAccessTokenClaims_WhenIdCookieIsAbsent()
    {
        var reader = new Mock<ICognitoIdTokenReader>();
        var context = NewContext();
        var user = Authenticated(
            new Claim("email", "campus@cbsama.cat"),
            new Claim("name", "Núria Solé"),
            new Claim("cognito:groups", "club-basquet-sama"));

        var result = await AuthEndpoints.Me(user, context, reader.Object, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var body = await ReadBody(context);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("email").GetString().Should().Be("campus@cbsama.cat");
        json.RootElement.GetProperty("name").GetString().Should().Be("Núria Solé");
        json.RootElement.GetProperty("groups")[0].GetString().Should().Be("club-basquet-sama");
        reader.Verify(tokenReader => tokenReader.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Me_WithoutProfile_Returns401()
    {
        var context = NewContext();
        var result = await AuthEndpoints.Me(
            Authenticated(),
            context,
            Mock.Of<ICognitoIdTokenReader>(),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Logout_ClearsBothCookies()
    {
        var context = NewContext();
        var result = AuthEndpoints.Logout(context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        var cookies = CookieHeaders(context);
        var accessCookie = cookies.Single(cookie => cookie.StartsWith("ae_access=", StringComparison.Ordinal)).ToLowerInvariant();
        var idCookie = cookies.Single(cookie => cookie.StartsWith("ae_id=", StringComparison.Ordinal)).ToLowerInvariant();
        accessCookie.Should().Contain("expires=").And.Contain("path=/").And.NotContain("path=/me").And.NotContain("domain=");
        idCookie.Should().Contain("expires=").And.Contain("path=/me").And.NotContain("domain=");
        accessCookie.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax");
        idCookie.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax");
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));

    private static DefaultHttpContext NewContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string SetCookieHeader(HttpContext context) =>
        string.Join("\n", CookieHeaders(context));

    private static string[] CookieHeaders(HttpContext context) =>
        context.Response.Headers.SetCookie
            .Where(cookie => !string.IsNullOrEmpty(cookie))
            .Select(cookie => cookie!)
            .ToArray();

    private static void AssertSessionCookie(string cookie, string prefix, string path)
    {
        cookie.Should().StartWith(prefix);
        var normalized = cookie.ToLowerInvariant();
        normalized.Should().Contain("httponly");
        normalized.Should().Contain("secure");
        normalized.Should().Contain("samesite=lax");
        normalized.Should().Contain(path);
        normalized.Should().NotContain("domain=");
        if (path == "path=/")
        {
            normalized.Should().NotContain("path=/me");
        }
    }

    private static async Task<string> ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
