using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/integrations/{clientId:guid}/financial-capabilities")]
public sealed class AdminFinancialCapabilitiesController : ControllerBase
{
    private readonly ITenantProvider _tenant;
    private readonly FinancialCapabilityService _service;
    public AdminFinancialCapabilitiesController(ITenantProvider tenant, FinancialCapabilityService service)
    { _tenant = tenant; _service = service; }

    [HttpGet]
    public async Task<IActionResult> Get(Guid clientId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(_tenant.TenantId) ? BadRequest() : Ok(await _service.GetAsync(_tenant.TenantId!, clientId, false, ct));

    [HttpPost("probe")]
    public async Task<IActionResult> Probe(Guid clientId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(_tenant.TenantId) ? BadRequest() : Ok(await _service.GetAsync(_tenant.TenantId!, clientId, true, ct));
}
