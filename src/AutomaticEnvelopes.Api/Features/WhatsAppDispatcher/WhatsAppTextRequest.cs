using System.Text.Json.Serialization;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public record WhatsAppTextRequest(
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("text")] WhatsAppMessageBody Text,
    [property: JsonPropertyName("messaging_product")] string MessagingProduct = "whatsapp",
    [property: JsonPropertyName("recipient_type")] string RecipientType = "individual",
    [property: JsonPropertyName("type")] string Type = "text"
);

public record WhatsAppMessageBody(
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("preview_url")] bool PreviewUrl = false
);