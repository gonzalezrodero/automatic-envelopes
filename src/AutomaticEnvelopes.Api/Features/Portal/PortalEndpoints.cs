using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Knowledge;
using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalEndpoints
{
    private const string QuestionNotFound = "Question was not found.";
    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}/dashboard")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Dashboard(
        string tenantId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        IDocumentStore store,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (await MissingTenant(session, tenantId, ct) is { } missing)
        {
            return missing;
        }

        var conversations = await PortalConversations.ReadAsync(store, tenantId, ct);
        return Results.Ok(PortalDashboardBuilder.Build(conversations, DateTimeOffset.UtcNow));
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}/conversations")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Conversations(
        string tenantId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        IDocumentStore store,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (await MissingTenant(session, tenantId, ct) is { } missing)
        {
            return missing;
        }

        var conversations = await PortalConversations.ReadAsync(store, tenantId, ct);
        var visible = PortalConversations.Filter(
            conversations,
            QueryValue(httpContext, "q"),
            QueryValue(httpContext, "from"),
            QueryValue(httpContext, "to"),
            IncludeAnonymized(httpContext));
        return Results.Ok(visible);
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}/conversations/{conversationId}")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Conversation(
        string tenantId,
        string conversationId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        IDocumentStore store,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (!TenantIds.IsValid(conversationId))
        {
            return Results.BadRequest(new { Error = "conversationId is not valid." });
        }

        if (await MissingTenant(session, tenantId, ct) is { } missing)
        {
            return missing;
        }

        var conversations = await PortalConversations.ReadAsync(store, tenantId, ct);
        var match = PortalConversations.Find(conversations, conversationId);
        return match is null
            ? Results.NotFound(new { Error = "Conversation was not found." })
            : Results.Ok(match);
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}/documents")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Documents(
        string tenantId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        IKnowledgeCatalog catalog,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (await MissingTenant(session, tenantId, ct) is { } missing)
        {
            return missing;
        }

        var files = await catalog.ListAsync(tenantId, ct);
        var documents = files
            .OrderByDescending(file => file.UploadedAt)
            .Select(file => PortalKnowledgeDocument.From(tenantId, file))
            .ToArray();
        return Results.Ok(documents);
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}/unanswered")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Unanswered(
        string tenantId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        IUnansweredInbox inbox,
        CancellationToken ct)
    {
        if (await AdmitTenant(user, tenantId, httpContext, session, ct) is { } blocked)
        {
            return blocked;
        }

        var questions = await inbox.ListOpenAsync(tenantId, ct);
        return Results.Ok(questions.Select(PortalUnansweredQuestion.From).ToArray());
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}/unanswered/answered")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Answered(
        string tenantId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        IUnansweredInbox inbox,
        CancellationToken ct)
    {
        if (await AdmitTenant(user, tenantId, httpContext, session, ct) is { } blocked)
        {
            return blocked;
        }

        var questions = await inbox.ListAnsweredAsync(tenantId, ct);
        return Results.Ok(questions.Select(PortalAnsweredQuestion.From).ToArray());
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverinePost("/tenants/{tenantId}/unanswered/{questionId}")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> AnswerUnanswered(
        string tenantId,
        string questionId,
        AnswerUnansweredRequest? body,
        HttpContext httpContext,
        IDocumentSession session,
        IUnansweredInbox inbox,
        CancellationToken ct)
    {
        var gate = await AdmitAnswer(tenantId, questionId, body, httpContext, session, ct);
        if (gate.Blocked is { } blocked)
        {
            return blocked;
        }

        var saved = await inbox.AnswerAsync(tenantId, gate.Id, gate.Answer, ct);
        return saved is null
            ? Results.NotFound(new { Error = QuestionNotFound })
            : Results.Ok(PortalUnansweredQuestion.From(saved));
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverinePost("/tenants/{tenantId}/unanswered/{questionId}/revision")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> ReviseUnanswered(
        string tenantId,
        string questionId,
        AnswerUnansweredRequest? body,
        HttpContext httpContext,
        IDocumentSession session,
        IUnansweredInbox inbox,
        CancellationToken ct)
    {
        var gate = await AdmitAnswer(tenantId, questionId, body, httpContext, session, ct);
        if (gate.Blocked is { } blocked)
        {
            return blocked;
        }

        var saved = await inbox.ReviseAsync(tenantId, gate.Id, gate.Answer, ct);
        return saved is null
            ? Results.NotFound(new { Error = QuestionNotFound })
            : Results.Ok(PortalAnsweredQuestion.From(saved));
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineDelete("/tenants/{tenantId}/unanswered/{questionId}")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> WithdrawUnanswered(
        string tenantId,
        string questionId,
        HttpContext httpContext,
        IDocumentSession session,
        IUnansweredInbox inbox,
        CancellationToken ct)
    {
        var gate = await AdmitQuestion(tenantId, questionId, httpContext, session, ct);
        if (gate.Blocked is { } blocked)
        {
            return blocked;
        }

        var removed = await inbox.WithdrawAsync(tenantId, gate.Id, ct);
        return removed
            ? Results.NoContent()
            : Results.NotFound(new { Error = QuestionNotFound });
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineDelete("/tenants/{tenantId}/unanswered/{questionId}/discard")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> DiscardUnanswered(
        string tenantId,
        string questionId,
        HttpContext httpContext,
        IDocumentSession session,
        IUnansweredInbox inbox,
        CancellationToken ct)
    {
        var gate = await AdmitQuestion(tenantId, questionId, httpContext, session, ct);
        if (gate.Blocked is { } blocked)
        {
            return blocked;
        }

        var removed = await inbox.DiscardAsync(tenantId, gate.Id, ct);
        return removed
            ? Results.NoContent()
            : Results.NotFound(new { Error = QuestionNotFound });
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverinePost("/tenants/{tenantId}/documents")]
    [EnableRateLimiting("AdminPolicy")]
    [RequestSizeLimit(IngestLimits.MaxUploadBytes)]
    public static async Task<IResult> Upload(
        string tenantId,
        IFormFile file,
        HttpContext httpContext,
        IDocumentSession session,
        IEnumerable<IDocumentExtractor> extractors,
        IKnowledgeIngestionService ingestionService,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(httpContext.User, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (await MissingTenant(session, tenantId, ct) is { } missing)
        {
            return missing;
        }

        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { Error = "No file uploaded." });
        }

        if (IngestLimits.RejectFile(file) is { } tooLarge)
        {
            return tooLarge;
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var extractor = extractors.FirstOrDefault(item => item.SupportedExtensions.Contains(extension));
        if (extractor == null)
        {
            return Results.BadRequest(new { Error = $"File type '{extension}' is not supported. Please upload .pdf, .md, or .txt." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            if (IngestLimits.RejectPdf(stream, extractor) is { } tooManyPages)
            {
                return tooManyPages;
            }

            var extractedText = await extractor.ExtractTextAsync(stream);
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return Results.BadRequest(new { Error = "The uploaded file contains no readable text." });
            }

            if (IngestLimits.RejectText(extractedText) is { } tooLong)
            {
                return tooLong;
            }

            var fileName = Path.GetFileName(file.FileName);
            var mime = KnowledgeIngestionService.MimeFor(fileName);
            if (mime is null)
            {
                return Results.BadRequest(new { Error = $"File type '{extension}' is not supported. Please upload .pdf, .md, or .txt." });
            }

            var saved = await ingestionService.IngestDocumentAsync(tenantId, extractedText, fileName, file.Length, mime, ct);
            if (saved is null)
            {
                return Results.BadRequest(new { Error = "The uploaded file contains no readable text." });
            }

            return Results.Created($"/tenants/{tenantId}/documents/{saved.Id:D}", PortalKnowledgeDocument.From(tenantId, saved));
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Ingestion failed");
        }
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineDelete("/tenants/{tenantId}/documents/{documentId}")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> DeleteDocument(
        string tenantId,
        string documentId,
        HttpContext httpContext,
        IDocumentSession session,
        IKnowledgeCatalog catalog,
        IKnowledgeBaseService knowledge,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(httpContext.User, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (!Guid.TryParse(documentId, out var id))
        {
            return Results.BadRequest(new { Error = "documentId is not valid." });
        }

        if (await MissingTenant(session, tenantId, ct) is { } missing)
        {
            return missing;
        }

        var file = await catalog.FindAsync(tenantId, id, ct);
        if (file is null)
        {
            return Results.NotFound(new { Error = "Document was not found." });
        }

        await knowledge.DeleteChunksForDocumentAsync(tenantId, id, ct);
        await knowledge.DeleteLegacyChunksAsync(tenantId, file.Filename, ct);
        await catalog.DeleteAsync(tenantId, id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult?> AdmitTenant(
        ClaimsPrincipal user,
        string tenantId,
        HttpContext httpContext,
        IDocumentSession session,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        return await MissingTenant(session, tenantId, ct);
    }

    private readonly record struct AdmittedQuestion(Guid Id, IResult? Blocked);

    private static async Task<AdmittedQuestion> AdmitQuestion(
        string tenantId,
        string questionId,
        HttpContext httpContext,
        IDocumentSession session,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(httpContext.User, tenantId) is { } rejected)
        {
            return new AdmittedQuestion(Guid.Empty, rejected);
        }

        if (!Guid.TryParse(questionId, out var id))
        {
            return new AdmittedQuestion(Guid.Empty, Results.BadRequest(new { Error = "questionId is not valid." }));
        }

        if (await MissingTenant(session, tenantId, ct) is { } missing)
        {
            return new AdmittedQuestion(Guid.Empty, missing);
        }

        return new AdmittedQuestion(id, null);
    }

    private readonly record struct AdmittedAnswer(Guid Id, string Answer, IResult? Blocked);

    private static async Task<AdmittedAnswer> AdmitAnswer(
        string tenantId,
        string questionId,
        AnswerUnansweredRequest? body,
        HttpContext httpContext,
        IDocumentSession session,
        CancellationToken ct)
    {
        if (await AdmitTenant(httpContext.User, tenantId, httpContext, session, ct) is { } blocked)
        {
            return new AdmittedAnswer(Guid.Empty, string.Empty, blocked);
        }

        if (!Guid.TryParse(questionId, out var id))
        {
            return new AdmittedAnswer(Guid.Empty, string.Empty, Results.BadRequest(new { Error = "questionId is not valid." }));
        }

        if (!UnansweredInboxRules.TryNormalizeAnswer(body?.Answer, out var answer, out var error))
        {
            return new AdmittedAnswer(Guid.Empty, string.Empty, Results.BadRequest(new { Error = error }));
        }

        return new AdmittedAnswer(id, answer, null);
    }

    private static string? QueryValue(HttpContext httpContext, string name)
    {
        var value = httpContext.Request.Query[name];
        return value.Count == 0 ? null : value.ToString();
    }

    private static bool IncludeAnonymized(HttpContext httpContext)
    {
        var raw = QueryValue(httpContext, "includeAnonymized");
        return !bool.TryParse(raw, out var parsed) || parsed;
    }

    private static async Task<IResult?> MissingTenant(IDocumentSession session, string tenantId, CancellationToken ct)
    {
        var profile = await session.LoadAsync<TenantProfile>(tenantId, ct);
        return profile is null
            ? Results.NotFound(new { Error = "Tenant was not found." })
            : null;
    }
}
