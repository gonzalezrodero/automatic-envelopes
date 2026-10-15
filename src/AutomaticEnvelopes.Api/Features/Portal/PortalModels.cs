using System.Text.Json.Serialization;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Knowledge;

namespace AutomaticEnvelopes.Api.Features.Portal;

public sealed class PortalConversationEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }
}

public sealed class PortalConversation
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("userPhone")]
    public string UserPhone { get; init; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    [JsonPropertyName("anonymized")]
    public bool Anonymized { get; init; }

    [JsonPropertyName("deletionRequestedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? DeletionRequestedAt { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<PortalConversationEvent> Events { get; init; }
}

public sealed class PortalConversationPreview
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("topic")]
    public required string Topic { get; init; }

    [JsonPropertyName("preview")]
    public required string Preview { get; init; }

    [JsonPropertyName("at")]
    public required string At { get; init; }

    [JsonPropertyName("anonymized")]
    public bool Anonymized { get; init; }

    [JsonPropertyName("phoneMasked")]
    public required string PhoneMasked { get; init; }
}

public sealed class PortalDayActivity
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("received")]
    public int Received { get; set; }

    [JsonPropertyName("replied")]
    public int Replied { get; set; }
}

public sealed class PortalTopicCount
{
    [JsonPropertyName("topic")]
    public required string Topic { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed class PortalDashboard
{
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("windowLabel")]
    public required string WindowLabel { get; init; }

    [JsonPropertyName("conversationCount")]
    public int ConversationCount { get; init; }

    [JsonPropertyName("messagesThisWeek")]
    public int MessagesThisWeek { get; init; }

    [JsonPropertyName("activeUsers")]
    public int ActiveUsers { get; init; }

    [JsonPropertyName("deletionRequests")]
    public int DeletionRequests { get; init; }

    [JsonPropertyName("topTopic")]
    public PortalTopicCount? TopTopic { get; init; }

    [JsonPropertyName("topics")]
    public required IReadOnlyList<PortalTopicCount> Topics { get; init; }

    [JsonPropertyName("activity")]
    public required IReadOnlyList<PortalDayActivity> Activity { get; init; }

    [JsonPropertyName("recent")]
    public required IReadOnlyList<PortalConversationPreview> Recent { get; init; }
}

public sealed class PortalUnansweredQuestion
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("conversationId")]
    public required string ConversationId { get; init; }

    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("askedAt")]
    public required DateTimeOffset AskedAt { get; init; }

    public static PortalUnansweredQuestion From(UnansweredQuestion question) => new()
    {
        Id = question.Id.ToString("D"),
        ConversationId = question.ConversationId,
        Question = question.Question,
        AskedAt = question.AskedAt
    };
}

public sealed class PortalAnsweredQuestion
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("answer")]
    public required string Answer { get; init; }

    [JsonPropertyName("answeredAt")]
    public required DateTimeOffset AnsweredAt { get; init; }

    public static PortalAnsweredQuestion From(UnansweredQuestion question) => new()
    {
        Id = question.Id.ToString("D"),
        Question = question.Question,
        Answer = question.Answer ?? string.Empty,
        AnsweredAt = question.AnsweredAt ?? question.AskedAt
    };
}

public sealed class AnswerUnansweredRequest
{
    [JsonPropertyName("answer")]
    public string? Answer { get; set; }
}

public sealed class PortalKnowledgeDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("mime")]
    public required string Mime { get; init; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    [JsonPropertyName("uploadedAt")]
    public required DateTimeOffset UploadedAt { get; init; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    public static PortalKnowledgeDocument From(string tenantId, KnowledgeFile file) => new()
    {
        Id = file.Id.ToString("D"),
        TenantId = tenantId,
        Filename = file.Filename,
        Mime = file.Mime,
        Bytes = file.Bytes,
        UploadedAt = file.UploadedAt,
        ChunkCount = file.ChunkCount,
        Status = file.Status
    };
}
