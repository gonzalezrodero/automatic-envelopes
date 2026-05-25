using Alba;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SamaBot.Api.Core.Entities;
using System.Net.Http.Headers;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SamaBot.Tests.E2E;

[Collection("Integration")]
public class IngestPdfEndpointTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task Post_Ingest_ValidFile_ReturnsOk_AndStoresChunksInMarten()
    {
        // Arrange
        var tenantId = "test-tenant-999";
        var fileName = $"test_integration_{Guid.NewGuid()}.pdf";
        var pdfBytes = CreateSimplePdfBytes("Integration test content for RAG.");

        await fixture.SeedTenantAsync(tenantId, "dummy-bot-phone");

        // Act
        await fixture.Host.Scenario(s =>
        {
            s.Post.Url($"/api/admin/ingest/{tenantId}");

            var fileContent = new ByteArrayContent(pdfBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            var multipart = new MultipartFormDataContent
            {
                { fileContent, "file", fileName }
            };

            var stream = multipart.ReadAsStream();
            s.ConfigureHttpContext(context =>
            {
                context.Request.ContentType = multipart.Headers.ContentType?.ToString();
                context.Request.ContentLength = stream.Length;
                context.Request.Body = stream;
            });

            s.StatusCodeShouldBeOk();
        });

        // Assert
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);
        var chunks = await session.Query<DocumentChunk>()
            .Where(x => x.SourceDocument == fileName)
            .ToListAsync();

        chunks.Should().NotBeEmpty();
    }

    private static byte[] CreateSimplePdfBytes(string content)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(content, 10, new PdfPoint(25, 800), font);

        return builder.Build(); // Returns the byte[] directly
    }
}