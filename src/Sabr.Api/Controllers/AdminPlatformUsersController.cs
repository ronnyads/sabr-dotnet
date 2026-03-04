using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/users")]
public sealed class AdminPlatformUsersController : ControllerBase
{
    private readonly PlatformUserService _platformUserService;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public AdminPlatformUsersController(PlatformUserService platformUserService)
    {
        _platformUserService = platformUserService;
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
        if (skip < 0)
        {
            return BadRequest(new { errors = new[] { new { field = "skip", message = "Skip must be 0 or greater" } } });
        }

        if (limit <= 0 || limit > MaxPageSize)
        {
            return BadRequest(new { errors = new[] { new { field = "limit", message = $"Limit must be between 1 and {MaxPageSize}" } } });
        }

        PlatformUserRole? parsedRole = null;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Enum.TryParse<PlatformUserRole>(role, true, out var roleValue))
            {
                return BadRequest(new { errors = new[] { new { field = "role", message = "Invalid role" } } });
            }

            parsedRole = roleValue;
        }

        var result = await _platformUserService.ListAsync(skip, limit, parsedRole, includeInactive, search, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await _platformUserService.GetAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PlatformUserCreateRequest request, CancellationToken cancellationToken)
    {
        var actorRole = GetActorRole();
        if (actorRole == null)
        {
            return Forbid();
        }

        var requestId = ResolveRequestId();
        var actorId = ResolveActorId();

        var result = await _platformUserService.CreateAsync(request, actorRole.Value, actorId, requestId, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] PlatformUserUpdateRequest request, CancellationToken cancellationToken)
    {
        var actorRole = GetActorRole();
        if (actorRole == null)
        {
            return Forbid();
        }

        var requestId = ResolveRequestId();
        var actorId = ResolveActorId();

        var result = await _platformUserService.UpdateAsync(id, request, actorRole.Value, actorId, requestId, cancellationToken);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => string.Equals(e.Field, "id", StringComparison.OrdinalIgnoreCase)))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> SetStatus([FromRoute] Guid id, [FromBody] PlatformUserStatusUpdateRequest request, CancellationToken cancellationToken)
    {
        var actorRole = GetActorRole();
        if (actorRole == null)
        {
            return Forbid();
        }

        var requestId = ResolveRequestId();
        var actorId = ResolveActorId();

        var result = await _platformUserService.SetStatusAsync(id, request.IsActive, actorRole.Value, actorId, requestId, cancellationToken);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => string.Equals(e.Field, "id", StringComparison.OrdinalIgnoreCase)))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword([FromRoute] Guid id, [FromBody] PlatformUserResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var actorRole = GetActorRole();
        if (actorRole == null)
        {
            return Forbid();
        }

        var requestId = ResolveRequestId();
        var actorId = ResolveActorId();

        var result = await _platformUserService.ResetPasswordAsync(id, request.TemporaryPassword, actorRole.Value, actorId, requestId, cancellationToken);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => string.Equals(e.Field, "id", StringComparison.OrdinalIgnoreCase)))
            {
                return NotFound(new { errors = result.Errors });
            }

            return BadRequest(new { errors = result.Errors });
        }

        return Ok(new { success = true });
    }

    private PlatformUserRole? GetActorRole()
    {
        var roleClaim = User.FindFirstValue(ClaimTypes.Role);
        if (string.IsNullOrWhiteSpace(roleClaim))
        {
            return null;
        }

        if (Enum.TryParse<PlatformUserRole>(roleClaim, true, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private Guid? ResolveActorId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var parsed) ? parsed : null;
    }

    private Guid ResolveRequestId()
    {
        if (Request.Headers.TryGetValue("X-Request-Id", out var value) && Guid.TryParse(value.ToString(), out var parsed))
        {
            return parsed;
        }

        if (HttpContext.TraceIdentifier != null && Guid.TryParse(HttpContext.TraceIdentifier, out var traceId))
        {
            return traceId;
        }

        return Guid.NewGuid();
    }
}
