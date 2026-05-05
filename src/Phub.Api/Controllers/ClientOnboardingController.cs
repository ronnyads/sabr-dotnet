using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Services;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/onboarding")]
public sealed class ClientOnboardingController : ControllerBase
{
    private readonly ClientService _clientService;

    public ClientOnboardingController(ClientService clientService)
    {
        _clientService = clientService;
    }

    public sealed class UpdateStepRequest
    {
        public int Step { get; set; }
    }

    [HttpPut("step")]
    public async Task<IActionResult> UpdateStep([FromBody] UpdateStepRequest request, CancellationToken cancellationToken)
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

        var result = await _clientService.SetOnboardingStepAsync(clientId, request.Step, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(new { success = true, step = request.Step });
    }
}

