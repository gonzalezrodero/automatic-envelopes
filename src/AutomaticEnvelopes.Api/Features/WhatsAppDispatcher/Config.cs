using Amazon.SimpleSystemsManagement;
using AutomaticEnvelopes.Api.Common.Configuration;
using Microsoft.Extensions.Options;
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

        services.AddAWSService<IAmazonSimpleSystemsManagement>();
        services.AddSingleton<IWhatsAppAccessTokenSource>(provider =>
        {
            // Lambdas set SSM_PATH_WHATSAPP. Without it, send with the configured shared token.
            var readTenantTokens = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSM_PATH_WHATSAPP"));
            return new WhatsAppAccessTokenSource(
                readTenantTokens ? provider.GetRequiredService<IAmazonSimpleSystemsManagement>() : null,
                provider.GetRequiredService<IOptions<WhatsAppOptions>>(),
                provider.GetRequiredService<ILogger<WhatsAppAccessTokenSource>>());
        });

        return services;
    }
}
