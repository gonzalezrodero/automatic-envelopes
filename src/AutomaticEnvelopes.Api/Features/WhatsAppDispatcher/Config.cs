using Refit;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public static class Config
{
    public static IServiceCollection AddWhatsAppDispatcherFeature(this IServiceCollection services, IConfiguration config)
    {
        var baseUrl = config["WhatsApp:BaseUrl"]
                      ?? throw new InvalidOperationException("WhatsApp:BaseUrl is missing.");

        services.AddRefitClient<IWhatsAppClient>()
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri(baseUrl);
            });

        return services;
    }
}