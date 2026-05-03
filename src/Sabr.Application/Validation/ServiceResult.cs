namespace Phub.Application.Validation;

/// <summary>
/// Resultado de uma operação de serviço.
/// Extensão do padrão original — agora suporta código de erro semântico
/// para mapeamento direto em HTTP status codes nos controllers.
/// 100% compatível com chamadores existentes.
/// </summary>
public sealed class ServiceResult<T>
{
    public bool Succeeded      { get; private set; }
    public T?   Data           { get; private set; }
    public string? ErrorCode   { get; private set; }
    public List<ValidationError> Errors { get; } = new();

    // ── Sucesso ───────────────────────────────────────────────────────────────
    public static ServiceResult<T> Success(T data)
        => new() { Succeeded = true, Data = data };

    // ── Falha com lista de erros (compat. original) ───────────────────────────
    public static ServiceResult<T> Failure(IEnumerable<ValidationError> errors)
    {
        var result = new ServiceResult<T> { Succeeded = false };
        result.Errors.AddRange(errors);
        return result;
    }

    // ── Falha com código semântico + erros ────────────────────────────────────
    public static ServiceResult<T> Failure(string errorCode, IEnumerable<ValidationError> errors)
    {
        var result = new ServiceResult<T> { Succeeded = false, ErrorCode = errorCode };
        result.Errors.AddRange(errors);
        return result;
    }

    // ── Falha com código semântico + mensagem simples ─────────────────────────
    public static ServiceResult<T> Failure(string errorCode, string field, string message)
        => Failure(errorCode, new[] { new ValidationError(field, message) });

    // ── Falha por recurso não encontrado ─────────────────────────────────────
    public static ServiceResult<T> NotFound(string field, string message)
        => Failure(ServiceErrorCodes.NotFound, field, message);

    // ── Falha por conflito de concorrência ────────────────────────────────────
    public static ServiceResult<T> Conflict(string field, string message)
        => Failure(ServiceErrorCodes.ConcurrencyConflict, field, message);

    // ── Falha por acesso negado ───────────────────────────────────────────────
    public static ServiceResult<T> Forbidden(string field, string message)
        => Failure(ServiceErrorCodes.Forbidden, field, message);
}

/// <summary>Códigos de erro canônicos do sistema — mapeados para HTTP em <c>ServiceResultExtensions</c>.</summary>
public static class ServiceErrorCodes
{
    public const string NotFound           = "NOT_FOUND";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string Forbidden          = "FORBIDDEN";
    public const string ValidationError    = "VALIDATION_ERROR";
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";
    public const string SkuNotAuthorized   = "SKU_NOT_AUTHORIZED";
    public const string DraftNotFound      = "DRAFT_NOT_FOUND";
    public const string PricingInvalid     = "PRICING_INVALID";
    public const string CategoryNotFound   = "CATEGORY_NOT_FOUND";
    public const string CategoryInactive   = "CATEGORY_INACTIVE";
    public const string CategoryHasActiveChildren = "CATEGORY_HAS_ACTIVE_CHILDREN";
    public const string CategoryCycleDetected = "CATEGORY_CYCLE_DETECTED";
    public const string VariantAlreadyExists = "VARIANT_ALREADY_EXISTS";
    public const string AnatelRequired     = "ANATEL_REQUIRED";
    public const string InvalidImageType   = "INVALID_IMAGE_TYPE";
    public const string ImageLimitExceeded = "IMAGE_LIMIT_EXCEEDED";
    public const string ProductMissingCatalogLinks = "PRODUCT_MISSING_CATALOG_LINKS";
    public const string MlAuthInvalid      = "ML_AUTH_INVALID";
}

