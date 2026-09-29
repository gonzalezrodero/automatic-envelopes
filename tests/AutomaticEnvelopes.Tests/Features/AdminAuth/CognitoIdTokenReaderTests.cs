using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

public class CognitoIdTokenReaderTests : IDisposable
{
    private const string Issuer = "https://cognito-idp.eu-west-1.amazonaws.com/eu-west-1_pool";
    private readonly RSA rsa = RSA.Create(2048);

    private static AdminAuthOptions AuthOptions => new()
    {
        Region = "eu-west-1",
        UserPoolId = "eu-west-1_pool",
        ClientId = "client-123",
        Domain = "auth.example.com"
    };

    [Fact]
    public async Task ReadAsync_ReturnsEmailNameAndGroups()
    {
        var token = WriteToken("id", "client-123",
        [
            new Claim("email", "admin@core-webhook.eu"),
            new Claim("name", "Daniel González"),
            new Claim("cognito:groups", "admin"),
            new Claim("cognito:groups", "club-basquet-sama")
        ]);

        var profile = await CreateReader().ReadAsync(token, CancellationToken.None);

        profile.Email.Should().Be("admin@core-webhook.eu");
        profile.Name.Should().Be("Daniel González");
        profile.Subject.Should().Be("subject-1");
        profile.Groups.Should().Equal("admin", "club-basquet-sama");
    }

    [Fact]
    public async Task ReadAsync_TrimsName_AndRejectsMissingEmail()
    {
        var reader = CreateReader();
        var named = WriteToken("id", "client-123",
        [
            new Claim("email", "campus@cbsama.cat"),
            new Claim("name", "  Núria Solé  ")
        ]);
        var nameless = WriteToken("id", "client-123", [new Claim("name", "No Email")]);
        var noSubject = WriteToken("id", "client-123",
        [
            new Claim("email", "campus@cbsama.cat"),
            new Claim("sub", " ")
        ]);

        var profile = await reader.ReadAsync(named, CancellationToken.None);
        var missingEmail = () => reader.ReadAsync(nameless, CancellationToken.None);
        var missingSubject = () => reader.ReadAsync(noSubject, CancellationToken.None);

        profile.Name.Should().Be("Núria Solé");
        await missingEmail.Should().ThrowAsync<SecurityTokenException>();
        await missingSubject.Should().ThrowAsync<SecurityTokenException>();
    }

