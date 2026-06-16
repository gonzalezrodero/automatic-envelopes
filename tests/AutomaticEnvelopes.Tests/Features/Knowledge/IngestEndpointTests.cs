using AutomaticEnvelopes.Api.Features.Knowledge;
using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Moq;
using Moq.AutoMock;

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
            .Setup(x => x.IngestDocumentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            It.IsAny<CancellationToken>()),
        Times.Once);
    }
}