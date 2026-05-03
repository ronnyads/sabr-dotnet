using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/admin/supplier-products")]
public sealed class AdminSupplierProductsController : ControllerBase
{
    private readonly SupplierProductApprovalService _service;

    public AdminSupplierProductsController(SupplierProductApprovalService service)
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
        var result = await _service.ListPendingAsync(ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await _service.GetAsync(id, ct);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] AdminApproveSupplierProductRequest request, CancellationToken ct)
    {
        var result = await _service.ApproveAsync(id, ApproverUserId, request, ct);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] AdminRejectSupplierProductRequest request, CancellationToken ct)
    {
        var result = await _service.RejectAsync(id, request, ct);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/request-adjustment")]
    public async Task<IActionResult> RequestAdjustment(Guid id, [FromBody] AdminRequestAdjustmentRequest request, CancellationToken ct)
    {
        var result = await _service.RequestAdjustmentAsync(id, request, ct);
        return result.ToActionResult();
    }
}
