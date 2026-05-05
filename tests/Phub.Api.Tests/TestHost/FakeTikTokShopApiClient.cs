using Phub.Application.Abstractions;

namespace Phub.Api.Tests.TestHost;

public sealed class FakeTikTokShopApiClient : ITikTokShopApiClient
{
    public Exception? ExchangeCodeException { get; set; }
    public Exception? RefreshTokenException { get; set; }
    public Exception? AuthorizedShopsException { get; set; }
    public Exception? CategoriesException { get; set; }
    public Exception? SearchOrdersException { get; set; }
    public Exception? GetOrderDetailException { get; set; }
    public Exception? SearchPackagesException { get; set; }
    public Exception? GetPackageDetailException { get; set; }
    public Exception? GetPackageShippingDocumentException { get; set; }

    public TikTokShopTokenEnvelope ExchangeCodeResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopTokenPayload
        {
            AccessToken = "tts-access-token",
            RefreshToken = "tts-refresh-token",
            ShopId = "1000001",
            ShopCipher = "shop-cipher-1000001",
            SellerName = "Loja Teste",
            SellerBaseRegion = "BR",
            ExpiresIn = 3600
        }
    };

    public TikTokShopTokenEnvelope RefreshTokenResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopTokenPayload
        {
            AccessToken = "tts-access-token-refreshed",
            RefreshToken = "tts-refresh-token-refreshed",
            ShopId = "1000001",
            ShopCipher = "shop-cipher-1000001",
            SellerName = "Loja Teste",
            SellerBaseRegion = "BR",
            ExpiresIn = 3600
        }
    };

    public List<TikTokShopAuthorizedShop> AuthorizedShops { get; } = [];
    public TikTokShopApiResponse<TikTokShopOrderSearchData> SearchOrdersResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopOrderSearchData()
    };
    public TikTokShopApiResponse<TikTokShopOrderDetailData> OrderDetailResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopOrderDetailData()
    };
    public TikTokShopApiResponse<TikTokShopPackageSearchData> PackageSearchResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopPackageSearchData()
    };
    public TikTokShopApiResponse<TikTokShopPackageDetail> PackageDetailResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopPackageDetail()
    };
    public TikTokShopApiResponse<TikTokShopShippingDocumentData> ShippingDocumentResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopShippingDocumentData()
    };

    public TikTokShopApiResponse<TikTokShopCategoryData> CategoriesResponse { get; set; } = new()
    {
        Code = 0,
        Message = "ok",
        Data = new TikTokShopCategoryData
        {
            CategoryList =
            [
                new TikTokShopCategory
                {
                    Id = "100",
                    ParentId = "0",
                    LocalName = "Categoria teste",
                    IsLeaf = true,
                    PermissionStatuses = ["AVAILABLE"]
                }
            ]
        }
    };

    public int GetAuthorizedShopsCalls { get; private set; }
    public int GetCategoriesCalls { get; private set; }
    public int RefreshTokenCalls { get; private set; }
    public int SearchOrdersCalls { get; private set; }
    public int GetOrderDetailCalls { get; private set; }
    public int SearchPackagesCalls { get; private set; }
    public int GetPackageDetailCalls { get; private set; }
    public int GetPackageShippingDocumentCalls { get; private set; }

    public Task<TikTokShopTokenEnvelope> ExchangeCodeAsync(
        string appKey,
        string appSecret,
        string authCode,
        CancellationToken cancellationToken = default)
    {
        if (ExchangeCodeException is not null)
        {
            throw ExchangeCodeException;
        }

        return Task.FromResult(ExchangeCodeResponse);
    }

    public Task<TikTokShopTokenEnvelope> RefreshTokenAsync(
        string appKey,
        string appSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        RefreshTokenCalls++;
        if (RefreshTokenException is not null)
        {
            throw RefreshTokenException;
        }

        return Task.FromResult(RefreshTokenResponse);
    }

    public Task<TikTokShopApiResponse<TikTokShopOrderSearchData>> SearchOrdersAsync(
        string accessToken,
        string appKey,
        string appSecret,
        DateTimeOffset from,
        DateTimeOffset to,
        string? shopCipher = null,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        SearchOrdersCalls++;
        if (SearchOrdersException is not null)
        {
            throw SearchOrdersException;
        }

        return Task.FromResult(SearchOrdersResponse);
    }

    public Task<TikTokShopApiResponse<TikTokShopOrderDetailData>> GetOrderDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string[] orderIds,
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        GetOrderDetailCalls++;
        if (GetOrderDetailException is not null)
        {
            throw GetOrderDetailException;
        }

        return Task.FromResult(OrderDetailResponse);
    }

    public Task<TikTokShopApiResponse<TikTokShopPackageSearchData>> SearchPackagesAsync(
        string accessToken,
        string appKey,
        string appSecret,
        DateTimeOffset from,
        DateTimeOffset to,
        string? shopCipher = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        SearchPackagesCalls++;
        if (SearchPackagesException is not null)
        {
            throw SearchPackagesException;
        }

        return Task.FromResult(PackageSearchResponse);
    }

    public Task<TikTokShopApiResponse<TikTokShopPackageDetail>> GetPackageDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string packageId,
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        GetPackageDetailCalls++;
        if (GetPackageDetailException is not null)
        {
            throw GetPackageDetailException;
        }

        return Task.FromResult(PackageDetailResponse);
    }

    public Task<TikTokShopApiResponse<TikTokShopShippingDocumentData>> GetPackageShippingDocumentAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string packageId,
        string? shopCipher = null,
        int documentType = 1,
        int documentSize = 0,
        CancellationToken cancellationToken = default)
    {
        GetPackageShippingDocumentCalls++;
        if (GetPackageShippingDocumentException is not null)
        {
            throw GetPackageShippingDocumentException;
        }

        return Task.FromResult(ShippingDocumentResponse);
    }

    public Task PingAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TikTokShopAuthorizedShop>> GetAuthorizedShopsAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        GetAuthorizedShopsCalls++;
        if (AuthorizedShopsException is not null)
        {
            throw AuthorizedShopsException;
        }

        return Task.FromResult<IReadOnlyList<TikTokShopAuthorizedShop>>(AuthorizedShops.ToList());
    }

    public Task<TikTokShopApiResponse<TikTokShopImageUploadData>> UploadImageFromUrlAsync(
        string imageUrl,
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TikTokShopApiResponse<TikTokShopImageUploadData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopImageUploadData
            {
                ImgId = "img-1",
                ImgUrl = imageUrl
            }
        });
    }

    public Task<TikTokShopApiResponse<TikTokShopCreateProductData>> CreateProductAsync(
        TikTokShopCreateProductRequest request,
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TikTokShopApiResponse<TikTokShopCreateProductData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopCreateProductData
            {
                ProductId = "product-1",
                Skus = [new TikTokShopCreatedSkuItem { Id = "sku-1", SellerSku = request.Skus.FirstOrDefault()?.SellerSku ?? string.Empty }]
            }
        });
    }

    public Task<TikTokShopApiResponse<TikTokShopProductListData>> GetProductsAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        int pageSize = 20,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TikTokShopApiResponse<TikTokShopProductListData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopProductListData()
        });
    }

    public Task<TikTokShopApiResponse<TikTokShopCategoryData>> GetCategoriesAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        GetCategoriesCalls++;
        if (CategoriesException is not null)
        {
            throw CategoriesException;
        }

        return Task.FromResult(CategoriesResponse);
    }

    public void Reset()
    {
        ExchangeCodeException = null;
        RefreshTokenException = null;
        AuthorizedShopsException = null;
        CategoriesException = null;
        SearchOrdersException = null;
        GetOrderDetailException = null;
        SearchPackagesException = null;
        GetPackageDetailException = null;
        GetPackageShippingDocumentException = null;
        ExchangeCodeResponse = new TikTokShopTokenEnvelope
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopTokenPayload
            {
                AccessToken = "tts-access-token",
                RefreshToken = "tts-refresh-token",
                ShopId = "1000001",
                ShopCipher = "shop-cipher-1000001",
                SellerName = "Loja Teste",
                SellerBaseRegion = "BR",
                ExpiresIn = 3600
            }
        };
        RefreshTokenResponse = new TikTokShopTokenEnvelope
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopTokenPayload
            {
                AccessToken = "tts-access-token-refreshed",
                RefreshToken = "tts-refresh-token-refreshed",
                ShopId = "1000001",
                ShopCipher = "shop-cipher-1000001",
                SellerName = "Loja Teste",
                SellerBaseRegion = "BR",
                ExpiresIn = 3600
            }
        };
        AuthorizedShops.Clear();
        SearchOrdersResponse = new TikTokShopApiResponse<TikTokShopOrderSearchData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopOrderSearchData()
        };
        OrderDetailResponse = new TikTokShopApiResponse<TikTokShopOrderDetailData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopOrderDetailData()
        };
        PackageSearchResponse = new TikTokShopApiResponse<TikTokShopPackageSearchData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopPackageSearchData()
        };
        PackageDetailResponse = new TikTokShopApiResponse<TikTokShopPackageDetail>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopPackageDetail()
        };
        ShippingDocumentResponse = new TikTokShopApiResponse<TikTokShopShippingDocumentData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopShippingDocumentData()
        };
        CategoriesResponse = new TikTokShopApiResponse<TikTokShopCategoryData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopCategoryData
            {
                CategoryList =
                [
                    new TikTokShopCategory
                    {
                        Id = "100",
                        ParentId = "0",
                        LocalName = "Categoria teste",
                        IsLeaf = true,
                        PermissionStatuses = ["AVAILABLE"]
                    }
                ]
            }
        };
        GetAuthorizedShopsCalls = 0;
        GetCategoriesCalls = 0;
        RefreshTokenCalls = 0;
        SearchOrdersCalls = 0;
        GetOrderDetailCalls = 0;
        SearchPackagesCalls = 0;
        GetPackageDetailCalls = 0;
        GetPackageShippingDocumentCalls = 0;
    }
}
