using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantSlug}/clients/{clientId:guid}/integrations/tiktokshop")]
public sealed class AdminTenantTikTokShopController : ControllerBase
{
    private readonly TikTokShopOAuthService _oauthService;
    private readonly ILogger<AdminTenantTikTokShopController> _logger;

    public AdminTenantTikTokShopController(
        TikTokShopOAuthService oauthService,
        ILogger<AdminTenantTikTokShopController> logger)
    {
        _oauthService = oauthService;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var result = await _oauthService.GetClientStatusAsync(tenantSlug, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> ForceDisconnect(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Admin force-disconnect TikTok Shop. tenantSlug={TenantSlug} clientId={ClientId} adminUser={AdminUser} traceId={TraceId}",
            tenantSlug, clientId, User.Identity?.Name, HttpContext.TraceIdentifier);

        var result = await _oauthService.DisconnectAsync(tenantSlug, clientId, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return NoContent();
    }

    private IActionResult MapValidationError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Any(e => string.Equals(e.Field, "connection", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(new ApiError
            {
                Code = "TIKTOK_SHOP_NOT_CONNECTED",
                Message = "TikTok Shop connection not found",
                Errors = errors,
                TraceId = HttpContext.TraceIdentifier
            });
        }

        return BadRequest(new ApiError
        {
            Code = "VALIDATION_ERROR",
            Message = "Invalid request",
            Errors = errors,
            TraceId = HttpContext.TraceIdentifier
        });
    }
}
