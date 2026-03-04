using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Models;
using Sabr.Application.Services;

namespace Sabr.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/integrations/mercadolivre")]
public sealed class MercadoLivreWebhookController : ControllerBase
{
    private readonly MercadoLivreWebhookService _webhookService;

    public MercadoLivreWebhookController(MercadoLivreWebhookService webhookService)
    {
        _webhookService = webhookService;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> ReceiveWebhook(
        [FromBody] MercadoLivreWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        var secret = Request.Query["secret"].ToString();
        if (string.IsNullOrWhiteSpace(secret) && Request.Headers.TryGetValue("X-Webhook-Secret", out var headerSecret))
        {
            secret = headerSecret.ToString();
        }

        var result = await _webhookService.IngestAsync(payload, secret, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            var code = result.Errors.Any(item => string.Equals(item.Message, "ML_WEBHOOK_VERIFICATION_FAILED", StringComparison.OrdinalIgnoreCase))
                ? "ML_WEBHOOK_VERIFICATION_FAILED"
                : "ML_WEBHOOK_REJECTED";
            var status = code == "ML_WEBHOOK_VERIFICATION_FAILED"
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status400BadRequest;

            return StatusCode(status, new ApiError
            {
                Code = code,
                Message = "Webhook validation failed",
                Errors = result.Errors,
                TraceId = HttpContext.TraceIdentifier
            });
        }

        return StatusCode(StatusCodes.Status202Accepted, result.Data);
    }
}
