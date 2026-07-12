using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.AutoMock;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace AutomaticEnvelopes.Tests.Features.WhatsAppWebhook;

public class WhatsAppPayloadProcessorTests
{
    private readonly AutoMocker mocker;
    private readonly WhatsAppPayloadProcessor sut;
    private readonly Mock<ILogger<WhatsAppPayloadProcessor>> loggerMock;
    private const string TestSecret = "my_super_secret_test_key";

    public WhatsAppPayloadProcessorTests()
    {
        mocker = new AutoMocker();

        var options = Options.Create(new WhatsAppOptions
        {
            AppSecret = TestSecret
        });
        mocker.Use(options);

        // Extract the logger mock to verify the structured logging alerts
        loggerMock = mocker.GetMock<ILogger<WhatsAppPayloadProcessor>>();

        sut = mocker.CreateInstance<WhatsAppPayloadProcessor>();
    }

    [Fact]
    public async Task IsSignatureValidAsync_WithCorrectSignature_ReturnsTrue()
    {
        // Arrange
        var payload = """{"test":"payload"}""";
        var expectedHash = ComputeHmacSha256(payload, TestSecret);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Hub-Signature-256"] = $"sha256={expectedHash}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.IsSignatureValidAsync(context.Request);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsSignatureValidAsync_WithInvalidSignature_ReturnsFalse()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Hub-Signature-256"] = "sha256=invalid_hash";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"test":"payload"}"""));

        // Act
        var result = await sut.IsSignatureValidAsync(context.Request);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha1=invalidformat")]
    public async Task IsSignatureValidAsync_WithMissingOrMalformedSignature_ReturnsFalseAndLogsWarning(string signature)
    {
        // Arrange
        var context = new DefaultHttpContext();
        if (!string.IsNullOrEmpty(signature))
        {
            context.Request.Headers["X-Hub-Signature-256"] = signature;
        }
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"test":"payload"}"""));

        // Act
        var result = await sut.IsSignatureValidAsync(context.Request);

        // Assert
        result.Should().BeFalse();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Missing or malformed 'X-Hub-Signature-256' header")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithValidMetaPayload_ReturnsParsedMessage()
    {
        // Arrange
        var payload = """
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "changes": [
                {
                  "value": {
                    "metadata": { "phone_number_id": "12345" },
                    "messages": [
                      {
                        "from": "34666555444",
                        "id": "wamid.HBgL",
                        "timestamp": "1603059201",
                        "text": { "body": "Hola SamàBot!" },
                        "type": "text"
                      }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().NotBeNull();
        result!.MessageId.Should().Be("wamid.HBgL");
        result.BotPhoneNumberId.Should().Be("12345");
        result.PhoneNumber.Should().Be("34666555444");
        result.Text.Should().Be("Hola SamàBot!");
        result.Timestamp.ToUnixTimeSeconds().Should().Be(1603059201);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithStatusUpdate_ReturnsNullAndLogsDebug()
    {
        // Arrange
        var payload = """
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "changes": [
                {
                  "value": {
                    "metadata": { "phone_number_id": "12345" },
                    "statuses": [
                      {
                        "id": "wamid.HBgL",
                        "status": "read",
                        "timestamp": "1603059202"
                      }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("missing 'messages' array")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithNonTextMessage_ReturnsNullAndLogsInformation()
    {
        // Arrange
        var payload = """
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "changes": [
                {
                  "value": {
                    "metadata": { "phone_number_id": "12345" },
                    "messages": [
                      {
                        "from": "34666555444",
                        "id": "wamid.HBgL",
                        "timestamp": "1603059201",
                        "type": "image",
                        "image": { "id": "987654321" }
                      }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Received non-text message type")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithMissingEntryArray_ReturnsNullAndLogsDebug()
    {
        // Arrange
        // JSON missing the "entry" array completely
        var payload = """{ "object": "whatsapp_business_account" }""";

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("missing 'entry' array")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithMissingChangesArray_ReturnsNullAndLogsDebug()
    {
        // Arrange
        // JSON containing entry, but missing the "changes" array
        var payload = """{ "object": "whatsapp_business_account", "entry": [ { "id": "123" } ] }""";

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("missing 'changes' array within entry")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithMissingEssentialFields_ReturnsNullAndLogsWarning()
    {
        // Arrange
        // We include the keys so GetProperty() doesn't throw KeyNotFoundException, 
        // but we leave their values empty to trigger the string.IsNullOrEmpty validation block.
        var payload = """
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "changes": [
                {
                  "value": {
                    "metadata": { "phone_number_id": "" },
                    "messages": [
                      {
                        "from": "",
                        "id": "",
                        "timestamp": "",
                        "type": "text",
                        "text": { "body": "" }
                      }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Message payload is missing essential fields")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithMalformedJson_ReturnsNullAndLogsError()
    {
        // Arrange
        var malformedPayload = """{ "object": "whatsapp_business_account", "entry": [ """;

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(malformedPayload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to parse incoming WhatsApp webhook payload as valid JSON")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WhenUnexpectedExceptionOccurs_LogsError()
    {
        // Arrange
        // Passing an object where an array is expected will trigger an InvalidOperationException 
        // when GetArrayLength() is called on the "entry" property, falling into the generic catch.
        var payload = """{ "object": "whatsapp_business_account", "entry": { "invalid_type": "not_an_array" } }""";

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unexpected error occurred while parsing WhatsApp message payload")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithEmptyBody_ReturnsNull()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream();

        // Act
        var result = await sut.ExtractMessageAsync(context.Request);

        // Assert
        result.Should().BeNull();
    }

    private static string ComputeHmacSha256(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hashBytes);
    }
}