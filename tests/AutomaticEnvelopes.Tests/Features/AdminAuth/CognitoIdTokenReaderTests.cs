using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AwesomeAssertions;
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
        profile.Groups.Should().Equal("admin", "club-basquet-sama");
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
        var jwt = new JwtSecurityToken(
            issuer: Issuer,
            audience: audience,
            claims: claims.Append(new Claim("token_use", tokenUse)),
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
