using System.Security.Claims;
using System.Text.Json;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.Usage;
using AutomaticEnvelopes.Tests.Extensions;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticEnvelopes.Tests.Features.Usage;

[Collection("Integration")]
public class MonthlyUsageIntegrationTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task Handler_CountsOneReply_EmailsEachThresholdOnce_ThenStopsCallingBedrock()
    {
        fixture.BedrockClientMock.Invocations.Clear();
        Drain();
        var tenantId = "u" + Guid.NewGuid().ToString("N");
        var bot = "9" + Guid.NewGuid().ToString("N")[..12];
        var phone = "34610" + Random.Shared.Next(100000, 999999);
        await fixture.SeedTenantAsync(tenantId, bot, name: "Club Prova", monthlyResponseLimit: 5, contactEmail: "club@example.com");
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();
        var month = UsageMonth.Current();
        var ct = TestContext.Current.CancellationToken;
        await using (var session = store.LightweightSession(tenantId))
        {
            session.Store(new TenantMonthUsage { Id = month, Responses = 3, InputTokens = 1, OutputTokens = 1 });
            session.Events.Append(phone, new MessageReceived("old", phone, "Hola", tenantId, bot, DateTimeOffset.UtcNow));
            session.Events.Append(phone, new ReplyGenerated("old-r", bot, phone, "Hola", tenantId));
            await session.SaveChangesAsync(ct);
        }

        var ask = new AnalyzeChatSession(phone, tenantId, bot, "¿Horario?");
        await fixture.Host.InvokeMessageAndWaitAsync(ask);
        await fixture.Host.InvokeMessageAndWaitAsync(ask);
        await using (var session = store.LightweightSession(tenantId))
        {
            var usage = await session.LoadAsync<TenantMonthUsage>(month, ct);
            usage!.Responses.Should().Be(5);
            usage.InputTokens.Should().Be(23);
            usage.OutputTokens.Should().Be(15);
            usage.Notified80.Should().BeTrue();
            usage.Notified100.Should().BeTrue();
        }

        var emails = Drain();
        emails.Should().HaveCount(2);
        emails[0].FromEmailAddress.Should().Be(IntegrationAppFixture.UsageFromAddress);
        emails[0].Destination.ToAddresses.Should().Equal("club@example.com");
        emails[0].Content.Simple.Subject.Data.Should().Contain("80%");
        emails[0].Content.Simple.Body.Text.Data.Should().Contain("Club Prova");
        emails[1].Content.Simple.Subject.Data.Should().Contain("Límite");

        var before = ConverseCount();
        await fixture.Host.InvokeMessageAndWaitAsync(ask);
        ConverseCount().Should().Be(before);
        await using (var session = store.LightweightSession(tenantId))
        {
            var stream = await session.Events.FetchStreamAsync(phone, token: ct);
            stream.Select(item => item.Data).OfType<ReplyGenerated>().Last().Text.Should().Be(UsageMessages.Spanish);
            (await session.LoadAsync<TenantMonthUsage>(month, ct))!.Responses.Should().Be(5);
        }

        Drain().Should().BeEmpty();
        await fixture.Host.InvokeMessageAndWaitAsync(new AnalyzeChatSession(phone, tenantId, bot, "BORRAR DATOS"));
        await using (var session = store.LightweightSession(tenantId))
            (await session.LoadAsync<TenantMonthUsage>(month, ct))!.Responses.Should().Be(5);

        var result = await fixture.Host.Scenario(s =>
        {
            s.Get.Url($"/tenants/{tenantId}/usage");
            s.StatusCodeShouldBe(200);
        });
        using var json = JsonDocument.Parse(result.ReadAsText());
        var root = json.RootElement;
        root.GetProperty("month").GetString().Should().Be(month);
        root.GetProperty("responses").GetInt32().Should().Be(5);
        root.GetProperty("limit").GetInt32().Should().Be(5);
        root.GetProperty("tokens").GetProperty("input").GetInt64().Should().Be(23);
        root.GetProperty("tokens").GetProperty("output").GetInt64().Should().Be(15);
        result.Context.Response.Headers.CacheControl.ToString().Should().Be("no-store");

        await fixture.Host.Scenario(s =>
        {
            s.Patch.Json(new UpdateTenantSettingsRequest { SystemPrompt = "persona", PrivacyPolicyUrl = "https://club.example/p", MonthlyResponseLimit = 20 }).ToUrl($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(200);
        });
        await using (var session = store.LightweightSession(tenantId))
        {
            var usage = await session.LoadAsync<TenantMonthUsage>(month, ct);
            usage!.Notified80.Should().BeFalse();
            usage.Notified100.Should().BeFalse();
            usage.Responses.Should().Be(5);
        }
    }

    [Fact]
    public async Task GetUsage_IsTenantScoped_AndDefaultsTheLimit()
    {
        var tenantId = "u" + Guid.NewGuid().ToString("N");
        var other = "u" + Guid.NewGuid().ToString("N");
        await fixture.SeedTenantAsync(tenantId, "8" + Guid.NewGuid().ToString("N")[..12]);
        await fixture.SeedTenantAsync(other, "8" + Guid.NewGuid().ToString("N")[..12]);
        var result = await fixture.Host.Scenario(s =>
        {
            s.Get.Url($"/tenants/{tenantId}/usage");
            s.StatusCodeShouldBe(200);
        });
        using var json = JsonDocument.Parse(result.ReadAsText());
        json.RootElement.GetProperty("responses").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("tokens").GetProperty("input").GetInt64().Should().Be(0);
        json.RootElement.GetProperty("limit").GetInt32().Should().Be(1000);
        json.RootElement.GetProperty("month").GetString().Should().Be(UsageMonth.Current());
        await fixture.Host.Scenario(s =>
        {
            s.RemoveClaim(ClaimTypes.Role);
            s.RemoveClaim("cognito:groups");
            s.WithClaim(new Claim(ClaimTypes.Role, other));
            s.Get.Url($"/tenants/{tenantId}/usage");
            s.StatusCodeShouldBe(403);
        });
    }

    private List<Amazon.SimpleEmailV2.Model.SendEmailRequest> Drain()
    {
        var emails = new List<Amazon.SimpleEmailV2.Model.SendEmailRequest>();
        while (fixture.SentEmails.TryDequeue(out var email))
            emails.Add(email);
        return emails;
    }

    private int ConverseCount() => fixture.BedrockClientMock.Invocations.Count(item => item.Method.Name == "ConverseAsync");
}
