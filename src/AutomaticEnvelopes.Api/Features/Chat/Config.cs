using AutomaticEnvelopes.Api.Features.Chat.Tools;
using AutomaticEnvelopes.Api.Features.Tenants.Sama.CampusPricing;

namespace AutomaticEnvelopes.Api.Features.Chat;

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