using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AutomaticEnvelopes.Api.Features.Knowledge.Extractors;

public class PdfDocumentExtractor : IDocumentExtractor
{
    public string[] SupportedExtensions => [".pdf"];

    public static int CountPages(Stream stream)
    {
        using var document = Open(stream);
        return document.NumberOfPages;
    }

    public async Task<string> ExtractTextAsync(Stream stream)
    {
        var textBuilder = new StringBuilder();

        await Task.Run(() =>
        {
            using var document = Open(stream);

            foreach (var page in document.GetPages())
            {
                var pageText = ContentOrderTextExtractor.GetText(page);
                textBuilder.AppendLine(pageText);
            }
        });

        return textBuilder.ToString();
    }

    // The page cap runs only after Open. Decoding uses a Flate filter that stops past MaxDecodedPdfBytes.
    private static PdfDocument Open(Stream stream) =>
        PdfDocument.Open(stream, new ParsingOptions
        {
            FilterProvider = new BoundedFlateFilterProvider(IngestLimits.MaxDecodedPdfBytes)
        });
}