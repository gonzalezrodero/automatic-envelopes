using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using AutomaticEnvelopes.Api.Common.Extensions;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;
using System.Text.Json;
using Wolverine;
using static Amazon.Lambda.SQSEvents.SQSBatchResponse;
using static Amazon.Lambda.SQSEvents.SQSEvent;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace AutomaticEnvelopes.Api;

public class SqsLambdaHandler
{
    private static readonly Lazy<IServiceProvider> services = new(BuildWorkerProvider);

    private readonly IMessageBus bus;
    private readonly ILogger<SqsLambdaHandler> logger;
    private readonly JsonSerializerOptions jsonOptions;

    public SqsLambdaHandler()
    {
        bus = services.Value.GetRequiredService<IMessageBus>();
        logger = services.Value.GetRequiredService<ILogger<SqsLambdaHandler>>();
        jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public SqsLambdaHandler(IMessageBus bus, ILogger<SqsLambdaHandler> logger)
    {
        this.bus = bus;
        this.logger = logger;
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

        var initLogger = host.Services.GetRequiredService<ILogger<SqsLambdaHandler>>();
        initLogger.LogInformation("AWS Lambda Worker cold start completed. Host initialized successfully.");

        return host.Services;
    }

    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var response = new SQSBatchResponse { BatchItemFailures = [] };
        logger.LogInformation("SQS Lambda Handler triggered. Processing a batch of {RecordCount} records.", sqsEvent.Records.Count);

        using var cts = new CancellationTokenSource();
        if (context != null && context.RemainingTime > TimeSpan.FromMilliseconds(500))
        {
            cts.CancelAfter(context.RemainingTime.Subtract(TimeSpan.FromMilliseconds(500)));
        }

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                if (IsSystemQueue(record))
                {
                    await ProcessSystemMessageAsync(record, cts.Token);
                }
                else
                {
                    await ProcessWhatsAppMessageAsync(record, cts.Token);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fatal error while processing SQS record. MessageId: {MessageId}. Adding to batch item failures.", record.MessageId);
                response.BatchItemFailures.Add(new BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        logger.LogInformation("Finished processing SQS batch. Failures: {FailureCount} out of {TotalCount}.", response.BatchItemFailures.Count, sqsEvent.Records.Count);
        return response;
    }

    private static bool IsSystemQueue(SQSMessage record)
    {
        return record.EventSourceArn?.Contains("system-queue") ?? false;
    }

    private async Task ProcessSystemMessageAsync(SQSMessage record, CancellationToken ct)
    {
        logger.LogInformation("Attempting to deserialize internal SYSTEM SQS record. MessageId: {MessageId}", record.MessageId);

        var expiredMessage = JsonSerializer.Deserialize<ChatWindowExpired>(record.Body, jsonOptions);

        if (expiredMessage != null && !string.IsNullOrEmpty(expiredMessage.PhoneNumber))
        {
            logger.LogInformation("Successfully deserialized ChatWindowExpired for {PhoneNumber}. Invoking Wolverine bus.", expiredMessage.PhoneNumber);
            await bus.InvokeAsync(expiredMessage, ct);
        }
        else
        {
            logger.LogWarning("Deserialization returned null for System SQS record. MessageId: {MessageId}.", record.MessageId);
        }
    }

    private async Task ProcessWhatsAppMessageAsync(SQSMessage record, CancellationToken ct)
    {
        logger.LogInformation("Attempting to deserialize WHATSAPP SQS record. MessageId: {MessageId}", record.MessageId);

        var message = JsonSerializer.Deserialize<ProcessWhatsAppMessage>(record.Body, jsonOptions);

        if (message != null && !string.IsNullOrEmpty(message.BotPhoneNumberId))
        {
            logger.LogInformation("Successfully deserialized WhatsApp SQS message. BotId: {BotPhoneNumberId}. Invoking Wolverine bus.", message.BotPhoneNumberId);
            await bus.InvokeAsync(message, ct);
        }
        else
        {
            logger.LogWarning("Deserialization returned null for WhatsApp SQS record. MessageId: {MessageId}.", record.MessageId);
        }
    }
}