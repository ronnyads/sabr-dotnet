using Microsoft.AspNetCore.Mvc;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/supplier/wallet")]
public sealed class SupplierWalletController : ControllerBase
{
    private readonly SupplierWalletService _service;

    public SupplierWalletController(SupplierWalletService service)
    {
        _service = service;
    }

    private Guid SupplierId =>
        Guid.Parse(User.FindFirst("supplierId")!.Value);

    [HttpGet]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var result = await _service.GetSummaryAsync(SupplierId, ct);
        return result.ToActionResult();
    }

    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _service.GetEntriesAsync(SupplierId, page, pageSize, ct);
        return result.ToActionResult();
    }
}
