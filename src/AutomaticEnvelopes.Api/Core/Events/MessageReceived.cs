using Wolverine.Persistence.Sagas;

namespace AutomaticEnvelopes.Api.Core.Events;

public record MessageReceived(
    string MessageId,
    [property: SagaIdentity] string PhoneNumber,
    string Text,
    string TenantId,
    string BotPhoneNumberId,
    DateTimeOffset ReceivedAt
);