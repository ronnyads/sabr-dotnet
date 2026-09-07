using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController, Authorize, Route("api/v1/client/wallet")]
public sealed class ClientWalletController : ControllerBase
{
    private readonly ITenantProvider _tenant;
    private readonly WalletDepositService _service;
    public ClientWalletController(ITenantProvider tenant, WalletDepositService service) { _tenant = tenant; _service = service; }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (!TryContext(out var tenantId, out var clientId)) return Forbid();
        return Ok(await _service.GetClientWalletAsync(tenantId, clientId, limit, ct));
    }

    [HttpPost("deposits"), RequestSizeLimit(10_600_000)]
    public async Task<IActionResult> Deposit([FromForm] WalletDepositCreateRequest request, [FromForm] IFormFile? proof, CancellationToken ct)
    {
        if (!TryContext(out var tenantId, out var clientId)) return Forbid();
        if (proof == null) return BadRequest(new { error = "Envie o comprovante." });
        await using var stream = new MemoryStream();
        await proof.CopyToAsync(stream, ct);
        var result = await _service.CreateAsync(tenantId, clientId, request.AmountCents, request.ClientNote, proof.FileName, proof.ContentType, stream.ToArray(), ct);
        return result.Succeeded ? Created($"api/v1/client/wallet/deposits/{result.Data!.Id}", result.Data) : BadRequest(new { errors = result.Errors });
    }

    [HttpGet("deposits/{id:guid}/proof")]
    public async Task<IActionResult> Proof(Guid id, CancellationToken ct)
    {
        if (!TryContext(out var tenantId, out var clientId)) return Forbid();
        var proof = await _service.GetProofAsync(id, tenantId, clientId, ct);
        return proof == null ? NotFound() : File(proof.Content, proof.ContentType, proof.FileName);
    }

    private bool TryContext(out string tenantId, out Guid clientId)
    {
        tenantId = _tenant.TenantId ?? string.Empty;
        clientId = Guid.Empty;
        return string.Equals(User.FindFirst("accountType")?.Value, AccountTypes.Client, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(tenantId)
               && Guid.TryParse(User.FindFirst("clientId")?.Value, out clientId);
    }
}
