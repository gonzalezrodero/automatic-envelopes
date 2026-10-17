using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Common.Configuration;

public class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";
    public const string VerifyTokenRequired = "WhatsApp:VerifyToken must be configured.";

    public string VerifyToken { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string PhoneNumberId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
}

public static class WhatsAppOptionsRegistration
{
    public static IServiceCollection AddWhatsAppOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        bool validateVerifyToken = false)
    {
        var options = services.AddOptions<WhatsAppOptions>()
            .Bind(configuration.GetSection(WhatsAppOptions.SectionName));

        if (validateVerifyToken)
        {
            options.Validate(
                    static options => !string.IsNullOrWhiteSpace(options.VerifyToken),
                    WhatsAppOptions.VerifyTokenRequired)
                .ValidateOnStart();
        }

        return services;
    }
}