using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/admin/withdrawals")]
public sealed class AdminWithdrawalsController : ControllerBase
{
    private readonly SupplierWithdrawalService _service;

    public AdminWithdrawalsController(SupplierWithdrawalService service)
    {
        _service = service;
    }

    private Guid ApproverUserId
    {
        get
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
            return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
        }
    }

    [HttpGet]
    public async Task<IActionResult> ListPending(CancellationToken ct)
    {
        var result = await _service.ListPendingAdminAsync(ct);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] AdminApproveWithdrawalRequest request, CancellationToken ct)
    {
        var result = await _service.ApproveAsync(id, ApproverUserId, request, ct);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] AdminRejectWithdrawalRequest request, CancellationToken ct)
    {
        var result = await _service.RejectAsync(id, request, ct);
        return result.ToActionResult();
    }
}
