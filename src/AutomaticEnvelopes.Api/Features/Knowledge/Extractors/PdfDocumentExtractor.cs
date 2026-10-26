using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AutomaticEnvelopes.Api.Features.Knowledge.Extractors;

public class PdfDocumentExtractor : IDocumentExtractor
{
    public string[] SupportedExtensions => [".pdf"];

    public static int CountPages(Stream stream)
    {
        using var document = PdfDocument.Open(stream);
        return document.NumberOfPages;
    }

    // PdfPig inflates each FlateDecode stream before this method sees text. Its size check runs
    // only between chained filters, and only after that buffer exists, so one content stream can
    // exhaust memory while the upload is still under the byte cap. The page cap does not bound
    // that buffer, and stopping the builder early would run only after the allocation.
    // ParsingOptions has no decoded-size limit.
    public async Task<string> ExtractTextAsync(Stream stream)
    {
        var textBuilder = new StringBuilder();

        await Task.Run(() =>
        {
            using var document = PdfDocument.Open(stream);

            foreach (var page in document.GetPages())
            {
                var pageText = ContentOrderTextExtractor.GetText(page);
                textBuilder.AppendLine(pageText);
            }
        });

        return textBuilder.ToString();
    }
}