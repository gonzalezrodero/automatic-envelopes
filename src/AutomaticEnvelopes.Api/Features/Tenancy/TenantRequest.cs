using System.Security.Claims;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public static class TenantRequest
{
    public static IResult? Reject(ClaimsPrincipal user, string tenantId)
    {
        if (!TenantAccess.IsAuthenticated(user))
        {
            return Results.Unauthorized();
        }

        if (!TenantIds.IsValid(tenantId))
        {
            return Results.BadRequest(new { Error = "tenantId is not valid." });
        }

        if (!TenantAccess.CanAccess(user, tenantId))
        {
            return Results.Json(
                new { Error = "You do not have access to this organization." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    public static void NoStore(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
    }
}
