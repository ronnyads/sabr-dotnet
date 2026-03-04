using Sabr.Domain.Entities;

namespace Sabr.Infrastructure.Services;

public interface IProtheusOutboxProcessor
{
    Task ProcessAsync(ProtheusOutboxEvent item, CancellationToken cancellationToken = default);
}
