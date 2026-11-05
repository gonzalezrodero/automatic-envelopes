using System.IO.Compression;
using System.Text;
using AutomaticEnvelopes.Api.Features.Knowledge;
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

    [Fact]
    public async Task ExtractTextAsync_FlateStreamPastDecodeLimit_Throws()
    {
        // One byte past the cap. The compressed file stays under the upload limit.
        var pdf = PdfWithFlateStream(CompressZeros(IngestLimits.MaxDecodedPdfBytes + 1));
        pdf.Length.Should().BeLessThan((int)IngestLimits.MaxUploadBytes);
        using var stream = new MemoryStream(pdf);

        Func<Task> act = async () => await sut.ExtractTextAsync(stream);

        await act.Should().ThrowAsync<PdfDocumentFormatException>()
            .WithMessage("*decode limit*");
    }

    private static byte[] CompressZeros(long count)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            var remaining = count;
            while (remaining > 0)
            {
                var size = (int)Math.Min(chunk.Length, remaining);
                zlib.Write(chunk.AsSpan(0, size));
                remaining -= size;
            }
        }

        return output.ToArray();
    }

    private static byte[] PdfWithFlateStream(byte[] zlibStream)
    {
        using var document = new MemoryStream();
        var offsets = new List<long> { 0 };

        void Write(string text) => document.Write(Encoding.ASCII.GetBytes(text));

        void Object(string body)
        {
            offsets.Add(document.Position);
            Write(body);
        }

        Write("%PDF-1.4\n");
        Object("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Object("2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");
        Object("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");

        offsets.Add(document.Position);
        Write("4 0 obj\n");
        Write($"<< /Length {zlibStream.Length} /Filter /FlateDecode >>\nstream\n");
        document.Write(zlibStream);
        Write("\nendstream\nendobj\n");

        var xref = document.Position;
        Write($"xref\n0 {offsets.Count}\n");
        Write("0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write("trailer\n");
        Write($"<< /Size {offsets.Count} /Root 1 0 R >>\n");
        Write("startxref\n");
        Write($"{xref}\n");
        Write("%%EOF\n");
        return document.ToArray();
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