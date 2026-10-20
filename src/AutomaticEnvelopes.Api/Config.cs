using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api;
using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.AdminAuth;
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
using System.Security.Claims;
using System.Threading.RateLimiting;
using Wolverine;
using Wolverine.AmazonSqs;
using Wolverine.ErrorHandling;
using Wolverine.Marten;

namespace AutomaticEnvelopes.Api;

public static class Config
{
    public static IServiceCollection AddFeatures(
        this IServiceCollection services,
        IConfiguration configuration,
        bool validateVerifyToken = false)
    {
        services.AddWhatsAppOptions(configuration, validateVerifyToken);
        services.AddChatFeature();
        services.AddWhatsAppWebhookFeature();
        services.AddKnowledgeFeature();
        services.AddWhatsAppDispatcherFeature(configuration);
        return services;
    }

    public static IServiceCollection AddRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy("AdminPolicy", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: AdminRateLimitKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            // Login, logout, GET /me, and /tenants stay off AdminPolicy so a portal session is not capped at 10/min.
            options.AddPolicy(AdminAuthPolicies.Auth, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: AuthRateLimitKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = AdminAuthPolicies.PermitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        return services;
    }

    public static string AdminRateLimitKey(HttpContext httpContext)
    {
        var caller = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(caller))
        {
            caller = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        return $"{caller}:{RateLimitPath(httpContext.Request.Path)}";
    }

    public static string AuthRateLimitKey(HttpContext httpContext)
    {
        return $"{httpContext.Connection.RemoteIpAddress}:{RateLimitPath(httpContext.Request.Path)}";
    }

    private static string RateLimitPath(PathString path)
    {
        return (path.Value ?? string.Empty).TrimEnd('/').ToLowerInvariant();
    }

    public static IServiceCollection AddAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        return services.AddAdminPortalAuth(configuration);
    }

    public static IServiceCollection AddCustomAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(TenantAccess.PlatformAdminPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => TenantAccess.IsAdmin(context.User));
            })
            .AddPolicy("TenantAdmin", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    if (context.Resource is not HttpContext httpContext)
                    {
                        return false;
                    }

                    var tenantId = httpContext.Request.RouteValues["tenantId"]?.ToString();
                    return !string.IsNullOrEmpty(tenantId) && TenantAccess.CanAccess(context.User, tenantId);
                });
            });

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
            opts.Schema.For<KnowledgeFile>().MultiTenanted();
            opts.Schema.For<AnonymizedChat>().MultiTenanted();
            opts.Schema.For<UnansweredQuestion>().MultiTenanted();
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
                .ToSqsQueue("automatic-envelopes-system-queue")
                .SendInline()
                .UseInterop(queue => new RawJsonSqsMapper());

            if (config.GetValue<bool>("EnableSqsListener"))
            {
                opts.ListenToSqsQueue("automatic-envelopes-messages-queue")
                    .ReceiveRawJsonMessage(typeof(ProcessWhatsAppMessage));

                opts.ListenToSqsQueue("automatic-envelopes-system-queue")
                    .ReceiveRawJsonMessage(typeof(ChatWindowExpired));
            }
        });
    }

    public static WebApplication EnsureVectorExtensionExists(this WebApplication app, string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        VectorSchema.Ensure(conn);
        return app;
    }
}