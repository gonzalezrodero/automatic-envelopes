using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;

namespace AutomaticEnvelopes.Api.Features.Knowledge;

public static class Config
{
    public static IServiceCollection AddKnowledgeFeature(this IServiceCollection services)
    {
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddTransient<IKnowledgeIngestionService, KnowledgeIngestionService>();
        services.AddTransient<IDocumentExtractor, TextDocumentExtractor>();
        services.AddTransient<IDocumentExtractor, PdfDocumentExtractor>();
        
        return services;
    }
}