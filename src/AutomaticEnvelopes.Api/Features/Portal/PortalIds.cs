using System.Security.Cryptography;
using System.Text;

namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalIds
{
    /// <summary>
    /// Stream keys are WhatsApp numbers. Those numbers are often digits only, which
    /// also matches a tenant id, so the portal path id is always a hash.
    /// </summary>
    public static string ForStream(string streamKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(streamKey));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
