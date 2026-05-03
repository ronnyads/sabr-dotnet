using Phub.Application.Abstractions;
using Phub.Application.Models;

namespace Phub.Api.Tests.TestHost;

public sealed class FakeMercadoLivreApiClient : IMercadoLivreApiClient
{
    public Exception? ExchangeCodeException { get; set; }
    public Exception? CreateItemException { get; set; }
    public Exception? RefreshTokenException { get; set; }

    public MercadoLivreTokenResponse ExchangeCodeResponse { get; set; } = new()
    {
        AccessToken = "ml-access-token",
        RefreshToken = "ml-refresh-token",
        ExpiresInSeconds = 3600
    };

    public MercadoLivreTokenResponse RefreshTokenResponse { get; set; } = new()
    {
        AccessToken = "ml-access-token-refreshed",
        RefreshToken = "ml-refresh-token-refreshed",
        ExpiresInSeconds = 3600
    };

    public MercadoLivreUserMeResponse UserMeResponse { get; set; } = new()
    {
        SellerId = "1000001",
        Nickname = "seller-test"
    };

    public Dictionary<string, List<string>> SearchOrdersBySeller { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, MercadoLivreOrderDetails> OrdersById { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, MercadoLivreShipmentDetails> ShipmentsById { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, MercadoLivreShipmentLabelResult> ShipmentLabelsById { get; } = new(StringComparer.Ordinal);
    public List<MercadoLivrePublishCall> PublishCalls { get; } = new();
    public Dictionary<string, MercadoLivreCreateItemResult> PublishResultBySku { get; } = new(StringComparer.Ordinal);
    public MercadoLivreCreateItemResult? PublishMultiResult { get; set; }
    public MercadoLivreFeeEstimateResponse FeeEstimateResponse { get; set; } = new()
    {
        SaleFeeAmount = 10m,
        FixedFeeAmount = 2m,
        TotalFeeAmount = 12m,
        RawJson = "{}"
    };
    public MercadoLivreCategoryCapabilityResponse CategoryCapabilityResponse { get; set; } = new()
    {
        IsLeaf = true,
        AllowsVariations = true,
        MaxVariationsAllowed = 50,
        MaxVariationAttributes = 2,
        AllowedVariationAttributes = new List<string> { "COLOR", "SIZE" }
    };
    public List<MercadoLivreCategoryAttributeResponse> CategoryAttributesResponse { get; set; } = new()
    {
        new MercadoLivreCategoryAttributeResponse
        {
            Id = "COLOR",
            Name = "Cor",
            IsVariation = true
        },
        new MercadoLivreCategoryAttributeResponse
        {
            Id = "SIZE",
            Name = "Tamanho",
            IsVariation = true
        }
    };
    public List<MercadoLivreDomainDiscoverySuggestion> DomainDiscoverySuggestions { get; set; } = new();
    public Queue<Exception> DomainDiscoveryExceptions { get; } = new();
    public Queue<Exception> FeeEstimateExceptions { get; } = new();
    public Queue<Exception> CategoryCapabilityExceptions { get; } = new();
    public Queue<Exception> CategoryAttributesExceptions { get; } = new();
    public int DomainDiscoveryCalls { get; private set; }
    public int FeeEstimateCalls { get; private set; }
    public int CategoryCapabilityCalls { get; private set; }
    public int CategoryAttributesCalls { get; private set; }
    public int RefreshTokenCalls { get; private set; }
    private int _publishCounter;

    public List<MercadoLivreStockUpdateCall> StockUpdates { get; } = new();

    public Task<MercadoLivreTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (ExchangeCodeException is not null)
        {
            throw ExchangeCodeException;
        }

        return Task.FromResult(ExchangeCodeResponse);
    }

    public Task<MercadoLivreTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        RefreshTokenCalls++;
        if (RefreshTokenException is not null)
        {
            throw RefreshTokenException;
        }

        return Task.FromResult(RefreshTokenResponse);
    }

    public Task<MercadoLivreUserMeResponse> GetUserMeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(UserMeResponse);
    }

