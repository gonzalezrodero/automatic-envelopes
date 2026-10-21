using AutomaticEnvelopes.Api.Features.Chat.Tools;

namespace AutomaticEnvelopes.Api.Features.Chat;

public static class Config
{
    public static IServiceCollection AddChatFeature(this IServiceCollection services)
    {
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IPersonalDataFilter, ChatPersonalDataFilter>();
        services.AddScoped<IUnansweredQuestionDocuments, MartenUnansweredQuestionDocuments>();
        services.AddScoped<IUnansweredInbox, UnansweredInbox>();
        services.AddTransient<IToolExecutionService, ToolExecutionService>();

        return services;
    }
}
