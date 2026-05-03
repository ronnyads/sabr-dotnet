using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/supplier/withdrawals")]
public sealed class SupplierWithdrawalsController : ControllerBase
{
    private readonly SupplierWithdrawalService _service;

    public SupplierWithdrawalsController(SupplierWithdrawalService service)
    {
        _service = service;
    }

    private Guid SupplierId =>
        Guid.Parse(User.FindFirst("supplierId")!.Value);

    [HttpPost]
    public async Task<IActionResult> Request([FromBody] SupplierWithdrawalRequest request, CancellationToken ct)
    {
        var result = await _service.RequestAsync(SupplierId, request, ct);
        return result.ToActionResult(successStatus: 201);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _service.ListAsync(SupplierId, page, pageSize, ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await _service.GetAsync(SupplierId, id, ct);
        return result.ToActionResult();
    }
}
