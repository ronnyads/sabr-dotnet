using System.Net;
using System.Net.Http.Json;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;

namespace Sabr.Infrastructure.Integrations.Mabang;

public sealed class MabangApiClient : IMabangApiClient
{
    private static readonly object CircuitLock = new();
    private static int _consecutiveFailures;
    private static DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    private readonly HttpClient _httpClient;
    private readonly MercadoLivreOptions _options;

    public MabangApiClient(HttpClient httpClient, Microsoft.Extensions.Options.IOptions<MercadoLivreOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task SendLabelAsync(MabangLabelDispatchRequest request, CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, _options.Resilience.RetryMaxAttempts);
        var baseDelayMs = Math.Max(50, _options.Resilience.RetryBaseDelayMs);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ThrowIfCircuitOpen();
            try
            {
                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    _options.Mabang.LabelEndpoint);
                if (!string.IsNullOrWhiteSpace(_options.Mabang.ApiKey))
                {
                    httpRequest.Headers.TryAddWithoutValidation(
                        string.IsNullOrWhiteSpace(_options.Mabang.ApiKeyHeader) ? "X-Api-Key" : _options.Mabang.ApiKeyHeader,
                        _options.Mabang.ApiKey);
                }

                httpRequest.Content = JsonContent.Create(new
                {
                    tenantId = request.TenantId,
                    clientId = request.ClientId,
                    sellerId = request.SellerId,
                    shipmentId = request.ShipmentId,
                    contentType = request.ContentType,
                    labelSha256 = request.LabelSha256,
                    labelBase64 = request.LabelBase64
                });

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    RegisterSuccess();
                    return;
                }

                if (!IsTransient(response.StatusCode) || attempt == maxAttempts)
                {
                    RegisterFailure();
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"Mabang label dispatch failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}",
                        null,
                        response.StatusCode);
                }

                RegisterFailure();
                await Task.Delay(CalculateDelay(baseDelayMs, attempt), cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                lastException = ex;
                RegisterFailure();
                await Task.Delay(CalculateDelay(baseDelayMs, attempt), cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                RegisterFailure();
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("Mabang label dispatch failed");
    }

    private void ThrowIfCircuitOpen()
    {
        lock (CircuitLock)
        {
            if (_circuitOpenUntil > DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("MABANG_CIRCUIT_OPEN");
            }
        }
    }

    private void RegisterSuccess()
    {
        lock (CircuitLock)
        {
            _consecutiveFailures = 0;
            _circuitOpenUntil = DateTimeOffset.MinValue;
        }
    }

    private void RegisterFailure()
    {
        lock (CircuitLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= Math.Max(1, _options.Resilience.CircuitBreakerFailureThreshold))
            {
                _circuitOpenUntil = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Max(1, _options.Resilience.CircuitBreakerDurationSeconds));
                _consecutiveFailures = 0;
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.RequestTimeout)
        {
            return true;
        }

        return (int)statusCode >= 500;
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException or HttpRequestException)
        {
            if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
            {
                return IsTransient(httpEx.StatusCode.Value);
            }

            return true;
        }

        return false;
    }

    private static TimeSpan CalculateDelay(int baseDelayMs, int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var ms = (int)Math.Min(10000, baseDelayMs * multiplier);
        return TimeSpan.FromMilliseconds(ms);
    }
}
