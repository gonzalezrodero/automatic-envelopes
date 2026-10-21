using System.Text;
using System.Text.Json.Nodes;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

public class TenantProfileSerializationTests
{
    [Fact]
    public void DocumentWithoutEnabledTools_DeserializesAsNull_AndPortalShowsNone()
    {
        using var store = DocumentStore.For(opts =>
        {
            opts.Connection("Host=127.0.0.1;Port=1;Database=unused");
            opts.Schema.For<TenantProfile>().SingleTenanted();
        });

        var current = new TenantProfile
        {
            Id = "club-basquet-sama",
            BotPhoneNumberId = "109283746510293",
            SystemPrompt = "Persona",
            PrivacyPolicyUrl = "https://example.com/p",
            EnabledTools = ["calculate_family_campus_price"]
        };

        var node = JsonNode.Parse(store.Options.Serializer().ToJson(current))!.AsObject();
        node.Remove("EnabledTools");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var legacy = store.Options.Serializer().FromJson<TenantProfile>(stream);

        legacy.EnabledTools.Should().BeNull();
        legacy.SystemPrompt.Should().Be("Persona");
        TenantPortalProfile.From(legacy).EnabledTools.Should().BeEmpty();
    }
}
