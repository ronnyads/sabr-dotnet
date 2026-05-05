using Phub.Application.Models;

namespace Phub.Application.Abstractions;

public interface IMabangApiClient
{
    Task SendLabelAsync(MabangLabelDispatchRequest request, CancellationToken cancellationToken = default);
}
