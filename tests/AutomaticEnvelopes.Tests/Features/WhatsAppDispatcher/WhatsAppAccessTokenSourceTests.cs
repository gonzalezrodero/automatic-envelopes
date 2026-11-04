using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.WhatsAppDispatcher;

public class WhatsAppAccessTokenSourceTests
{
    private const string TenantToken = "tenant-access-token-value";
    private const string SharedToken = "shared-access-token-value";
    private const string TenantId = "club-a";

    private readonly Mock<IAmazonSimpleSystemsManagement> ssm = new();
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly CaptureLogger logger = new();

    [Fact]
    public async Task LoadsTheTenantParameterOnce_ThenUsesTheCache()
    {
        SetupParameter(TenantId, TenantToken);
        var source = CreateSource();

        var first = await source.GetAccessTokenAsync(TenantId, CancellationToken.None);
        clock.UtcNow = clock.UtcNow.Add(WhatsAppAccessTokenSource.CacheDuration).AddTicks(-1);
        var second = await source.GetAccessTokenAsync(TenantId, CancellationToken.None);

        first.Should().Be(TenantToken);
        second.Should().Be(TenantToken);
        ssm.Verify(client => client.GetParameterAsync(
            It.Is<GetParameterRequest>(request =>
                request.Name == "/automatic-envelopes/whatsapp/tenants/club-a/access-token" &&
                request.WithDecryption == true),
            It.IsAny<CancellationToken>()), Times.Once);
        logger.Text.Should().NotContain(TenantToken);
        logger.Text.Should().NotContain(SharedToken);
    }

    [Fact]
    public async Task ReadsTheParameterAgainAfterTheCacheExpires()
    {
        SetupParameter(TenantId, TenantToken);
        var source = CreateSource();

        await source.GetAccessTokenAsync(TenantId, CancellationToken.None);
        clock.UtcNow = clock.UtcNow.Add(WhatsAppAccessTokenSource.CacheDuration);
        var refreshed = await source.GetAccessTokenAsync(TenantId, CancellationToken.None);

        refreshed.Should().Be(TenantToken);
        ssm.Verify(client => client.GetParameterAsync(
            It.IsAny<GetParameterRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task MissingParameter_Throws_AndIsNotCached()
    {
        ssm.Setup(client => client.GetParameterAsync(
                It.IsAny<GetParameterRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ParameterNotFoundException("missing"));
        var source = CreateSource();
        const string path = "/automatic-envelopes/whatsapp/tenants/club-a/access-token";

        var first = () => source.GetAccessTokenAsync(TenantId, CancellationToken.None);
        var second = () => source.GetAccessTokenAsync(TenantId, CancellationToken.None);

        (await first.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(TenantId).And.Contain(path);
        (await second.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(path);
        ssm.Verify(client => client.GetParameterAsync(
            It.IsAny<GetParameterRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        logger.Text.Should().Contain(TenantId);
        logger.Text.Should().Contain(path);
        logger.Text.Should().NotContain(SharedToken);
        logger.Text.Should().NotContain(TenantToken);
    }

    [Fact]
    public async Task EmptyParameter_ThrowsTheParameterPath()
    {
        SetupParameter(TenantId, "  ");
        var source = CreateSource();
        const string path = "/automatic-envelopes/whatsapp/tenants/club-a/access-token";

        var act = () => source.GetAccessTokenAsync(TenantId, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(TenantId).And.Contain(path).And.NotContain("  ");
        logger.Text.Should().Contain(path);
        logger.Text.Should().NotContain(SharedToken);
    }

    [Fact]
    public async Task DifferentTenants_DoNotShareACacheEntry()
    {
        SetupParameter("club-a", TenantToken);
        SetupParameter("club-b", "other-tenant-access-token");
        var source = CreateSource();

        var first = await source.GetAccessTokenAsync("club-a", CancellationToken.None);
        var second = await source.GetAccessTokenAsync("club-b", CancellationToken.None);
        var firstAgain = await source.GetAccessTokenAsync("club-a", CancellationToken.None);

        first.Should().Be(TenantToken);
        second.Should().Be("other-tenant-access-token");
        firstAgain.Should().Be(TenantToken);
        ssm.Verify(client => client.GetParameterAsync(
            It.Is<GetParameterRequest>(request => request.Name.EndsWith("/club-a/access-token", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        ssm.Verify(client => client.GetParameterAsync(
            It.Is<GetParameterRequest>(request => request.Name.EndsWith("/club-b/access-token", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        logger.Text.Should().NotContain("other-tenant-access-token");
    }

    [Fact]
    public async Task SsmErrorsOtherThanNotFound_AreNotCached()
    {
        ssm.Setup(client => client.GetParameterAsync(
                It.IsAny<GetParameterRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonServiceException("AccessDenied"));
        var source = CreateSource();

        var first = () => source.GetAccessTokenAsync(TenantId, CancellationToken.None);
        var second = () => source.GetAccessTokenAsync(TenantId, CancellationToken.None);

        await first.Should().ThrowAsync<AmazonServiceException>();
        await second.Should().ThrowAsync<AmazonServiceException>();
        ssm.Verify(client => client.GetParameterAsync(
            It.IsAny<GetParameterRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Club_A")]
    [InlineData("../other")]
    public async Task InvalidTenantId_DoesNotCallSsm(string tenantId)
    {
        var source = CreateSource();

        var act = () => source.GetAccessTokenAsync(tenantId, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain($"tenant '{tenantId}'").And.Contain(WhatsAppAccessTokenSource.ParameterPathPattern);
        ssm.Verify(client => client.GetParameterAsync(
            It.IsAny<GetParameterRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        logger.Text.Should().Contain("cannot be read because the tenant id is invalid");
        logger.Text.Should().Contain(WhatsAppAccessTokenSource.ParameterPathPattern);
        logger.Text.Should().NotContain(SharedToken);
        logger.Text.Should().NotContain(TenantToken);
    }

    [Fact]
    public async Task WithoutSsm_UsesTheConfiguredToken()
    {
        var source = new WhatsAppAccessTokenSource(
            null,
            Options.Create(new WhatsAppOptions { AccessToken = SharedToken }),
            logger,
            clock);

        var token = await source.GetAccessTokenAsync(TenantId, CancellationToken.None);

        token.Should().Be(SharedToken);
        logger.Text.Should().NotContain(SharedToken);
    }

    private WhatsAppAccessTokenSource CreateSource() =>
        new(ssm.Object, Options.Create(new WhatsAppOptions { AccessToken = SharedToken }), logger, clock);

    private void SetupParameter(string tenantId, string value)
    {
        var name = WhatsAppAccessTokenSource.ParameterName(tenantId);
        ssm.Setup(client => client.GetParameterAsync(
                It.Is<GetParameterRequest>(request => request.Name == name && request.WithDecryption == true),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetParameterResponse
            {
                Parameter = new Parameter { Name = name, Value = value }
            });
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CaptureLogger : ILogger<WhatsAppAccessTokenSource>
    {
        public string Text => string.Join('\n', Lines);

        private List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
                Lines.Add(exception.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
