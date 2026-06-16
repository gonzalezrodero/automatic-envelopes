using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using Moq;
using Moq.AutoMock;

namespace AutomaticEnvelopes.Tests.Features.Knowledge.Services;

public class KnowledgeIngestionServiceTests
{
    private readonly AutoMocker mocker;
    private readonly KnowledgeIngestionService sut;

    public KnowledgeIngestionServiceTests()
    {
        mocker = new AutoMocker();
        sut = mocker.CreateInstance<KnowledgeIngestionService>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task IngestDocumentAsync_EmptyText_ReturnsEarlyAndDoesNotCallDatabase(string? emptyText)
    {
        // Act
        await sut.IngestDocumentAsync("TestTenant", emptyText!, "file.md");

        // Assert
        var dbMock = mocker.GetMock<IKnowledgeBaseService>();

        dbMock.Verify(x => x.ClearTenantChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        dbMock.Verify(x => x.IngestChunksAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Happy path: standard text that fits in a single chunk
    [Fact]
    public async Task IngestDocumentAsync_ValidText_ClearsOldChunksAndIngestsNewOnes()
    {
        // Arrange
        var tenantId = "ClubSama-123";
        var fileName = "document.md";
        var text = "This is a valid extracted text.";

        // Act
        await sut.IngestDocumentAsync(tenantId, text, fileName);

        // Assert
        var dbMock = mocker.GetMock<IKnowledgeBaseService>();

        // Verify we clear the old data first
        dbMock.Verify(x => x.ClearTenantChunksAsync(tenantId, It.IsAny<CancellationToken>()), Times.Once);

        // Verify we ingest exactly one chunk containing the exact text
        dbMock.Verify(x => x.IngestChunksAsync(
            tenantId,
            It.Is<List<string>>(chunks => chunks.Count == 1 && chunks[0] == text),
            fileName,
            It.IsAny<CancellationToken>()),
        Times.Once);
    }

    [Fact]
    public async Task IngestDocumentAsync_LongText_ChunksTextCorrectlyWithOverlap()
    {
        // Arrange
        var tenantId = "ClubSama-123";
        var fileName = "long_document.md";

        // Create a string of 1200 characters to force multiple chunks
        var text = new string('A', 1200);

        // Act
        await sut.IngestDocumentAsync(tenantId, text, fileName);

        // Assert
        var dbMock = mocker.GetMock<IKnowledgeBaseService>();

        dbMock.Verify(x => x.IngestChunksAsync(
            tenantId,
            It.Is<List<string>>(chunks =>
                chunks.Count == 2 &&
                chunks[0].Length == 1000 &&
                chunks[1].Length == 400), // Second chunk starts at 800, so it takes the remaining 400 chars
            fileName,
            It.IsAny<CancellationToken>()),
        Times.Once);
    }
}