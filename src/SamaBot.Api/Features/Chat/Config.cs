using SamaBot.Api.Features.Chat.Tools;
using SamaBot.Api.Features.Tenants.Sama.CampusPricing;

namespace SamaBot.Api.Features.Chat;

public static class Config
{
    public static IServiceCollection AddChatFeature(this IServiceCollection services)
    {
        services.AddScoped<IChatService, ChatService>();
        services.AddTransient<IToolExecutionService, ToolExecutionService>();
        services.AddTransient<IBedrockTool, CampusCalculatorTool>();

        return services;
    }
}