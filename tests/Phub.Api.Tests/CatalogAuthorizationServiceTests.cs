using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class CatalogAuthorizationServiceTests
{
    [Fact]
    public async Task Approved_client_can_access_public_catalog_without_plan()
    {
        await using var db = CreateDb();
        var client = new Client { TenantId = "tenant-a", AccountName = "Cliente", Email = "a@test.local", Status = ClientStatus.Approved };
        var catalog = new Catalog { Name = "Público", AccessMode = CatalogAccessMode.Public, IsActive = true };
        db.AddRange(client, catalog, new ProductCatalog { CatalogId = catalog.Id, ProductSku = "SKU-1" });
        await db.SaveChangesAsync();

        var allowed = await new CatalogAuthorizationService(db).IsSkuAllowedAsync("tenant-a", client.Id, " sku-1 ");

        Assert.True(allowed);
    }

    [Fact]
    public async Task Pending_client_cannot_access_public_catalog()
    {
        await using var db = CreateDb();
        var client = new Client { TenantId = "tenant-a", AccountName = "Cliente", Email = "a@test.local", Status = ClientStatus.PendingDocuments };
        var catalog = new Catalog { Name = "Público", AccessMode = CatalogAccessMode.Public, IsActive = true };
        db.AddRange(client, catalog, new ProductCatalog { CatalogId = catalog.Id, ProductSku = "SKU-1" });
        await db.SaveChangesAsync();

        var allowed = await new CatalogAuthorizationService(db).IsSkuAllowedAsync("tenant-a", client.Id, "SKU-1");

        Assert.False(allowed);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);
}
