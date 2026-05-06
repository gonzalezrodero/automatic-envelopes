using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Moq;
using SamaBot.Api.Common.Extensions;

namespace SamaBot.Tests.Extensions;

public class AwsConfigurationExtensionsTests : IDisposable
{
    private readonly Mock<IAmazonSecretsManager> secretsMock = new();
    private readonly Mock<IAmazonSimpleSystemsManagement> ssmMock = new();
    private readonly WebApplicationBuilder builder;

    public AwsConfigurationExtensionsTests()
    {
        builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        ClearEnvironmentVariables();
    }

    [Fact]
    public void AddAwsSecureConfigurationCore_WithMartenArn_InjectsConnectionString()
    {
        // Arrange
        var testArn = "arn:aws:secretsmanager:eu-west-1:123:secret:marten";
        Environment.SetEnvironmentVariable("SECRET_ARN_MARTEN", testArn);
        Environment.SetEnvironmentVariable("DB_HOST", "localhost:5432");

        var secretJson = "{\"username\":\"dbadmin\", \"password\":\"secret123\"}";

        secretsMock
            .Setup(x => x.GetSecretValueAsync(It.Is<GetSecretValueRequest>(req => req.SecretId == testArn), default))
            .ReturnsAsync(new GetSecretValueResponse { SecretString = secretJson });

        // Act
        builder.AddAwsSecureConfigurationCore(secretsMock.Object, ssmMock.Object);

        // Assert
        var injectedValue = builder.Configuration["ConnectionStrings:Marten"];
        injectedValue.Should().NotBeNull();
        injectedValue.Should().Contain("Host=localhost");
        injectedValue.Should().Contain("Port=5432");
        injectedValue.Should().Contain("Username=dbadmin");
        injectedValue.Should().Contain("Database=chatbot");

        secretsMock.Verify(x => x.GetSecretValueAsync(It.IsAny<GetSecretValueRequest>(), default), Times.Once);
        ssmMock.Verify(x => x.GetParametersByPathAsync(It.IsAny<GetParametersByPathRequest>(), default), Times.Never);
    }

    [Fact]
    public void AddAwsSecureConfigurationCore_WithSsmPath_InjectsWhatsAppConfigurationInPascalCase()
    {
        // Arrange
        var testPath = "/chatbot/testing/whatsapp";
        Environment.SetEnvironmentVariable("SSM_PATH_WHATSAPP", testPath);

        var ssmResponse = new GetParametersByPathResponse
        {
            Parameters =
            [
                new Parameter { Name = $"{testPath}/app-secret", Value = "my-secret-123" },
                new Parameter { Name = $"{testPath}/verify-token", Value = "verify-me" }
            ]
        };

        ssmMock
            .Setup(x => x.GetParametersByPathAsync(It.Is<GetParametersByPathRequest>(req => req.Path == testPath && req.WithDecryption == true), default))
            .ReturnsAsync(ssmResponse);

        // Act
        builder.AddAwsSecureConfigurationCore(secretsMock.Object, ssmMock.Object);

        // Assert
        builder.Configuration["WhatsApp:AppSecret"].Should().Be("my-secret-123");
        builder.Configuration["WhatsApp:VerifyToken"].Should().Be("verify-me");

        ssmMock.Verify(x => x.GetParametersByPathAsync(It.IsAny<GetParametersByPathRequest>(), default), Times.Once);
        secretsMock.Verify(x => x.GetSecretValueAsync(It.IsAny<GetSecretValueRequest>(), default), Times.Never);
    }

    [Fact]
    public void AddAwsSecureConfigurationCore_WithoutEnvironmentVariables_DoesNotCallAws()
    {
        // Arrange
        // (Environment variables are already cleared in constructor)

        // Act
        builder.AddAwsSecureConfigurationCore(secretsMock.Object, ssmMock.Object);

        // Assert
        builder.Configuration["ConnectionStrings:Marten"].Should().BeNull();
        builder.Configuration["WhatsApp:AppSecret"].Should().BeNull();

        secretsMock.Verify(x => x.GetSecretValueAsync(It.IsAny<GetSecretValueRequest>(), default), Times.Never);
        ssmMock.Verify(x => x.GetParametersByPathAsync(It.IsAny<GetParametersByPathRequest>(), default), Times.Never);
    }

    public void Dispose()
    {
        ClearEnvironmentVariables();
        GC.SuppressFinalize(this);
    }

    private static void ClearEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("SECRET_ARN_MARTEN", null);
        Environment.SetEnvironmentVariable("SSM_PATH_WHATSAPP", null);
    }
}