    public Task<IReadOnlyList<string>> SearchOrdersAsync(
        string sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (SearchOrdersBySeller.TryGetValue(sellerId, out var orderIds))
        {
            return Task.FromResult<IReadOnlyList<string>>(orderIds);
        }

        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<MercadoLivreOrderDetails?> GetOrderAsync(
        string orderId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (OrdersById.TryGetValue(orderId, out var details))
        {
            return Task.FromResult<MercadoLivreOrderDetails?>(details);
        }

        return Task.FromResult<MercadoLivreOrderDetails?>(null);
    }

    public Task<MercadoLivreShipmentDetails?> GetShipmentAsync(
        string shipmentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (ShipmentsById.TryGetValue(shipmentId, out var details))
        {
            return Task.FromResult<MercadoLivreShipmentDetails?>(details);
        }

        return Task.FromResult<MercadoLivreShipmentDetails?>(null);
    }

    public Task<MercadoLivreShipmentLabelResult?> GetShipmentLabelAsync(
        string shipmentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (ShipmentLabelsById.TryGetValue(shipmentId, out var label))
        {
            return Task.FromResult<MercadoLivreShipmentLabelResult?>(label);
        }

        return Task.FromResult<MercadoLivreShipmentLabelResult?>(null);
    }

    public Task<MercadoLivreCreateItemResult> CreateItemAsync(
        MercadoLivreCreateItemRequest request,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (CreateItemException is not null)
        {
            throw CreateItemException;
        }

        PublishCalls.Add(new MercadoLivrePublishCall
        {
            SabrVariantSku = request.SabrVariantSku,
            Title = request.Title,
            CategoryId = request.CategoryId,
            Price = request.Price,
            AvailableQuantity = request.AvailableQuantity,
            VariationSkus = request.Variations.Select(item => item.SabrVariantSku).ToList(),
            Attributes = request.Attributes
                .Select(item => new MercadoLivreCreateItemAttributeRequest
                {
                    Id = item.Id,
                    ValueId = item.ValueId,
                    ValueName = item.ValueName
                })
                .ToList()
        });

        if (request.Variations.Count > 0)
        {
            if (PublishMultiResult is not null)
            {
                return Task.FromResult(PublishMultiResult);
            }

            Interlocked.Increment(ref _publishCounter);
            return Task.FromResult(new MercadoLivreCreateItemResult
            {
                ItemId = $"ITEM-PUBLISHED-{_publishCounter:000}",
                Permalink = $"https://produto.mercadolivre.com.br/ITEM-PUBLISHED-{_publishCounter:000}",
                ApiUrl = $"/items/ITEM-PUBLISHED-{_publishCounter:000}",
                Variations = request.Variations.Select((item, index) => new MercadoLivreCreateItemVariationResult
                {
                    SabrVariantSku = item.SabrVariantSku,
                    VariationId = $"VAR-{index + 1:000}"
                }).ToList()
            });
        }

        if (PublishResultBySku.TryGetValue(request.SabrVariantSku, out var preset))
        {
            return Task.FromResult(preset);
        }

        Interlocked.Increment(ref _publishCounter);
        return Task.FromResult(new MercadoLivreCreateItemResult
        {
            ItemId = $"ITEM-PUBLISHED-{_publishCounter:000}",
            VariationId = null,
            Permalink = $"https://produto.mercadolivre.com.br/ITEM-PUBLISHED-{_publishCounter:000}",
            ApiUrl = $"/items/ITEM-PUBLISHED-{_publishCounter:000}"
        });
    }

    public Task<MercadoLivreFeeEstimateResponse> EstimateFeesAsync(
        MercadoLivreFeeEstimateRequest request,
        string? siteId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        FeeEstimateCalls++;
        if (FeeEstimateExceptions.Count > 0)
        {
            throw FeeEstimateExceptions.Dequeue();
        }

        return Task.FromResult(FeeEstimateResponse);
    }

    public Task<MercadoLivreCategoryCapabilityResponse> GetCategoryCapabilitiesAsync(
        string categoryId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        CategoryCapabilityCalls++;
        if (CategoryCapabilityExceptions.Count > 0)
        {
            throw CategoryCapabilityExceptions.Dequeue();
        }

        return Task.FromResult(CategoryCapabilityResponse);
    }

    public Task<IReadOnlyList<MercadoLivreCategoryAttributeResponse>> GetCategoryAttributesAsync(
        string categoryId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        CategoryAttributesCalls++;
        if (CategoryAttributesExceptions.Count > 0)
        {
            throw CategoryAttributesExceptions.Dequeue();
        }

        return Task.FromResult<IReadOnlyList<MercadoLivreCategoryAttributeResponse>>(CategoryAttributesResponse);
    }

    public Task<IReadOnlyList<MercadoLivreDomainDiscoverySuggestion>> SuggestCategoriesByDomainDiscoveryAsync(
        string siteId,
        string query,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        DomainDiscoveryCalls++;
        if (DomainDiscoveryExceptions.Count > 0)
        {
            throw DomainDiscoveryExceptions.Dequeue();
        }

        return Task.FromResult<IReadOnlyList<MercadoLivreDomainDiscoverySuggestion>>(DomainDiscoverySuggestions);
    }

    public Task UpdateItemStockAsync(
        string itemId,
        int availableQuantity,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        StockUpdates.Add(new MercadoLivreStockUpdateCall
        {
            ItemId = itemId,
            VariationId = null,
            AvailableQuantity = availableQuantity
        });
        return Task.CompletedTask;
    }

    public Task UpdateVariationStockAsync(
        string itemId,
        string variationId,
        int availableQuantity,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        StockUpdates.Add(new MercadoLivreStockUpdateCall
        {
            ItemId = itemId,
            VariationId = variationId,
            AvailableQuantity = availableQuantity
        });
        return Task.CompletedTask;
    }

    public Task PingAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RevokeApplicationAsync(long sellerId, string accessToken, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void Reset()
    {
        ExchangeCodeException = null;
        CreateItemException = null;
        RefreshTokenException = null;
        ExchangeCodeResponse = new MercadoLivreTokenResponse
        {
            AccessToken = "ml-access-token",
            RefreshToken = "ml-refresh-token",
            ExpiresInSeconds = 3600
        };
        RefreshTokenResponse = new MercadoLivreTokenResponse
        {
            AccessToken = "ml-access-token-refreshed",
            RefreshToken = "ml-refresh-token-refreshed",
            ExpiresInSeconds = 3600
        };
        UserMeResponse = new MercadoLivreUserMeResponse
        {
            SellerId = "1000001",
            Nickname = "seller-test"
        };
        SearchOrdersBySeller.Clear();
        OrdersById.Clear();
        ShipmentsById.Clear();
        ShipmentLabelsById.Clear();
        PublishCalls.Clear();
        PublishResultBySku.Clear();
        PublishMultiResult = null;
        FeeEstimateResponse = new MercadoLivreFeeEstimateResponse
        {
            SaleFeeAmount = 10m,
            FixedFeeAmount = 2m,
            TotalFeeAmount = 12m,
            RawJson = "{}"
        };
        CategoryCapabilityResponse = new MercadoLivreCategoryCapabilityResponse
        {
            IsLeaf = true,
            AllowsVariations = true,
            MaxVariationsAllowed = 50,
            MaxVariationAttributes = 2,
            AllowedVariationAttributes = new List<string> { "COLOR", "SIZE" }
        };
        CategoryAttributesResponse = new List<MercadoLivreCategoryAttributeResponse>
        {
            new MercadoLivreCategoryAttributeResponse
            {
                Id = "COLOR",
                Name = "Cor",
                IsVariation = true
            },
            new MercadoLivreCategoryAttributeResponse
            {
                Id = "SIZE",
                Name = "Tamanho",
                IsVariation = true
            }
        };
        DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>();
        DomainDiscoveryExceptions.Clear();
        DomainDiscoveryCalls = 0;
        FeeEstimateExceptions.Clear();
        CategoryCapabilityExceptions.Clear();
        CategoryAttributesExceptions.Clear();
        FeeEstimateCalls = 0;
        CategoryCapabilityCalls = 0;
        CategoryAttributesCalls = 0;
        RefreshTokenCalls = 0;
        StockUpdates.Clear();
        _publishCounter = 0;
    }
}

public sealed class MercadoLivreStockUpdateCall
{
    public string ItemId { get; set; } = string.Empty;
    public string? VariationId { get; set; }
    public int AvailableQuantity { get; set; }
}

public sealed class MercadoLivrePublishCall
{
    public string SabrVariantSku { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public decimal Price { get; set; }
    public int AvailableQuantity { get; set; }
    public List<string> VariationSkus { get; set; } = new();
    public List<MercadoLivreCreateItemAttributeRequest> Attributes { get; set; } = new();
}

