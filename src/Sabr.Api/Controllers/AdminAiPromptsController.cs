using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Models;
using Sabr.Application.Services;

namespace Sabr.Api.Controllers;

/// <summary>
/// Endpoints para gerenciar prompts de IA (admin only).
/// </summary>
[ApiController]
[Route("api/v1/admin/ai-prompts")]
[Authorize(Roles = "Admin,SuperAdmin")]
public sealed class AdminAiPromptsController : ControllerBase
{
    private readonly AiPromptConfigService _aiPromptConfigService;

    public AdminAiPromptsController(AiPromptConfigService aiPromptConfigService)
    {
        _aiPromptConfigService = aiPromptConfigService;
    }

    /// <summary>
    /// Lista todos os prompts configurados.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<AiPromptConfigResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _aiPromptConfigService.ListAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Obtém um prompt específico.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AiPromptConfigResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await _aiPromptConfigService.GetByIdAsync(id, cancellationToken);
        if (result == null)
            return NotFound();

        return Ok(result);
    }

    /// <summary>
    /// Cria ou atualiza um prompt.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AiPromptConfigResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpsertAsync(
        [FromBody] AiPromptConfigUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _aiPromptConfigService.UpsertAsync(request, cancellationToken);

        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors });

        return Ok(result.Data);
    }

    /// <summary>
    /// Deleta um prompt.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await _aiPromptConfigService.DeleteAsync(id, cancellationToken);

        if (!result.Succeeded)
            return NotFound();

        return NoContent();
    }
}
