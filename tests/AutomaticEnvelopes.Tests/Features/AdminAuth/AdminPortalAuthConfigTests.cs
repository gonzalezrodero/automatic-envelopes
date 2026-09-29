using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

public class AdminPortalAuthConfigTests
{
    [Fact]
    public async Task CorsPolicy_AllowsExactPortalOriginsWithCredentials()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ADMIN_PORTAL_ORIGINS"] = "http://localhost:5173,https://admin.core-webhook.eu"
        });
        var policy = await GetPolicy(provider);

        policy.AllowAnyOrigin.Should().BeFalse();
        policy.SupportsCredentials.Should().BeTrue();
        policy.Origins.Should().BeEquivalentTo("http://localhost:5173", "https://admin.core-webhook.eu");
        policy.Origins.Should().NotContain("*");
        policy.IsOriginAllowed("http://localhost:5173").Should().BeTrue();
        policy.IsOriginAllowed("https://admin.core-webhook.eu").Should().BeTrue();
        policy.IsOriginAllowed("https://evil.example").Should().BeFalse();
        policy.IsOriginAllowed("http://localhost:5173.evil.example").Should().BeFalse();
    }

    [Fact]
    public async Task CorsPolicy_UsesAppSettingsWhenEnvironmentIsUnset()
    {
        var configured = AdminAuthOptions.FromConfiguration(
            new ConfigurationBuilder().AddJsonFile(AdminAuthAppSettings.Path()).Build());
        await using var provider = BuildProvider([]);
        var policy = await GetPolicy(provider);

        configured.CorsOrigins.Should().NotBeEmpty();
        configured.AllowedRedirectUris.Should().NotBeEmpty();
        policy.AllowAnyOrigin.Should().BeFalse();
        policy.SupportsCredentials.Should().BeTrue();
        policy.Origins.Should().BeEquivalentTo(configured.CorsOrigins);
        policy.Origins.Should().NotContain("*");
    }

    [Fact]
    public void AddAdminPortalAuth_WithoutOrigins_Throws()
    {
        var services = new ServiceCollection();
        var act = () => services.AddAdminPortalAuth(new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromConfiguration_RejectsWildcard_AndPrefersEnvironmentList()
    {
        var wildcard = () => AdminAuthOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ADMIN_PORTAL_ORIGINS"] = "*" })
            .Build());
        wildcard.Should().Throw<InvalidOperationException>();

        var options = AdminAuthOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminAuth:AllowedOrigins:0"] = "https://old.example",
                ["ADMIN_PORTAL_ORIGINS"] = "https://admin.core-webhook.eu",
                ["COGNITO_DOMAIN"] = "https://automatic-envelopes-admin-prod.auth.eu-west-1.amazoncognito.com/",
                ["COGNITO_USER_POOL_ID"] = "eu-west-1_pool",
                ["AWS_REGION"] = "eu-west-1",
                ["COGNITO_CLIENT_ID"] = "client-123"
            })
            .Build());

        options.Host.Should().Be("automatic-envelopes-admin-prod.auth.eu-west-1.amazoncognito.com");
        options.CorsOrigins.Should().Equal("https://admin.core-webhook.eu");
        options.TokenEndpoint.Should().Be("https://automatic-envelopes-admin-prod.auth.eu-west-1.amazoncognito.com/oauth2/token");
        options.Issuer.Should().Be("https://cognito-idp.eu-west-1.amazonaws.com/eu-west-1_pool");
    }

    [Fact]
    public void Host_StripsHttpSchemeAndPath()
    {
        var options = new AdminAuthOptions
        {
            Domain = "http://auth.example.com/oauth2/token",
            Region = " ",
            UserPoolId = "eu-west-1_pool"
        };

        var fromBlankRegion = AdminAuthOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AWS_REGION"] = " " })
            .Build());

        options.Host.Should().Be("auth.example.com");
        options.TokenEndpoint.Should().Be("https://auth.example.com/oauth2/token");
        fromBlankRegion.Region.Should().Be("eu-west-1");
        fromBlankRegion.AllowedOrigins.Should().BeEmpty();
        fromBlankRegion.CorsOrigins.Should().BeEmpty();
    }

    [Fact]
    public async Task JwtBearer_ValidatesAccessTokenClientId_AndReadsCookieWhenHeaderIsMissing()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["AWS_REGION"] = "eu-west-1",
            ["COGNITO_USER_POOL_ID"] = "eu-west-1_pool",
            ["COGNITO_CLIENT_ID"] = "client-123",
            ["COGNITO_DOMAIN"] = "auth.example.com"
        });
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        jwt.SaveToken.Should().BeTrue();
        jwt.Authority.Should().Be("https://cognito-idp.eu-west-1.amazonaws.com/eu-west-1_pool");
        jwt.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        jwt.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        jwt.TokenValidationParameters.RoleClaimType.Should().Be("cognito:groups");

        var matching = new JwtSecurityToken(claims: [new Claim("token_use", "access"), new Claim("client_id", "client-123")]);
        var mismatched = new JwtSecurityToken(claims: [new Claim("token_use", "access"), new Claim("client_id", "other")]);
        var idToken = new JwtSecurityToken(audience: "client-123", claims: [new Claim("token_use", "id")]);
        jwt.TokenValidationParameters.AudienceValidator!([], matching, jwt.TokenValidationParameters).Should().BeTrue();
        jwt.TokenValidationParameters.AudienceValidator!([], mismatched, jwt.TokenValidationParameters).Should().BeFalse();
        jwt.TokenValidationParameters.AudienceValidator!(idToken.Audiences, idToken, jwt.TokenValidationParameters).Should().BeFalse();

        var fromCookie = await Receive(jwt, cookie: "ae_access=from-cookie", authorization: null);
        fromCookie.Should().Be("from-cookie");

        var fromHeader = await Receive(jwt, cookie: "ae_access=from-cookie", authorization: "Bearer header-token");
        fromHeader.Should().BeNull();
    }

    [Fact]
    public async Task JwtBearer_RejectsAccessTokensWhenClientIdIsMissing()
    {
        await using var provider = BuildProvider([]);
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var token = new JwtSecurityToken(claims: [new Claim("token_use", "access"), new Claim("client_id", "client-123")]);

        jwt.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        jwt.Authority.Should().BeNull();
        jwt.TokenValidationParameters.AudienceValidator!([], token, jwt.TokenValidationParameters).Should().BeFalse();
    }

    [Fact]
    public void CognitoHttpClient_DisablesRedirects_AndUsesAFifteenSecondTimeout()
    {
        using var provider = BuildProvider([]);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CognitoHttpClient.Name);
        var handler = PrimaryHandler(client);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        handler.Should().BeOfType<SocketsHttpHandler>().Which.AllowAutoRedirect.Should().BeFalse();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(AdminAuthAppSettings.Path())
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminPortalAuth(configuration);
        return services.BuildServiceProvider();
    }

    private static HttpMessageHandler PrimaryHandler(HttpClient client)
    {
        var handler = typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client) as HttpMessageHandler;
        handler.Should().NotBeNull();
        while (handler is DelegatingHandler delegating && delegating.InnerHandler != null)
        {
            handler = delegating.InnerHandler;
        }

        return handler;
    }

    private static async Task<CorsPolicy> GetPolicy(ServiceProvider provider)
    {
        var policy = await provider.GetRequiredService<ICorsPolicyProvider>()
            .GetPolicyAsync(new DefaultHttpContext(), AdminPortalCors.PolicyName);
        policy.Should().NotBeNull();
        return policy!;
    }

    private static async Task<string?> Receive(JwtBearerOptions jwt, string cookie, string? authorization)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = cookie;
        if (authorization != null)
        {
            http.Request.Headers.Authorization = authorization;
        }

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, displayName: null, typeof(JwtBearerHandler));
        var context = new MessageReceivedContext(http, scheme, jwt);
        await jwt.Events.OnMessageReceived(context);
        return context.Token;
    }
}
