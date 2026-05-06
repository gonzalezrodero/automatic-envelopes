using Microsoft.Extensions.Options;
using SamaBot.Api.Common.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SamaBot.Api.Features.WhatsAppWebhook;

public interface IWhatsAppPayloadProcessor
{
    Task<bool> IsSignatureValidAsync(HttpRequest request);
    Task<ProcessWhatsAppMessage?> ExtractMessageAsync(HttpRequest request);
}

public class WhatsAppPayloadProcessor : IWhatsAppPayloadProcessor
{
    private readonly WhatsAppOptions options;

    public WhatsAppPayloadProcessor(IOptions<WhatsAppOptions> options)
    {
        this.options = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(this.options.AppSecret);
    }

    public async Task<bool> IsSignatureValidAsync(HttpRequest request)
    {
        var signatureHeader = request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith("sha256=")) return false;

        var incomingSignature = signatureHeader["sha256=".Length..];

        var body = await ReadRequestBodyAsync(request);
        var expectedSignature = ComputeSignature(body);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(incomingSignature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedSignature)
        );
    }

    public async Task<ProcessWhatsAppMessage?> ExtractMessageAsync(HttpRequest request)
    {
        var body = await ReadRequestBodyAsync(request);
        return ParseJsonToMessage(body);
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();

        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        // Reset for downstream parsers
        request.Body.Position = 0;

        return body;
    }

    private string ComputeSignature(string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(options.AppSecret);
        var bodyBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(bodyBytes);

        return Convert.ToHexStringLower(hashBytes);
    }

    private static ProcessWhatsAppMessage? ParseJsonToMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("entry", out var entries) || entries.GetArrayLength() == 0) return null;

            var firstEntry = entries[0];
            if (!firstEntry.TryGetProperty("changes", out var changes) || changes.GetArrayLength() == 0) return null;

            var valueNode = changes[0].GetProperty("value");
            if (!valueNode.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0) return null;

            var messageNode = messages[0];

            var fromNumber = messageNode.GetProperty("from").GetString();
            var messageId = messageNode.GetProperty("id").GetString();
            var timestampStr = messageNode.GetProperty("timestamp").GetString();
            var botNumberId = valueNode.GetProperty("metadata").GetProperty("phone_number_id").GetString();

            if (!messageNode.TryGetProperty("text", out var textNode) || !textNode.TryGetProperty("body", out var bodyNode)) return null;

            var messageText = bodyNode.GetString();

            if (string.IsNullOrEmpty(fromNumber) || string.IsNullOrEmpty(messageText) || string.IsNullOrEmpty(messageId) || timestampStr == null || botNumberId == null)
            {
                return null;
            }

            var timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestampStr));
            return new ProcessWhatsAppMessage(messageId, botNumberId, fromNumber, messageText, timestamp);
        }
        catch (JsonException)
        {
            // Safely ignored: Invalid JSON payload
        }

        return null; // Not a standard incoming text message
    }
}