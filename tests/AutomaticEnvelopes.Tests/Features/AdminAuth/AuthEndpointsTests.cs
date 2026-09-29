using System.Reflection;
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
    private static readonly IOptions<AdminAuthOptions> LogoutOptions = Options.Create(new AdminAuthOptions
    {
        Region = "eu-west-1",
        UserPoolId = "eu-west-1_pool",
        ClientId = "client-123",
        Domain = "auth.example.com",
        AllowedOrigins = ["http://localhost:5173", "https://admin.core-webhook.eu"],
        AllowedLogoutUris = ["http://localhost:5173/admin/login", "https://admin.core-webhook.eu/login"]
    });

    [Fact]
    public void ProtectedConstructor_KeepsTheEndpointTypeInstantiable()
    {
        var ctor = typeof(AuthEndpoints).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

        ctor.Should().NotBeNull();
        ctor!.Invoke(null).Should().BeOfType<AuthEndpoints>();
    }

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
            .ReturnsAsync(new AdminUserProfile("admin@core-webhook.eu", "Daniel González", ["admin", "club-basquet-sama"], "user-1"));

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

    [Theory]
    [MemberData(nameof(IncompleteBodies))]
    public async Task Exchange_RejectsIncompleteBody(TokenExchangeRequest? request)
    {
        var cognito = new Mock<ICognitoTokenClient>();
        var context = NewContext();

        var result = await AuthEndpoints.Exchange(
            request,
            context,
            cognito.Object,
            Mock.Of<ICognitoIdTokenReader>(),
            PortalOptions,
            NullLogger<AuthEndpoints>.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = await ReadBody(context);
        body.Should().Contain("code, codeVerifier, and redirectUri are required.");
        cognito.Verify(
            client => client.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public static TheoryData<TokenExchangeRequest?> IncompleteBodies()
    {
        var data = new TheoryData<TokenExchangeRequest?>();
        data.Add((TokenExchangeRequest?)null);
        data.Add(new TokenExchangeRequest());
        data.Add(new TokenExchangeRequest { Code = " ", CodeVerifier = Verifier, RedirectUri = "https://portal.example/callback" });
        data.Add(new TokenExchangeRequest { Code = "auth-code", CodeVerifier = " ", RedirectUri = "https://portal.example/callback" });
        data.Add(new TokenExchangeRequest { Code = "auth-code", CodeVerifier = Verifier, RedirectUri = " " });
        return data;
    }

    [Fact]
    public async Task Exchange_RejectsOversizedCode_AndVerifierWithDisallowedCharacters()
    {
        var cognito = new Mock<ICognitoTokenClient>();

        foreach (var request in new[]
        {
            new TokenExchangeRequest { Code = new string('a', 2049), CodeVerifier = Verifier, RedirectUri = RedirectUri },
            new TokenExchangeRequest { Code = "auth-code", CodeVerifier = new string('a', 42) + "+", RedirectUri = RedirectUri },
            new TokenExchangeRequest { Code = "auth-code", CodeVerifier = new string('a', 129), RedirectUri = RedirectUri }
        })
        {
            var context = NewContext();
            var result = await AuthEndpoints.Exchange(
                request,
                context,
                cognito.Object,
                Mock.Of<ICognitoIdTokenReader>(),
                PortalOptions,
                NullLogger<AuthEndpoints>.Instance,
                CancellationToken.None);
            await result.ExecuteAsync(context);
            context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        }

        cognito.Verify(
            client => client.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Exchange_AcceptsPkceVerifierWithUnreservedCharacters()
    {
        var verifier = "a-._~" + new string('b', 38);
        var cognito = new Mock<ICognitoTokenClient>();
        cognito.Setup(client => client.ExchangeAsync("auth-code", verifier, RedirectUri, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CognitoAuthException(StatusCodes.Status401Unauthorized));
        var context = NewContext();

        var result = await AuthEndpoints.Exchange(
            new TokenExchangeRequest { Code = "auth-code", CodeVerifier = verifier, RedirectUri = RedirectUri },
            context,
            cognito.Object,
            Mock.Of<ICognitoIdTokenReader>(),
            PortalOptions,
            NullLogger<AuthEndpoints>.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        cognito.Verify(
            client => client.ExchangeAsync("auth-code", verifier, RedirectUri, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, "The authorization code could not be accepted.")]
    [InlineData(StatusCodes.Status500InternalServerError, "Authentication is not configured.")]
    public async Task Exchange_MapsCognitoClientErrors(int statusCode, string message)
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
        (await ReadBody(context)).Should().Contain(message);
    }

    [Fact]
    public async Task Exchange_UsesOneHourCookie_WhenCognitoOmitsExpiry()
    {
        var cognito = new Mock<ICognitoTokenClient>();
        cognito.Setup(client => client.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CognitoTokenSet("access-value", "id-value", 0));
        var reader = new Mock<ICognitoIdTokenReader>();
        reader.Setup(tokenReader => tokenReader.EnsureAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        reader.Setup(tokenReader => tokenReader.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminUserProfile("admin@core-webhook.eu", "Ada", ["admin"], "user-1"));
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

        CookieHeaders(context).Should().Contain(cookie => cookie.Contains("max-age=3600", StringComparison.OrdinalIgnoreCase));
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
            .ReturnsAsync(new AdminUserProfile("admin@core-webhook.eu", "Daniel González", ["admin"], "user-1"));
        var context = NewContext();
        context.Request.Headers.Cookie = "ae_id=id-value";

        var result = await AuthEndpoints.Me(
            Authenticated(new Claim("email", "other@example.com"), new Claim("sub", "user-1")),
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
    public async Task Me_AcceptsIdTokenWhenAccessSubjectIsTheInboundNameIdentifier()
    {
        var reader = new Mock<ICognitoIdTokenReader>();
        reader.Setup(tokenReader => tokenReader.ReadAsync("id-value", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminUserProfile("admin@core-webhook.eu", "Ada", ["admin"], "user-1"));
        var context = NewContext();
        context.Request.Headers.Cookie = "ae_id=id-value";

        var result = await AuthEndpoints.Me(
            Authenticated(new Claim(ClaimTypes.NameIdentifier, "user-1")),
            context,
            reader.Object,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("user-2")]
    [InlineData("")]
    public async Task Me_RejectsIdTokenWhenSubjectDoesNotMatchAccessToken(string idSubject)
    {
        var reader = new Mock<ICognitoIdTokenReader>();
        reader.Setup(tokenReader => tokenReader.ReadAsync("id-value", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminUserProfile("admin@core-webhook.eu", "Ada", ["admin"], idSubject));
        var context = NewContext();
        context.Request.Headers.Cookie = "ae_id=id-value";

        var result = await AuthEndpoints.Me(
            Authenticated(new Claim("sub", "user-1"), new Claim("email", "admin@core-webhook.eu")),
            context,
            reader.Object,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Me_WithoutAuthentication_Returns401()
    {
        var context = NewContext();
        var result = await AuthEndpoints.Me(
            new ClaimsPrincipal(new ClaimsIdentity()),
            context,
            Mock.Of<ICognitoIdTokenReader>(),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Me_WhenIdTokenCannotBeRead_Returns401(bool cognitoFailure)
    {
        var reader = new Mock<ICognitoIdTokenReader>();
        reader.Setup(tokenReader => tokenReader.ReadAsync("expired-id", It.IsAny<CancellationToken>()))
            .ThrowsAsync(cognitoFailure
                ? new CognitoAuthException(StatusCodes.Status502BadGateway)
                : new SecurityTokenException("expired"));
        var context = NewContext();
        context.Request.Headers.Cookie = "ae_id=expired-id";

        var result = await AuthEndpoints.Me(
            Authenticated(new Claim("email", "admin@core-webhook.eu")),
            context,
            reader.Object,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Me_UsesEmailWhenNameIsBlank_AndReadsRoleClaimsAsGroups()
    {
        var context = NewContext();
        var user = Authenticated(
            new Claim(ClaimTypes.Email, "campus@cbsama.cat"),
            new Claim("name", " "),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, " "));

        var result = await AuthEndpoints.Me(user, context, Mock.Of<ICognitoIdTokenReader>(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(await ReadBody(context));
        json.RootElement.GetProperty("email").GetString().Should().Be("campus@cbsama.cat");
        json.RootElement.GetProperty("name").GetString().Should().Be("campus@cbsama.cat");
        json.RootElement.GetProperty("groups").EnumerateArray().Select(group => group.GetString())
            .Should().Equal("admin");
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
    public async Task Logout_ClearsBothCookies_AndReturnsTheCognitoLogoutUrl()
    {
        var context = NewContext();
        context.Request.Headers.Origin = "http://localhost:5173";
        var result = AuthEndpoints.Logout(context, LogoutOptions);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(await ReadBody(context));
        json.RootElement.GetProperty("cognitoLogoutUrl").GetString().Should().Be(
            "https://auth.example.com/logout?client_id=client-123&logout_uri=http%3A%2F%2Flocalhost%3A5173%2Fadmin%2Flogin");
        var cookies = CookieHeaders(context);
        var accessCookie = cookies.Single(cookie => cookie.StartsWith("ae_access=", StringComparison.Ordinal)).ToLowerInvariant();
        var idCookie = cookies.Single(cookie => cookie.StartsWith("ae_id=", StringComparison.Ordinal)).ToLowerInvariant();
        accessCookie.Should().Contain("expires=").And.Contain("path=/").And.NotContain("path=/me").And.NotContain("domain=");
        idCookie.Should().Contain("expires=").And.Contain("path=/me").And.NotContain("domain=");
        accessCookie.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax");
        idCookie.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    public async Task Logout_RejectsAnOriginThatIsNotAllowListed(string? origin)
    {
        var context = NewContext();
        if (origin != null)
        {
            context.Request.Headers.Origin = origin;
        }

        var result = AuthEndpoints.Logout(context, LogoutOptions);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        SetCookieHeader(context).Should().BeEmpty();
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
