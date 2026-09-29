using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

public class TenantAccessTests
{
    [Theory]
    [InlineData("club-basquet-sama", true)]
    [InlineData("a", true)]
    [InlineData("escola-harmonia", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("Club-Basquet", false)]
    [InlineData("club_basquet", false)]
    [InlineData("../etc", false)]
    [InlineData("club.basquet", false)]
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
            new Claim("cognito:groups", "club-basquet-sama"));

        TenantAccess.IsAdmin(user).Should().BeTrue();
        TenantAccess.CanAccess(user, "escola-harmonia").Should().BeTrue();
        TenantAccess.CanAccess(user, "club-basquet-sama").Should().BeTrue();
    }

    [Fact]
    public void TenantGroup_CanAccessOnlyTheMatchingId()
    {
        var user = Principal(new Claim(ClaimTypes.Role, "club-basquet-sama"));

        TenantAccess.IsAdmin(user).Should().BeFalse();
        TenantAccess.CanAccess(user, "club-basquet-sama").Should().BeTrue();
        TenantAccess.CanAccess(user, "escola-harmonia").Should().BeFalse();
        TenantAccess.CanAccess(user, "Club-Basquet-Sama").Should().BeFalse();
        TenantAccess.VisibleTenantIds(user).Should().Equal("club-basquet-sama");
    }

    [Fact]
    public void JsonArrayGroupClaim_IsReadAsTenantIds()
    {
        var user = Principal(new Claim("cognito:groups", """["club-basquet-sama","escola-harmonia"]"""));

        TenantAccess.Groups(user).Should().Equal("club-basquet-sama", "escola-harmonia");
        TenantAccess.VisibleTenantIds(user).Should().Equal("club-basquet-sama", "escola-harmonia");
        TenantAccess.CanAccess(user, "escola-harmonia").Should().BeTrue();
    }

    [Fact]
    public void VisibleTenantIds_DropTheAdminGroupAndInvalidNames()
    {
        var user = Principal(
            new Claim("cognito:groups", "admin"),
            new Claim("cognito:groups", "not_a_slug"),
            new Claim(ClaimTypes.Role, "club-basquet-sama"));

        TenantAccess.VisibleTenantIds(user).Should().Equal("club-basquet-sama");
    }

    [Fact]
    public void Groups_TrimsRoleClaims_SkipsBlanks_AndDeduplicatesInOrder()
    {
        var user = Principal(
            new Claim("cognito:groups", " club-basquet-sama "),
            new Claim(ClaimTypes.Role, "  "),
            new Claim(ClaimTypes.Role, " club-basquet-sama "),
            new Claim(ClaimTypes.Role, "escola-harmonia"));

        TenantAccess.Groups(user).Should().Equal("club-basquet-sama", "escola-harmonia");
    }

    [Fact]
    public void UnauthenticatedPrincipal_IsNotAnAdmin()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        TenantAccess.IsAuthenticated(user).Should().BeFalse();
        TenantAccess.IsAdmin(user).Should().BeFalse();
        TenantAccess.CanAccess(user, "club-basquet-sama").Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));
}
