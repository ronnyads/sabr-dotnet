namespace Phub.Application.Abstractions;

/// <summary>
/// Serviço para execução de prompts de IA via API Anthropic (Claude).
/// Responsável por substituir placeholders e executar os prompts configurados.
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Executa um prompt de template com substituição de variáveis.
    /// </summary>
    /// <param name="promptTemplate">Template do prompt com placeholders como {name}, {brand}, etc.</param>
    /// <param name="variables">Dicionário de variáveis para substituição dos placeholders</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>Resposta da IA (texto livre ou JSON estruturado)</returns>
    Task<string> ExecuteAsync(
        string promptTemplate,
        Dictionary<string, string> variables,
        CancellationToken cancellationToken = default);
}
