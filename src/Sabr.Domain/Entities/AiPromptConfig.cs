namespace Sabr.Domain.Entities;

/// <summary>
/// Configuração de prompts de IA para diferentes features e canais de marketplace.
/// Administradores configuram estes prompts, que são executados via API Anthropic.
/// </summary>
public sealed class AiPromptConfig
{
    /// <summary>ID único do prompt</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Feature que este prompt controla.
    /// Exemplos: "ml_title", "ml_description", "ml_sale_terms", "ml_shipping", "ml_attributes"
    /// </summary>
    public string Feature { get; set; } = "";

    /// <summary>
    /// Canal de marketplace para o qual este prompt se aplica.
    /// "mercadolivre", "shopee", ou "*" para todos
    /// </summary>
    public string Channel { get; set; } = "";

    /// <summary>Nome amigável do prompt (para exibição no painel admin)</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Template do prompt com placeholders.
    /// Placeholders suportados: {title}, {description}, {name}, {brand}, {category}, {price}, {cost}, {attributes}, {condition}
    /// </summary>
    public string Prompt { get; set; } = "";

    /// <summary>Se o prompt está ativo e será usado na execução</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Data/hora de criação</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Data/hora da última atualização</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
