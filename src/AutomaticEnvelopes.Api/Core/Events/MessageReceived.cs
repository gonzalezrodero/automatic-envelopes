using System.Text.Json.Serialization;
using Wolverine.Persistence.Sagas;

namespace AutomaticEnvelopes.Api.Core.Events;

public static class ChatSagaIds
{
    public static string For(string tenantId, string phoneNumber) => $"{tenantId}:{phoneNumber}";
}

public record MessageReceived(
    string MessageId,
    string PhoneNumber,
    string Text,
    string TenantId,
    string BotPhoneNumberId,
    DateTimeOffset ReceivedAt)
{
    [JsonIgnore]
    [SagaIdentity]
    public string SagaId => ChatSagaIds.For(TenantId, PhoneNumber);
}
