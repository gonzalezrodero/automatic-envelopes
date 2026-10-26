using Refit;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public static class Config
{
    public static IServiceCollection AddWhatsAppDispatcherFeature(this IServiceCollection services, IConfiguration config)
    {
        var baseUrl = config["WhatsApp:BaseUrl"]
                      ?? throw new InvalidOperationException("WhatsApp:BaseUrl is missing.");

        // Refit 16 builds this client at compile time. AddRefitClient still asks for the
        // reflection request builder, which the deployed Lambda does not ship.
        services.AddRefitGeneratedClient<IWhatsAppClient>()
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri(baseUrl);
            });

        return services;
    }
}