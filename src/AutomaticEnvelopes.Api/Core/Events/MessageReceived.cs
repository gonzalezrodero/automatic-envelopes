using System.Text.Json.Serialization;
using Wolverine.Persistence.Sagas;

namespace AutomaticEnvelopes.Api.Core.Events;

/// <summary>
/// Saga document id for one debounce window. Tenant ids are <c>[a-z0-9-]</c> and
/// WhatsApp numbers are digits, so the colon cannot collide with either part or
/// with a legacy document whose id is the phone number alone.
/// </summary>
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
    /// <summary>
    /// Wolverine reads this member (<c>[SagaIdentity]</c> wins over a property named
    /// <c>Id</c> or <c>SagaId</c>) and loads <c>ChatDebounceSaga</c> by the value.
    /// </summary>
    [JsonIgnore]
    [SagaIdentity]
    public string SagaId => ChatSagaIds.For(TenantId, PhoneNumber);
}
