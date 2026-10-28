using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;
using AutomaticEnvelopes.Api.Features.Chat;
using Wolverine;
using Wolverine.AmazonSqs;
using Wolverine.Util;

namespace AutomaticEnvelopes.Api;

/// <summary>
/// The system queue carries both the 10-second debounce expiry and the recovery ping.
/// Raw JSON has no Wolverine envelope header, so the listener tells them apart by body.
/// </summary>
public sealed class SystemQueueEnvelopeMapper : ISqsEnvelopeMapper
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string BuildMessageBody(Envelope envelope) => Encoding.UTF8.GetString(envelope.Data!);

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope) => [];

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        envelope.Data = Encoding.UTF8.GetBytes(messageBody);
        envelope.ContentType = "application/json";

        if (messageBody.Contains(RecoverStuckChatDebounceSagas.Kind, StringComparison.Ordinal))
        {
            envelope.MessageType = typeof(RecoverStuckChatDebounceSagas).ToMessageTypeName();
            envelope.Message = JsonSerializer.Deserialize<RecoverStuckChatDebounceSagas>(messageBody, Json);
            return;
        }

        envelope.MessageType = typeof(ChatWindowExpired).ToMessageTypeName();
        envelope.Message = JsonSerializer.Deserialize<ChatWindowExpired>(messageBody, Json);
    }
}
