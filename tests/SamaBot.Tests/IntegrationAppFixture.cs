using Alba;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.RuntimeCompiler;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SamaBot.Api.Common.Configuration;
using SamaBot.Api.Core.Entities;
using SamaBot.Api.Features.Tenancy;
using SamaBot.Api.Features.WhatsAppDispatcher;
using SamaBot.Api.Features.WhatsAppWebhook;
using System.Text;
using Testcontainers.PostgreSql;

namespace SamaBot.Tests;

public class IntegrationAppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("samabot_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly IContainer _sqsContainer = new ContainerBuilder("softwaremill/elasticmq-native:latest")
        .WithPortBinding(9324, true)
        .Build();

    public IAlbaHost Host { get; private set; } = null!;

    public Mock<IAmazonBedrockRuntime> BedrockClientMock { get; } = new();
    public Mock<IWhatsAppClient> WhatsAppClientMock { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _sqsContainer.StartAsync());

        await Task.Delay(2000);

        var sqsPort = _sqsContainer.GetMappedPublicPort(9324);
        var sqsServiceUrl = $"http://127.0.0.1:{sqsPort}";

        var sqsConfig = new Amazon.SQS.AmazonSQSConfig { ServiceURL = sqsServiceUrl, AuthenticationRegion = "us-east-1" };
        using var sqsClient = new Amazon.SQS.AmazonSQSClient("dummy", "dummy", sqsConfig);

        await sqsClient.CreateQueueAsync("chatbot-messages-queue");
        await sqsClient.CreateQueueAsync("wolverine-dead-letter-queue");

        Environment.SetEnvironmentVariable("ConnectionStrings__Marten", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("BedrockSettings__ModelId", "dummy-model");

        Environment.SetEnvironmentVariable("EnableSqsListener", "true");

        Environment.SetEnvironmentVariable("AWS_REGION", "eu-west-1");
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "dummy");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "dummy");
        Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", "");
        Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL", sqsServiceUrl);
        Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_SQS", sqsServiceUrl);

        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseDefaultServiceProvider(options => options.ValidateScopes = false);

            builder.ConfigureServices(services =>
            {
                SetupMockResponses();

                services.Replace(ServiceDescriptor.Singleton(BedrockClientMock.Object));
                services.Replace(ServiceDescriptor.Singleton(WhatsAppClientMock.Object));

                services.AddSingleton<IAssemblyGenerator, AssemblyGenerator>();

                services.CritterStackDefaults(opts =>
                {
                    opts.Development.GeneratedCodeMode = TypeLoadMode.Auto;
                    opts.Production.GeneratedCodeMode = TypeLoadMode.Auto;
                });

                services.Configure<StoreOptions>(opts => {
                    opts.AutoCreateSchemaObjects = AutoCreate.All;

                    opts.Schema.For<DocumentChunk>().MultiTenanted();
                    opts.Schema.For<ProcessedMessage>().MultiTenanted();
                    opts.Schema.For<TenantProfile>().SingleTenanted();
                });

                services.Configure<WhatsAppOptions>(opts => {
                    opts.AccessToken = "integration_test_access_token";
                    opts.BaseUrl = "https://dummy-whatsapp-api.com";
                    opts.PhoneNumberId = "integration_test_phone_id";
                    opts.AppSecret = "integration_test_secret";
                    opts.VerifyToken = "integration_test_verify_token";
                });
            });
        });
    }

    private void SetupMockResponses()
    {
        WhatsAppClientMock.Setup(x => x.SendMessageAsync(It.IsAny<string>(), It.IsAny<WhatsAppTextRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WhatsAppResponse("ok", [], []));

        BedrockClientMock.Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InvokeModelRequest request, CancellationToken ct) =>
            {
                var requestJson = Encoding.UTF8.GetString(request.Body.ToArray());
                string jsonResponse;

                if (requestJson.Contains("inputText") || requestJson.Contains("texts") || (request.ModelId?.Contains("embed") ?? false))
                {
                    var vector512 = new float[512];
                    vector512[0] = 0.1f;
                    var vectorJson = System.Text.Json.JsonSerializer.Serialize(vector512);

                    jsonResponse = $$"""
                    {
                        "embedding": {{vectorJson}},
                        "embeddings": [{{vectorJson}}]
                    }
                    """;
                }
                else if (requestJson.Contains("language detection module"))
                {
                    jsonResponse = $$"""
                    {
                        "content": [ { "text": "es" } ]
                    }
                    """;
                }
                else if (requestJson.Contains("strict data privacy filter"))
                {
                    jsonResponse = $$"""
                    {
                        "content": [ { "text": "[NAME] ha solicitado el borrado." } ]
                    }
                    """;
                }
                else
                {
                    var responseText = requestJson.Contains("secret")
                        ? "Mocked AI Response: The secret access code is 998877."
                        : "Mocked AI Response: Soy SamaBot y esto es un test E2E.";

                    jsonResponse = $$"""
                    {
                        "content": [ { "text": "{{responseText}}" } ]
                    }
                    """;
                }

                return new InvokeModelResponse
                {
                    HttpStatusCode = System.Net.HttpStatusCode.OK,
                    Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonResponse)) { Position = 0 }
                };
            });
    }

    public async Task DisposeAsync()
    {
        if (Host != null) await Host.DisposeAsync();
        await _postgres.DisposeAsync();
        await _sqsContainer.DisposeAsync();
    }

    public async Task SeedTenantAsync(
        string tenantSlug,
        string botPhoneId,
        string systemPrompt = "You are a helpful test assistant.",
        string privacyPolicyUrl = "https://example.com/privacy")
    {
        using var session = Host.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        session.Store(new TenantProfile
        {
            Id = tenantSlug,
            BotPhoneNumberId = botPhoneId,
            SystemPrompt = systemPrompt,
            PrivacyPolicyUrl = privacyPolicyUrl
        });

        await session.SaveChangesAsync();
    }
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationAppFixture> { }