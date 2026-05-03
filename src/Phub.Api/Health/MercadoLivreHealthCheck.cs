using Microsoft.Extensions.Diagnostics.HealthChecks;
using Phub.Application.Abstractions;

namespace Phub.Api.Health;

/// <summary>
/// Verifica se a API do Mercado Livre está acessível (ping no endpoint de categorias).
/// Usado como health check "degraded" — não bloqueia readiness, apenas reporta.
/// </summary>
public sealed class MercadoLivreHealthCheck : IHealthCheck
{
    private readonly IMercadoLivreApiClient _mlClient;
    private readonly ILogger<MercadoLivreHealthCheck> _logger;

    public MercadoLivreHealthCheck(IMercadoLivreApiClient mlClient, ILogger<MercadoLivreHealthCheck> logger)
    {
        _mlClient = mlClient;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            // Um ping leve — verifica se o endpoint de status do ML responde
            await _mlClient.PingAsync(cts.Token);

            return HealthCheckResult.Healthy("Mercado Livre API reachable.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MercadoLivre health check timed out.");
            return HealthCheckResult.Degraded("Mercado Livre API did not respond within 5s.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MercadoLivre health check failed.");
            return HealthCheckResult.Degraded("Mercado Livre API unreachable.", ex);
        }
    }
}