    [Fact]
    public async Task ReadAsync_WhenCognitoIsNotConfigured_Returns500()
    {
        var reader = new CognitoIdTokenReader(new StaticKeys([]), Options.Create(new AdminAuthOptions()));

        var act = () => reader.ReadAsync("token", CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task ReadAsync_UsesEmailWhenNameIsMissing()
    {
        var token = WriteToken("id", "client-123", [new Claim("email", "campus@cbsama.cat")]);

        var profile = await CreateReader().ReadAsync(token, CancellationToken.None);

        profile.Name.Should().Be("campus@cbsama.cat");
        profile.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_RejectsWrongAudience_ExpiredToken_AndAccessToken()
    {
        var reader = CreateReader();
        var wrongAudience = WriteToken("id", "other-client", [new Claim("email", "admin@core-webhook.eu")]);
        var expired = WriteToken(
            "id",
            "client-123",
            [new Claim("email", "admin@core-webhook.eu")],
            expires: DateTime.UtcNow.AddMinutes(-10));
        var accessToken = WriteToken("access", "client-123", [new Claim("client_id", "client-123"), new Claim("email", "admin@core-webhook.eu")]);

        var wrongAudienceAct = () => reader.ReadAsync(wrongAudience, CancellationToken.None);
        var expiredAct = () => reader.ReadAsync(expired, CancellationToken.None);
        var accessAct = () => reader.ReadAsync(accessToken, CancellationToken.None);

        await wrongAudienceAct.Should().ThrowAsync<SecurityTokenException>();
        await expiredAct.Should().ThrowAsync<SecurityTokenException>();
        await accessAct.Should().ThrowAsync<SecurityTokenException>();
    }

    [Fact]
    public async Task EnsureAccessTokenAsync_AcceptsMatchingClientId_AndRejectsMismatch()
    {
        var reader = CreateReader();
        var accessToken = WriteToken("access", audience: null, [new Claim("client_id", "client-123")]);
        var wrongClient = WriteToken("access", audience: null, [new Claim("client_id", "other-client")]);

        await reader.EnsureAccessTokenAsync(accessToken, CancellationToken.None);
        var act = () => reader.EnsureAccessTokenAsync(wrongClient, CancellationToken.None);

        await act.Should().ThrowAsync<SecurityTokenException>();
    }

    [Fact]
    public void Read_ParsesJsonArrayGroupClaim()
    {
        var groups = CognitoGroupClaims.Read([new Claim("cognito:groups", """["admin","club-basquet-sama"]""")]);

        groups.Should().Equal("admin", "club-basquet-sama");
    }

    [Fact]
    public void Read_KeepsMalformedGroupText_AndDropsBlanks()
    {
        var groups = CognitoGroupClaims.Read(
        [
            new Claim("email", "admin@core-webhook.eu"),
            new Claim("cognito:groups", " "),
            new Claim("cognito:groups", "[not-json"),
            new Claim("cognito:groups", """["admin","","club-basquet-sama","admin"]""")
        ]);

        groups.Should().Equal("[not-json", "admin", "club-basquet-sama");
    }

    [Fact]
    public async Task GetSigningKeysAsync_ParsesJwksAndCachesIt()
    {
        var calls = 0;
        var parameters = rsa.ExportParameters(false);
        var json = $$"""
            {"keys":[{"kty":"RSA","kid":"k1","use":"sig","alg":"RS256","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}]}
            """;
        var handler = new CountingHandler(() =>
        {
            calls++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var provider = new CognitoJwksProvider(
            new StubFactory(handler),
            Options.Create(AuthOptions),
            NullLogger<CognitoJwksProvider>.Instance);

        var first = await provider.GetSigningKeysAsync(CancellationToken.None);
        var second = await provider.GetSigningKeysAsync(CancellationToken.None);

        calls.Should().Be(1);
        first.Should().ContainSingle();
        first.Single().KeyId.Should().Be("k1");
        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task GetSigningKeysAsync_WhenRefreshFailsWithNoCache_Returns502()
    {
        var provider = CreateProvider(new AsyncHandler((_, _) => throw new HttpRequestException("down")));

        var act = () => provider.GetSigningKeysAsync(CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Theory]
    [InlineData("{\"keys\":[]}")]
    [InlineData("not-json")]
    public async Task GetSigningKeysAsync_WhenJwksIsUnusable_Returns502(string body)
    {
        var provider = CreateProvider(new AsyncHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        })));

        var act = () => provider.GetSigningKeysAsync(CancellationToken.None);

        var error = await act.Should().ThrowAsync<CognitoAuthException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task GetSigningKeysAsync_WhenRefreshFails_ReturnsStaleKeys()
    {
        var calls = 0;
        var provider = CreateProvider(new AsyncHandler((_, _) =>
        {
            calls++;
            if (calls > 1)
            {
                throw new HttpRequestException("refresh failed");
            }

            return Task.FromResult(JsonResponse(RsaJwks()));
        }));

        var first = await provider.GetSigningKeysAsync(CancellationToken.None);
        typeof(CognitoJwksProvider)
            .GetField("freshUntil", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(provider, DateTimeOffset.UtcNow.AddMinutes(-5));

        var second = await provider.GetSigningKeysAsync(CancellationToken.None);

        calls.Should().Be(2);
        second.Should().BeSameAs(first);
        second.Single().KeyId.Should().Be("k1");
    }

    [Fact]
    public async Task GetSigningKeysAsync_RethrowsWhenTheCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        var provider = CreateProvider(new AsyncHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }));

        var act = () => provider.GetSigningKeysAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSigningKeysAsync_SecondCallerUsesKeysLoadedByTheFirst()
    {
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = CreateProvider(new AsyncHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            return JsonResponse(RsaJwks());
        }));

        var first = provider.GetSigningKeysAsync(CancellationToken.None);
        await started.Task;
        var second = provider.GetSigningKeysAsync(CancellationToken.None);
        release.TrySetResult();

        var keys = await Task.WhenAll(first, second);

        calls.Should().Be(1);
        keys[0].Should().BeSameAs(keys[1]);
    }

    public void Dispose()
    {
        rsa.Dispose();
        GC.SuppressFinalize(this);
    }

    private CognitoIdTokenReader CreateReader()
    {
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        return new CognitoIdTokenReader(new StaticKeys([key]), Options.Create(AuthOptions));
    }

    private string WriteToken(string tokenUse, string? audience, IEnumerable<Claim> claims, DateTime? expires = null)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var validTo = expires ?? DateTime.UtcNow.AddMinutes(10);
        var notBefore = expires is null ? DateTime.UtcNow.AddMinutes(-5) : validTo.AddMinutes(-5);
        var withSubject = claims.Any(claim => claim.Type == "sub")
            ? claims
            : claims.Append(new Claim("sub", "subject-1"));
        var jwt = new JwtSecurityToken(
            issuer: Issuer,
            audience: audience,
            claims: withSubject.Append(new Claim("token_use", tokenUse)),
            notBefore: notBefore,
            expires: validTo,
            signingCredentials: signingCredentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private sealed class StaticKeys(IReadOnlyCollection<SecurityKey> keys) : ICognitoSigningKeys
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct) =>
            Task.FromResult(keys);
    }

    private static CognitoJwksProvider CreateProvider(HttpMessageHandler handler) =>
        new(new StubFactory(handler), Options.Create(AuthOptions), NullLogger<CognitoJwksProvider>.Instance);

    private string RsaJwks()
    {
        var parameters = rsa.ExportParameters(false);
        return $$"""
            {"keys":[{"kty":"RSA","kid":"k1","use":"sig","alg":"RS256","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}]}
            """;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class CountingHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder());
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
