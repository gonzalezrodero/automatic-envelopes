using System.Text.Json.Serialization;
using AutomaticEnvelopes.Api.Features.Tenancy;

namespace AutomaticEnvelopes.Api.Features.Usage;

public sealed class TenantUsageResponse
{
    [JsonPropertyName("month")]
    public required string Month { get; init; }

    [JsonPropertyName("responses")]
    public required int Responses { get; init; }

    [JsonPropertyName("tokens")]
    public required UsageTokenCounts Tokens { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    public static TenantUsageResponse Create(TenantProfile profile, TenantMonthUsage? usage, string month) => new()
    {
        Month = month,
        Responses = usage?.Responses ?? 0,
        Tokens = new UsageTokenCounts
        {
            Input = usage?.InputTokens ?? 0,
            Output = usage?.OutputTokens ?? 0
        },
        Limit = profile.EffectiveMonthlyResponseLimit
    };
}

public sealed class UsageTokenCounts
{
    [JsonPropertyName("input")]
    public required long Input { get; init; }

    [JsonPropertyName("output")]
    public required long Output { get; init; }
}
