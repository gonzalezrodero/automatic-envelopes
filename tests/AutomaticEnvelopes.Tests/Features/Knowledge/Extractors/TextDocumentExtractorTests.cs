using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AwesomeAssertions;
using System.Text;

namespace AutomaticEnvelopes.Tests.Features.Knowledge.Extractors;

public class TextDocumentExtractorTests
{
    private readonly TextDocumentExtractor sut = new();

    [Fact]
    public void SupportedExtensions_ShouldContainMdAndTxt()
    {
        // Act
        var extensions = sut.SupportedExtensions;

        // Assert
        extensions.Should().BeEquivalentTo([".md", ".txt"]);
    }

    [Fact]
    public async Task ExtractTextAsync_ValidStream_ReturnsExtractedText()
    {
        // Arrange
        var expectedText = "# Test Document\nThis is a sample markdown text.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(expectedText));

        // Act
        var result = await sut.ExtractTextAsync(stream);

        // Assert
        result.Should().Be(expectedText);
    }
}