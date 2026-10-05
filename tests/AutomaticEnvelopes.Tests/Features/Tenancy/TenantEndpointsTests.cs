using System.Reflection;
using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

public class TenantEndpointsTests
{
    [Fact]
    public void ProtectedConstructor_KeepsTheEndpointTypeInstantiable()
    {
        var ctor = typeof(TenantEndpoints).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

        ctor.Should().NotBeNull();
        ctor!.Invoke(null).Should().BeOfType<TenantEndpoints>();
    }

    [Theory]
    [InlineData(nameof(TenantEndpoints.List))]
    [InlineData(nameof(TenantEndpoints.Get))]
    [InlineData(nameof(TenantEndpoints.Update))]
    public void PortalRoutes_RequireAnAuthenticatedSession_AndUseAuthPolicy(string methodName)
    {
        var method = typeof(TenantEndpoints).GetMethod(methodName);
        method.Should().NotBeNull();
        method!.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        method.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.Should().Be(AdminAuthPolicies.Auth);
    }

    [Fact]
    public async Task List_WithoutAuthentication_Returns401_WithoutQuerying()
    {
        var session = new Mock<IDocumentSession>(MockBehavior.Strict);
        var result = await TenantEndpoints.List(Anonymous(), new DefaultHttpContext(), session.Object, CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Get_WithoutAuthentication_Returns401()
    {
        var result = await TenantEndpoints.Get(
            "example-tenant",
            Anonymous(),
            new DefaultHttpContext(),
            new Mock<IDocumentSession>(MockBehavior.Strict).Object,
            CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("Bad_Id")]
    [InlineData("../etc")]
    [InlineData("")]
    public async Task Get_InvalidId_Returns400_WithoutLoading(string tenantId)
    {
        var result = await TenantEndpoints.Get(
            tenantId,
            Admin(),
            new DefaultHttpContext(),
            new Mock<IDocumentSession>(MockBehavior.Strict).Object,
            CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Get_OtherTenant_Returns403_EvenWhenTheDocumentIsMissing()
    {
        var session = new Mock<IDocumentSession>(MockBehavior.Strict);
        var result = await TenantEndpoints.Get(
            "other-tenant",
            TenantUser("example-tenant"),
            new DefaultHttpContext(),
            session.Object,
            CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Get_AllowedMissingTenant_Returns404()
    {
        var session = new Mock<IDocumentSession>();
        session.Setup(store => store.LoadAsync<TenantProfile>("example-tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantProfile?)null);

        var result = await TenantEndpoints.Get(
            "example-tenant",
            TenantUser("example-tenant"),
            new DefaultHttpContext(),
            session.Object,
            CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Update_RejectsAnEmptyPrompt_AndAPublicHttpUrl_WithoutWriting()
    {
        var session = new Mock<IDocumentSession>(MockBehavior.Strict);
        var user = TenantUser("example-tenant");

        var emptyPrompt = await TenantEndpoints.Update(
            "example-tenant",
            new UpdateTenantSettingsRequest { SystemPrompt = "  ", PrivacyPolicyUrl = "https://example.com/p" },
            user,
            new DefaultHttpContext(),
            session.Object,
            CancellationToken.None);
        var publicHttp = await TenantEndpoints.Update(
            "example-tenant",
            new UpdateTenantSettingsRequest { SystemPrompt = "persona", PrivacyPolicyUrl = "http://example.com/p" },
            user,
            new DefaultHttpContext(),
            session.Object,
            CancellationToken.None);
        var missingBody = await TenantEndpoints.Update(
            "example-tenant",
            null,
            user,
            new DefaultHttpContext(),
            session.Object,
            CancellationToken.None);

        Status(emptyPrompt).Should().Be(StatusCodes.Status400BadRequest);
        Status(publicHttp).Should().Be(StatusCodes.Status400BadRequest);
        Status(missingBody).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Update_RejectsAPromptPastTheLimit()
    {
        var result = await TenantEndpoints.Update(
            "example-tenant",
            new UpdateTenantSettingsRequest
            {
                SystemPrompt = new string('a', TenantSettingsValidation.MaxSystemPromptLength + 1),
                PrivacyPolicyUrl = "https://example.com/p"
            },
            Admin(),
            new DefaultHttpContext(),
            new Mock<IDocumentSession>(MockBehavior.Strict).Object,
            CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Update_OtherTenant_Returns403_WithoutLoading()
    {
        var result = await TenantEndpoints.Update(
            "other-tenant",
            new UpdateTenantSettingsRequest { SystemPrompt = "persona", PrivacyPolicyUrl = "https://example.com/p" },
            TenantUser("example-tenant"),
            new DefaultHttpContext(),
            new Mock<IDocumentSession>(MockBehavior.Strict).Object,
            CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Update_AllowedMissingTenant_Returns404_WithoutStoring()
    {
        var session = new Mock<IDocumentSession>(MockBehavior.Strict);
        session.Setup(store => store.LoadAsync<TenantProfile>("example-tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantProfile?)null);

        var result = await TenantEndpoints.Update(
            "example-tenant",
            new UpdateTenantSettingsRequest { SystemPrompt = "persona", PrivacyPolicyUrl = "https://example.com/p" },
            TenantUser("example-tenant"),
            new DefaultHttpContext(),
            session.Object,
            CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void TryValidate_TrimsThePrompt_AndNormalizesLoopbackHttp()
    {
        var ok = TenantSettingsValidation.TryValidate(
            new UpdateTenantSettingsRequest
            {
                SystemPrompt = "  persona  ",
                PrivacyPolicyUrl = " http://localhost:5173/privacy "
            },
            out var settings,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        settings.SystemPrompt.Should().Be("persona");
        settings.PrivacyPolicyUrl.Should().Be("http://localhost:5173/privacy");
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal Admin() =>
        new(new ClaimsIdentity([new Claim("cognito:groups", TenantAccess.AdminGroup)], authenticationType: "Bearer"));

    private static ClaimsPrincipal TenantUser(string tenantId) =>
        new(new ClaimsIdentity([new Claim("cognito:groups", tenantId)], authenticationType: "Bearer"));
}
