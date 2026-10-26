using System.Security.Claims;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public static class TenantRequest
{
    public const string OrganizationInvalid = "No reconocemos esa organización. Vuelve a entrar en el portal.";
    public const string OrganizationNotFound = "No encontramos esa organización. Comprueba que has entrado en la correcta.";
    public const string OrganizationForbidden = "No tienes acceso a esta organización. Entra con otra cuenta o pide acceso.";

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
