using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants")]
public sealed class AdminTenantsController : ControllerBase
{
    private readonly ClientService _clientService;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public AdminTenantsController(ClientService clientService)
    {
        _clientService = clientService;
    }

    // Platform admin view: list all tenants (1 client per tenant in current model).
    // We reuse ClientService listing without filtering by tenantId.
    [HttpGet]
    public async Task<IActionResult> List(
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

        var result = await _clientService.ListAsync(skip, limit, parsedStatus, search, tenantId: null, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    // Creates a new tenant + its initial client account (seed).
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientSeedRequest request, CancellationToken cancellationToken)
    {
        var result = await _clientService.CreateByAdminAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }
}

