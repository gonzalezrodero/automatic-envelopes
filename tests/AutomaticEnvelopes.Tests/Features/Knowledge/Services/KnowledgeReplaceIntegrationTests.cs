using System.Data;
using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticEnvelopes.Tests.Features.Knowledge.Services;

[Collection("Integration")]
public class KnowledgeReplaceIntegrationTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task DeleteLegacyChunks_RemovesOmittedAndEmptyDocumentIds_ForThatFileOnly()
    {
        var tenantId = $"legacy-{Guid.NewGuid():N}";
        var source = $"horario-{Guid.NewGuid():N}.txt";
        var otherSource = $"otro-{Guid.NewGuid():N}.txt";
        var ct = TestContext.Current.CancellationToken;
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();

        var omitted = Chunk(source, Guid.Empty, "sin id");
        var empty = Chunk(source, Guid.Empty, "id vacio");
        var current = Chunk(source, Guid.NewGuid(), "version actual");
        var other = Chunk(otherSource, Guid.Empty, "otro archivo");

        using (var session = store.LightweightSession(tenantId))
        {
            session.Store(omitted, empty, current, other);
            await session.SaveChangesAsync(ct);
        }

        await OmitDocumentIdAsync(store, tenantId, omitted.Id, ct);
        await OmitDocumentIdAsync(store, tenantId, other.Id, ct);

        using (var check = store.LightweightSession(tenantId))
        {
            (await DocumentIdExistsAsync(check, omitted.Id, tenantId, ct)).Should().BeFalse();
            var loaded = await check.LoadAsync<DocumentChunk>(omitted.Id, ct);
            loaded!.DocumentId.Should().Be(Guid.Empty);
        }

        await using (var scope = fixture.Host.Services.CreateAsyncScope())
        {
            var knowledge = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>();
            await knowledge.DeleteLegacyChunksAsync(tenantId, source, ct);
        }

        using var read = store.LightweightSession(tenantId);
        var left = await read.Query<DocumentChunk>().ToListAsync(ct);
        left.Select(chunk => chunk.Id).Should().BeEquivalentTo([current.Id, other.Id]);
    }

    [Fact]
    public async Task Reingest_ReplacesCataloguedChunksAndLegacyChunks_ForThatFilename()
    {
        var tenantId = $"replace-{Guid.NewGuid():N}";
        var fileName = $"horario-{Guid.NewGuid():N}.txt";
        var otherName = $"otro-{Guid.NewGuid():N}.txt";
        var ct = TestContext.Current.CancellationToken;

        await using var scope = fixture.Host.Services.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IKnowledgeIngestionService>();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var first = await ingestion.IngestDocumentAsync(tenantId, "texto original", fileName, 14, "text/plain", ct);
        var other = await ingestion.IngestDocumentAsync(tenantId, "otro documento", otherName, 14, "text/plain", ct);
        first.Should().NotBeNull();
        other.Should().NotBeNull();

        var legacy = Chunk(fileName, Guid.Empty, "texto heredado");
        using (var session = store.LightweightSession(tenantId))
        {
            session.Store(legacy);
            await session.SaveChangesAsync(ct);
        }

        await OmitDocumentIdAsync(store, tenantId, legacy.Id, ct);

        var replaced = await ingestion.IngestDocumentAsync(tenantId, "texto nuevo", fileName, 11, "text/plain", ct);

        replaced!.Id.Should().Be(first!.Id);
        using var read = store.LightweightSession(tenantId);
        var chunks = await read.Query<DocumentChunk>().ToListAsync(ct);
        var replacedChunks = chunks.Where(chunk => chunk.SourceDocument == fileName).ToArray();
        replacedChunks.Should().ContainSingle();
        replacedChunks[0].DocumentId.Should().Be(first.Id);
        replacedChunks[0].Content.Should().Be("texto nuevo");
        replacedChunks.Should().NotContain(chunk => chunk.Id == legacy.Id);
        chunks.Should().Contain(chunk => chunk.SourceDocument == otherName && chunk.DocumentId == other!.Id && chunk.Content == "otro documento");
    }

    private static DocumentChunk Chunk(string source, Guid documentId, string content) => new(
        Guid.NewGuid(),
        content,
        source,
        new float[512],
        DateTimeOffset.UtcNow,
        documentId);

    private static async Task OmitDocumentIdAsync(IDocumentStore store, string tenantId, Guid id, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        session.QueueSqlCommand(
            "update public.mt_doc_documentchunk set data = data - 'DocumentId' where id = ? and tenant_id = ?",
            id,
            tenantId);
        await session.SaveChangesAsync(ct);
    }

    private static async Task<bool> DocumentIdExistsAsync(
        IDocumentSession session,
        Guid id,
        string tenantId,
        CancellationToken ct)
    {
        var connection = session.Connection;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "select data ? 'DocumentId' from public.mt_doc_documentchunk where id = @id and tenant_id = @tenant";
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "id";
        idParameter.Value = id;
        command.Parameters.Add(idParameter);
        var tenantParameter = command.CreateParameter();
        tenantParameter.ParameterName = "tenant";
        tenantParameter.Value = tenantId;
        command.Parameters.Add(tenantParameter);
        var result = await command.ExecuteScalarAsync(ct);
        return result is true;
    }
}
