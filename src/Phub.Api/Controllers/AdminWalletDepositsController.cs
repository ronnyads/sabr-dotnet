using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController, Authorize(Roles = "Admin,SuperAdmin,Finance"), Route("api/v1/admin/wallet/deposits")]
public sealed class AdminWalletDepositsController : ControllerBase
{
    private readonly WalletDepositService _service;
    public AdminWalletDepositsController(WalletDepositService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status = "Pending", [FromQuery] int skip = 0, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        WalletDepositStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse(status, true, out WalletDepositStatus value)) return BadRequest(new { error = "Status inválido." });
            parsed = value;
        }
        return Ok(await _service.ListAdminAsync(parsed, skip, limit, ct));
    }

    [HttpGet("{id:guid}/proof")]
    public async Task<IActionResult> Proof(Guid id, CancellationToken ct)
    {
        var proof = await _service.GetProofAsync(id, null, null, ct);
        return proof == null ? NotFound() : File(proof.Content, proof.ContentType, proof.FileName);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] WalletDepositReviewRequest request, CancellationToken ct)
    {
        var result = await _service.ApproveAsync(id, ActorId(), request.Note, ct);
        return result.Succeeded ? Ok(result.Data) : Conflict(new { errors = result.Errors });
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] WalletDepositReviewRequest request, CancellationToken ct)
    {
        var result = await _service.RejectAsync(id, ActorId(), request.Note, ct);
        return result.Succeeded ? Ok(result.Data) : Conflict(new { errors = result.Errors });
    }

    private Guid ActorId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : Guid.Empty;
}
