namespace Phub.Application.Models;

public sealed class MercadoLivreCatalogImportRequest
{
    public string Query { get; set; } = string.Empty;
    public string[] Brands { get; set; } = ["Boca Rosa", "Principia"];
    public int PhysicalStock { get; set; } = 1000;
    public bool PreviewOnly { get; set; }
}

public sealed class MercadoLivreCatalogImportResult
{
    public int ListingsFound { get; set; }
    public int ProductsMatched { get; set; }
    public int ProductsCreated { get; set; }
    public int ProductsUpdated { get; set; }
    public int MappingsCreated { get; set; }
    public List<MercadoLivreCatalogImportItemResult> Items { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class MercadoLivreCatalogImportItemResult
{
    public string ItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string Brand { get; set; } = string.Empty;
    public long CatalogPriceCents { get; set; }
    public string Action { get; set; } = string.Empty;
}
