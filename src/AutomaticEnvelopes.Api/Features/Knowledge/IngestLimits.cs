using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;

namespace AutomaticEnvelopes.Api.Features.Knowledge;

public static class IngestLimits
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    public const int MaxPdfPages = 100;
    public const int MaxCharacters = 200_000;

    public static IResult? RejectFile(IFormFile file)
    {
        if (file.Length > MaxUploadBytes)
        {
            return Results.Json(
                new { Error = $"The file is larger than {MaxUploadBytes} bytes." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        return null;
    }

    public static IResult? RejectText(string text)
    {
        if (text.Length > MaxCharacters)
        {
            return Results.BadRequest(new { Error = $"The document is longer than {MaxCharacters} characters." });
        }

        return null;
    }

    public static IResult? RejectPdf(Stream stream, IDocumentExtractor extractor)
    {
        if (extractor is not PdfDocumentExtractor)
        {
            return null;
        }

        if (!stream.CanSeek)
        {
            return Results.BadRequest(new { Error = "The PDF could not be read." });
        }

        var pages = PdfDocumentExtractor.CountPages(stream);
        stream.Position = 0;
        if (pages > MaxPdfPages)
        {
            return Results.BadRequest(new { Error = $"The PDF has more than {MaxPdfPages} pages." });
        }

        return null;
    }
}
