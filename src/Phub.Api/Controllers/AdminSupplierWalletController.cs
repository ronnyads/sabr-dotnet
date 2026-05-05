using Microsoft.AspNetCore.Mvc;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/admin/suppliers/{supplierId:guid}/wallet")]
public sealed class AdminSupplierWalletController : ControllerBase
{
    private readonly SupplierWalletService _service;

    public AdminSupplierWalletController(SupplierWalletService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetSummary(Guid supplierId, CancellationToken ct)
    {
        var result = await _service.GetSummaryAsync(supplierId, ct);
        return result.ToActionResult();
    }

    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(
        Guid supplierId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _service.GetEntriesAsync(supplierId, page, pageSize, ct);
        return result.ToActionResult();
    }
}
