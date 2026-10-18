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