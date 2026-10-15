using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Features.Knowledge;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Knowledge.Services;

public class KnowledgeCatalogLegacyTests
{
    [Fact]
    public void IncludeLegacySources_ListsChunkSourcesThatHaveNoCatalogRow()
    {
        var sharedId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var otherId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var earlier = new DateTimeOffset(2024, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var later = earlier.AddDays(2);
        var catalogued = new KnowledgeFile
        {
            Id = Guid.NewGuid(),
            Filename = "already.txt",
            Mime = "text/plain",
            Bytes = 12,
            UploadedAt = later,
            ChunkCount = 1,
            Status = "indexed"
        };

        var chunks = new[]
        {
            Chunk("already.txt", Guid.Empty, later),
            Chunk("reglamento.pdf", sharedId, later, "page 2"),
            Chunk("reglamento.pdf", sharedId, earlier, "page 1"),
            Chunk("notas.md", Guid.Empty, later),
            Chunk("notas.md", otherId, earlier),
            Chunk("reglas.markdown", Guid.Empty, earlier),
            Chunk("aviso.txt", Guid.Empty, later)
        };

        var (files, created) = KnowledgeCatalog.IncludeLegacySources("club-a", [catalogued], chunks);
        var again = KnowledgeCatalog.IncludeLegacySources("club-a", [catalogued], chunks);
        var otherClub = KnowledgeCatalog.IncludeLegacySources("club-b", [catalogued], chunks);

        files.Should().Contain(file => file.Id == catalogued.Id && file.Filename == "already.txt");
        created.Should().NotContain(file => file.Filename == "already.txt");

        var pdf = files.Should().ContainSingle(file => file.Filename == "reglamento.pdf").Subject;
        pdf.Id.Should().Be(sharedId);
        pdf.Mime.Should().Be("application/pdf");
        pdf.Bytes.Should().Be(0);
        pdf.Status.Should().Be("indexed");
        pdf.ChunkCount.Should().Be(2);
        pdf.UploadedAt.Should().Be(earlier);

        var mixed = files.Should().ContainSingle(file => file.Filename == "notas.md").Subject;
        mixed.Id.Should().NotBe(Guid.Empty);
        mixed.Id.Should().NotBe(otherId);
        mixed.Id.Should().Be(again.Files.Single(file => file.Filename == "notas.md").Id);
        mixed.Id.Should().NotBe(otherClub.Files.Single(file => file.Filename == "notas.md").Id);
        mixed.Mime.Should().Be("text/markdown");
        mixed.ChunkCount.Should().Be(2);
        mixed.UploadedAt.Should().Be(earlier);

        files.Should().ContainSingle(file =>
            file.Filename == "reglas.markdown" &&
            file.Mime == "text/markdown" &&
            file.Id != Guid.Empty &&
            file.Bytes == 0 &&
            file.Status == "indexed");
        var notice = files.Should().ContainSingle(file => file.Filename == "aviso.txt" && file.Mime == "text/plain").Subject;
        notice.Id.Should().Be(again.Files.Single(file => file.Filename == "aviso.txt").Id);
        created.Select(file => file.Filename).Should().BeEquivalentTo(
            "reglamento.pdf",
            "notas.md",
            "reglas.markdown",
            "aviso.txt");
    }

    private static DocumentChunk Chunk(string source, Guid documentId, DateTimeOffset created, string content = "text") =>
        new(Guid.NewGuid(), content, source, [], created, documentId);
}
