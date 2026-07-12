using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook;

public interface IWhatsAppPayloadProcessor
{
    Task<bool> IsSignatureValidAsync(HttpRequest request);
    Task<ProcessWhatsAppMessage?> ExtractMessageAsync(HttpRequest request);
}

public class WhatsAppPayloadProcessor : IWhatsAppPayloadProcessor
{
    private readonly WhatsAppOptions options;
    private readonly ILogger<WhatsAppPayloadProcessor> logger;

    public WhatsAppPayloadProcessor(IOptions<WhatsAppOptions> options, ILogger<WhatsAppPayloadProcessor> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        ArgumentException.ThrowIfNullOrWhiteSpace(this.options.AppSecret);
    }

    public async Task<bool> IsSignatureValidAsync(HttpRequest request)
    {
        var signatureHeader = request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith("sha256="))
        {
            logger.LogWarning("Missing or malformed 'X-Hub-Signature-256' header. Expected format 'sha256=...'");
            return false;
        }

        var incomingSignature = signatureHeader["sha256=".Length..];

        var body = await ReadRequestBodyAsync(request);
        var expectedSignature = ComputeSignature(body);

        var isValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(incomingSignature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedSignature)
        );

        if (!isValid)
        {
            logger.LogWarning("WhatsApp signature validation failed. Computed HMAC does not match incoming signature.");
        }
        else
        {
            logger.LogDebug("WhatsApp signature validated successfully.");
        }

        return isValid;
    }

    public async Task<ProcessWhatsAppMessage?> ExtractMessageAsync(HttpRequest request)
    {
        var body = await ReadRequestBodyAsync(request);
        return ParseJsonToMessage(body, logger);
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

    private static ProcessWhatsAppMessage? ParseJsonToMessage(string body, ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("entry", out var entries) || entries.GetArrayLength() == 0)
            {
                logger.LogDebug("Payload ignored: missing 'entry' array. Not a standard webhook event.");
                return null;
            }

            var firstEntry = entries[0];
            if (!firstEntry.TryGetProperty("changes", out var changes) || changes.GetArrayLength() == 0)
            {
                logger.LogDebug("Payload ignored: missing 'changes' array within entry.");
                return null;
            }

            var valueNode = changes[0].GetProperty("value");
            if (!valueNode.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
            {
                // Extremely common scenario: read receipts, delivery statuses, or system updates.
                logger.LogDebug("Payload ignored: missing 'messages' array. Likely a status update or read receipt.");
                return null;
            }

            var messageNode = messages[0];

            var fromNumber = messageNode.GetProperty("from").GetString();
            var messageId = messageNode.GetProperty("id").GetString();
            var timestampStr = messageNode.GetProperty("timestamp").GetString();
            var botNumberId = valueNode.GetProperty("metadata").GetProperty("phone_number_id").GetString();

            if (!messageNode.TryGetProperty("text", out var textNode) || !textNode.TryGetProperty("body", out var bodyNode))
            {
                // Logs when a user sends an image, audio, or document
                var msgType = messageNode.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : "unknown";
                logger.LogInformation("Received non-text message type '{MessageType}' from {FromNumber}. Ignoring.", msgType, fromNumber);
                return null;
            }

            var messageText = bodyNode.GetString();

            if (string.IsNullOrEmpty(fromNumber) || string.IsNullOrEmpty(messageText) || string.IsNullOrEmpty(messageId) || timestampStr == null || botNumberId == null)
            {
                logger.LogWarning("Message payload is missing essential fields (From, Text, MessageId, Timestamp, or BotId). Payload structure might have changed.");
                return null;
            }

            var timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestampStr));

            logger.LogInformation("Successfully parsed WhatsApp text message. MessageId: {MessageId}, From: {FromNumber}, BotPhoneNumberId: {BotPhoneNumberId}", messageId, fromNumber, botNumberId);

            return new ProcessWhatsAppMessage(messageId, botNumberId, fromNumber, messageText, timestamp);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse incoming WhatsApp webhook payload as valid JSON.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error occurred while parsing WhatsApp message payload.");
        }

        return null;
    }
}