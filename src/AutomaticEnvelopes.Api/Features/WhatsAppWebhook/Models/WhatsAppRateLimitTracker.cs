using Marten.Schema;

namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;

[UseOptimisticConcurrency]
public class WhatsAppRateLimitTracker
{
    public string Id { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public DateTimeOffset WindowResetTime { get; set; }
    public Guid Version { get; set; }
}