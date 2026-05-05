namespace Phub.Infrastructure.Options;

/// <summary>
/// Configurações para a API Anthropic (Claude).
/// </summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>API Key da Anthropic para autenticação</summary>
    public string ApiKey { get; set; } = "";
}
