using AutomaticEnvelopes.Api.Features.AdminAuth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

public class AdminCsrfMiddlewareTests
{
    private const string Allowed = "http://localhost:5173";

    [Theory]
    [InlineData("GET", "/api/admin/ingest/club")]
    [InlineData("POST", "/api/whatsapp/webhook")]
    [InlineData("POST", "/auth/token")]
    public async Task SkipsRequestsThatAreNotCookieAuthenticatedAdminWrites(string method, string path)
    {
        var called = false;
        var middleware = Middleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Request(method, path, cookie: "session");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task BearerRequest_SkipsTheOriginCheck()
    {
        var called = false;
        var middleware = Middleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Request("POST", "/api/admin/ingest/club", cookie: "session");
        context.Request.Headers.Authorization = "Bearer machine-token";

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Theory]
    [InlineData("POST", "/api/admin/ingest/club")]
    [InlineData("PATCH", "/tenants/club")]
    [InlineData("PUT", "/tenants/club")]
    [InlineData("DELETE", "/tenants/club/documents/1")]
    public async Task MissingOrForeignOrigin_Returns403(string method, string path)
    {
        var middleware = Middleware(_ => throw new InvalidOperationException("The endpoint should not run."));

        var missing = Request(method, path, cookie: "session");
        await middleware.InvokeAsync(missing);
        missing.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var foreign = Request(method, path, cookie: "session");
        foreign.Request.Headers.Origin = "https://evil.example";
        await middleware.InvokeAsync(foreign);
        foreign.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task AllowedOrigin_Continues()
    {
        var called = false;
        var middleware = Middleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Request("PATCH", "/tenants/club", cookie: "session");
        context.Request.Headers.Origin = Allowed;

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task RefererIsUsedOnlyWhenOriginIsMissing()
    {
        var called = false;
        var middleware = Middleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var fromReferer = Request("POST", "/api/admin/ingest/club", cookie: "session");
        fromReferer.Request.Headers.Referer = $"{Allowed}/admin/tenants";
        await middleware.InvokeAsync(fromReferer);
        called.Should().BeTrue();

        called = false;
        var foreignOriginWins = Request("POST", "/api/admin/ingest/club", cookie: "session");
        foreignOriginWins.Request.Headers.Origin = "https://evil.example";
        foreignOriginWins.Request.Headers.Referer = $"{Allowed}/admin/tenants";
        await middleware.InvokeAsync(foreignOriginWins);
        called.Should().BeFalse();
        foreignOriginWins.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    private static AdminCsrfMiddleware Middleware(RequestDelegate next) =>
        new(next, Options.Create(new AdminAuthOptions { AllowedOrigins = [Allowed] }), NullLogger<AdminCsrfMiddleware>.Instance);

    private static DefaultHttpContext Request(string method, string path, string? cookie)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{AdminAuthCookies.AccessToken}={cookie}";
        }

        return context;
    }
}
