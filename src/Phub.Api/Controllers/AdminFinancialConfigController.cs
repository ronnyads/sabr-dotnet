using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/admin/financial-config")]
public sealed class AdminFinancialConfigController : ControllerBase
{
    private readonly PlatformFinancialConfigService _service;

    public AdminFinancialConfigController(PlatformFinancialConfigService service)
    {
        _service = service;
    }

    private Guid ActorUserId
    {
        get
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
            return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
        }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var result = await _service.GetAsync(ct);
        return result.ToActionResult();
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdatePlatformFinancialConfigRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateAsync(request, ActorUserId, ct);
        return result.ToActionResult();
    }
}
