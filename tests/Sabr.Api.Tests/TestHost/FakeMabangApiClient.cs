using Sabr.Application.Abstractions;
using Sabr.Application.Models;

namespace Sabr.Api.Tests.TestHost;

public sealed class FakeMabangApiClient : IMabangApiClient
{
    public List<MabangLabelDispatchRequest> Requests { get; } = new();
    public int FailuresRemaining { get; set; }

    public Task SendLabelAsync(MabangLabelDispatchRequest request, CancellationToken cancellationToken = default)
    {
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new HttpRequestException("Simulated Mabang transient failure", null, System.Net.HttpStatusCode.TooManyRequests);
        }

        Requests.Add(request);
        return Task.CompletedTask;
    }

    public void Reset()
    {
        Requests.Clear();
        FailuresRemaining = 0;
    }
}
