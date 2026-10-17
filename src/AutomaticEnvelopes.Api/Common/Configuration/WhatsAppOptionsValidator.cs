using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Common.Configuration;

public sealed class WhatsAppOptionsValidator : IValidateOptions<WhatsAppOptions>
{
    public const string VerifyTokenRequired = "WhatsApp:VerifyToken must be configured.";

    public ValidateOptionsResult Validate(string? name, WhatsAppOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.VerifyToken))
        {
            return ValidateOptionsResult.Fail(VerifyTokenRequired);
        }

        return ValidateOptionsResult.Success;
    }
}

public static class WhatsAppOptionsSetup
{
    public static IServiceCollection AddWhatsAppOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<WhatsAppOptions>, WhatsAppOptionsValidator>();
        services.AddOptions<WhatsAppOptions>()
            .Bind(configuration.GetSection(WhatsAppOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}
