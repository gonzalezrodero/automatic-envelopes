namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook;

public static class Config
{
    public static IServiceCollection AddWhatsAppWebhookFeature(this IServiceCollection services)
    {
        services.AddScoped<IWhatsAppPayloadProcessor, WhatsAppPayloadProcessor>();
        return services;
    }
}
