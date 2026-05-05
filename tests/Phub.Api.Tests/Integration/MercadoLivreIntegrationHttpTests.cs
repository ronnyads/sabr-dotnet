using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Phub.Api.Tests.TestHost;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests.Integration;

public sealed class MercadoLivreIntegrationHttpTests : IClassFixture<MercadoLivreTestWebApplicationFactory>
{
    private readonly MercadoLivreTestWebApplicationFactory _factory;

    public MercadoLivreIntegrationHttpTests(MercadoLivreTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ConnectUrl_ReturnsAuthorizationUrlWithState()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-01";
        const string tenantSlug = "mltenant";
        var clientId = Guid.NewGuid();
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/integrations/mercadolivre/connect-url",
            new MercadoLivreConnectUrlRequest { ReturnUrl = "/client/integrations/mercadolivre" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<MercadoLivreConnectUrlResult>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.Url));
        Assert.Contains("response_type=code", payload.Url, StringComparison.Ordinal);
        Assert.Contains("&state=", payload.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectUrl_WhenMlAppIsNotConfigured_Returns400MlAppNotConfigured()
    {
        var originalClientId = Environment.GetEnvironmentVariable("MercadoLivre__ClientId");
        var originalClientSecret = Environment.GetEnvironmentVariable("MercadoLivre__ClientSecret");
        var originalRedirectUri = Environment.GetEnvironmentVariable("MercadoLivre__RedirectUri");

        Environment.SetEnvironmentVariable("MercadoLivre__ClientId", "__SET_VIA_ENV__");
        Environment.SetEnvironmentVariable("MercadoLivre__ClientSecret", "__SET_VIA_ENV__");
        Environment.SetEnvironmentVariable("MercadoLivre__RedirectUri", "__SET_VIA_ENV__");

        try
        {
            using var genericFactory = new TestWebApplicationFactory();
            await genericFactory.ResetDatabaseAsync();

            const string tenantId = "tenant-ml-config-01";
            const string tenantSlug = "mlconfig";
            var clientId = Guid.NewGuid();
            await SeedTenantClientInFactoryAsync(genericFactory, tenantId, tenantSlug, clientId);

            using var client = genericFactory.CreateTenantClient(tenantSlug, tenantId, clientId);
            var response = await client.PostAsJsonAsync(
                "/api/v1/client/integrations/mercadolivre/connect-url",
                new MercadoLivreConnectUrlRequest { ReturnUrl = "/client/integrations/mercadolivre" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(payload);
            Assert.Equal("ML_APP_NOT_CONFIGURED", payload!.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MercadoLivre__ClientId", originalClientId);
            Environment.SetEnvironmentVariable("MercadoLivre__ClientSecret", originalClientSecret);
            Environment.SetEnvironmentVariable("MercadoLivre__RedirectUri", originalRedirectUri);
        }
    }

    [Fact]
    public async Task Status_WhenMlAppIsNotConfigured_ReturnsDisconnectedFromDatabaseState()
    {
        using var genericFactory = new TestWebApplicationFactory();
        await genericFactory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-config-02";
        const string tenantSlug = "mlstatus";
        var clientId = Guid.NewGuid();
        await SeedTenantClientInFactoryAsync(genericFactory, tenantId, tenantSlug, clientId);

        using var client = genericFactory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/client/integrations/mercadolivre/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<MercadoLivreIntegrationStatusResult>();
        Assert.NotNull(payload);
        Assert.False(payload!.Connected);
        Assert.Empty(payload.Connections);
    }

    [Fact]
    public async Task Callback_ValidState_PersistsConnectionAndRedirects()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-02";
        const string tenantSlug = "mltenantcb";
        var clientId = Guid.NewGuid();
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        _factory.FakeMercadoLivreApiClient.ExchangeCodeResponse = new MercadoLivreTokenResponse
        {
            AccessToken = "cb-access",
            RefreshToken = "cb-refresh",
            ExpiresInSeconds = 1800
        };
        _factory.FakeMercadoLivreApiClient.UserMeResponse = new MercadoLivreUserMeResponse
        {
            SellerId = "1001001",
            Nickname = "callback-nick"
        };

        using var authClient = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var connectUrlResponse = await authClient.PostAsJsonAsync(
            "/api/v1/client/integrations/mercadolivre/connect-url",
            new MercadoLivreConnectUrlRequest { ReturnUrl = "/client/integrations/mercadolivre" });
        var connectUrl = await connectUrlResponse.Content.ReadFromJsonAsync<MercadoLivreConnectUrlResult>();
        Assert.NotNull(connectUrl);

        var state = ExtractQueryValue(connectUrl!.Url, "state");
        Assert.False(string.IsNullOrWhiteSpace(state));

        using var callbackClient = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var callbackResponse = await callbackClient.GetAsync(
            $"/api/v1/client/integrations/mercadolivre/callback?code=test-code&state={Uri.EscapeDataString(state!)}");

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.NotNull(callbackResponse.Headers.Location);
        Assert.Contains("ml=connected", callbackResponse.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = await db.TenantMarketplaceConnections.SingleOrDefaultAsync();
        Assert.NotNull(connection);
        Assert.Equal(tenantId, connection!.TenantId);
        Assert.Equal(clientId, connection.ClientId);
        Assert.Equal(ParseSellerId("1001001"), connection.SellerId);
    }

    [Fact]
    public async Task Callback_WhenCodeExchangeFails_RedirectsToOauthError_InsteadOf500()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-02b";
        const string tenantSlug = "mltenantcbfail";
        var clientId = Guid.NewGuid();
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using var authClient = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var connectUrlResponse = await authClient.PostAsJsonAsync(
            "/api/v1/client/integrations/mercadolivre/connect-url",
            new MercadoLivreConnectUrlRequest { ReturnUrl = "/client/integrations/mercadolivre" });
        var connectUrl = await connectUrlResponse.Content.ReadFromJsonAsync<MercadoLivreConnectUrlResult>();
        Assert.NotNull(connectUrl);

        var state = ExtractQueryValue(connectUrl!.Url, "state");
        Assert.False(string.IsNullOrWhiteSpace(state));

        _factory.FakeMercadoLivreApiClient.ExchangeCodeException =
            new HttpRequestException("ML oauth token rejected", null, HttpStatusCode.BadRequest);

        using var callbackClient = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var callbackResponse = await callbackClient.GetAsync(
            $"/api/v1/client/integrations/mercadolivre/callback?code=expired-code&state={Uri.EscapeDataString(state!)}");

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.NotNull(callbackResponse.Headers.Location);
        Assert.Contains("ml=oauth_error", callbackResponse.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TenantBypass_IsExclusiveToMercadoLivreCallback()
    {
        await _factory.ResetDatabaseAsync();

        using var anonymousClient = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");

        var callbackResponse = await anonymousClient.GetAsync("/api/v1/client/integrations/mercadolivre/callback");
        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.NotNull(callbackResponse.Headers.Location);
        Assert.Contains("ml=missing_code_or_state", callbackResponse.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        var bootstrapResponse = await anonymousClient.PostAsJsonAsync(
            "/api/v1/auth/bootstrap",
            new
            {
                adminKey = "invalid",
                email = "admin@example.test",
                password = "123456"
            });

        Assert.Equal(HttpStatusCode.BadRequest, bootstrapResponse.StatusCode);
        var bootstrapBody = await bootstrapResponse.Content.ReadAsStringAsync();
        Assert.Contains("Tenant not found", bootstrapBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncNow_IsIdempotent_DoesNotDuplicateOrderItemOrReservation()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-03";
        const string tenantSlug = "mlsync";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001002";
        const string baseSku = "SKU-BASE-ML-03";
        const string variantSku = "SKU-VAR-ML-03";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 10, reservedStock: 0);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-ML-03", null, variantSku);

        _factory.FakeMercadoLivreApiClient.SearchOrdersBySeller[sellerId] = new List<string> { "ORDER-ML-03" };
        _factory.FakeMercadoLivreApiClient.OrdersById["ORDER-ML-03"] = new MercadoLivreOrderDetails
        {
            MlOrderId = "ORDER-ML-03",
            Status = "paid",
            Items = new List<MercadoLivreOrderItemDetails>
            {
                new()
                {
                    MlItemId = "ITEM-ML-03",
                    MlVariationId = null,
                    Quantity = 2,
                    RawJson = "{}"
                }
            },
            RawJson = "{}"
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var first = await client.PostAsJsonAsync("/api/v1/client/integrations/mercadolivre/sync-now", new { sellerId });
        var second = await client.PostAsJsonAsync("/api/v1/client/integrations/mercadolivre/sync-now", new { sellerId });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1, await db.MarketplaceOrders.CountAsync());
        Assert.Equal(1, await db.MarketplaceOrderItems.CountAsync());
        Assert.Equal(1, await db.StockReservations.CountAsync());

        var variant = await db.ProductVariants.SingleAsync(item => item.VariantSku == variantSku);
        Assert.Equal(10, variant.PhysicalStock);
        Assert.Equal(2, variant.ReservedStock);
        Assert.Equal(8, variant.AvailableStock);
    }

    [Fact]
    public async Task MarkPaid_WithUnmappedItems_Returns422MlUnmappedItem()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-08";
        const string tenantSlug = "mlunmapped";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001003";
        var orderId = Guid.NewGuid();

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MarketplaceOrders.Add(new MarketplaceOrder
            {
                Id = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlOrderId = "ORDER-ML-08",
                Status = "ready",
                ImportedAt = DateTimeOffset.UtcNow,
                RawJson = "{}"
            });
            db.MarketplaceOrderItems.Add(new MarketplaceOrderItem
            {
                Id = Guid.NewGuid(),
                MarketplaceOrderId = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlItemId = "ITEM-ML-08",
                MlVariationId = null,
                SabrVariantSku = null,
                Quantity = 1,
                ReservedQuantity = 0,
                MappingState = MarketplaceMappingStates.Unmapped,
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/client/orders/{orderId}/mark-paid",
            new { force = false });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_UNMAPPED_ITEM", payload!.Code);
    }

    [Fact]
    public async Task MarkPaid_ConsumesReservation_UpdatesStocks_AndIsIdempotent()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-04";
        const string tenantSlug = "mlpaid";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001004";
        const string baseSku = "SKU-BASE-ML-04";
        const string variantSku = "SKU-VAR-ML-04";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 10, reservedStock: 0);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-ML-04", null, variantSku);

        _factory.FakeMercadoLivreApiClient.SearchOrdersBySeller[sellerId] = new List<string> { "ORDER-ML-04" };
        _factory.FakeMercadoLivreApiClient.OrdersById["ORDER-ML-04"] = new MercadoLivreOrderDetails
        {
            MlOrderId = "ORDER-ML-04",
            Status = "paid",
            Items = new List<MercadoLivreOrderItemDetails>
            {
                new()
                {
                    MlItemId = "ITEM-ML-04",
                    Quantity = 2,
                    RawJson = "{}"
                }
            },
            RawJson = "{}"
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var sync = await client.PostAsJsonAsync("/api/v1/client/integrations/mercadolivre/sync-now", new { sellerId });
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        Guid orderId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            orderId = (await db.MarketplaceOrders.SingleAsync()).Id;
        }

        var first = await client.PostAsJsonAsync($"/api/v1/client/orders/{orderId}/mark-paid", new { force = false });
        var second = await client.PostAsJsonAsync($"/api/v1/client/orders/{orderId}/mark-paid", new { force = false });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstPayload = await first.Content.ReadFromJsonAsync<MarketplaceMarkPaidResult>();
        var secondPayload = await second.Content.ReadFromJsonAsync<MarketplaceMarkPaidResult>();
        Assert.NotNull(firstPayload);
        Assert.NotNull(secondPayload);
        Assert.False(firstPayload!.AlreadyPaid);
        Assert.True(secondPayload!.AlreadyPaid);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reservation = await verifyDb.StockReservations.SingleAsync();
        var variant = await verifyDb.ProductVariants.SingleAsync(item => item.VariantSku == variantSku);

        Assert.Equal(StockReservationStatus.Consumed, reservation.Status);
        Assert.Equal(8, variant.PhysicalStock);
        Assert.Equal(0, variant.ReservedStock);
        Assert.Equal(8, variant.AvailableStock);
    }

    [Fact]
    public async Task MarkPaid_AfterCutoff_Returns409WithoutForce_AndPersistsRiskWithForce()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-05";
        const string tenantSlug = "mlrisk";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001005";
        const string baseSku = "SKU-BASE-ML-05";
        const string variantSku = "SKU-VAR-ML-05";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 6, reservedStock: 2);

        var orderId = Guid.NewGuid();
        var orderItemId = Guid.NewGuid();
        var nowUtc = DateTimeOffset.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MarketplaceOrders.Add(new MarketplaceOrder
            {
                Id = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlOrderId = "ORDER-ML-05",
                Status = "paid",
                ShipByDeadlineAt = nowUtc.AddMinutes(-10),
                ImportedAt = nowUtc.AddHours(-1),
                RawJson = "{}"
            });
            db.MarketplaceOrderItems.Add(new MarketplaceOrderItem
            {
                Id = orderItemId,
                MarketplaceOrderId = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlItemId = "ITEM-ML-05",
                SabrVariantSku = variantSku,
                Quantity = 2,
                ReservedQuantity = 2,
                MappingState = MarketplaceMappingStates.Mapped,
                RawJson = "{}"
            });
            db.StockReservations.Add(new StockReservation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                SabrVariantSku = variantSku,
                MarketplaceOrderId = orderId,
                MarketplaceOrderItemId = orderItemId,
                Quantity = 2,
                Status = StockReservationStatus.Reserved,
                ReservedAt = nowUtc.AddMinutes(-30),
                ExpiresAt = nowUtc.AddHours(23)
            });
            db.TenantMarketplaceConnections.Add(new TenantMarketplaceConnection
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                Nickname = "risk-seller",
                AccessToken = "token",
                RefreshToken = "refresh",
                TokenExpiresAt = nowUtc.AddHours(1)
            });
            db.TenantMarketplaceListingMaps.Add(new TenantMarketplaceListingMap
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlItemId = "ITEM-ML-05",
                MlVariationId = null,
                SabrVariantSku = variantSku
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var withoutForce = await client.PostAsJsonAsync($"/api/v1/client/orders/{orderId}/mark-paid", new { force = false });
        Assert.Equal(HttpStatusCode.Conflict, withoutForce.StatusCode);

        var conflict = await withoutForce.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(conflict);
        Assert.Equal("PAYMENT_CONFIRMATION_REQUIRED", conflict!.Code);

        var withForce = await client.PostAsJsonAsync($"/api/v1/client/orders/{orderId}/mark-paid", new { force = true });
        Assert.Equal(HttpStatusCode.OK, withForce.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await verifyDb.MarketplaceOrders.SingleAsync(item => item.Id == orderId);
        Assert.NotNull(order.SabrPaymentConfirmedAt);
        Assert.Contains("PAID_AFTER_DEADLINE", order.RiskFlagsJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpireReservations_ReleasesReservedStock_AndSyncsAvailability()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-06";
        const string tenantSlug = "mlexpire";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001006";
        const string baseSku = "SKU-BASE-ML-06";
        const string variantSku = "SKU-VAR-ML-06";
        var nowUtc = DateTimeOffset.UtcNow;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 5, reservedStock: 2);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-ML-06", null, variantSku);

        var orderId = Guid.NewGuid();
        var orderItemId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MarketplaceOrders.Add(new MarketplaceOrder
            {
                Id = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlOrderId = "ORDER-ML-06",
                Status = "created",
                ImportedAt = nowUtc.AddHours(-1),
                RawJson = "{}"
            });
            db.MarketplaceOrderItems.Add(new MarketplaceOrderItem
            {
                Id = orderItemId,
                MarketplaceOrderId = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlItemId = "ITEM-ML-06",
                SabrVariantSku = variantSku,
                Quantity = 2,
                ReservedQuantity = 2,
                MappingState = MarketplaceMappingStates.Mapped,
                RawJson = "{}"
            });
            db.StockReservations.Add(new StockReservation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                SabrVariantSku = variantSku,
                MarketplaceOrderId = orderId,
                MarketplaceOrderItemId = orderItemId,
                Quantity = 2,
                Status = StockReservationStatus.Reserved,
                ReservedAt = nowUtc.AddHours(-2),
                ExpiresAt = nowUtc.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var syncService = scope.ServiceProvider.GetRequiredService<MercadoLivreSyncService>();
            var expired = await syncService.ExpireReservationsAsync();
            Assert.Equal(1, expired);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reservation = await verifyDb.StockReservations.SingleAsync();
        var orderItem = await verifyDb.MarketplaceOrderItems.SingleAsync();
        var variant = await verifyDb.ProductVariants.SingleAsync(item => item.VariantSku == variantSku);

        Assert.Equal(StockReservationStatus.Released, reservation.Status);
        Assert.Equal(0, orderItem.ReservedQuantity);
        Assert.Equal(0, variant.ReservedStock);
        Assert.Equal(5, variant.AvailableStock);
        Assert.Contains(
            _factory.FakeMercadoLivreApiClient.StockUpdates,
            item => item.ItemId == "ITEM-ML-06" && item.AvailableQuantity == 5);
    }

    [Fact]
    public async Task AvailableZero_SyncsStockForAllMappings_AndAdminStatusIsScoped()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-07";
        const string tenantSlug = "mladmin";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-ML-07";
        const string variantSku = "SKU-VAR-ML-07";
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 5, reservedStock: 5);

        var nowUtc = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TenantMarketplaceConnections.AddRange(
                new TenantMarketplaceConnection
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = ParseSellerId("1001101"),
                    Nickname = "seller-a",
                    AccessToken = "token-a",
                    RefreshToken = "refresh-a",
                    TokenExpiresAt = nowUtc.AddHours(1)
                },
                new TenantMarketplaceConnection
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = ParseSellerId("1001102"),
                    Nickname = "seller-b",
                    AccessToken = "token-b",
                    RefreshToken = "refresh-b",
                    TokenExpiresAt = nowUtc.AddHours(1)
                });
            db.TenantMarketplaceListingMaps.AddRange(
                new TenantMarketplaceListingMap
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = ParseSellerId("1001101"),
                    MlItemId = "ITEM-ML-07-A",
                    MlVariationId = null,
                    SabrVariantSku = variantSku
                },
                new TenantMarketplaceListingMap
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = ParseSellerId("1001102"),
                    MlItemId = "ITEM-ML-07-B",
                    MlVariationId = null,
                    SabrVariantSku = variantSku
                });
            db.MarketplaceOrders.Add(new MarketplaceOrder
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId("1001101"),
                MlOrderId = "ORDER-ML-07",
                Status = "created",
                ImportedAt = nowUtc.AddMinutes(-10),
                RawJson = "{}"
            });
            db.MarketplaceOrderItems.Add(new MarketplaceOrderItem
            {
                Id = Guid.NewGuid(),
                MarketplaceOrderId = db.MarketplaceOrders.Local.First().Id,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId("1001101"),
                MlItemId = "ITEM-ML-07-A",
                Quantity = 1,
                ReservedQuantity = 0,
                MappingState = MarketplaceMappingStates.Unmapped,
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var stockService = scope.ServiceProvider.GetRequiredService<StockAvailabilityService>();
            await stockService.SyncStockForSkuAsync(tenantId, clientId, variantSku);
        }

        Assert.Equal(2, _factory.FakeMercadoLivreApiClient.StockUpdates.Count(item => item.AvailableQuantity == 0));
        Assert.Contains(_factory.FakeMercadoLivreApiClient.StockUpdates, item => item.ItemId == "ITEM-ML-07-A");
        Assert.Contains(_factory.FakeMercadoLivreApiClient.StockUpdates, item => item.ItemId == "ITEM-ML-07-B");

        using var adminClient = _factory.CreateAdminClient();
        var statusResponse = await adminClient.GetAsync(
            $"/api/v1/admin/tenants/{tenantSlug}/clients/{clientId}/integrations/mercadolivre/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var statusPayload = await statusResponse.Content.ReadFromJsonAsync<MercadoLivreIntegrationStatusResult>();
        Assert.NotNull(statusPayload);
        Assert.True(statusPayload!.Connected);
        Assert.Equal(2, statusPayload.MappingsCount);
        Assert.Equal(1, statusPayload.OrdersCount);
        Assert.Equal(1, statusPayload.UnmappedItemsCount);

        var missingClient = Guid.NewGuid();
        var missingResponse = await adminClient.GetAsync(
            $"/api/v1/admin/tenants/{tenantSlug}/clients/{missingClient}/integrations/mercadolivre/status");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        var missingError = await missingResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(missingError);
        Assert.Equal("CLIENT_NOT_FOUND", missingError!.Code);
    }

    [Fact]
    public async Task Webhook_AcknowledgesFast_AndDeduplicatesByNotificationId()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-webhook-01";
        const string tenantSlug = "mlwebhook";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001201";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-WH-01", null, "SKU-VAR-WH-01");
        await SeedVariantAsync("SKU-BASE-WH-01", "SKU-VAR-WH-01", 10, 0);

        using var client = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var payload = new MercadoLivreWebhookPayload
        {
            Id = "notif-001",
            Topic = "orders_v2",
            Resource = "/orders/ORDER-WH-01",
            UserId = sellerId
        };

        var first = await client.PostAsJsonAsync("/api/v1/integrations/mercadolivre/webhook?secret=DEV_ML_WEBHOOK_SECRET", payload);
        var second = await client.PostAsJsonAsync("/api/v1/integrations/mercadolivre/webhook?secret=DEV_ML_WEBHOOK_SECRET", payload);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        var firstPayload = await first.Content.ReadFromJsonAsync<MercadoLivreWebhookIngestResult>();
        var secondPayload = await second.Content.ReadFromJsonAsync<MercadoLivreWebhookIngestResult>();
        Assert.NotNull(firstPayload);
        Assert.NotNull(secondPayload);
        Assert.False(firstPayload!.Duplicate);
        Assert.True(secondPayload!.Duplicate);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.MarketplaceEventLogs.CountAsync());
    }

    [Fact]
    public async Task Webhook_WithInvalidSecret_FailsClosed()
    {
        await _factory.ResetDatabaseAsync();

        using var client = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var response = await client.PostAsJsonAsync(
            "/api/v1/integrations/mercadolivre/webhook?secret=WRONG_SECRET",
            new MercadoLivreWebhookPayload
            {
                Id = "notif-invalid",
                Topic = "orders_v2",
                Resource = "/orders/ORDER-WH-INVALID",
                UserId = "1001299"
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("ML_WEBHOOK_VERIFICATION_FAILED", apiError!.Code);
    }

    [Fact]
    public async Task WebhookWorkerPath_ProcessesPendingEvent_AndSyncsOrders()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-webhook-02";
        const string tenantSlug = "mlwebhook2";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001202";
        const string baseSku = "SKU-BASE-WH-02";
        const string variantSku = "SKU-VAR-WH-02";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, 8, 0);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-WH-02", null, variantSku);

        _factory.FakeMercadoLivreApiClient.SearchOrdersBySeller[sellerId] = new List<string> { "ORDER-WH-02" };
        _factory.FakeMercadoLivreApiClient.OrdersById["ORDER-WH-02"] = new MercadoLivreOrderDetails
        {
            MlOrderId = "ORDER-WH-02",
            SellerId = sellerId,
            Status = "created",
            Items = new List<MercadoLivreOrderItemDetails>
            {
                new()
                {
                    MlItemId = "ITEM-WH-02",
                    Quantity = 1,
                    RawJson = "{}"
                }
            },
            RawJson = "{}"
        };

        using var anonymous = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var webhookResponse = await anonymous.PostAsJsonAsync(
            "/api/v1/integrations/mercadolivre/webhook?secret=DEV_ML_WEBHOOK_SECRET",
            new MercadoLivreWebhookPayload
            {
                Id = "notif-wh-02",
                Topic = "orders_v2",
                Resource = "/orders/ORDER-WH-02",
                UserId = sellerId
            });
        Assert.Equal(HttpStatusCode.Accepted, webhookResponse.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var webhookService = scope.ServiceProvider.GetRequiredService<MercadoLivreWebhookService>();
            var processed = await webhookService.ProcessPendingEventsAsync(20);
            Assert.True(processed >= 1);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.MarketplaceOrders.CountAsync(item => item.MlOrderId == "ORDER-WH-02"));
            var evt = await db.MarketplaceEventLogs.SingleAsync(item => item.NotificationId == "notif-wh-02");
            Assert.Equal(MarketplaceEventStatuses.Processed, evt.Status);
        }
    }

    [Fact]
    public async Task WebhookWorkerPath_RejectsOwnerMismatchAndMarksFailed()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-webhook-03";
        const string tenantSlug = "mlwebhook3";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001203";
        const string baseSku = "SKU-BASE-WH-03";
        const string variantSku = "SKU-VAR-WH-03";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, 8, 0);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-WH-03", null, variantSku);

        _factory.FakeMercadoLivreApiClient.OrdersById["ORDER-WH-03"] = new MercadoLivreOrderDetails
        {
            MlOrderId = "ORDER-WH-03",
            SellerId = "1001298",
            Status = "created",
            Items = new List<MercadoLivreOrderItemDetails>
            {
                new()
                {
                    MlItemId = "ITEM-WH-03",
                    Quantity = 1,
                    RawJson = "{}"
                }
            },
            RawJson = "{}"
        };

        using var anonymous = _factory.CreateAnonymousClientWithoutRedirect("http://localhost");
        var webhookResponse = await anonymous.PostAsJsonAsync(
            "/api/v1/integrations/mercadolivre/webhook?secret=DEV_ML_WEBHOOK_SECRET",
            new MercadoLivreWebhookPayload
            {
                Id = "notif-wh-03",
                Topic = "orders_v2",
                Resource = "/orders/ORDER-WH-03",
                UserId = sellerId
            });
        Assert.Equal(HttpStatusCode.Accepted, webhookResponse.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var webhookService = scope.ServiceProvider.GetRequiredService<MercadoLivreWebhookService>();
            await webhookService.ProcessPendingEventsAsync(20);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.MarketplaceOrders.CountAsync(item => item.MlOrderId == "ORDER-WH-03"));
            var evt = await db.MarketplaceEventLogs.SingleAsync(item => item.NotificationId == "notif-wh-03");
            Assert.Equal(MarketplaceEventStatuses.Failed, evt.Status);
            Assert.Equal("ML_WEBHOOK_RESOURCE_OWNER_MISMATCH", evt.LastError);
        }
    }

    [Fact]
    public async Task AdminLabelEndpoint_FetchesAndStoresLabelWhenMissing()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-label-01";
        const string tenantSlug = "mllabel";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001301";
        const string shipmentId = "SHIPMENT-LABEL-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, sellerId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MarketplaceShipments.Add(new MarketplaceShipment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                ShipmentId = shipmentId,
                MlOrderId = "ORDER-LABEL-01",
                Status = "ready_to_ship"
            });
            await db.SaveChangesAsync();
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes("fake-pdf-content");
        _factory.FakeMercadoLivreApiClient.ShipmentLabelsById[shipmentId] = new MercadoLivreShipmentLabelResult
        {
            ShipmentId = shipmentId,
            SourceUrl = "https://api.mercadolibre.com/shipment_labels/SHIPMENT-LABEL-01",
            ContentType = "application/pdf",
            Content = bytes,
            Sha256 = "dummy-sha"
        };

        using var adminClient = _factory.CreateAdminClient();
        var response = await adminClient.GetAsync(
            $"/api/v1/admin/tenants/{tenantSlug}/clients/{clientId}/integrations/mercadolivre/operations/labels/{shipmentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var downloaded = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, downloaded);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var shipment = await verifyDb.MarketplaceShipments.SingleAsync(item => item.ShipmentId == shipmentId);
        Assert.NotNull(shipment.LabelContentBytes);
        Assert.Equal(bytes.Length, shipment.LabelContentBytes!.Length);
        Assert.False(string.IsNullOrWhiteSpace(shipment.LabelSourceUrl));
        Assert.False(string.IsNullOrWhiteSpace(shipment.LabelSha256));
    }

    [Fact]
    public async Task AdminLabelEndpoint_EnqueuesMabangDispatch_AndQueueProcessorSendsLabel()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-label-02";
        const string tenantSlug = "mllabel2";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001302";
        const string shipmentId = "SHIPMENT-LABEL-02";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedConnectionAsync(tenantId, clientId, sellerId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MarketplaceShipments.Add(new MarketplaceShipment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                ShipmentId = shipmentId,
                MlOrderId = "ORDER-LABEL-02",
                Status = "ready_to_ship"
            });
            await db.SaveChangesAsync();
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes("fake-pdf-content-2");
        _factory.FakeMercadoLivreApiClient.ShipmentLabelsById[shipmentId] = new MercadoLivreShipmentLabelResult
        {
            ShipmentId = shipmentId,
            SourceUrl = "https://api.mercadolibre.com/shipment_labels/SHIPMENT-LABEL-02",
            ContentType = "application/pdf",
            Content = bytes,
            Sha256 = "dummy-sha-2"
        };

        using var adminClient = _factory.CreateAdminClient();
        var response = await adminClient.GetAsync(
            $"/api/v1/admin/tenants/{tenantSlug}/clients/{clientId}/integrations/mercadolivre/operations/labels/{shipmentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queueItems = await db.MarketplaceEventLogs
                .Where(item => item.Topic == MarketplaceEventTopics.MabangLabelDispatch
                               && item.ResourceId == shipmentId)
                .ToListAsync();
            Assert.Single(queueItems);
            Assert.Equal(MarketplaceEventStatuses.Pending, queueItems[0].Status);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var dispatchService = scope.ServiceProvider.GetRequiredService<MarketplaceMabangDispatchService>();
            var processed = await dispatchService.ProcessQueueAsync(20);
            Assert.Equal(1, processed);
        }

        Assert.Single(_factory.FakeMabangApiClient.Requests);
        Assert.Equal(shipmentId, _factory.FakeMabangApiClient.Requests[0].ShipmentId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queueItem = await db.MarketplaceEventLogs.SingleAsync(
                item => item.Topic == MarketplaceEventTopics.MabangLabelDispatch
                        && item.ResourceId == shipmentId);
            Assert.Equal(MarketplaceEventStatuses.Processed, queueItem.Status);
            Assert.NotNull(queueItem.ProcessedAt);
        }
    }

    [Fact]
    public async Task MarkPaid_UsesSlaRulePrecedence_ByLogisticTypeAndShippingMode()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-sla-01";
        const string tenantSlug = "mlsla";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001401";
        const string baseSku = "SKU-BASE-SLA-01";
        const string variantSku = "SKU-VAR-SLA-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 5, reservedStock: 1);

        var orderId = Guid.NewGuid();
        var orderItemId = Guid.NewGuid();
        var nowUtc = DateTimeOffset.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MarketplaceOrders.Add(new MarketplaceOrder
            {
                Id = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlOrderId = "ORDER-SLA-01",
                Status = "paid",
                ShippingMode = "me2",
                LogisticType = "flex",
                ShipByDeadlineAt = nowUtc.AddMinutes(-5),
                ImportedAt = nowUtc.AddHours(-1),
                RawJson = "{}"
            });
            db.MarketplaceOrderItems.Add(new MarketplaceOrderItem
            {
                Id = orderItemId,
                MarketplaceOrderId = orderId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = ParseSellerId(sellerId),
                MlItemId = "ITEM-SLA-01",
                SabrVariantSku = variantSku,
                Quantity = 1,
                ReservedQuantity = 1,
                MappingState = MarketplaceMappingStates.Mapped,
                RawJson = "{}"
            });
            db.StockReservations.Add(new StockReservation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                SabrVariantSku = variantSku,
                MarketplaceOrderId = orderId,
                MarketplaceOrderItemId = orderItemId,
                Quantity = 1,
                Status = StockReservationStatus.Reserved,
                ReservedAt = nowUtc.AddMinutes(-10),
                ExpiresAt = nowUtc.AddHours(1)
            });
            db.TenantMarketplaceSlaRules.AddRange(
                new TenantMarketplaceSlaRule
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    LogisticType = "flex",
                    ShippingMode = "me2",
                    CutoffLocalTime = "15:30"
                },
                new TenantMarketplaceSlaRule
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    LogisticType = "flex",
                    ShippingMode = null,
                    CutoffLocalTime = "10:00"
                });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync($"/api/v1/client/orders/{orderId}/mark-paid", new { force = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        Assert.Equal("PAYMENT_CONFIRMATION_REQUIRED", doc.RootElement.GetProperty("code").GetString());
        var cutoff = doc.RootElement.GetProperty("errors").GetProperty("cutoffLocalTime").GetString();
        Assert.Equal("15:30", cutoff);
    }

    [Fact]
    public async Task PublishValidate_ReturnsEligibilityByVariantSku()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-publish-01";
        const string tenantSlug = "mlpublish";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-PUBLISH-01";
        const string variantSku = "SKU-VAR-PUBLISH-01";
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 3, reservedStock: 0);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var product = await db.Products.SingleAsync(item => item.Sku == baseSku);
            product.WidthCm = 0;
            product.HeightCm = 0;
            product.LengthCm = 0;
            product.WeightKg = 0;
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/integrations/mercadolivre/publish/validate",
            new MercadoLivrePublishValidateRequest
            {
                SabrVariantSkus = new List<string> { variantSku }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MercadoLivrePublishValidateResult>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Total);
        Assert.Equal(0, payload.Eligible);
        Assert.Equal(1, payload.Ineligible);
        Assert.Contains(payload.Items[0].Reasons, item => item == "MISSING_IMAGE");
        Assert.Contains(payload.Items[0].Reasons, item => item == "MISSING_DIMENSIONS");
    }

    [Fact]
    public async Task Publish_CreatesItemAndAutoMapping()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-publish-02";
        const string tenantSlug = "mlpublish2";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001502";
        const string baseSku = "SKU-BASE-PUBLISH-02";
        const string variantSku = "SKU-VAR-PUBLISH-02";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 10, reservedStock: 2);
        await SeedConnectionAsync(tenantId, clientId, sellerId);
        await EnsureProductReadyForPublishAsync(baseSku);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/integrations/mercadolivre/publish",
            new MercadoLivrePublishRequest
            {
                SellerId = sellerId,
                SabrVariantSkus = new List<string> { variantSku }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MercadoLivrePublishResult>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Published);
        Assert.Equal(0, payload.Failed);
        Assert.Single(payload.Items);
        Assert.Equal("PUBLISHED", payload.Items[0].Status);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var map = await db.TenantMarketplaceListingMaps.SingleAsync(item =>
                item.TenantId == tenantId &&
                item.ClientId == clientId &&
                item.SellerId == ParseSellerId(sellerId) &&
                item.SabrVariantSku == variantSku);
            Assert.False(string.IsNullOrWhiteSpace(map.MlItemId));
        }

        Assert.Single(_factory.FakeMercadoLivreApiClient.PublishCalls);
        Assert.Contains(_factory.FakeMercadoLivreApiClient.StockUpdates, item => item.AvailableQuantity == 8);
    }

    [Fact]
    public async Task ListListings_ReturnsPublishedRows()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-listings-01";
        const string tenantSlug = "mllistings";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001601";
        const string baseSku = "SKU-BASE-LISTINGS-01";
        const string variantSku = "SKU-VAR-LISTINGS-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 4, reservedStock: 1);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-LIST-01", null, variantSku);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync($"/api/v1/client/integrations/mercadolivre/listings?sellerId={sellerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MercadoLivreListListingsResult>();
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Total);
        Assert.Single(payload.Items);
        Assert.Equal(sellerId, payload.Items[0].SellerId);
        Assert.Equal(variantSku, payload.Items[0].SabrVariantSku);
        Assert.Equal(3, payload.Items[0].AvailableStock);
    }

    [Fact]
    public async Task Reconcile_IsIdempotentAndDoesNotDuplicateOrders()
    {
        await _factory.ResetDatabaseAsync();

        const string tenantId = "tenant-ml-reconcile-01";
        const string tenantSlug = "mlreconcile";
        var clientId = Guid.NewGuid();
        const string sellerId = "1001701";
        const string baseSku = "SKU-BASE-RECON-01";
        const string variantSku = "SKU-VAR-RECON-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedVariantAsync(baseSku, variantSku, physicalStock: 8, reservedStock: 0);
        await SeedConnectionAndMappingAsync(tenantId, clientId, sellerId, "ITEM-RECON-01", null, variantSku);

        _factory.FakeMercadoLivreApiClient.SearchOrdersBySeller[sellerId] = new List<string> { "ORDER-RECON-01" };
        _factory.FakeMercadoLivreApiClient.OrdersById["ORDER-RECON-01"] = new MercadoLivreOrderDetails
        {
            MlOrderId = "ORDER-RECON-01",
            Status = "created",
            Items = new List<MercadoLivreOrderItemDetails>
            {
                new()
                {
                    MlItemId = "ITEM-RECON-01",
                    Quantity = 1,
                    RawJson = "{}"
                }
            },
            RawJson = "{}"
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var first = await client.PostAsJsonAsync("/api/v1/client/integrations/mercadolivre/reconcile", new { sellerId });
        var second = await client.PostAsJsonAsync("/api/v1/client/integrations/mercadolivre/reconcile", new { sellerId });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.MarketplaceOrders.CountAsync(item => item.MlOrderId == "ORDER-RECON-01"));
        Assert.Equal(1, await db.MarketplaceOrderItems.CountAsync(item => item.MlItemId == "ITEM-RECON-01"));
        Assert.Equal(1, await db.StockReservations.CountAsync(item => item.SabrVariantSku == variantSku));
    }

    private async Task SeedTenantClientAsync(string tenantId, string tenantSlug, Guid clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Tenants.AnyAsync(item => item.Id == tenantId))
        {
            db.Tenants.Add(new Phub.Domain.Entities.Tenant
            {
                Id = tenantId,
                Name = $"Tenant {tenantSlug}",
                Slug = tenantSlug,
                Status = TenantStatus.Active
            });
        }

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
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedTenantClientInFactoryAsync(
        TestWebApplicationFactory factory,
        string tenantId,
        string tenantSlug,
        Guid clientId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Tenants.AnyAsync(item => item.Id == tenantId))
        {
            db.Tenants.Add(new Phub.Domain.Entities.Tenant
            {
                Id = tenantId,
                Name = $"Tenant {tenantSlug}",
                Slug = tenantSlug,
                Status = TenantStatus.Active
            });
        }

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
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedVariantAsync(string baseSku, string variantSku, int physicalStock, int reservedStock)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Products.AnyAsync(item => item.Sku == baseSku))
        {
            db.Products.Add(new Product
            {
                Sku = baseSku,
                Name = $"Produto {baseSku}",
                Brand = "Marca Teste",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = true
            });
        }

        if (!await db.ProductVariants.AnyAsync(item => item.VariantSku == variantSku))
        {
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantSku,
                BaseSku = baseSku,
                Name = $"Variante {variantSku}",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                PhysicalStock = physicalStock,
                ReservedStock = reservedStock,
                AvailableStock = Math.Max(0, physicalStock - reservedStock),
                IsActive = true
            });
        }
        else
        {
            var variant = await db.ProductVariants.SingleAsync(item => item.VariantSku == variantSku);
            variant.PhysicalStock = physicalStock;
            variant.ReservedStock = reservedStock;
            variant.AvailableStock = Math.Max(0, physicalStock - reservedStock);
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedConnectionAndMappingAsync(
        string tenantId,
        Guid clientId,
        string sellerId,
        string mlItemId,
        string? mlVariationId,
        string sabrVariantSku)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var nowUtc = DateTimeOffset.UtcNow;
        var sellerIdValue = ParseSellerId(sellerId);

        if (!await db.TenantMarketplaceConnections.AnyAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre
                        && item.SellerId == sellerIdValue))
        {
            db.TenantMarketplaceConnections.Add(new TenantMarketplaceConnection
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = sellerIdValue,
                Nickname = sellerId.ToLowerInvariant(),
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                TokenExpiresAt = nowUtc.AddHours(1)
            });
        }

        if (!await db.TenantMarketplaceListingMaps.AnyAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre
                        && item.SellerId == sellerIdValue
                        && item.MlItemId == mlItemId
                        && item.MlVariationId == mlVariationId))
        {
            db.TenantMarketplaceListingMaps.Add(new TenantMarketplaceListingMap
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = sellerIdValue,
                MlItemId = mlItemId,
                MlVariationId = mlVariationId,
                SabrVariantSku = sabrVariantSku
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedConnectionAsync(string tenantId, Guid clientId, string sellerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var nowUtc = DateTimeOffset.UtcNow;
        var sellerIdValue = ParseSellerId(sellerId);
        if (!await db.TenantMarketplaceConnections.AnyAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre
                        && item.SellerId == sellerIdValue))
        {
            db.TenantMarketplaceConnections.Add(new TenantMarketplaceConnection
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = sellerIdValue,
                Nickname = sellerId.ToLowerInvariant(),
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                TokenExpiresAt = nowUtc.AddHours(1)
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task EnsureProductReadyForPublishAsync(string baseSku)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = await db.Products.SingleAsync(item => item.Sku == baseSku);
        product.CategoryId = "MLB1055";
        product.Description = "Produto pronto para publicacao";
        product.WidthCm = 10;
        product.HeightCm = 10;
        product.LengthCm = 10;
        product.WeightKg = 1;

        if (!await db.ProductImages.AnyAsync(item => item.ProductSku == baseSku))
        {
            db.ProductImages.Add(new ProductImage
            {
                Id = Guid.NewGuid(),
                ProductSku = baseSku,
                Url = "https://images.example.test/produto.jpg",
                MimeType = "image/jpeg",
                SizeBytes = 1024,
                SortOrder = 0,
                IsPrimary = true
            });
        }

        await db.SaveChangesAsync();
    }

    private static long ParseSellerId(string sellerId)
    {
        return long.Parse(sellerId, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string? ExtractQueryValue(string url, string key)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var uri = new Uri(url, UriKind.Absolute);
        var query = uri.Query.TrimStart('?');
        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
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

