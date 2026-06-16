using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AwesomeAssertions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AutomaticEnvelopes.Tests.Features.Knowledge.Extractors;

public class PdfDocumentExtractorTests
{
    private readonly PdfDocumentExtractor sut = new();

    [Fact]
    public void SupportedExtensions_ShouldContainPdf()
    {
        // Act
        var extensions = sut.SupportedExtensions;

        // Assert
        extensions.Should().BeEquivalentTo([".pdf"]);
    }

    [Fact]
    public async Task ExtractTextAsync_InvalidPdfStream_ThrowsException()
    {
        // Arrange
        var invalidPdfBytes = "This is not a valid PDF binary"u8.ToArray();
        using var stream = new MemoryStream(invalidPdfBytes);

        // Act
        Func<Task> act = async () => await sut.ExtractTextAsync(stream);

        // Assert
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ExtractTextAsync_ValidPdfCreatedInMemory_ReturnsText()
    {
        // Arrange
        var expectedText = "Hello from AutomaticEnvelopes In-Memory PDF!";
        var pdfStream = CreateValidPdfStreamWithText(expectedText);

        // Act
        var result = await sut.ExtractTextAsync(pdfStream);

        // Assert
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().Contain(expectedText);
    }

    // Método auxiliar para construir el PDF
    private static MemoryStream CreateValidPdfStreamWithText(string textToInject)
    {
        var builder = new PdfDocumentBuilder();

        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText(textToInject, 12, new PdfPoint(50, 700), font);

        var pdfBytes = builder.Build();
        return new MemoryStream(pdfBytes);
    }
}