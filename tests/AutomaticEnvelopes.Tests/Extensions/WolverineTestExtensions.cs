using Alba;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;

namespace AutomaticEnvelopes.Tests.Extensions;

public static class WolverineTestExtensions
{
    /// <summary>
    /// Directly publishes a message to the Wolverine bus and tracks its execution, waiting until all cascading 
    /// handlers (and their cascade handlers) finish processing entirely before yielding back.
    /// Ideal for atomic Component Tests targeting specific Handlers without going through the HTTP pipeline.
    /// </summary>
    public static async Task<ITrackedSession> InvokeMessageAndWaitAsync(this IAlbaHost host, object message)
    {
        return await host.ExecuteAndWaitAsync(async () => 
        {
            using var scope = host.Services.CreateScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);
        });
    }
}
