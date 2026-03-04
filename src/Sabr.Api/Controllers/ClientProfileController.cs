using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/profile")]
public sealed class ClientProfileController : ControllerBase
{
    private readonly ClientService _clientService;

    public ClientProfileController(ClientService clientService)
    {
        _clientService = clientService;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (!string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var clientIdClaim = User.FindFirst("clientId")?.Value;
        if (!Guid.TryParse(clientIdClaim, out var clientId))
        {
            return Unauthorized(new { error = "Invalid client context" });
        }

        var result = await _clientService.GetAsync(clientId, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { error = "Client not found" });
        }

        return Ok(result.Data);
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] ClientUpdateRequest request, CancellationToken cancellationToken)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (!string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var clientIdClaim = User.FindFirst("clientId")?.Value;
        if (!Guid.TryParse(clientIdClaim, out var clientId))
        {
            return Unauthorized(new { error = "Invalid client context" });
        }

        var result = await _clientService.CompleteProfileAsync(clientId, request, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }
}
