using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Finance,SuperAdmin")]
[Route("api/v1/clients/{clientId:guid}/stores")]
public sealed class ClientStoresController : ControllerBase
{
    private readonly ClientStoreService _storeService;
    private readonly ITenantProvider _tenantProvider;

    public ClientStoresController(ClientStoreService storeService, ITenantProvider tenantProvider)
    {
        _storeService = storeService;
        _tenantProvider = tenantProvider;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid clientId,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        var result = await _storeService.ListAsync(clientId, tenantId, includeInactive, cancellationToken);
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

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid clientId,
        [FromBody] ClientStoreCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        var result = await _storeService.CreateAsync(clientId, tenantId, request, cancellationToken);
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

    [HttpDelete("{storeId:guid}")]
    public async Task<IActionResult> Delete(Guid clientId, Guid storeId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        var result = await _storeService.DeactivateAsync(clientId, storeId, tenantId, cancellationToken);
        if (!result.Succeeded)
        {
            if (IsStoreNotFound(result))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(new { success = true });
    }

    private static bool IsClientNotFound<T>(Phub.Application.Validation.ServiceResult<T> result)
        => result.Errors.Any(error => string.Equals(error.Field, "clientId", StringComparison.OrdinalIgnoreCase));

    private static bool IsStoreNotFound<T>(Phub.Application.Validation.ServiceResult<T> result)
        => result.Errors.Any(error => string.Equals(error.Field, "storeId", StringComparison.OrdinalIgnoreCase));
}
