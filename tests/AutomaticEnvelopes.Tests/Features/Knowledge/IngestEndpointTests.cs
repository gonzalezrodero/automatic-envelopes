using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Knowledge;
using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Moq;
using Moq.AutoMock;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace AutomaticEnvelopes.Tests.Features.Knowledge;

public class IngestEndpointTests
{
    private readonly AutoMocker mocker;
    private readonly IngestEndpoint sut;
    private readonly Mock<IDocumentSession> sessionMock;

    public IngestEndpointTests()
    {
        mocker = new AutoMocker();
        sut = mocker.CreateInstance<IngestEndpoint>();

        sessionMock = mocker.GetMock<IDocumentSession>();

        sessionMock.Setup(s => s.LoadAsync<TenantProfile>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((string id, CancellationToken ct) =>
                       id == "TestTenant" || id == "ClubSama-123"
                       ? new TenantProfile { Id = id }
                       : null);
    }

    [Fact]
    public async Task Ingest_FileOverByteCap_Returns413()
    {
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(IngestLimits.MaxUploadBytes + 1);
        fileMock.Setup(f => f.FileName).Returns("big.pdf");

        var result = await sut.Ingest(
            "TestTenant",
            fileMock.Object,
            sessionMock.Object,
            [],
            mocker.GetMock<IKnowledgeIngestionService>().Object,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task Ingest_TextOverCharacterCap_Returns400()
    {
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.FileName).Returns("notes.txt");
        fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

        var extractor = new Mock<IDocumentExtractor>();
        extractor.Setup(e => e.SupportedExtensions).Returns([".txt"]);
        extractor.Setup(e => e.ExtractTextAsync(It.IsAny<Stream>()))
            .ReturnsAsync(new string('a', IngestLimits.MaxCharacters + 1));

        var service = mocker.GetMock<IKnowledgeIngestionService>();
        var result = await sut.Ingest(
            "TestTenant",
            fileMock.Object,
            sessionMock.Object,
            [extractor.Object],
            service.Object,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        service.Verify(
            s => s.IngestDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Ingest_PdfOverPageCap_Returns400()
    {
        var builder = new PdfDocumentBuilder();
        for (var page = 0; page < IngestLimits.MaxPdfPages + 1; page++)
        {
            builder.AddPage(PageSize.A4);
        }

        var bytes = builder.Build();
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(bytes.Length);
        fileMock.Setup(f => f.FileName).Returns("long.pdf");
        fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(bytes));

        var service = mocker.GetMock<IKnowledgeIngestionService>();
        var result = await sut.Ingest(
            "TestTenant",
            fileMock.Object,
            sessionMock.Object,
            [new PdfDocumentExtractor()],
            service.Object,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        service.Verify(
            s => s.IngestDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("respuestas.md")]
    [InlineData("Respuestas.MD")]
    [InlineData("docs/respuestas.md")]
    public async Task Ingest_ReservedFileName_Returns400WithoutIngesting(string fileName)
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.FileName).Returns(fileName);

        var service = mocker.GetMock<IKnowledgeIngestionService>();
        var result = await sut.Ingest(
            "TestTenant",
            fileMock.Object,
            sessionMock.Object,
            [new PdfDocumentExtractor()],
            service.Object,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        ErrorOf(result).Should().Be($"The file name '{AnsweredKnowledge.FileName}' is reserved.");
        service.Verify(
            s => s.IngestDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Ingest_UnreadablePdf_ReturnsGenericBadRequest()
    {
        var bytes = "This is not a valid PDF binary"u8.ToArray();
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(bytes.Length);
        fileMock.Setup(f => f.FileName).Returns("broken.pdf");
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(bytes));

        var service = mocker.GetMock<IKnowledgeIngestionService>();
        var result = await sut.Ingest(
            "TestTenant",
            fileMock.Object,
            sessionMock.Object,
            [new PdfDocumentExtractor()],
            service.Object,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        ErrorOf(result).Should().Be(IngestLimits.PdfUnreadable);
        service.Verify(
            s => s.IngestDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Ingest_EmptyFile_ReturnsBadRequest()
    {
        // Arrange
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(0); // Simulate empty file

        var extractors = new List<IDocumentExtractor>();
        var service = mocker.GetMock<IKnowledgeIngestionService>().Object;

        // Act
        var result = await sut.Ingest("TestTenant", fileMock.Object, sessionMock.Object, extractors, service, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
              .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Ingest_UnregisteredTenant_ReturnsBadRequest()
    {
        // Arrange
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);

        var extractors = new List<IDocumentExtractor>();
        var service = mocker.GetMock<IKnowledgeIngestionService>().Object;

        // Act
        var result = await sut.Ingest("FakeTenant", fileMock.Object, sessionMock.Object, extractors, service, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
              .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Ingest_UnsupportedExtension_ReturnsBadRequest()
    {
        // Arrange
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.FileName).Returns("image.png");

        var pdfExtractorMock = new Mock<IDocumentExtractor>();
        pdfExtractorMock.Setup(e => e.SupportedExtensions).Returns([".pdf"]);

        var extractors = new List<IDocumentExtractor> { pdfExtractorMock.Object };
        var service = mocker.GetMock<IKnowledgeIngestionService>().Object;

        // Act
        var result = await sut.Ingest("TestTenant", fileMock.Object, sessionMock.Object, extractors, service, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
              .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Ingest_ExtensionWithoutAMime_ReturnsBadRequest()
    {
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.FileName).Returns("notes.csv");
        fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

        var extractor = new Mock<IDocumentExtractor>();
        extractor.Setup(e => e.SupportedExtensions).Returns([".csv"]);
        extractor.Setup(e => e.ExtractTextAsync(It.IsAny<Stream>())).ReturnsAsync("cells");

        var result = await sut.Ingest(
            "TestTenant",
            fileMock.Object,
            sessionMock.Object,
            [extractor.Object],
            mocker.GetMock<IKnowledgeIngestionService>().Object,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Ingest_ExtractorReturnsEmptyText_ReturnsBadRequest()
    {
        // Arrange
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.FileName).Returns("empty.pdf");
        fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

        var pdfExtractorMock = new Mock<IDocumentExtractor>();
        pdfExtractorMock.Setup(e => e.SupportedExtensions).Returns([".pdf"]);
        pdfExtractorMock.Setup(e => e.ExtractTextAsync(It.IsAny<Stream>())).ReturnsAsync(string.Empty);

        var extractors = new List<IDocumentExtractor> { pdfExtractorMock.Object };
        var service = mocker.GetMock<IKnowledgeIngestionService>().Object;

        // Act
        var result = await sut.Ingest("TestTenant", fileMock.Object, sessionMock.Object, extractors, service, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
              .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Ingest_ServiceThrowsGenericException_ReturnsProblem()
    {
        // Arrange
        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.FileName).Returns("error.pdf");
        fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

        var pdfExtractorMock = new Mock<IDocumentExtractor>();
        pdfExtractorMock.Setup(e => e.SupportedExtensions).Returns([".pdf"]);
        pdfExtractorMock.Setup(e => e.ExtractTextAsync(It.IsAny<Stream>())).ReturnsAsync("Extracted content");

        var extractors = new List<IDocumentExtractor> { pdfExtractorMock.Object };
        var serviceMock = mocker.GetMock<IKnowledgeIngestionService>();

        serviceMock
            .Setup(x => x.IngestDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Random failure in DB"));

        // Act
        var result = await sut.Ingest("TestTenant", fileMock.Object, sessionMock.Object, extractors, serviceMock.Object, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
              .Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Ingest_ValidFile_ReturnsOkAndCallsServiceWithCorrectTenant()
    {
        // Arrange
        var tenantId = "ClubSama-123";
        var fileName = "reglas_2026.md";
        var extractedText = "# Reglas 2026\n...";

        var fileMock = mocker.GetMock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

        var mdExtractorMock = new Mock<IDocumentExtractor>();
        mdExtractorMock.Setup(e => e.SupportedExtensions).Returns([".md", ".txt"]);
        mdExtractorMock.Setup(e => e.ExtractTextAsync(It.IsAny<Stream>())).ReturnsAsync(extractedText);

        var extractors = new List<IDocumentExtractor> { mdExtractorMock.Object };
        var serviceMock = mocker.GetMock<IKnowledgeIngestionService>();

        // Act
        var result = await sut.Ingest(tenantId, fileMock.Object, sessionMock.Object, extractors, serviceMock.Object, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
              .Which.StatusCode.Should().Be(200);

        serviceMock.Verify(x => x.IngestDocumentAsync(
            tenantId,
            extractedText,
            fileName,
            100,
            "text/markdown",
            It.IsAny<CancellationToken>()),
        Times.Once);
    }

    private static string ErrorOf(IResult result)
    {
        var value = result.Should().BeAssignableTo<IValueHttpResult>().Which.Value;
        value.Should().NotBeNull();
        return value!.GetType().GetProperty("Error")!.GetValue(value)!.ToString()!;
    }
}
