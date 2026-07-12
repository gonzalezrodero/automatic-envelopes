using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.AutoMock;
using System.Security.Cryptography;
using System.Text;

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

        // Extraemos el mock del logger para poder verificar las alertas
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
    public async Task ExtractMessageAsync_WithStatusUpdate_ReturnsNullAndLogsWarning()
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
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("No messages array found") || v.ToString().Contains("Status update")),
                null,
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExtractMessageAsync_WithNonTextMessage_ReturnsNullAndLogsWarning()
    {
        // Arrange
        // Payload que contiene un mensaje, pero no es de texto (ej. imagen, audio)
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
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Unsupported message type") || v.ToString().Contains("image")),
                null,
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.AtLeastOnce);
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
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error") || v.ToString()!.Contains("Deserialization")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)!),
            Times.AtLeastOnce);
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