using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;
using Sabr.Domain.Entities;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests.Integration;

public sealed class AdminProductVariantsHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminProductVariantsHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminProductVariants_CrudAndConflictFlow_IsConsistent()
    {
        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = "SKU-BASE-01",
                Name = "Produto Base",
                Brand = "Marca Base",
                CategoryId = "uncategorized",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();

        var createResponse = await client.PostAsJsonAsync("/api/v1/admin/products/SKU-BASE-01/variants", new AdminProductVariantCreateRequest
        {
            VariantSku = "sku-var-01",
            Name = "Variacao 01",
            CostPriceCents = 1200,
            CatalogPriceCents = 1800,
            PhysicalStock = 10,
            ReservedStock = 3,
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<AdminProductVariantResult>();
        Assert.NotNull(created);
        Assert.Equal("SKU-VAR-01", created!.VariantSku);
        Assert.Equal(7, created.AvailableStock);

        var duplicateResponse = await client.PostAsJsonAsync("/api/v1/admin/products/SKU-BASE-01/variants", new AdminProductVariantCreateRequest
        {
            VariantSku = "SKU-VAR-01"
        });

        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        var duplicateError = await duplicateResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(duplicateError);
        Assert.Equal("VARIANT_ALREADY_EXISTS", duplicateError!.Code);

        var updateResponse = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-BASE-01/variants/SKU-VAR-01", new AdminProductVariantUpdateRequest
        {
            PhysicalStock = 14,
            ReservedStock = 4,
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<AdminProductVariantResult>();
        Assert.NotNull(updated);
        Assert.Equal(10, updated!.AvailableStock);

        var invalidStockResponse = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-BASE-01/variants/SKU-VAR-01", new AdminProductVariantUpdateRequest
        {
            PhysicalStock = 1,
            ReservedStock = 3
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidStockResponse.StatusCode);
        var invalidStockError = await invalidStockResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(invalidStockError);
        Assert.Equal("VALIDATION_ERROR", invalidStockError!.Code);

        var listResponse = await client.GetAsync("/api/v1/admin/products/SKU-BASE-01/variants");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var variants = await listResponse.Content.ReadFromJsonAsync<IReadOnlyCollection<AdminProductVariantResult>>();
        Assert.NotNull(variants);
        Assert.Contains(variants!, item => item.VariantSku == "SKU-VAR-01");

        var deactivateFirst = await client.DeleteAsync("/api/v1/admin/products/SKU-BASE-01/variants/SKU-VAR-01");
        Assert.Equal(HttpStatusCode.NoContent, deactivateFirst.StatusCode);

        var deactivateSecond = await client.DeleteAsync("/api/v1/admin/products/SKU-BASE-01/variants/SKU-VAR-01");
        Assert.Equal(HttpStatusCode.NoContent, deactivateSecond.StatusCode);
    }

    [Fact]
    public async Task AdminProductVariants_WhenBaseProductMissing_ReturnsProductNotFound()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/products/SKU-MISSING/variants", new AdminProductVariantCreateRequest
        {
            VariantSku = "SKU-MISSING-01"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("PRODUCT_NOT_FOUND", apiError!.Code);
    }
}
