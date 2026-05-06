using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using SamaBot.Api.Common.Extensions;
using SamaBot.Api.Features.WhatsAppWebhook;
using System.Text.Json;
using Wolverine;
using static Amazon.Lambda.SQSEvents.SQSBatchResponse;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SamaBot.Api;

public class SqsLambdaHandler
{
    private static readonly Lazy<IServiceProvider> services = new(BuildWorkerProvider);

    private readonly IMessageBus bus;
    private readonly JsonSerializerOptions jsonOptions;

    public SqsLambdaHandler()
    {
        bus = services.Value.GetRequiredService<IMessageBus>();
        jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public SqsLambdaHandler(IMessageBus bus)
    {
        this.bus = bus;
        this.jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    private static IServiceProvider BuildWorkerProvider()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.AddLogging();
        builder.AddAwsSecureConfiguration();

        var conn = builder.Configuration.GetConnectionString("Marten")!;

        builder.Services.AddDatabase(conn);
        builder.Services.AddAi(builder.Configuration);
        builder.Services.AddFeatures(builder.Configuration);
        builder.Services.AddWolverine(builder.Configuration);

        var host = builder.Build();
        host.Start();

        return host.Services;
    }

    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var response = new SQSBatchResponse { BatchItemFailures = [] };

        using var cts = new CancellationTokenSource();
        if (context != null && context.RemainingTime > TimeSpan.FromMilliseconds(500))
        {
            cts.CancelAfter(context.RemainingTime.Subtract(TimeSpan.FromMilliseconds(500)));
        }

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                var message = JsonSerializer.Deserialize<ProcessWhatsAppMessage>(record.Body, jsonOptions);
                if (message != null)
                {
                    await bus.InvokeAsync(message, cts.Token);
                }
            }
            catch (Exception)
            {
                response.BatchItemFailures.Add(new BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return response;
    }
}