namespace AutomaticEnvelopes.Api.Core.Entities;

/// <summary>
/// Represents a chunk of text from the club's documentation (PDFs)
/// stored with its vector embedding for RAG search.
/// </summary>
public record DocumentChunk(
    Guid Id,
    string Content,
    string SourceDocument,
    float[] Embedding,
    DateTimeOffset CreatedAt
);