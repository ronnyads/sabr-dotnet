namespace Phub.Application.Models;

/// <summary>
/// Request do admin para criar/atualizar um prompt de IA.
/// </summary>
public sealed class AiPromptConfigUpsertRequest
{
    /// <summary>ID do prompt (null para criar novo)</summary>
    public Guid? Id { get; set; }

    /// <summary>Feature controlada por este prompt (ex: "ml_title", "ml_description")</summary>
    public string Feature { get; set; } = "";

    /// <summary>Canal de marketplace (ex: "mercadolivre", "shopee", "*" para todos)</summary>
    public string Channel { get; set; } = "";

    /// <summary>Nome amigável do prompt</summary>
    public string Name { get; set; } = "";

    /// <summary>Template do prompt com placeholders</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Se o prompt está ativo</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Response ao listar/obter um prompt de IA.
/// </summary>
public sealed class AiPromptConfigResult
{
    public Guid Id { get; set; }
    public string Feature { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Request do cliente para gerar conteúdo via IA durante a publicação.
/// </summary>
public sealed class ListingDraftAiGenerateRequest
{
    /// <summary>ID do draft de publicação</summary>
    public Guid DraftId { get; set; }

    /// <summary>
    /// Feature a gerar.
    /// Exemplos: "ml_title", "ml_description", "ml_sale_terms", "ml_shipping"
    /// </summary>
    public string Feature { get; set; } = "";
}

/// <summary>
/// Response ao gerar conteúdo via IA.
/// </summary>
public sealed class AiGenerateResponse
{
    /// <summary>Feature que foi processada</summary>
    public string Feature { get; set; } = "";

    /// <summary>Resultado da IA (texto livre: título, descrição, etc.)</summary>
    public string Result { get; set; } = "";

    /// <summary>
    /// Para features estruturadas (garantia, frete, etc.),
    /// contém dados parseados do resultado JSON.
    /// Exemplos: { "warrantyType": "Garantia do fabricante", "warrantyTime": "12 meses" }
    /// </summary>
    public Dictionary<string, string>? Structured { get; set; }
}
