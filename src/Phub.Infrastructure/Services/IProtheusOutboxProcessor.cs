using Phub.Domain.Entities;

namespace Phub.Infrastructure.Services;

public interface IProtheusOutboxProcessor
{
    Task ProcessAsync(ProtheusOutboxEvent item, CancellationToken cancellationToken = default);
}
