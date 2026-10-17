using System.Security.Cryptography;
using System.Text;

namespace AutomaticEnvelopes.Api.Common;

public static class LogMask
{
    public static string Phone(string? phoneNumber) =>
        string.IsNullOrWhiteSpace(phoneNumber) ? string.Empty : Id(phoneNumber);

    public static string Id(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();
}
