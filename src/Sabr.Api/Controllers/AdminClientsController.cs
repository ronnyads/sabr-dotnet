using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Api.Models;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantId:guid}/clients")]
public sealed class AdminClientsController : ControllerBase
{
    private readonly ClientService _clientService;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public AdminClientsController(ClientService clientService)
    {
        _clientService = clientService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromRoute] Guid tenantId,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            return BadRequest(new { errors = new[] { new { field = "skip", message = "Skip must be 0 or greater" } } });
        }

        if (limit <= 0 || limit > MaxPageSize)
        {
            return BadRequest(new { errors = new[] { new { field = "limit", message = $"Limit must be between 1 and {MaxPageSize}" } } });
        }

        ClientStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ClientStatus>(status, true, out var statusValue))
            {
                return BadRequest(new { errors = new[] { new { field = "status", message = "Invalid status" } } });
            }

            parsedStatus = statusValue;
        }

        var result = await _clientService.ListAsync(skip, limit, parsedStatus, search, tenantId.ToString("N"), cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public IActionResult Create([FromRoute] Guid tenantId, [FromBody] ClientSeedRequest request, CancellationToken cancellationToken)
    {
        return BadRequest(new
        {
            errors = new[]
            {
                new { field = "tenantId", message = "Use POST /api/v1/admin/tenants to create a new tenant/client." }
            }
        });
    }

    [HttpGet("{clientId:guid}")]
    public async Task<IActionResult> Get(Guid clientId, CancellationToken cancellationToken)
    {
        var result = await _clientService.GetAsync(clientId, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPut("{clientId:guid}")]
    public async Task<IActionResult> Update(Guid clientId, [FromBody] ClientUpdateRequest request, CancellationToken cancellationToken)
    {
        var result = await _clientService.UpdateAsync(clientId, request, cancellationToken);
        if (!result.Succeeded)
        {
            if (IsClientNotFound(result))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpDelete("{clientId:guid}")]
    public async Task<IActionResult> Delete(Guid clientId, CancellationToken cancellationToken)
    {
        var result = await _clientService.DeactivateAsync(clientId, cancellationToken);
        if (!result.Succeeded)
        {
            if (IsClientNotFound(result))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(new { success = true });
    }

    [HttpPost("{clientId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid clientId, CancellationToken cancellationToken)
    {
        var result = await _clientService.ApproveAsync(clientId, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPost("{clientId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid clientId, [FromBody] ClientRejectionRequest request, CancellationToken cancellationToken)
    {
        var result = await _clientService.RejectAsync(clientId, request.Reason, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPost("{clientId:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid clientId, CancellationToken cancellationToken)
    {
        var result = await _clientService.ResetPasswordAsync(clientId, cancellationToken);
        if (!result.Succeeded)
        {
            if (IsClientNotFound(result))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    private static bool IsClientNotFound<T>(Sabr.Application.Validation.ServiceResult<T> result)
        => result.Errors.Any(error => string.Equals(error.Field, "clientId", StringComparison.OrdinalIgnoreCase));
}
