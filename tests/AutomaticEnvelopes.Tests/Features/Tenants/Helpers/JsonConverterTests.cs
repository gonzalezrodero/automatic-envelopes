using Amazon.Runtime.Documents;
using AutomaticEnvelopes.Api.Features.Tenants.Helpers;
using AwesomeAssertions;
using System.Text.Json;

namespace AutomaticEnvelopes.Tests.Features.Tenants.Helpers;

public class JsonConverterTests
{
    [Fact]
    public void ToAwsDocument_ParsesAllJsonTypesCorrectly()
    {
        // Arrange
        var json = """
        {
            "text": "hello",
            "integer": 42,
            "float": 3.14,
            "isActive": true,
            "isDeleted": false,
            "nullValue": null,
            "array": [1, 2]
        }
        """;
        using var document = JsonDocument.Parse(json);

        // Act
        var result = JsonConverter.ToAwsDocument(document.RootElement);

        // Assert
        var dict = result.AsDictionary();
        dict["text"].AsString().Should().Be("hello");
        dict["integer"].AsInt().Should().Be(42);
        dict["float"].AsDouble().Should().Be(3.14);
        dict["isActive"].AsBool().Should().BeTrue();
        dict["isDeleted"].AsBool().Should().BeFalse();
        dict["nullValue"].IsNull().Should().BeTrue();
        dict["array"].AsList().Count.Should().Be(2);
    }

    [Fact]
    public void ToJsonString_SerializesComplexAwsDocumentCorrectly()
    {
        // Arrange: Create a nested AWS Document with all mapped types
        var dict = new Dictionary<string, Document>
        {
            { "text", new Document("hello") },
            { "integer", new Document(42) },
            { "float", new Document(3.14) },
            { "isActive", new Document(true) },
            { "isDeleted", new Document(false) },
            { "nullValue", new Document() }, // This generates a null Document type
            { "array", new Document(new List<Document> { new Document(1), new Document(2) }) },
            { "nestedObj", new Document(new Dictionary<string, Document> { { "child", new Document("value") } }) }
        };
        var awsDocument = new Document(dict);

        // Act
        var jsonString = JsonConverter.ToJsonString(awsDocument);

        // Assert: Parse the generated string back to verify structure without relying on exact string spacing
        using var jsonDoc = JsonDocument.Parse(jsonString);
        var root = jsonDoc.RootElement;

        root.GetProperty("text").GetString().Should().Be("hello");
        root.GetProperty("integer").GetInt32().Should().Be(42);
        root.GetProperty("float").GetDouble().Should().Be(3.14);
        root.GetProperty("isActive").GetBoolean().Should().BeTrue();
        root.GetProperty("isDeleted").GetBoolean().Should().BeFalse();
        root.GetProperty("nullValue").ValueKind.Should().Be(JsonValueKind.Null);

        root.GetProperty("array").GetArrayLength().Should().Be(2);
        root.GetProperty("array")[0].GetInt32().Should().Be(1);

        root.GetProperty("nestedObj").GetProperty("child").GetString().Should().Be("value");
    }

    [Fact]
    public void ToJsonString_HandlesFallbackForUnmappedTypes()
    {
        // Arrange: Passing a long integer to test the fallback else branch
        // AWS Document does not expose an IsLong() method, so it hits the .ToString() fallback
        var longDoc = new Document(9223372036854775807L);

        // Act
        var jsonString = JsonConverter.ToJsonString(longDoc);

        // Assert
        jsonString.Should().Be("\"9223372036854775807\""); // Fallback writes as a string
    }
}