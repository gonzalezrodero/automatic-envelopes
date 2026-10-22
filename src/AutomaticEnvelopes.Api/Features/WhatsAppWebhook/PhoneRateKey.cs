using System.Security.Cryptography;
using System.Text;

namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook;

public static class PhoneRateKey
{
    public const int MinSecretLength = 16;

    public static bool TryHash(string? tenantId, string? phone, string? secret, out string trackerId)
    {
        trackerId = string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(phone) || string.IsNullOrEmpty(secret) || secret.Length < MinSecretLength)
        {
            return false;
        }

        var payload = Encoding.UTF8.GetBytes(tenantId + "\n" + phone);
        trackerId = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();
        return true;
    }
}
