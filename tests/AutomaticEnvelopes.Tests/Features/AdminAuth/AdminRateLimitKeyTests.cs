using System.Net;
using System.Security.Claims;
using AutomaticEnvelopes.Api;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

public class AdminRateLimitKeyTests
{
    [Fact]
    public void Uses_the_jwt_sub_and_the_route()
    {
        var context = Context(new Claim("sub", "user-1"));

        Config.AdminRateLimitKey(context).Should().Be("user-1:/tenants/acme/documents");
    }

    [Fact]
    public void Uses_the_inbound_name_identifier_when_sub_was_mapped()
    {
        var context = Context(new Claim(ClaimTypes.NameIdentifier, "mapped-user"));

        Config.AdminRateLimitKey(context).Should().Be("mapped-user:/tenants/acme/documents");
    }

    [Fact]
    public void Uses_the_client_ip_when_there_is_no_subject()
    {
        var context = Context();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.8");

        Config.AdminRateLimitKey(context).Should().Be("203.0.113.8:/tenants/acme/documents");
    }

    private static DefaultHttpContext Context(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/tenants/acme/documents";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, claims.Length == 0 ? null : "Bearer"));
        return context;
    }
}
