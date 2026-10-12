using System.Security.Cryptography;
using System.Text;
using AutomaticEnvelopes.Api.Features.Tenancy;

namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalIds
{
    /// <summary>
    /// Stream keys are phone numbers. The portal only accepts [a-z0-9-] in a path,
    /// so a phone becomes a stable 32-character hex id.
    /// </summary>
    public static string ForStream(string streamKey)
    {
        if (TenantIds.IsValid(streamKey))
        {
            return streamKey;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(streamKey));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
