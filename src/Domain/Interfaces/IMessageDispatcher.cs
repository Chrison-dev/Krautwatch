namespace Krautwatch.Domain.Interfaces;

/// <summary>
/// The dispatch port (DR-009 §5). The Application layer publishes message contracts through this
/// abstraction; the concrete transport (Wolverine — Postgres by default, RabbitMQ opt-in) is an
/// Infrastructure adapter. Keeps Application free of any transport dependency.
/// </summary>
public interface IMessageDispatcher
{
    Task PublishAsync(object message, CancellationToken ct = default);
}
