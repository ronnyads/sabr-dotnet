using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/supplier/products")]
public sealed class SupplierProductsController : ControllerBase
{
    private readonly SupplierProductService _service;

    public SupplierProductsController(SupplierProductService service)
    {
        _service = service;
    }

    private Guid SupplierId =>
        Guid.Parse(User.FindFirst("supplierId")!.Value);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _service.ListAsync(SupplierId, ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await _service.GetAsync(SupplierId, id, ct);
        return result.ToActionResult();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SupplierProductUpsertRequest request, CancellationToken ct)
    {
        var result = await _service.CreateDraftAsync(SupplierId, request, ct);
        return result.ToActionResult(successStatus: 201);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SupplierProductUpsertRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateAsync(SupplierId, id, request, ct);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var result = await _service.SubmitForReviewAsync(SupplierId, id, ct);
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await _service.DeleteAsync(SupplierId, id, ct);
        return result.ToActionResult();
    }
}
