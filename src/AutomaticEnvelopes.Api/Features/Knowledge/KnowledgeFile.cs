namespace AutomaticEnvelopes.Api.Features.Knowledge;

/// <summary>
/// One uploaded file for a tenant. Chunks point at <see cref="Id"/> so a later
/// upload of another file does not wipe this one.
/// </summary>
public class KnowledgeFile
{
    public Guid Id { get; set; }

    public string Filename { get; set; } = string.Empty;

    public string Mime { get; set; } = string.Empty;

    public long Bytes { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public int ChunkCount { get; set; }

    public string Status { get; set; } = "indexed";
}
