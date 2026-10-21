using System.Text.Json;
using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AwesomeAssertions;
using Marten;
using Moq;
using Moq.AutoMock;
using System.Linq.Expressions;

namespace AutomaticEnvelopes.Tests.Features.Knowledge.Services;

public class KnowledgeBaseServiceTests
{
    private readonly AutoMocker mocker;
    private readonly KnowledgeBaseService sut;
    private readonly Mock<IDocumentSession> mockSession;
    private const string TestTenantId = "34111222333";

    public KnowledgeBaseServiceTests()
    {
        mocker = new AutoMocker();

        mockSession = new Mock<IDocumentSession>();
        mocker.GetMock<IDocumentStore>()
            .Setup(store => store.LightweightSession(TestTenantId))
            .Returns(mockSession.Object);

        sut = mocker.CreateInstance<KnowledgeBaseService>();
    }

    [Fact]
    public async Task SearchAsync_GeneratesEmbeddingAndQueriesDatabase_ReturnsChunks()
    {
        // Arrange
        var query = "Test query";
        var mockVector = new float[] { 0.1f, 0.2f, 0.3f };

        mocker.GetMock<IEmbeddingService>()
            .Setup(e => e.GenerateEmbeddingAsync(
                query,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockVector);

        var expectedChunks = new List<DocumentChunk>
        {
            new(Guid.NewGuid(), "Result 1", "doc.pdf", mockVector, DateTimeOffset.UtcNow)
        };

        mockSession
            .Setup(s => s.QueryAsync<DocumentChunk>(
                It.Is<string>(sql => sql.Contains("<=>") && sql.Contains("tenant_id = ?")),
                It.IsAny<CancellationToken>(),
                It.IsAny<object[]>()))
            .ReturnsAsync(expectedChunks);

        // Act 
        var result = await sut.SearchAsync(TestTenantId, query, limit: 1, ct: TestContext.Current.CancellationToken);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result[0].Content.Should().Be("Result 1");

        mockSession.Verify(s =>
            s.QueryAsync<DocumentChunk>(
                It.Is<string>(sql => sql.Contains("WHERE tenant_id = ?") && !sql.Contains(TestTenantId)),
                It.IsAny<CancellationToken>(),
                It.Is<object[]>(args => args.Length >= 1 && (string)args[0] == TestTenantId)),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_QueriesOnlyTheRequestedTenant()
    {
        const string otherTenantId = "other-tenant";
        var otherSession = new Mock<IDocumentSession>();
        mocker.GetMock<IDocumentStore>()
            .Setup(store => store.LightweightSession(otherTenantId))
            .Returns(otherSession.Object);
        mocker.GetMock<IEmbeddingService>()
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0.4f]);
        otherSession
            .Setup(s => s.QueryAsync<DocumentChunk>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<object[]>()))
            .ReturnsAsync([]);

        await sut.SearchAsync(otherTenantId, "horario", limit: 2, ct: TestContext.Current.CancellationToken);

        mockSession.Verify(
            s => s.QueryAsync<DocumentChunk>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()),
            Times.Never);
        otherSession.Verify(s => s.QueryAsync<DocumentChunk>(
            It.Is<string>(sql => sql.Contains("tenant_id = ?") && !sql.Contains(otherTenantId)),
            It.IsAny<CancellationToken>(),
            It.Is<object[]>(args => (string)args[0] == otherTenantId && (int)args[2] == 2)),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_BlankTenant_ReturnsEmptyWithoutQuerying(string tenantId)
    {
        var result = await sut.SearchAsync(tenantId, "query", ct: TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
        mocker.GetMock<IEmbeddingService>().Verify(
            service => service.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mocker.GetMock<IDocumentStore>().Verify(store => store.LightweightSession(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IngestChunksAsync_GeneratesEmbeddingsAndStoresInSession_CallsSaveChanges()
    {
        // Arrange
        var content = "This is a test chunk of text from the PDF.";
        var source = "SummerCamp_2026.pdf";
        var mockVector = new float[] { 0.5f, 0.5f, 0.5f };

        mocker.GetMock<IEmbeddingService>()
            .Setup(e => e.GenerateEmbeddingAsync(
                content,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockVector);

        // Act
        await sut.IngestChunksAsync(TestTenantId, [content], source, ct: TestContext.Current.CancellationToken);

        // Assert
        mockSession
            .Verify(s => s.Store(It.Is<DocumentChunk>(chunk =>
                chunk.Content == content &&
                chunk.SourceDocument == source &&
                chunk.Embedding.SequenceEqual(mockVector))),
            Times.Once);

        mockSession
            .Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void MissingDocumentId_DeserializesAsEmpty()
    {
        const string json = """
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Content": "viejo",
              "SourceDocument": "horario.txt",
              "Embedding": [0.1],
              "CreatedAt": "2026-01-01T00:00:00+00:00"
            }
            """;

        var chunk = JsonSerializer.Deserialize<DocumentChunk>(json);

        chunk.Should().NotBeNull();
        chunk!.DocumentId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task ClearTenantChunksAsync_DeletesChunksAndSaves()
    {
        // Act
        await sut.ClearTenantChunksAsync(TestTenantId, ct: TestContext.Current.CancellationToken);

        // Assert
        mockSession.Verify(s => s.DeleteWhere(It.IsAny<Expression<Func<DocumentChunk, bool>>>()), Times.Once);
        mockSession.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}