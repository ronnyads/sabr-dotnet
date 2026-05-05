using Phub.Domain.Entities;

namespace Phub.Infrastructure.Services;

/// <summary>
/// Mock processor used until real Protheus sync is plugged in.
/// </summary>
public sealed class MockProtheusOutboxProcessor : IProtheusOutboxProcessor
{
    public Task ProcessAsync(ProtheusOutboxEvent item, CancellationToken cancellationToken = default)
    {
        // Intentionally no-op for now. Events are marked as processed by the worker.
        return Task.CompletedTask;
    }
}
