using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

public class TenantAccessTests
{
    [Theory]
    [InlineData("example-tenant", true)]
    [InlineData("a", true)]
    [InlineData("other-tenant", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("Example-Tenant", false)]
    [InlineData("example_tenant", false)]
    [InlineData("../etc", false)]
    [InlineData("example.tenant", false)]
    public void IsValid_MatchesThePortalPathPattern(string tenantId, bool expected)
    {
        TenantIds.IsValid(tenantId).Should().Be(expected);
    }

    [Fact]
    public void IsValid_RejectsIdsLongerThan64Characters()
    {
        TenantIds.IsValid(new string('a', 64)).Should().BeTrue();
        TenantIds.IsValid(new string('a', 65)).Should().BeFalse();
    }

    [Fact]
    public void AdminGroup_CanAccessEveryTenant_EvenWhenATenantGroupIsAlsoPresent()
    {
        var user = Principal(
            new Claim("cognito:groups", "admin"),
            new Claim("cognito:groups", "example-tenant"));

        TenantAccess.IsAdmin(user).Should().BeTrue();
        TenantAccess.CanAccess(user, "other-tenant").Should().BeTrue();
        TenantAccess.CanAccess(user, "example-tenant").Should().BeTrue();
    }

    [Fact]
    public void TenantGroup_CanAccessOnlyTheMatchingId()
    {
        var user = Principal(new Claim(ClaimTypes.Role, "example-tenant"));

        TenantAccess.IsAdmin(user).Should().BeFalse();
        TenantAccess.CanAccess(user, "example-tenant").Should().BeTrue();
        TenantAccess.CanAccess(user, "other-tenant").Should().BeFalse();
        TenantAccess.CanAccess(user, "Example-Tenant").Should().BeFalse();
        TenantAccess.VisibleTenantIds(user).Should().Equal("example-tenant");
    }

    [Fact]
    public void JsonArrayGroupClaim_IsReadAsTenantIds()
    {
        var user = Principal(new Claim("cognito:groups", """["example-tenant","other-tenant"]"""));

        TenantAccess.Groups(user).Should().Equal("example-tenant", "other-tenant");
        TenantAccess.VisibleTenantIds(user).Should().Equal("example-tenant", "other-tenant");
        TenantAccess.CanAccess(user, "other-tenant").Should().BeTrue();
    }

    [Fact]
    public void VisibleTenantIds_DropTheAdminGroupAndInvalidNames()
    {
        var user = Principal(
            new Claim("cognito:groups", "admin"),
            new Claim("cognito:groups", "not_a_slug"),
            new Claim(ClaimTypes.Role, "example-tenant"));

        TenantAccess.VisibleTenantIds(user).Should().Equal("example-tenant");
    }

    [Fact]
    public void Groups_TrimsRoleClaims_SkipsBlanks_AndDeduplicatesInOrder()
    {
        var user = Principal(
            new Claim("cognito:groups", " example-tenant "),
            new Claim(ClaimTypes.Role, "  "),
            new Claim(ClaimTypes.Role, " example-tenant "),
            new Claim(ClaimTypes.Role, "other-tenant"));

        TenantAccess.Groups(user).Should().Equal("example-tenant", "other-tenant");
    }

    [Fact]
    public void UnauthenticatedPrincipal_IsNotAnAdmin()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        TenantAccess.IsAuthenticated(user).Should().BeFalse();
        TenantAccess.IsAdmin(user).Should().BeFalse();
        TenantAccess.CanAccess(user, "example-tenant").Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));
}
