using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;

namespace AutomaticEnvelopes.Api.Features.Knowledge;

public static class IngestLimits
{
    /// <summary>
    /// Largest accepted file. Lambda's synchronous payload is 6 MB, and a Function URL
    /// base64-encodes a binary body, so the file that fits is about 4.5 MB. 4 MiB stays under that.
    /// <c>file.Length</c> is the guard on Lambda. <c>[RequestSizeLimit]</c> applies only under Kestrel.
    /// </summary>
    public const long MaxUploadBytes = 4 * 1024 * 1024;
    public const int MaxPdfPages = 100;
    public const int MaxCharacters = 200_000;
    public const string PdfUnreadable = "No se ha podido leer el PDF.";
    public const string FileTooLarge = "El archivo es demasiado grande.";
    public const string DocumentTooLong = "El documento es demasiado largo.";
    public const string PdfTooManyPages = "El PDF tiene demasiadas páginas.";
    public const string UnsupportedType = "Ese archivo no se admite. Usa PDF, Markdown o texto.";
    public const string EmptyFile = "No hay ningún archivo.";
    public const string NoReadableText = "Ese archivo no tiene texto.";
    public const string ReservedName = "Ese nombre de archivo está reservado.";
    public const string IngestionFailedDetail = "No se ha podido procesar el documento. Prueba con otro archivo.";

    public static IResult? RejectFile(IFormFile file)
    {
        if (file.Length > MaxUploadBytes)
        {
            return Results.Json(
                new { Error = FileTooLarge },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        return null;
    }

    public static IResult? RejectText(string text)
    {
        if (text.Length > MaxCharacters)
        {
            return Results.BadRequest(new { Error = DocumentTooLong });
        }

        return null;
    }

    public static IResult? RejectReservedName(string? fileName)
    {
        var name = Path.GetFileName(fileName);
        if (!string.Equals(name, AnsweredKnowledge.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Results.BadRequest(new { Error = ReservedName });
    }

    public static IResult RejectPdfRead(Exception exception, ILogger logger, string tenantId)
    {
        logger.LogError(exception, "Failed to read an uploaded PDF. TenantId: {TenantId}.", tenantId);
        return Results.BadRequest(new { Error = PdfUnreadable });
    }

    public static IResult IngestionFailed(Exception exception, ILogger logger, string tenantId)
    {
        logger.LogError(exception, "Document ingestion failed. TenantId: {TenantId}.", tenantId);
        return Results.Problem(
            detail: IngestionFailedDetail,
            title: "No se ha podido procesar el documento.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    public static IResult? RejectPdf(Stream stream, IDocumentExtractor extractor)
    {
        if (extractor is not PdfDocumentExtractor)
        {
            return null;
        }

        if (!stream.CanSeek)
        {
            return Results.BadRequest(new { Error = PdfUnreadable });
        }

        var pages = PdfDocumentExtractor.CountPages(stream);
        stream.Position = 0;
        if (pages > MaxPdfPages)
        {
            return Results.BadRequest(new { Error = PdfTooManyPages });
        }

        return null;
    }
}
