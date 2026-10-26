using System.Security.Claims;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public static class TenantRequest
{
    public const string OrganizationInvalid = "Esa organización no es válida.";
    public const string OrganizationNotFound = "No encontramos esa organización.";
    public const string OrganizationForbidden = "No tienes acceso a esta organización.";

    public static IResult? Reject(ClaimsPrincipal user, string tenantId)
    {
        if (!TenantAccess.IsAuthenticated(user))
        {
            return Results.Unauthorized();
        }

        if (!TenantIds.IsValid(tenantId))
        {
            return Results.BadRequest(new { Error = OrganizationInvalid });
        }

        if (!TenantAccess.CanAccess(user, tenantId))
        {
            return Results.Json(
                new { Error = OrganizationForbidden },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    public static void NoStore(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
    }
}
