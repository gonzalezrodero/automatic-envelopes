using AwesomeAssertions;
using AutomaticEnvelopes.Api.Features.Knowledge;
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
    [InlineData("notes.pdf", "application/pdf")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("notes.markdown", "text/markdown")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("notes.csv", null)]
    public void MimeFor_MapsThePortalFileTypes(string fileName, string? mime)
    {
        KnowledgeIngestionService.MimeFor(fileName).Should().Be(mime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task IngestDocumentAsync_EmptyText_ReturnsEarlyAndDoesNotCallDatabase(string? emptyText)
    {
        // Act
        await sut.IngestDocumentAsync("TestTenant", emptyText!, "file.md", 0, "text/markdown", TestContext.Current.CancellationToken);

        // Assert
        var dbMock = mocker.GetMock<IKnowledgeBaseService>();

        dbMock.Verify(x => x.ClearTenantChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        dbMock.Verify(x => x.DeleteLegacyChunksAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        dbMock.Verify(x => x.IngestChunksAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>()), Times.Never);
        mocker.GetMock<IKnowledgeCatalog>().Verify(x => x.UpsertAsync(It.IsAny<string>(), It.IsAny<KnowledgeFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Happy path: standard text that fits in a single chunk
    [Fact]
    public async Task IngestDocumentAsync_ValidText_IngestsChunksWithoutClearingTheTenant()
    {
        // Arrange
        var tenantId = "ClubSama-123";
        var fileName = "document.md";
        var text = "This is a valid extracted text.";

        // Act
        var saved = await sut.IngestDocumentAsync(tenantId, text, fileName, 28, "text/markdown", TestContext.Current.CancellationToken);

        // Assert
        saved.Should().NotBeNull();
        saved!.Filename.Should().Be(fileName);
        saved.ChunkCount.Should().Be(1);
        var dbMock = mocker.GetMock<IKnowledgeBaseService>();

        dbMock.Verify(x => x.ClearTenantChunksAsync(tenantId, It.IsAny<CancellationToken>()), Times.Never);
        dbMock.Verify(x => x.DeleteChunksForDocumentAsync(tenantId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        dbMock.Verify(x => x.DeleteLegacyChunksAsync(tenantId, fileName, It.IsAny<CancellationToken>()), Times.Once);

        dbMock.Verify(x => x.IngestChunksAsync(
            tenantId,
            It.Is<List<string>>(chunks => chunks.Count == 1 && chunks[0] == text),
            fileName,
            It.IsAny<CancellationToken>(),
            It.IsAny<Guid>()),
        Times.Once);
        mocker.GetMock<IKnowledgeCatalog>().Verify(
            x => x.UpsertAsync(tenantId, It.Is<KnowledgeFile>(file => file.Filename == fileName && file.Bytes == 28), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task IngestDocumentAsync_SameFilename_ReplacesCataloguedAndLegacyChunks()
    {
        var tenantId = "ClubSama-123";
        var fileName = "document.md";
        var existing = new KnowledgeFile { Id = Guid.NewGuid(), Filename = fileName };
        mocker.GetMock<IKnowledgeCatalog>()
            .Setup(x => x.FindByFilenameAsync(tenantId, fileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await sut.IngestDocumentAsync(tenantId, "replacement text", fileName, 16, "text/markdown", TestContext.Current.CancellationToken);

        var dbMock = mocker.GetMock<IKnowledgeBaseService>();
        dbMock.Verify(x => x.DeleteChunksForDocumentAsync(tenantId, existing.Id, It.IsAny<CancellationToken>()), Times.Once);
        dbMock.Verify(x => x.DeleteLegacyChunksAsync(tenantId, fileName, It.IsAny<CancellationToken>()), Times.Once);
        dbMock.Verify(x => x.IngestChunksAsync(
            tenantId,
            It.IsAny<List<string>>(),
            fileName,
            It.IsAny<CancellationToken>(),
            existing.Id),
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
        await sut.IngestDocumentAsync(tenantId, text, fileName, text.Length, "text/markdown", TestContext.Current.CancellationToken);

        // Assert
        var dbMock = mocker.GetMock<IKnowledgeBaseService>();

        dbMock.Verify(x => x.IngestChunksAsync(
            tenantId,
            It.Is<List<string>>(chunks =>
                chunks.Count == 2 &&
                chunks[0].Length == 1000 &&
                chunks[1].Length == 400), // Second chunk starts at 800, so it takes the remaining 400 chars
            fileName,
            It.IsAny<CancellationToken>(),
            It.IsAny<Guid>()),
        Times.Once);
    }
}