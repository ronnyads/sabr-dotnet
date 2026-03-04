using Sabr.Application.Models;

namespace Sabr.Application.Abstractions;

public interface IMabangApiClient
{
    Task SendLabelAsync(MabangLabelDispatchRequest request, CancellationToken cancellationToken = default);
}
