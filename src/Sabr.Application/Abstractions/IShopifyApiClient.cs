namespace Sabr.Application.Abstractions;

public interface IShopifyApiClient
{
    Task<ShopifyTokenResponse> ExchangeCodeAsync(string shop, string code, CancellationToken cancellationToken = default);

    Task<ShopifyShopInfoResponse> GetShopInfoAsync(string shop, string accessToken, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShopifyOrderDto>> GetOrdersAsync(
        string shop,
        string accessToken,
        DateTimeOffset? since = null,
        int limit = 250,
        CancellationToken cancellationToken = default);

    Task PingAsync(CancellationToken cancellationToken = default);
}

// ── Response / DTO types ──────────────────────────────────────────────────────

public sealed class ShopifyTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
}

public sealed class ShopifyShopInfoResponse
{
    public string ShopDomain { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
}

public sealed class ShopifyOrderDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;          // e.g. "#1001"
    public string Email { get; set; } = string.Empty;
    public string FinancialStatus { get; set; } = string.Empty;
    public string FulfillmentStatus { get; set; } = string.Empty;
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public IReadOnlyList<ShopifyLineItemDto> LineItems { get; set; } = [];
}

public sealed class ShopifyLineItemDto
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public long? VariantId { get; set; }
    public long? ProductId { get; set; }
}
