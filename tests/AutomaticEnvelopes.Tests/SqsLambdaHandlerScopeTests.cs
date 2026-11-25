using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using AutomaticEnvelopes.Api;
using AutomaticEnvelopes.Api.Common.Extensions;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using Wolverine;
using static Amazon.Lambda.SQSEvents.SQSEvent;

namespace AutomaticEnvelopes.Tests;

public class SqsLambdaHandlerScopeTests
{
    private const string MartenConnectionString =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

    private const string LambdaFunctionName = "automatic-envelopes-worker-scope-test";

    [Fact]
    public async Task FunctionHandler_EmptyBotId_ReturnsNoBatchFailuresUnderScopeValidation()
    {
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SECRET_ARN_MARTEN")).Should().BeTrue();
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSM_PATH_WHATSAPP")).Should().BeTrue();
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SECRET_ARN_USAGE")).Should().BeTrue();

        var previousLambdaName = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
        Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", LambdaFunctionName);
        try
        {
            DatabaseSchemaStartup.AppliesAtStartup().Should().BeFalse();

            using var host = BuildWorkerHost();
            var provider = host.Services;

            var resolveFromRoot = () => provider.GetRequiredService<IMessageBus>();
            resolveFromRoot.Should().Throw<InvalidOperationException>().WithMessage("*scoped*");

            var handler = new SqsLambdaHandler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<SqsLambdaHandler>>());

            var message = new ProcessWhatsAppMessage(
                "scope-check",
                "",
                "+15550000000",
                "ignored",
                DateTimeOffset.UnixEpoch);
            var sqsEvent = new SQSEvent
            {
                Records =
                [
                    new SQSMessage
                    {
                        MessageId = "scope-check",
                        Body = JsonSerializer.Serialize(message),
                        EventSourceArn = "arn:aws:sqs:eu-west-1:000000000000:messages-queue"
                    }
                ]
            };

            var result = await handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

            result.BatchItemFailures.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", previousLambdaName);
        }
    }

    private static IHost BuildWorkerHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.AddLogging();
        builder.AddAwsSecureConfiguration();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Marten"] = MartenConnectionString,
            ["EnableSqsListener"] = "false",
        });

        var connectionString = builder.Configuration.GetConnectionString("Marten");
        connectionString.Should().Be(MartenConnectionString);

        builder.Services.AddDatabase(connectionString!);
        builder.Services.AddAi(builder.Configuration);
        builder.Services.AddFeatures(builder.Configuration);
        builder.Services.AddWolverine(builder.Configuration);

        // HostApplicationBuilder has no UseDefaultServiceProvider. Same options:
        // validate scopes, and do not validate on build.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false,
        }));

        return builder.Build();
    }
}
