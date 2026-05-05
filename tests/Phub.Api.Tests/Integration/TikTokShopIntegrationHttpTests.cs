using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Phub.Api.Controllers;
using Phub.Api.Tests.TestHost;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests.Integration;

public sealed class TikTokShopIntegrationHttpTests : IClassFixture<TikTokShopTestWebApplicationFactory>
{
    private readonly TikTokShopTestWebApplicationFactory _factory;

    public TikTokShopIntegrationHttpTests(TikTokShopTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Callback_WithTokenShopCipher_PersistsUsableConnection()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-01";
        const string tenantSlug = "ttscallback";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);

        _factory.FakeTikTokShopApiClient.ExchangeCodeResponse = new TikTokShopTokenEnvelope
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopTokenPayload
            {
                AccessToken = "access-callback",
                RefreshToken = "refresh-callback",
                ShopId = "1979655640",
                ShopCipher = "cipher-callback",
                SellerName = "Loja Callback",
                SellerBaseRegion = "BR",
                ExpiresIn = 3600
            }
        };

        using var authClient = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var connectUrlResponse = await authClient.PostAsJsonAsync(
            "/api/v1/client/integrations/tiktokshop/connect-url",
            new TikTokShopConnectUrlRequest { ReturnUrl = "/client/integrations/tiktokshop" });

        Assert.Equal(HttpStatusCode.OK, connectUrlResponse.StatusCode);
        var connectUrl = await connectUrlResponse.Content.ReadFromJsonAsync<TikTokShopConnectUrlResult>();
        Assert.NotNull(connectUrl);

        var state = ExtractQueryParam(connectUrl!.Url, "state");
        Assert.False(string.IsNullOrWhiteSpace(state));

        using var callbackClient = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var callbackResponse = await callbackClient.GetAsync(
            $"/api/v1/client/integrations/tiktokshop/callback?code=test-code&state={Uri.EscapeDataString(state!)}");

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync();
        Assert.Contains("tiktok=connected", html, StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.TenantMarketplaceConnections.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);

        Assert.NotNull(connection);
        Assert.Equal(1979655640, connection!.SellerId);
        Assert.Equal("cipher-callback", connection.ShopCipher);
        Assert.Equal("access-callback", connection.AccessToken);
        Assert.Equal("refresh-callback", connection.RefreshToken);
    }

    [Fact]
    public async Task Callback_WithoutUsableShopMetadata_PersistsIncompleteConnection_AndRedirectsConnected()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-02";
        const string tenantSlug = "ttscallbackfail";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);

        _factory.FakeTikTokShopApiClient.ExchangeCodeResponse = new TikTokShopTokenEnvelope
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopTokenPayload
            {
                AccessToken = "access-broken",
                RefreshToken = "refresh-broken",
                ShopId = "1979655640",
                ShopCipher = null,
                SellerName = "Loja Incompleta",
                SellerBaseRegion = "BR",
                ExpiresIn = 3600
            }
        };

        using var authClient = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var connectUrlResponse = await authClient.PostAsJsonAsync(
            "/api/v1/client/integrations/tiktokshop/connect-url",
            new TikTokShopConnectUrlRequest { ReturnUrl = "/client/integrations/tiktokshop" });
        var connectUrl = await connectUrlResponse.Content.ReadFromJsonAsync<TikTokShopConnectUrlResult>();
        Assert.NotNull(connectUrl);

        var state = ExtractQueryParam(connectUrl!.Url, "state");
        Assert.False(string.IsNullOrWhiteSpace(state));

        using var callbackClient = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var callbackResponse = await callbackClient.GetAsync(
            $"/api/v1/client/integrations/tiktokshop/callback?code=test-code&state={Uri.EscapeDataString(state!)}");

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync();
        Assert.Contains("tiktok=connected", html, StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.TenantMarketplaceConnections.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);

        Assert.NotNull(connection);
        Assert.Equal(1979655640, connection!.SellerId);
        Assert.Null(connection.ShopCipher);
        Assert.Equal("access-broken", connection.AccessToken);
        Assert.Equal(2, _factory.FakeTikTokShopApiClient.GetAuthorizedShopsCalls);
    }

    [Fact]
    public async Task Callback_WhenAuthorizedShopsLookupFails_PersistsTokenConnection_AndRedirectsConnected()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-02b";
        const string tenantSlug = "ttscallbacklookupfail";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);

        _factory.FakeTikTokShopApiClient.ExchangeCodeResponse = new TikTokShopTokenEnvelope
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopTokenPayload
            {
                AccessToken = "access-no-shop",
                RefreshToken = "refresh-no-shop",
                ShopId = null,
                ShopCipher = null,
                SellerName = "Loja Sem Metadata",
                SellerBaseRegion = "BR",
                ExpiresIn = 3600
            }
        };
        _factory.FakeTikTokShopApiClient.AuthorizedShopsException = new TikTokShopApiException(
            HttpStatusCode.NotFound,
            "404",
            "authorized shops endpoint not found",
            "req-404",
            "{\"message\":\"authorized shops endpoint not found\"}",
            "authorized_shops");

        using var authClient = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var connectUrlResponse = await authClient.PostAsJsonAsync(
            "/api/v1/client/integrations/tiktokshop/connect-url",
            new TikTokShopConnectUrlRequest { ReturnUrl = "/client/integrations/tiktokshop" });
        var connectUrl = await connectUrlResponse.Content.ReadFromJsonAsync<TikTokShopConnectUrlResult>();
        Assert.NotNull(connectUrl);

        var state = ExtractQueryParam(connectUrl!.Url, "state");
        Assert.False(string.IsNullOrWhiteSpace(state));

        using var callbackClient = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var callbackResponse = await callbackClient.GetAsync(
            $"/api/v1/client/integrations/tiktokshop/callback?code=test-code&state={Uri.EscapeDataString(state!)}");

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync();
        Assert.Contains("tiktok=connected", html, StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.TenantMarketplaceConnections.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);

        Assert.NotNull(connection);
        Assert.Equal(0, connection!.SellerId);
        Assert.Null(connection.ShopCipher);
        Assert.Equal("access-no-shop", connection.AccessToken);
        Assert.Equal(2, _factory.FakeTikTokShopApiClient.GetAuthorizedShopsCalls);
    }

    [Fact]
    public async Task Callback_WithIncompleteMetadata_TriggersSync_AndAutoMapsOrdersBySellerSku()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-sync";
        const string tenantSlug = "ttssyncafteroauth";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync("SELLER-001", "BASE-001");

        _factory.FakeTikTokShopApiClient.ExchangeCodeResponse = new TikTokShopTokenEnvelope
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopTokenPayload
            {
                AccessToken = "access-sync",
                RefreshToken = "refresh-sync",
                ShopId = null,
                ShopCipher = "cipher-sync",
                SellerName = "Loja Sync",
                SellerBaseRegion = "BR",
                ExpiresIn = 3600
            }
        };
        _factory.FakeTikTokShopApiClient.AuthorizedShopsException = new TikTokShopApiException(
            HttpStatusCode.NotFound,
            "404",
            "authorized shops endpoint not found",
            "req-404",
            "{\"message\":\"authorized shops endpoint not found\"}",
            "authorized_shops");
        _factory.FakeTikTokShopApiClient.SearchOrdersResponse = new TikTokShopApiResponse<TikTokShopOrderSearchData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopOrderSearchData
            {
                Orders =
                [
                    new TikTokShopOrderSummary
                    {
                        OrderId = "tts-order-1",
                        CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                ]
            }
        };
        _factory.FakeTikTokShopApiClient.OrderDetailResponse = new TikTokShopApiResponse<TikTokShopOrderDetailData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopOrderDetailData
            {
                Orders =
                [
                    new TikTokShopOrderDetail
                    {
                        OrderId = "tts-order-1",
                        OrderStatus = "AWAITING_SHIPMENT",
                        CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        UpdateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ShopId = "1979655640",
                        LineItems =
                        [
                            new TikTokShopOrderLineItem
                            {
                                Id = "line-1",
                                ProductId = "tt-product-1",
                                SkuId = "tt-sku-1",
                                SellerSku = "seller-001",
                                Quantity = 2
                            }
                        ]
                    }
                ]
            }
        };

        using var authClient = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var connectUrlResponse = await authClient.PostAsJsonAsync(
            "/api/v1/client/integrations/tiktokshop/connect-url",
            new TikTokShopConnectUrlRequest { ReturnUrl = "/client/integrations/tiktokshop" });
        var connectUrl = await connectUrlResponse.Content.ReadFromJsonAsync<TikTokShopConnectUrlResult>();
        Assert.NotNull(connectUrl);

        var state = ExtractQueryParam(connectUrl!.Url, "state");
        Assert.False(string.IsNullOrWhiteSpace(state));

        using var callbackClient = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var callbackResponse = await callbackClient.GetAsync(
            $"/api/v1/client/integrations/tiktokshop/callback?code=test-code&state={Uri.EscapeDataString(state!)}");

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync();
        Assert.Contains("tiktok=connected", html, StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.TenantMarketplaceConnections.SingleAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);
        var order = await db.MarketplaceOrders
            .Include(item => item.Items)
            .SingleAsync(item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);
        var mapping = await db.TenantMarketplaceListingMaps.SingleAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);

        Assert.Equal(1979655640, connection.SellerId);
        Assert.NotNull(connection.LastSyncAt);
        Assert.Equal("tts-order-1", order.MlOrderId);
        Assert.Single(order.Items);
        var orderItem = order.Items.Single();
        Assert.Equal("SELLER-001", orderItem.SabrVariantSku);
        Assert.Equal("MAPPED", orderItem.MappingState);
        Assert.Equal("tt-product-1", mapping.MlItemId);
        Assert.Equal("tt-sku-1", mapping.MlVariationId);
        Assert.Equal("SELLER-001", mapping.SabrVariantSku);
        Assert.Equal(1, _factory.FakeTikTokShopApiClient.GetAuthorizedShopsCalls);
        Assert.Equal(1, _factory.FakeTikTokShopApiClient.SearchOrdersCalls);
        Assert.Equal(1, _factory.FakeTikTokShopApiClient.GetOrderDetailCalls);
    }

    [Fact]
    public async Task GetCategories_WithValidConnection_Returns200()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-03";
        const string tenantSlug = "ttscategoriesok";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: "cipher-valid", sellerId: 1979655640);

        _factory.FakeTikTokShopApiClient.CategoriesResponse = new TikTokShopApiResponse<TikTokShopCategoryData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopCategoryData
            {
                CategoryList =
                [
                    new TikTokShopCategory
                    {
                        Id = "3001",
                        ParentId = "0",
                        LocalName = "Moda",
                        IsLeaf = true,
                        PermissionStatuses = ["AVAILABLE"]
                    }
                ]
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/client/integrations/tiktokshop/categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<List<TikTokShopCategoryItem>>();
        Assert.NotNull(payload);
        Assert.Single(payload!);
        Assert.Equal("3001", payload[0].Id);
    }

    [Fact]
    public async Task GetCategories_WithIncompleteConnection_Returns409ReconnectRequired()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-04";
        const string tenantSlug = "ttsincomplete";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: null, sellerId: 1979655640);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/client/integrations/tiktokshop/categories");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("TIKTOK_SHOP_RECONNECT_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task GetStatus_WithIncompleteConnection_FlagsReconnectRequirement()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-04-status";
        const string tenantSlug = "ttsincompletestatus";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: null, sellerId: 0);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/client/integrations/tiktokshop/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<TikTokShopStatusResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.IsConnected);
        Assert.True(payload.RequiresReconnect);
        Assert.False(string.IsNullOrWhiteSpace(payload.ConnectionWarning));
    }

    [Fact]
    public async Task SyncNow_WhenConnectionHasMissingShopCipher_HydratesMetadata_AndReturns200()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-sync-heal";
        const string tenantSlug = "ttssyncheal";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: null, sellerId: 0);

        _factory.FakeTikTokShopApiClient.AuthorizedShops.Add(new TikTokShopAuthorizedShop
        {
            ShopId = "1979655640",
            ShopCipher = "cipher-healed",
            SellerName = "Loja Curada"
        });

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsync("/api/v1/client/integrations/tiktokshop/sync-now", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.TenantMarketplaceConnections.SingleAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);

        Assert.Equal(1979655640, connection.SellerId);
        Assert.Equal("cipher-healed", connection.ShopCipher);
        Assert.Equal(1, _factory.FakeTikTokShopApiClient.GetAuthorizedShopsCalls);
        Assert.Equal(1, _factory.FakeTikTokShopApiClient.SearchOrdersCalls);
    }

    [Fact]
    public async Task SyncNow_WhenTikTokReturnsUnauthorized_Returns409ReconnectRequired()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-sync-401";
        const string tenantSlug = "ttssync401";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: "cipher-auth", sellerId: 1979655640);

        _factory.FakeTikTokShopApiClient.SearchOrdersException = new TikTokShopApiException(
            HttpStatusCode.Unauthorized,
            "401",
            "invalid access token",
            "req-sync-401",
            "{\"message\":\"invalid access token\"}",
            "search_orders");
        _factory.FakeTikTokShopApiClient.AuthorizedShopsException = new TikTokShopApiException(
            HttpStatusCode.Unauthorized,
            "401",
            "invalid access token",
            "req-auth-401",
            "{\"message\":\"invalid access token\"}",
            "authorized_shops");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsync("/api/v1/client/integrations/tiktokshop/sync-now", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("TIKTOK_SHOP_RECONNECT_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task SyncNow_WhenOrderDetailReturnsUnauthorized_Returns409ReconnectRequired()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-detail-401";
        const string tenantSlug = "ttsdetail401";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: "cipher-detail-auth", sellerId: 1979655640);

        _factory.FakeTikTokShopApiClient.SearchOrdersResponse = new TikTokShopApiResponse<TikTokShopOrderSearchData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopOrderSearchData
            {
                Orders =
                [
                    new TikTokShopOrderSummary
                    {
                        OrderId = "tts-order-detail-1",
                        CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                ]
            }
        };
        _factory.FakeTikTokShopApiClient.GetOrderDetailException = new TikTokShopApiException(
            HttpStatusCode.Unauthorized,
            "401",
            "invalid access token",
            "req-detail-401",
            "{\"message\":\"invalid access token\"}",
            "get_order_detail");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsync("/api/v1/client/integrations/tiktokshop/sync-now", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("TIKTOK_SHOP_RECONNECT_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task SyncNow_ImportsPackages_AndClientOrdersExposeShipmentSummary()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-orders-hub";
        const string tenantSlug = "ttsordershub";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: "cipher-orders-hub", sellerId: 1979655640);

        _factory.FakeTikTokShopApiClient.SearchOrdersResponse = new TikTokShopApiResponse<TikTokShopOrderSearchData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopOrderSearchData
            {
                Orders =
                [
                    new TikTokShopOrderSummary
                    {
                        OrderId = "tts-order-ship-1",
                        CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                ]
            }
        };
        _factory.FakeTikTokShopApiClient.OrderDetailResponse = new TikTokShopApiResponse<TikTokShopOrderDetailData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopOrderDetailData
            {
                Orders =
                [
                    new TikTokShopOrderDetail
                    {
                        OrderId = "tts-order-ship-1",
                        OrderStatus = "AWAITING_SHIPMENT",
                        CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        UpdateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ShopId = "1979655640",
                        LineItems =
                        [
                            new TikTokShopOrderLineItem
                            {
                                Id = "line-ship-1",
                                ProductId = "product-ship-1",
                                SkuId = "sku-ship-1",
                                SellerSku = "seller-ship-1",
                                Quantity = 1
                            }
                        ]
                    }
                ]
            }
        };
        _factory.FakeTikTokShopApiClient.PackageSearchResponse = new TikTokShopApiResponse<TikTokShopPackageSearchData>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopPackageSearchData
            {
                PackageList =
                [
                    new TikTokShopPackageSummary
                    {
                        PackageId = "pkg-1001",
                        PackageStatus = 110,
                        CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        UpdateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                ]
            }
        };
        _factory.FakeTikTokShopApiClient.PackageDetailResponse = new TikTokShopApiResponse<TikTokShopPackageDetail>
        {
            Code = 0,
            Message = "ok",
            Data = new TikTokShopPackageDetail
            {
                PackageId = "pkg-1001",
                PackageStatus = 110,
                DeliveryOption = 1,
                ShippingProvider = "TikTok Shipping",
                TrackingNumber = "BR123456789TT",
                OrderInfoList =
                [
                    new TikTokShopPackageOrderInfo
                    {
                        OrderId = "tts-order-ship-1"
                    }
                ]
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var syncResponse = await client.PostAsync("/api/v1/client/integrations/tiktokshop/sync-now", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/v1/client/orders/marketplace?provider=4");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listPayload = await listResponse.Content.ReadFromJsonAsync<PagedResult<MarketplaceOrderListItemResult>>();
        Assert.NotNull(listPayload);
        var listItem = Assert.Single(listPayload!.Items);
        Assert.Equal("tts-order-ship-1", listItem.MlOrderId);
        Assert.Equal(1, listItem.ShipmentsCount);
        Assert.Equal("BR123456789TT", listItem.TrackingNumber);
        Assert.Equal("TikTok Shipping", listItem.ShippingProvider);

        var detailResponse = await client.GetAsync($"/api/v1/client/orders/marketplace/{listItem.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detailPayload = await detailResponse.Content.ReadFromJsonAsync<MarketplaceOrderDetailResult>();
        Assert.NotNull(detailPayload);
        var shipment = Assert.Single(detailPayload!.Shipments);
        Assert.Equal("pkg-1001", shipment.ShipmentId);
        Assert.Equal("BR123456789TT", shipment.TrackingNumber);
        Assert.Equal("TikTok Shipping", shipment.ShippingProvider);
    }

    [Fact]
    public async Task GetCategories_WhenTikTokReturnsUnauthorized_Returns409ReconnectRequired()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-05";
        const string tenantSlug = "ttsunauthorized";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: "cipher-auth", sellerId: 1979655640);

        _factory.FakeTikTokShopApiClient.CategoriesException = new TikTokShopApiException(
            HttpStatusCode.Unauthorized,
            "401",
            "invalid access token",
            "req-401",
            "{\"message\":\"invalid access token\"}",
            "get_categories");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/client/integrations/tiktokshop/categories");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("TIKTOK_SHOP_RECONNECT_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task GetCategories_WhenTikTokReturnsBadRequestAboutShop_Returns409ReconnectRequired()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-06";
        const string tenantSlug = "ttsbadrequest";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: "cipher-bad-request", sellerId: 1979655640);

        _factory.FakeTikTokShopApiClient.CategoriesException = new TikTokShopApiException(
            HttpStatusCode.BadRequest,
            "400",
            "shop cipher is invalid for this auth token",
            "req-400",
            "{\"message\":\"shop cipher is invalid for this auth token\"}",
            "get_categories");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/client/integrations/tiktokshop/categories");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("TIKTOK_SHOP_RECONNECT_REQUIRED", payload!.Code);
    }

    [Fact]
    public async Task GetCategories_WhenTikTokIsUnavailable_Returns502UpstreamError()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-tts-07";
        const string tenantSlug = "ttsupstream";
        var clientId = Guid.NewGuid();
        await SeedClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, shopCipher: "cipher-upstream", sellerId: 1979655640);

        _factory.FakeTikTokShopApiClient.CategoriesException = new TikTokShopApiException(
            HttpStatusCode.ServiceUnavailable,
            "503",
            "service unavailable",
            "req-503",
            "{\"message\":\"service unavailable\"}",
            "get_categories");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/client/integrations/tiktokshop/categories");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("TIKTOK_SHOP_UPSTREAM_ERROR", payload!.Code);
    }

    private async Task SeedClientAsync(string tenantId, string tenantSlug, Guid clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await TestDataSeeder.SeedTenantAsync(db, tenantId, tenantSlug);

        if (!await db.Clients.AnyAsync(item => item.Id == clientId))
        {
            db.Clients.Add(new Client
            {
                Id = clientId,
                TenantId = tenantId,
                ProtheusCode = $"P-{clientId.ToString("N")[..6]}",
                AccountName = $"Client {clientId.ToString("N")[..6]}",
                Email = $"{clientId:N}@example.test",
                PasswordHash = "hash",
                Status = ClientStatus.Approved,
                MustChangePassword = false,
                ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE)
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task SeedConnectionAsync(string tenantId, Guid clientId, string? shopCipher, long sellerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = await db.TenantMarketplaceConnections.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.Provider == MarketplaceProvider.TikTokShop);

        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.TikTokShop,
                SellerId = sellerId,
                Nickname = "Loja teste TikTok",
                ShopCipher = shopCipher,
                AccessToken = "seed-access-token",
                RefreshToken = "seed-refresh-token",
                TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            db.TenantMarketplaceConnections.Add(connection);
        }
        else
        {
            connection.SellerId = sellerId;
            connection.ShopCipher = shopCipher;
            connection.AccessToken = "seed-access-token";
            connection.RefreshToken = "seed-refresh-token";
            connection.TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
            connection.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedVariantAsync(string variantSku, string baseSku)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.ProductVariants.AnyAsync(item => item.VariantSku == variantSku))
        {
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantSku,
                BaseSku = baseSku,
                Name = $"Variant {variantSku}",
                CatalogPriceCents = 1000,
                CostPriceCents = 500,
                PhysicalStock = 10,
                AvailableStock = 10,
                ReservedStock = 0,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }
    }

    private static string? ExtractQueryParam(string url, string key)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var uri = new Uri(url, UriKind.Absolute);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var pairKey = Uri.UnescapeDataString(pair[..idx]);
            if (!string.Equals(pairKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }

        return null;
    }
}
