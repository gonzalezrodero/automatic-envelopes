using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.Knowledge;
using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Portal;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.Portal;

public class PortalScreenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Mask_CoversEmptyShortAndForeignNumbers()
    {
        PortalPhone.Mask("").Should().Be("Número eliminado");
        PortalPhone.Mask("34").Should().Be("+34 ***");
        PortalPhone.Mask("+447911123456").Should().Be("+44 79** *** *56");
        PortalPhone.Mask("33612345678").Should().Be("+33 61** *** *78");
    }

    [Fact]
    public void Dashboard_CountsAnonymizedThreadsLanguagesAndEmptyPreviews()
    {
        var anonymized = Conversation("archived", anonymized: true, deletion: Now, events: []);
        var spanish = Conversation(
            "live",
            phone: "34600111222",
            language: "es",
            topic: "Horario",
            events: [Event("MessageReceived", "Hola", Now)]);
        var silent = Conversation(
            "silent",
            phone: "1",
            events: [Event("ReplyGenerated", "   ", Now)]);

        var dashboard = PortalDashboardBuilder.Build([anonymized, spanish, silent], Now);

        dashboard.DeletionRequests.Should().Be(1);
        dashboard.Languages.Should().ContainSingle(language => language.Language == "es" && language.Count == 1);
        dashboard.Recent.Should().Contain(item => item.Preview == "Historial anonimizado tras una solicitud de borrado.");
        dashboard.Recent.Should().Contain(item => item.Preview == "Sin mensajes");
        dashboard.Recent.Should().Contain(item => item.PhoneMasked == "+34 600 ** ** 22");
        dashboard.Recent.Should().Contain(item => item.PhoneMasked == "+1 ***");
    }

    [Fact]
    public void Filter_DropsThreadsOutsideTheRangeAndSearchesAnonymizedText()
    {
        var anonymized = Conversation("archived", anonymized: true, deletion: Now, events: []);
        var spanish = Conversation(
            "live",
            phone: "34600111222",
            topic: "Horario",
            events: [Event("MessageReceived", "Hola", Now)]);
        var undated = Conversation("blank", events: []);

        PortalConversations.Activity(anonymized).Should().Be(Now);
        PortalConversations.Filter([anonymized], "anonimizado", null, "2026-10-01", "2026-10-31", true)
            .Should().ContainSingle(item => item.Id == "archived");
        PortalConversations.Filter([spanish], null, null, "2026-11-01", null, true).Should().BeEmpty();
        PortalConversations.Filter([spanish], null, null, null, "2026-09-01", true).Should().BeEmpty();
        PortalConversations.Filter([undated], null, null, "2026-10-01", null, true).Should().BeEmpty();
        PortalConversations.Filter([anonymized], null, null, null, null, false).Should().BeEmpty();
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("conversations")]
    [InlineData("conversation")]
    [InlineData("documents")]
    [InlineData("upload")]
    [InlineData("delete")]
    public async Task Endpoints_RejectAnInvalidTenantId(string route)
    {
        var status = await Call(route, "not_valid", User("admin"), Session(null), "abc");
        status.Should().Be(400);
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("conversations")]
    [InlineData("conversation")]
    [InlineData("documents")]
    [InlineData("upload")]
    [InlineData("delete")]
    public async Task Endpoints_ReturnNotFoundWhenTheTenantIsMissing(string route)
    {
        var status = await Call(route, "club-a", User("club-a"), Session(null), "abc");
        status.Should().Be(404);
    }

    [Fact]
    public async Task Conversation_RejectsAnInvalidId()
    {
        var status = await PortalEndpoints.Conversation(
            "club-a",
            "Bad.Id",
            User("club-a"),
            new DefaultHttpContext(),
            Session(Profile("club-a")),
            new Mock<IDocumentStore>().Object,
            CancellationToken.None);
        Status(status).Should().Be(400);
    }

    [Fact]
    public async Task Delete_RejectsANonGuidAndAMissingDocument()
    {
        var bad = await PortalEndpoints.DeleteDocument(
            "club-a",
            "not-a-guid",
            User("club-a"),
            new DefaultHttpContext(),
            Session(Profile("club-a")),
            new Mock<IKnowledgeCatalog>().Object,
            new Mock<IKnowledgeBaseService>().Object,
            CancellationToken.None);
        Status(bad).Should().Be(400);

        var catalog = new Mock<IKnowledgeCatalog>();
        catalog.Setup(item => item.FindAsync("club-a", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeFile?)null);
        var missing = await PortalEndpoints.DeleteDocument(
            "club-a",
            Guid.NewGuid().ToString("D"),
            User("club-a"),
            new DefaultHttpContext(),
            Session(Profile("club-a")),
            catalog.Object,
            new Mock<IKnowledgeBaseService>().Object,
            CancellationToken.None);
        Status(missing).Should().Be(404);
    }

    [Fact]
    public async Task Upload_RejectsEmptyUnsupportedAndUnreadableFiles()
    {
        var empty = File("notes.txt", 0);
        Status(await Upload(empty, [], Ingestion())).Should().Be(400);

        var png = File("image.png", 12);
        Status(await Upload(png, [], Ingestion())).Should().Be(400);

        var blank = File("notes.txt", 12);
        Status(await Upload(blank, [Extractor(".txt", "   ").Object], Ingestion())).Should().Be(400);

        var csv = File("notes.csv", 12);
        Status(await Upload(csv, [Extractor(".csv", "hola").Object], Ingestion())).Should().Be(400);

        var saved = new Mock<IKnowledgeIngestionService>();
        saved.Setup(item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeFile?)null);
        var readable = File("notes.txt", 12);
        Status(await Upload(readable, [Extractor(".txt", "hola").Object], saved.Object)).Should().Be(400);

        var broken = Extractor(".txt", "hola");
        broken.Setup(item => item.ExtractTextAsync(It.IsAny<Stream>())).ThrowsAsync(new InvalidOperationException("boom"));
        Status(await Upload(File("notes.txt", 12), [broken.Object], Ingestion())).Should().Be(500);
    }

    private static async Task<int> Call(
        string route,
        string tenantId,
        ClaimsPrincipal user,
        IDocumentSession session,
        string conversationId)
    {
        var http = new DefaultHttpContext();
        var store = new Mock<IDocumentStore>().Object;
        var catalog = new Mock<IKnowledgeCatalog>().Object;
        var knowledge = new Mock<IKnowledgeBaseService>().Object;
        var result = route switch
        {
            "dashboard" => await PortalEndpoints.Dashboard(tenantId, user, http, session, store, CancellationToken.None),
            "conversations" => await PortalEndpoints.Conversations(tenantId, null, null, null, null, null, user, http, session, store, CancellationToken.None),
            "conversation" => await PortalEndpoints.Conversation(tenantId, conversationId, user, http, session, store, CancellationToken.None),
            "documents" => await PortalEndpoints.Documents(tenantId, user, http, session, catalog, CancellationToken.None),
            "upload" => await PortalEndpoints.Upload(tenantId, File("notes.txt", 4), user, http, session, [], Ingestion(), CancellationToken.None),
            "delete" => await PortalEndpoints.DeleteDocument(tenantId, Guid.NewGuid().ToString("D"), user, http, session, catalog, knowledge, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        return Status(result);
    }

    private static async Task<IResult> Upload(IFormFile file, IEnumerable<IDocumentExtractor> extractors, IKnowledgeIngestionService ingestion)
    {
        return await PortalEndpoints.Upload(
            "club-a",
            file,
            User("club-a"),
            new DefaultHttpContext(),
            Session(Profile("club-a")),
            extractors,
            ingestion,
            CancellationToken.None);
    }

    private static IFormFile File(string name, long length)
    {
        var file = new Mock<IFormFile>();
        file.Setup(item => item.FileName).Returns(name);
        file.Setup(item => item.Length).Returns(length);
        file.Setup(item => item.OpenReadStream()).Returns(new MemoryStream("hola"u8.ToArray()));
        return file.Object;
    }

    private static Mock<IDocumentExtractor> Extractor(string extension, string text)
    {
        var extractor = new Mock<IDocumentExtractor>();
        extractor.Setup(item => item.SupportedExtensions).Returns([extension]);
        extractor.Setup(item => item.ExtractTextAsync(It.IsAny<Stream>())).ReturnsAsync(text);
        return extractor;
    }

    private static IKnowledgeIngestionService Ingestion() => new Mock<IKnowledgeIngestionService>().Object;

    private static IDocumentSession Session(TenantProfile? profile)
    {
        var session = new Mock<IDocumentSession>();
        session.Setup(item => item.LoadAsync<TenantProfile>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        return session.Object;
    }

    private static TenantProfile Profile(string id) => new() { Id = id, BotPhoneNumberId = "1" };

    private static ClaimsPrincipal User(string role)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test");
        return new ClaimsPrincipal(identity);
    }

    private static int Status(IResult result) =>
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject.StatusCode!.Value;

    private static PortalConversation Conversation(
        string id,
        string phone = "",
        string? language = null,
        string topic = "",
        bool anonymized = false,
        DateTimeOffset? deletion = null,
        IReadOnlyList<PortalConversationEvent>? events = null) => new()
    {
        Id = id,
        TenantId = "club-a",
        UserPhone = phone,
        Language = language,
        Topic = topic,
        Anonymized = anonymized,
        DeletionRequestedAt = deletion,
        Events = events ?? []
    };

    private static PortalConversationEvent Event(string type, string text, DateTimeOffset at) => new()
    {
        Type = type,
        Text = text,
        At = at
    };
}
