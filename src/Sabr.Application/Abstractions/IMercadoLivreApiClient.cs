using Sabr.Application.Models;

namespace Sabr.Application.Abstractions;

public interface IMercadoLivreApiClient
{
    Task<MercadoLivreTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<MercadoLivreTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<MercadoLivreUserMeResponse> GetUserMeAsync(string accessToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SearchOrdersAsync(
        string sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<MercadoLivreOrderDetails?> GetOrderAsync(
        string orderId,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<MercadoLivreShipmentDetails?> GetShipmentAsync(
        string shipmentId,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<MercadoLivreShipmentLabelResult?> GetShipmentLabelAsync(
        string shipmentId,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<MercadoLivreCreateItemResult> CreateItemAsync(
        MercadoLivreCreateItemRequest request,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<MercadoLivreFeeEstimateResponse> EstimateFeesAsync(
        MercadoLivreFeeEstimateRequest request,
        string? siteId,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<MercadoLivreCategoryCapabilityResponse> GetCategoryCapabilitiesAsync(
        string categoryId,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MercadoLivreCategoryAttributeResponse>> GetCategoryAttributesAsync(
        string categoryId,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MercadoLivreDomainDiscoverySuggestion>> SuggestCategoriesByDomainDiscoveryAsync(
        string siteId,
        string query,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task UpdateItemStockAsync(
        string itemId,
        int availableQuantity,
        string accessToken,
        CancellationToken cancellationToken = default);
    Task UpdateVariationStockAsync(
        string itemId,
        string variationId,
        int availableQuantity,
        string accessToken,
        CancellationToken cancellationToken = default);
}
