using AutomaticEnvelopes.Api.Features.Usage;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests;

public static class TestUsage
{
    public static UsageMeter Meter(IDocumentStore store) =>
        new(store, Options.Create(new UsageOptions()), NullLogger<UsageMeter>.Instance);
}
