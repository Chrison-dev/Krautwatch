using Krautwatch.Domain.Interfaces;
using Wolverine;

namespace Krautwatch.Infrastructure.Messaging;

/// <summary>
/// The Wolverine adapter for the <see cref="IMessageDispatcher"/> dispatch port (DR-009 §5). The
/// transport (Postgres by default) is configured at the host level via <c>UseWolverine</c>; this
/// simply forwards Application message contracts onto the bus.
/// </summary>
public sealed class WolverineDispatcher(IMessageBus bus) : IMessageDispatcher
{
    public Task PublishAsync(object message, CancellationToken ct = default) =>
        bus.PublishAsync(message).AsTask();
}
