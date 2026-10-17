using AutomaticEnvelopes.Api.Common;

namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalIds
{
    /// <summary>
    /// Stream keys are WhatsApp numbers. Those numbers are often digits only, which
    /// also matches a tenant id, so the portal path id is always a hash.
    /// </summary>
    public static string ForStream(string streamKey) => LogMask.StableId(streamKey);
}
