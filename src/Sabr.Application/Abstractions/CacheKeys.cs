namespace Sabr.Application.Abstractions;

/// <summary>
/// Centraliza todas as chaves de cache do sistema.
/// Padrão: {contexto}:{discriminador}
/// </summary>
public static class CacheKeys
{
    // ── Categorias ────────────────────────────────────────────────────────────
    public const string CategoryTree       = "cat:tree";
    public static string CategoryById(Guid id)     => $"cat:id:{id}";
    public static string CategoryBySlug(string slug) => $"cat:slug:{slug.ToLowerInvariant()}";

    // ── Catálogos ─────────────────────────────────────────────────────────────
    public static string CatalogList(string tenantId) => $"catalog:list:{tenantId}";
    public static string CatalogById(Guid id)         => $"catalog:id:{id}";

    // ── Planos ────────────────────────────────────────────────────────────────
    public static string PlanList(string tenantId) => $"plan:list:{tenantId}";
    public static string PlanById(Guid id)         => $"plan:id:{id}";

    // ── Categorias ML (externas, TTL longo) ───────────────────────────────────
    public static string MlCategoryAttributes(string categoryId) => $"ml:cat-attrs:{categoryId}";
    public static string MlCategoryCapabilities(string categoryId) => $"ml:cat-caps:{categoryId}";

    // ── Prefixos para invalidação em lote ────────────────────────────────────
    public const string PrefixCategory = "cat:";
    public const string PrefixCatalog  = "catalog:";
    public const string PrefixPlan     = "plan:";
    public const string PrefixMl       = "ml:";
}
