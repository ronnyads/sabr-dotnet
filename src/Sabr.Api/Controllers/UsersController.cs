using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/users")]
public sealed class UsersController : ControllerBase
{
    private readonly UserService _userService;
    private readonly ITenantProvider _tenantProvider;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public UsersController(UserService userService, ITenantProvider tenantProvider)
    {
        _userService = userService;
        _tenantProvider = tenantProvider;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserCreateRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        var result = await _userService.CreateAsync(request, tenantId, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] bool includeInactive = false,
        [FromQuery] string? role = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        if (skip < 0)
        {
            return BadRequest(new { errors = new[] { new { field = "skip", message = "Skip must be 0 or greater" } } });
        }

        if (limit <= 0 || limit > MaxPageSize)
        {
            return BadRequest(new { errors = new[] { new { field = "limit", message = $"Limit must be between 1 and {MaxPageSize}" } } });
        }

        UserRole? parsedRole = null;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Enum.TryParse<UserRole>(role, true, out var roleValue))
            {
                return BadRequest(new { errors = new[] { new { field = "role", message = "Invalid role" } } });
            }

            parsedRole = roleValue;
        }

        var result = await _userService.ListAsync(tenantId, skip, limit, parsedRole, includeInactive, search, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        var result = await _userService.GetAsync(userId, tenantId, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPut("{userId:guid}")]
    public async Task<IActionResult> Update(Guid userId, [FromBody] UserUpdateRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        var result = await _userService.UpdateAsync(userId, request, tenantId, cancellationToken);
        if (!result.Succeeded)
        {
            if (IsUserNotFound(result))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Delete(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "TenantId is required" });
        }

        var result = await _userService.DeactivateAsync(userId, tenantId, cancellationToken);
        if (!result.Succeeded)
        {
            if (IsUserNotFound(result))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(new { success = true });
    }

    private static bool IsUserNotFound<T>(Sabr.Application.Validation.ServiceResult<T> result)
        => result.Errors.Any(error => string.Equals(error.Field, "userId", StringComparison.OrdinalIgnoreCase));
}
