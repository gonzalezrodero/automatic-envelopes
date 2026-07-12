using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api;
using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Marten;
using Npgsql;
using Wolverine;
using Wolverine.AmazonSqs;
using Wolverine.ErrorHandling;
using Wolverine.Marten;

namespace AutomaticEnvelopes.Api;

public static class Config
{
    public static IServiceCollection AddFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.AddChatFeature();
        services.AddWhatsAppWebhookFeature();
        services.AddKnowledgeFeature();
        services.AddWhatsAppDispatcherFeature(configuration);
        return services;
    }

    public static IServiceCollection AddDatabase(this IServiceCollection services, string connectionString)
    {
        services.AddNpgsqlDataSource(connectionString);

        services.CritterStackDefaults(opts =>
        {
            opts.Development.GeneratedCodeMode = TypeLoadMode.Auto;
            opts.Production.GeneratedCodeMode = TypeLoadMode.Static;
        });

        services.AddMarten(opts =>
        {
            opts.Connection(connectionString);
            opts.Events.StreamIdentity = StreamIdentity.AsString;
            opts.Events.TenancyStyle = TenancyStyle.Conjoined;

            opts.Storage.Add(new HnswIndexCustomizer());
            opts.Projections.Add(new ProcessedMessageProjection(), ProjectionLifecycle.Inline);

            opts.Schema.For<TenantProfile>().SingleTenanted();
            opts.Schema.For<ProcessedMessage>().MultiTenanted();
            opts.Schema.For<DocumentChunk>().MultiTenanted();
            opts.Schema.For<WhatsAppRateLimitTracker>().MultiTenanted();
        })
        .UseNpgsqlDataSource()
        .UseLightweightSessions()
        .IntegrateWithWolverine(cfg => cfg.UseWolverineManagedEventSubscriptionDistribution = true);

        return services;
    }

    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BedrockSettings>(configuration.GetSection("BedrockSettings"));
        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonBedrockRuntime>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IEmbeddingService, EmbeddingService>();
        return services;
    }

    public static ILoggingBuilder AddLogging(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = false;
            options.TimestampFormat = "HH:mm:ss ";
            options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
        });
        return logging;
    }

    public static IServiceCollection AddWolverine(this IServiceCollection services, IConfiguration config)
    {
        return services.AddWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(Program).Assembly);

            opts.CodeGeneration.AlwaysUseServiceLocationFor<IWhatsAppClient>();

            opts.Policies.AutoApplyTransactions();
            opts.Policies.OnException<ThrottlingException>()
                .RetryWithCooldown(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));

            // Optimistic Concurrency policy for the Spam Shield
            // If Lambdas collide while updating the rate limit tracker, wait a few ms and retry smoothly
            opts.Policies.OnException<ConcurrencyException>()
                .RetryWithCooldown(
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(250)
                );

            opts.Durability.Mode = DurabilityMode.Solo;

            var sqsUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_SQS");

            var sqs = opts.UseAmazonSqsTransport(sqsConfig =>
            {
                if (!string.IsNullOrEmpty(sqsUrl))
                {
                    sqsConfig.ServiceURL = sqsUrl;
                    sqsConfig.AuthenticationRegion = "us-west-1";
                }
            });

            sqs.SystemQueuesAreEnabled(false);

            opts.PublishMessage<ProcessWhatsAppMessage>()
                .ToSqsQueue("automatic-envelopes-messages-queue")
                .SendInline()
                .UseInterop(queue => new RawJsonSqsMapper());

            opts.PublishMessage<ChatWindowExpired>()
                .ToSqsQueue("automatic-envelopes-system-queue");

            if (config.GetValue<bool>("EnableSqsListener"))
            {
                opts.ListenToSqsQueue("automatic-envelopes-messages-queue")
                    .ReceiveRawJsonMessage(typeof(ProcessWhatsAppMessage));

                opts.ListenToSqsQueue("automatic-envelopes-system-queue");
            }
        });
    }

    public static WebApplication EnsureVectorExtensionExists(this WebApplication app, string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE OR REPLACE FUNCTION public.extract_embedding(data jsonb) 
            RETURNS vector IMMUTABLE PARALLEL SAFE AS $$
            BEGIN
                -- Ensure 'Embedding' matches your C# property name exactly
                RETURN CAST(data ->> 'Embedding' AS vector(512));
            EXCEPTION WHEN OTHERS THEN
                -- Fallback to a zero vector to avoid crashing the index
                RETURN array_fill(0, ARRAY[512])::vector;
            END;
            $$ LANGUAGE plpgsql;";

        cmd.ExecuteNonQuery();

        return app;
    }
}