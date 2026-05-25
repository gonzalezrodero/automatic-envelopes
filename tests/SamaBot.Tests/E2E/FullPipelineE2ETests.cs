using Alba;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SamaBot.Api.Core.Entities;
using SamaBot.Api.Core.Events;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SamaBot.Tests.E2E;

[Collection("Integration")]
public class FullPipelineE2ETests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task FullRAGJourney_FromIngestionToAIResponse()
    {
        var tenant = $"club-{Guid.NewGuid():N}";
        var botPhoneId = $"bot-{Guid.NewGuid():N}";
        await fixture.SeedTenantAsync(tenant, botPhoneId);

        // --- 1. Arrange: Data Ingestion ---
        var tempPdfPath = Path.Combine(Path.GetTempPath(), $"test_knowledge_{Guid.NewGuid()}.pdf");
        var secretInfo = "The secret access code for SamaBot is 998877.";
        CreateTestPdf(tempPdfPath, secretInfo);

        await fixture.Host.Scenario(s =>
        {
            s.Post.Url($"/api/admin/ingest/{tenant}");

            var fileContent = new ByteArrayContent(File.ReadAllBytes(tempPdfPath));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            var multipart = new MultipartFormDataContent
            {
                { fileContent, "file", Path.GetFileName(tempPdfPath) }
            };

            var stream = multipart.ReadAsStream();
            s.ConfigureHttpContext(context =>
            {
                context.Request.ContentType = multipart.Headers.ContentType?.ToString();
                context.Request.ContentLength = stream.Length;
                context.Request.Body = stream;
            });

            s.StatusCodeShouldBeOk();
        });

        // FIX: Wait safely until background ingestion tasks generate the chunks in the DB
        await WaitForConditionAsync(async () =>
        {
            using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
            var chunks = await session.Query<DocumentChunk>().ToListAsync();
            return chunks.Any();
        }, "Document chunks were not generated in time.");

        // --- 2. Arrange: Webhook Payload ---
        var testPhoneNumber = $"{Random.Shared.Next(600000000, 699999999)}";
        var payload = $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [ { "changes": [ { "value": {
            "metadata": { "phone_number_id": "{{botPhoneId}}" },
            "messages": [ { "from": "{{testPhoneNumber}}", "id": "wamid.RAG_TEST", "timestamp": "1603059201", "text": { "body": "What is the secret code?" }, "type": "text" } ]
          } } ] } ]
        }
        """;

        var signature = GenerateSignature(payload, "integration_test_secret");

        // --- 3. Act ---
        await fixture.Host.Scenario(s =>
        {
            s.Post.Text(payload).ContentType("application/json").ToUrl("/api/whatsapp/webhook");
            s.WithRequestHeader("X-Hub-Signature-256", signature);
            s.StatusCodeShouldBeOk();
        });

        // --- 4. Assert ---
        // FIX: Poll Marten until the SQS background worker finishes processing the webhook
        await WaitForConditionAsync(async () =>
        {
            using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
            var evts = await session.Events.FetchStreamAsync(testPhoneNumber);
            return evts.Any();
        }, "The event stream was never populated by the webhook.");

        using var assertSession = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var streamEvents = await assertSession.Events.FetchStreamAsync(testPhoneNumber);

        streamEvents.Should().NotBeEmpty("The event stream should have been populated by the webhook.");

        var received = streamEvents.Select(e => e.Data).OfType<MessageReceived>().FirstOrDefault();
        received.Should().NotBeNull();
        received!.TenantId.Should().Be(tenant);

        var reply = streamEvents.Select(e => e.Data).OfType<ReplyGenerated>().FirstOrDefault();
        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("998877");

        if (File.Exists(tempPdfPath)) File.Delete(tempPdfPath);
    }

    [Fact]
    public async Task Webhook_Idempotency_DuplicateMessages_AreIgnored()
    {
        var tenant = $"club-idemp-{Guid.NewGuid():N}";
        var botPhoneId = $"bot-idemp-{Guid.NewGuid():N}";
        await fixture.SeedTenantAsync(tenant, botPhoneId);

        var testPhoneNumber = $"{Random.Shared.Next(700000000, 799999999)}";
        var messageId = $"wamid.IDEMP_{Guid.NewGuid():N}";

        var payload = $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [ { "changes": [ { "value": {
            "metadata": { "phone_number_id": "{{botPhoneId}}" },
            "messages": [ { "from": "{{testPhoneNumber}}", "id": "{{messageId}}", "timestamp": "1603059201", "text": { "body": "Idempotency test!" }, "type": "text" } ]
          } } ] } ]
        }
        """;

        var signature = GenerateSignature(payload, "integration_test_secret");

        // --- Act 1: First Message ---
        await fixture.Host.Scenario(s =>
        {
            s.Post.Text(payload).ContentType("application/json").ToUrl("/api/whatsapp/webhook");
            s.WithRequestHeader("X-Hub-Signature-256", signature);
            s.StatusCodeShouldBeOk();
        });

        // FIX: Ensure the first message is fully saved in the DB before sending the duplicate
        await WaitForConditionAsync(async () =>
        {
            using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
            var evts = await session.Events.FetchStreamAsync(testPhoneNumber);
            return evts.Any(e => e.Data is MessageReceived mr && mr.MessageId == messageId);
        }, "First message was never processed by the background worker.");

        // --- Act 2: Duplicate Message ---
        await fixture.Host.Scenario(s =>
        {
            s.Post.Text(payload).ContentType("application/json").ToUrl("/api/whatsapp/webhook");
            s.WithRequestHeader("X-Hub-Signature-256", signature);
            s.StatusCodeShouldBeOk();
        });

        // Give Wolverine time to poll SQS, attempt to process, and reject the duplicate
        await Task.Delay(2000);

        // --- Assert ---
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var streamEvents = await session.Events.FetchStreamAsync(testPhoneNumber);

        streamEvents.Select(e => e.Data).OfType<MessageReceived>().Count(x => x.MessageId == messageId)
            .Should().Be(1, "The duplicate message should have been ignored by the handler.");
    }

    [Fact]
    public async Task GdprDeletion_WhenUserRequestsDeletion_StreamIsRemovedAndDataAnonymized()
    {
        var tenant = $"club-gdpr-{Guid.NewGuid():N}";
        var botPhoneId = $"bot-gdpr-{Guid.NewGuid():N}";
        await fixture.SeedTenantAsync(tenant, botPhoneId);

        var testPhoneNumber = $"{Random.Shared.Next(800000000, 899999999)}";

        // --- 1. Arrange: Create initial chat history ---
        // Send a normal message first to ensure the stream exists in Marten
        var payload1 = $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [ { "changes": [ { "value": {
            "metadata": { "phone_number_id": "{{botPhoneId}}" },
            "messages": [ { "from": "{{testPhoneNumber}}", "id": "wamid.MSG_NORMAL", "timestamp": "1603059201", "text": { "body": "Hello, how are you?" }, "type": "text" } ]
          } } ] } ]
        }
        """;
        var sig1 = GenerateSignature(payload1, "integration_test_secret");

        await fixture.Host.Scenario(s =>
        {
            s.Post.Text(payload1).ContentType("application/json").ToUrl("/api/whatsapp/webhook");
            s.WithRequestHeader("X-Hub-Signature-256", sig1);
            s.StatusCodeShouldBeOk();
        });

        // Wait for the first message to be processed and the stream to be created
        await WaitForConditionAsync(async () =>
        {
            using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
            var evts = await session.Events.FetchStreamAsync(testPhoneNumber);
            return evts.Any();
        }, "The initial message was not processed correctly.");

        // --- 2. Act: Send "BORRAR DATOS" ---
        var payload2 = $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [ { "changes": [ { "value": {
            "metadata": { "phone_number_id": "{{botPhoneId}}" },
            "messages": [ { "from": "{{testPhoneNumber}}", "id": "wamid.MSG_DELETE", "timestamp": "1603059202", "text": { "body": "BORRAR DATOS" }, "type": "text" } ]
          } } ] } ]
        }
        """;
        var sig2 = GenerateSignature(payload2, "integration_test_secret");

        await fixture.Host.Scenario(s =>
        {
            s.Post.Text(payload2).ContentType("application/json").ToUrl("/api/whatsapp/webhook");
            s.WithRequestHeader("X-Hub-Signature-256", sig2);
            s.StatusCodeShouldBeOk();
        });

        // --- 3. Assert: Verify Deletion and Anonymization ---
        // Wait until the stream is empty (physical deletion)
        await WaitForConditionAsync(async () =>
        {
            using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
            var evts = await session.Events.FetchStreamAsync(testPhoneNumber);
            return !evts.Any(); // We want this to return true when the list is empty
        }, "The chat history was not deleted from Marten.");

        // Verify that the anonymized conversation document has been saved
        using var assertSession = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var anonymizedDocs = await assertSession.Query<AnonymizedChat>().ToListAsync();

        anonymizedDocs.Should().NotBeEmpty("An anonymized chat record should have been saved.");
    }

    private static string GenerateSignature(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static void CreateTestPdf(string path, string content)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddText(content, 10, new PdfPoint(25, 800), font);
        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>
    /// Safely polls the database until a condition is met. Perfect for E2E tests with external async brokers.
    /// </summary>
    private static async Task WaitForConditionAsync(Func<Task<bool>> condition, string timeoutMessage, int timeoutSeconds = 15)
    {
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < timeout)
        {
            if (await condition()) return;
            await Task.Delay(300); // Check every 300ms
        }
        throw new TimeoutException(timeoutMessage);
    }
}