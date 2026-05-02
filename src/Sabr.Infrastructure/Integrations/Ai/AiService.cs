using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Infrastructure.Options;

namespace Sabr.Infrastructure.Integrations.Ai;

/// <summary>
/// Implementação de IAiService usando Anthropic Claude API via HTTP.
/// Executa prompts com substituição de variáveis.
/// </summary>
public sealed class AiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;

    public AiService(HttpClient httpClient, IOptions<AnthropicOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    /// <summary>
    /// Executa um prompt de template, substituindo placeholders pelas variáveis.
    /// </summary>
    public async Task<string> ExecuteAsync(
        string promptTemplate,
        Dictionary<string, string> variables,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(promptTemplate))
            throw new ArgumentException("Prompt não pode estar vazio", nameof(promptTemplate));

        // Substituir placeholders {key} pelos valores do dicionário
        var prompt = SubstitutePlaceholders(promptTemplate, variables);

        // Preparar request para Claude API
        var requestBody = new
        {
            model = "claude-haiku-4-5-20251001",
            max_tokens = 1024,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(requestBody),
                System.Text.Encoding.UTF8,
                "application/json")
        };

        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(responseContent);
        var root = doc.RootElement;

        // Extrair texto da resposta
        if (root.TryGetProperty("content", out var content) &&
            content.GetArrayLength() > 0 &&
            content[0].TryGetProperty("text", out var text))
        {
            return text.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Substitui placeholders {key} pelo valor correspondente no dicionário de variáveis.
    /// </summary>
    private static string SubstitutePlaceholders(string template, Dictionary<string, string> variables)
    {
        var result = template;

        foreach (var kvp in variables)
        {
            // Substituir {key} (case-sensitive)
            var placeholder = "{" + kvp.Key + "}";
            result = result.Replace(placeholder, kvp.Value ?? "", StringComparison.Ordinal);
        }

        return result;
    }
}
