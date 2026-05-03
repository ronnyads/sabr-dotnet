using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Phub.Application.Abstractions;
using Phub.Application.Exceptions;
using Phub.Application.Validation;

namespace Phub.Api.Controllers;

[ApiController]
[EnableRateLimiting("document-lookup")]
[Route("api/v1/utils/doc")]
public sealed class DocumentLookupController : ControllerBase
{
    private readonly IDocumentLookup _lookup;

    public DocumentLookupController(IDocumentLookup lookup)
    {
        _lookup = lookup;
    }

    [HttpGet("{document}")]
    public async Task<IActionResult> Get(string document, CancellationToken cancellationToken)
    {
        var digits = BrazilValidators.OnlyDigits(document);
        if (digits.Length == 11)
        {
            if (!BrazilValidators.IsValidCpf(digits))
            {
                return UnprocessableEntity(new { error = "CPF invalido" });
            }
            // CPF não consulta fonte externa; apenas valida
            return NoContent();
        }

        if (digits.Length == 14)
        {
            if (!BrazilValidators.IsValidCnpj(digits))
            {
                return UnprocessableEntity(new { error = "CNPJ invalido" });
            }

            try
            {
                var result = await _lookup.LookupAsync(digits, cancellationToken);
                if (result == null)
                {
                    return NotFound(new { error = "Documento nao encontrado" });
                }

                return Ok(result);
            }
            catch (ExternalServiceUnavailableException)
            {
                return StatusCode(503, new { error = "Serviço de validação temporariamente indisponível. Tente novamente em instantes." });
            }
        }

        return BadRequest(new { error = "Documento deve ter 11 (CPF) ou 14 (CNPJ) digitos" });
    }
}
