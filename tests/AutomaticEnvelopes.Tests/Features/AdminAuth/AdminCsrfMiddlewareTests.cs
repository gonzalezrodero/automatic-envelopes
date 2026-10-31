using AutomaticEnvelopes.Api.Features.AdminAuth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

public class AdminCsrfMiddlewareTests
{
    private const string Allowed = "http://localhost:5173";

    [Theory]
    [InlineData("GET", "/api/admin/ingest/club", null, null, null, 200)]
    [InlineData("POST", "/api/whatsapp/webhook", null, null, null, 200)]
    [InlineData("POST", "/auth/token", null, null, null, 403)]
    [InlineData("POST", "/auth/token", null, Allowed, null, 200)]
    [InlineData("POST", "/auth/token", null, "https://evil.example", null, 403)]
    [InlineData("POST", "/auth/logout", null, Allowed, null, 200)]
    [InlineData("POST", "/auth/logout", null, "https://evil.example", null, 403)]
    [InlineData("POST", "/api/admin/ingest/club", "Bearer machine-token", null, null, 200)]
    [InlineData("POST", "/api/admin/ingest/club", null, null, null, 403)]
    [InlineData("POST", "/api/admin/ingest/club", null, "https://evil.example", null, 403)]
    [InlineData("PATCH", "/tenants/club", null, null, null, 403)]
    [InlineData("PATCH", "/tenants/club", null, "https://evil.example", null, 403)]
    [InlineData("PUT", "/tenants/club", null, null, null, 403)]
    [InlineData("PUT", "/tenants/club", null, "https://evil.example", null, 403)]
    [InlineData("DELETE", "/tenants/club/documents/1", null, null, null, 403)]
    [InlineData("DELETE", "/tenants/club/documents/1", null, "https://evil.example", null, 403)]
    [InlineData("POST", "/api/admin/ingest/club", null, null, "https://evil.example/x", 403)]
    [InlineData("POST", "/api/admin/ingest/club", null, "http://localhost:5173.evil.example", null, 403)]
    [InlineData("PATCH", "/tenants/club", null, Allowed, null, 200)]
    [InlineData("POST", "/api/admin/ingest/club", null, null, "http://localhost:5173/admin", 200)]
    [InlineData("POST", "/api/admin/ingest/club", null, "https://evil.example", "http://localhost:5173/admin", 403)]
    public async Task CookieAuthenticatedAdminWrites_RequireAnAllowedOrigin(
        string method,
        string path,
        string? authorization,
        string? origin,
        string? referer,
        int status)
    {
        var middleware = new AdminCsrfMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new AdminAuthOptions { AllowedOrigins = [Allowed] }));
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Cookie = $"{AdminAuthCookies.AccessToken}=session";
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (referer is not null)
        {
            context.Request.Headers.Referer = referer;
        }

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(status);
    }

    [Theory]
    [InlineData("http://localhost:5173", 200)]
    [InlineData("https://admin.core-webhook.eu", 200)]
    [InlineData("https://evil.example", 403)]
    [InlineData(null, 403)]
    public async Task TokenExchange_RequiresAnAllowedOriginEvenWithoutASessionCookie(string? origin, int status)
    {
        var middleware = new AdminCsrfMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new AdminAuthOptions
            {
                AllowedOrigins = ["http://localhost:5173", "https://admin.core-webhook.eu"]
            }));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/auth/token";
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(status);
    }
}
