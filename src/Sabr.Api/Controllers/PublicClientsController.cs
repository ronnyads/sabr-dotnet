using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/public/clients")]
public sealed class PublicClientsController : ControllerBase
{
    private readonly ClientService _clientService;

    public PublicClientsController(ClientService clientService)
    {
        _clientService = clientService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] ClientPublicRegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await _clientService.RegisterPublicAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }
}
