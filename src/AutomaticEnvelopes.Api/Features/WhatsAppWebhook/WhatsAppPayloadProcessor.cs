using AutomaticEnvelopes.Api.Common;
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
    Task<IReadOnlyList<ProcessWhatsAppMessage>> ExtractMessageAsync(HttpRequest request);
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

    public async Task<IReadOnlyList<ProcessWhatsAppMessage>> ExtractMessageAsync(HttpRequest request)
    {
        var body = await ReadRequestBodyAsync(request);
        return ParseJsonToMessages(Encoding.UTF8.GetString(body), logger);
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();

        request.Body.Position = 0;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        request.Body.Position = 0;

        return buffer.ToArray();
    }

    private string ComputeSignature(ReadOnlySpan<byte> payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(options.AppSecret);
        var hashBytes = HMACSHA256.HashData(keyBytes, payload);

        return Convert.ToHexStringLower(hashBytes);
    }

    private static IReadOnlyList<ProcessWhatsAppMessage> ParseJsonToMessages(string body, ILogger logger)
    {
        var parsed = new List<ProcessWhatsAppMessage>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("entry", out var entries) || entries.GetArrayLength() == 0)
            {
                logger.LogDebug("Payload ignored: missing 'entry' array. Not a standard webhook event.");
                return parsed;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.GetArrayLength() == 0)
                {
                    logger.LogDebug("Payload ignored: missing 'changes' array within entry.");
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var valueNode))
                    {
                        continue;
                    }

                    if (!valueNode.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
                    {
                        logger.LogDebug("Payload ignored: missing 'messages' array. Likely a status update or read receipt.");
                        continue;
                    }

                    foreach (var messageNode in messages.EnumerateArray())
                    {
                        var message = ReadTextMessage(messageNode, valueNode, logger);
                        if (message != null)
                        {
                            parsed.Add(message);
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse incoming WhatsApp webhook payload as valid JSON.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error occurred while parsing WhatsApp message payload.");
        }

        return parsed;
    }

    private static ProcessWhatsAppMessage? ReadTextMessage(JsonElement messageNode, JsonElement valueNode, ILogger logger)
    {
        var fromNumber = messageNode.TryGetProperty("from", out var fromNode) ? fromNode.GetString() : null;
        var messageId = messageNode.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
        var timestampStr = messageNode.TryGetProperty("timestamp", out var timestampNode) ? timestampNode.GetString() : null;
        var botNumberId = valueNode.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("phone_number_id", out var phoneNode)
            ? phoneNode.GetString()
            : null;

        if (!messageNode.TryGetProperty("text", out var textNode) || !textNode.TryGetProperty("body", out var bodyNode))
        {
            var msgType = messageNode.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : "unknown";
            logger.LogInformation("Received non-text message type '{MessageType}' from {FromNumber}. Ignoring.", msgType, LogMask.Phone(fromNumber));
            return null;
        }

        var messageText = bodyNode.GetString();
        if (string.IsNullOrEmpty(fromNumber) || string.IsNullOrEmpty(messageText) || string.IsNullOrEmpty(messageId) || timestampStr == null || botNumberId == null)
        {
            logger.LogWarning("Message payload is missing essential fields (From, Text, MessageId, Timestamp, or BotId). Payload structure might have changed.");
            return null;
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestampStr));
        logger.LogInformation("Successfully parsed WhatsApp text message. MessageId: {MessageId}, From: {FromNumber}, BotPhoneNumberId: {BotPhoneNumberId}", messageId, LogMask.Phone(fromNumber), botNumberId);
        return new ProcessWhatsAppMessage(messageId, botNumberId, fromNumber, messageText, timestamp);
    }
}