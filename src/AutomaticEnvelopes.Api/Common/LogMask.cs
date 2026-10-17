using System.Security.Cryptography;
using System.Text;

namespace AutomaticEnvelopes.Api.Common;

/// <summary>
/// Stable identifiers for log properties that must not contain raw phone numbers.
/// <see cref="StableId"/> is the same prefix PortalIds.ForStream uses for portal
/// stream ids, so a log line can be correlated with a portal conversation without
/// writing the number.
/// </summary>
public static class LogMask
{
    public static string Phone(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return string.Empty;
        }

        return StableId(phoneNumber);
    }

    public static string StableId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
