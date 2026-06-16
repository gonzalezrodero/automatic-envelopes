using System.Text.Json.Serialization;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public record WhatsAppResponse(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("contacts")] List<WhatsAppContact> Contacts,
    [property: JsonPropertyName("messages")] List<WhatsAppMessageMetadata> Messages
);

public record WhatsAppContact(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("wa_id")] string WaId
);

public record WhatsAppMessageMetadata(
    [property: JsonPropertyName("id")] string Id
);
