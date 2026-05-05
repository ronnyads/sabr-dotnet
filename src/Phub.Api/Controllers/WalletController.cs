using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/wallet")]
public sealed class WalletController : ControllerBase
{
    private readonly WalletService _walletService;
    private readonly ITenantProvider _tenantProvider;

    public WalletController(WalletService walletService, ITenantProvider tenantProvider)
    {
        _walletService = walletService;
        _tenantProvider = tenantProvider;
    }

    [HttpPost("credit")]
    public async Task<IActionResult> Credit([FromBody] WalletCreditRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "Tenant not resolved" });
        }

        if (!TryGetIdempotencyKey(out var idempotencyKey, out var keyError))
        {
            return BadRequest(new { error = keyError });
        }

        if (!ValidateClientScope(request.ClientId, out var scopeError))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = scopeError });
        }

        var requestId = ResolveRequestId();
        var actorId = ResolveActorId();
        var actorType = ResolveActorType();

        var result = await _walletService.CreditAsync(
            tenantId,
            request,
            idempotencyKey,
            requestId,
            actorId,
            actorType,
            cancellationToken);

        return MapResult(result);
    }

    [HttpPost("debit")]
    public async Task<IActionResult> Debit([FromBody] WalletDebitRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "Tenant not resolved" });
        }

        if (!TryGetIdempotencyKey(out var idempotencyKey, out var keyError))
        {
            return BadRequest(new { error = keyError });
        }

        if (!ValidateClientScope(request.ClientId, out var scopeError))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = scopeError });
        }

        var requestId = ResolveRequestId();
        var actorId = ResolveActorId();
        var actorType = ResolveActorType();

        var result = await _walletService.DebitAsync(
            tenantId,
            request,
            idempotencyKey,
            requestId,
            actorId,
            actorType,
            cancellationToken);

        return MapResult(result);
    }

    private IActionResult MapResult(Phub.Application.Validation.ServiceResult<WalletOperationResult> result)
    {
        if (result.Succeeded && result.Data != null)
        {
            return Ok(result.Data);
        }

        if (result.Errors.Any(e => string.Equals(e.Field, "balance", StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { errors = result.Errors });
        }

        if (result.Errors.Any(e => string.Equals(e.Field, "idempotency", StringComparison.OrdinalIgnoreCase) &&
                                   e.Message.Contains("in progress", StringComparison.OrdinalIgnoreCase)))
        {
            Response.Headers.Append("Retry-After", "1");
            return Conflict(new { error = "InProgress", errors = result.Errors });
        }

        if (result.Errors.Any(e => string.Equals(e.Field, "idempotency", StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(new { errors = result.Errors });
        }

        return BadRequest(new { errors = result.Errors });
    }

    private bool TryGetIdempotencyKey(out string key, out string error)
    {
        if (Request.Headers.TryGetValue("Idempotency-Key", out var value))
        {
            key = value.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                error = string.Empty;
                return true;
            }
        }

        key = string.Empty;
        error = "Idempotency-Key header is required";
        return false;
    }

    private bool ValidateClientScope(Guid clientId, out string error)
    {
        var accountType = User.FindFirstValue("accountType");
        if (!string.Equals(accountType, "client", StringComparison.OrdinalIgnoreCase))
        {
            error = string.Empty;
            return true;
        }

        var claimClient = User.FindFirstValue("clientId");
        if (!Guid.TryParse(claimClient, out var claimClientId) || claimClientId != clientId)
        {
            error = "Client scope mismatch";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private Guid ResolveRequestId()
    {
        if (Request.Headers.TryGetValue("X-Request-Id", out var value) && Guid.TryParse(value.ToString(), out var parsed))
        {
            return parsed;
        }

        if (Guid.TryParse(HttpContext.TraceIdentifier, out var traceId))
        {
            return traceId;
        }

        return Guid.NewGuid();
    }

    private Guid? ResolveActorId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("clientId");
        return Guid.TryParse(sub, out var parsed) ? parsed : null;
    }

    private string ResolveActorType()
    {
        var accountType = User.FindFirstValue("accountType");
        return string.Equals(accountType, "client", StringComparison.OrdinalIgnoreCase)
            ? "TenantUser"
            : "PlatformUser";
    }
}
