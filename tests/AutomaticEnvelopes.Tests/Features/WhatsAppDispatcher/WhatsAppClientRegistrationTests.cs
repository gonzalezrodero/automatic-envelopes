using System.Net;
using AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace AutomaticEnvelopes.Tests.Features.WhatsAppDispatcher;

public class WhatsAppClientRegistrationTests
{
    [Fact]
    public async Task Container_resolves_the_generated_whatsapp_client()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WhatsApp:BaseUrl"] = "https://graph.facebook.com/v17.0/"
            })
            .Build();
        var capture = new CaptureHandler();
        var services = new ServiceCollection();
        services.AddWhatsAppDispatcherFeature(config);
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
        {
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = capture);
        });

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWhatsAppClient>();

        client.GetType().Namespace.Should().Be("Refit.Implementation");

        var response = await client.SendMessageAsync(
            "26096431083348486",
            new WhatsAppTextRequest("34600111222", new WhatsAppMessageBody("Hola")),
            "Bearer test-token",
            CancellationToken.None);

        response.Messages.Should().ContainSingle().Which.Id.Should().Be("wamid.out");
        capture.Request.Should().NotBeNull();
        capture.Request!.Method.Should().Be(HttpMethod.Post);
        capture.Request.RequestUri!.AbsolutePath.Should().Be("/v17.0/26096431083348486/messages");
        capture.Request.Headers.Authorization!.ToString().Should().Be("Bearer test-token");
        capture.Body.Should().Contain("\"to\":\"34600111222\"");
        capture.Body.Should().Contain("\"body\":\"Hola\"");
        capture.Body.Should().Contain("\"messaging_product\":\"whatsapp\"");
        capture.Body.Should().Contain("\"type\":\"text\"");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"messaging_product":"whatsapp","contacts":[],"messages":[{"id":"wamid.out"}]}""")
            };
        }
    }
}
