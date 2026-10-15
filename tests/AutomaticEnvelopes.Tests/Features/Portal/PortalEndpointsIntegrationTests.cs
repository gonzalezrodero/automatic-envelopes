using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Portal;
using AwesomeAssertions;
using Marten;

namespace AutomaticEnvelopes.Tests.Features.Portal;

[Collection("Integration")]
public class PortalEndpointsIntegrationTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task DashboardAndConversations_ReadTheTenantStream_AndHideOtherTenants()
    {
        var tenantId = $"club-{Guid.NewGuid():N}";
        var other = $"club-{Guid.NewGuid():N}";
        var phone = $"34600{Random.Shared.Next(100000, 999999)}";
        await fixture.SeedTenantAsync(tenantId, $"phone-{Guid.NewGuid():N}");
        await fixture.SeedTenantAsync(other, $"phone-{Guid.NewGuid():N}");
        await AppendChatAsync(tenantId, phone, "¿A qué hora abre el club?");

        var list = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Get.Url($"/tenants/{tenantId}/conversations");
            s.StatusCodeShouldBe(200);
        });

        using var listed = JsonDocument.Parse(list.ReadAsText());
        listed.RootElement.GetArrayLength().Should().Be(1);
        var row = listed.RootElement[0];
        var id = row.GetProperty("id").GetString();
        id.Should().MatchRegex("^[a-f0-9]{32}$");
        id.Should().NotBe(phone);
        id.Should().Be(PortalIds.ForStream(phone));
        row.GetProperty("userPhone").GetString().Should().Be(phone);
        row.GetProperty("topic").GetString().Should().Be("¿A qué hora abre el club?");
        row.GetProperty("events").GetArrayLength().Should().Be(2);

        await fixture.Host.Scenario(s =>
        {
            AsTenant(s, other);
            s.Get.Url($"/tenants/{tenantId}/conversations");
            s.StatusCodeShouldBe(403);
        });

        var detail = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Get.Url($"/tenants/{tenantId}/conversations/{id}");
            s.StatusCodeShouldBe(200);
        });
        using var one = JsonDocument.Parse(detail.ReadAsText());
        one.RootElement.GetProperty("events")[1].GetProperty("type").GetString().Should().Be("ReplyGenerated");

        var dashboard = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Get.Url($"/tenants/{tenantId}/dashboard");
            s.StatusCodeShouldBe(200);
        });
        using var stats = JsonDocument.Parse(dashboard.ReadAsText());
        stats.RootElement.GetProperty("conversationCount").GetInt32().Should().Be(1);
        stats.RootElement.GetProperty("messagesThisWeek").GetInt32().Should().BeGreaterThan(0);
        stats.RootElement.GetProperty("activeUsers").GetInt32().Should().Be(1);
        stats.RootElement.GetProperty("activity").GetArrayLength().Should().Be(14);
        dashboard.Context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task Documents_UploadListAndDelete_KeepTheFileOnThatTenant()
    {
        var tenantId = $"club-{Guid.NewGuid():N}";
        await fixture.SeedTenantAsync(tenantId, $"phone-{Guid.NewGuid():N}");

        var upload = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Post.MultipartFormData(TextFile("horario.txt", "El club abre a las 9.")).ToUrl($"/tenants/{tenantId}/documents");
            s.StatusCodeShouldBe(201);
        });

        using var created = JsonDocument.Parse(upload.ReadAsText());
        var documentId = created.RootElement.GetProperty("id").GetString();
        created.RootElement.GetProperty("filename").GetString().Should().Be("horario.txt");
        created.RootElement.GetProperty("mime").GetString().Should().Be("text/plain");
        created.RootElement.GetProperty("status").GetString().Should().Be("indexed");
        created.RootElement.GetProperty("chunkCount").GetInt32().Should().Be(1);

        var list = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Get.Url($"/tenants/{tenantId}/documents");
            s.StatusCodeShouldBe(200);
        });
        using var listed = JsonDocument.Parse(list.ReadAsText());
        listed.RootElement.GetArrayLength().Should().Be(1);

        await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Delete.Url($"/tenants/{tenantId}/documents/{documentId}");
            s.StatusCodeShouldBe(204);
        });

        var after = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Get.Url($"/tenants/{tenantId}/documents");
            s.StatusCodeShouldBe(200);
        });
        using var empty = JsonDocument.Parse(after.ReadAsText());
        empty.RootElement.GetArrayLength().Should().Be(0);
    }

    private async Task AppendChatAsync(string tenantId, string phone, string text)
    {
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession(tenantId);
        var received = new MessageReceived(
            MessageId: $"m-{Guid.NewGuid():N}",
            PhoneNumber: phone,
            Text: text,
            TenantId: tenantId,
            BotPhoneNumberId: "bot",
            ReceivedAt: DateTimeOffset.UtcNow);
        var reply = new ReplyGenerated(
            MessageId: $"r-{Guid.NewGuid():N}",
            BotPhoneNumberId: "bot",
            PhoneNumber: phone,
            Text: "Abrimos a las nueve.",
            TenantId: tenantId);
        session.Events.StartStream(phone, received, reply);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MultipartFormDataContent TextFile(string name, string text)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", name);
        return content;
    }

    private static void AsTenant(Alba.Scenario scenario, string tenantId)
    {
        scenario.RemoveClaim(ClaimTypes.Role);
        scenario.RemoveClaim("cognito:groups");
        scenario.WithClaim(new Claim(ClaimTypes.Role, tenantId));
    }
}
