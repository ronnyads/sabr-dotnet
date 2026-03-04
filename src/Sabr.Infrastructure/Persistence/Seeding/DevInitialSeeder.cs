using System.Text;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Models;
using Sabr.Application.Security;
using Sabr.Application.Services;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;
using Sabr.Domain.ValueObjects;

namespace Sabr.Infrastructure.Persistence.Seeding;

public sealed class DevInitialSeeder
{
    private const string PreferredTenantId = "dev_sabr";
    private const string TenantSlug = "sabr";
    private const string TenantName = "SABR DEV LOCAL";
    private const string PlatformAdminEmail = "admin.sabr.local@example.test";
    private const string TenantOwnerEmail = "owner.sabr.local@example.test";
    private const string ClientEmail = "cliente.sabr.local@example.test";
    private const string DefaultPassword = "SabrDev@123";
    private const string DefaultCategoryId = "MLB18272";
    private const string DefaultListingTypeId = "gold_special";
    private const string DefaultSiteId = "MLB";
    private const string DefaultOrigin = "Nacional";
    private const long DefaultSellerId = 1979655640;

    private static readonly Guid PreferredClientId = Guid.Parse("8d6578aa-e9f7-4f50-baf8-8f9f6eb8ad5e");
    private static readonly Guid ActivePlanId = Guid.Parse("d4c1014c-810d-46c2-90b4-4a2800064f09");
    private static readonly Guid ActiveCatalogId = Guid.Parse("6663470d-7493-4903-a456-b3c6ce2a7bd2");
    private static readonly Guid PreferredConnectionId = Guid.Parse("f97c24c8-2522-4cd8-88be-83bb2f7f96de");
    private static readonly Guid UncategorizedCategoryId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OutdoorsRootCategoryId = Guid.Parse("d1dd3089-c069-4f34-b21e-a529f6e95e85");
    private static readonly Guid CoolersCategoryId = Guid.Parse("6f920ce1-7ec8-46fa-bf5a-a5532e0688c5");
    private static readonly Guid ThermalBagsCategoryId = Guid.Parse("430af350-2fe2-4d3d-bfa7-0f8b22f90781");

    private readonly AppDbContext _dbContext;
    private readonly ListingDraftService _listingDraftService;

    public DevInitialSeeder(AppDbContext dbContext, ListingDraftService listingDraftService)
    {
        _dbContext = dbContext;
        _listingDraftService = listingDraftService;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = await EnsureTenantAsync(now, cancellationToken);
        await EnsurePlatformAdminAsync(now, cancellationToken);
        await EnsureTenantOwnerUserAsync(tenant.Id, now, cancellationToken);
        var client = await EnsureClientAsync(tenant.Id, now, cancellationToken);
        await EnsureCategoriesAsync(now, cancellationToken);
        await EnsurePlansCatalogsAndSubscriptionAsync(tenant.Id, client.Id, now, cancellationToken);

        var products = BuildProductSeeds();
        await EnsureProductsVariantsImagesAsync(products, now, cancellationToken);
        await EnsureProductCatalogLinksAsync(tenant.Id, ActiveCatalogId, products, now, cancellationToken);

        var connection = await EnsureMercadoLivreConnectionWithoutTokenAsync(
            tenant.Id,
            client.Id,
            now,
            cancellationToken);

        await EnsureListingDraftsAsync(
            tenant.Id,
            client.Id,
            connection,
            products,
            now,
            cancellationToken);

        await PrintSummaryAsync(tenant.Id, client.Id, connection.Id, products, cancellationToken);
    }

    private async Task<Tenant> EnsureTenantAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(
            item => item.Slug == TenantSlug,
            cancellationToken);

        if (tenant == null)
        {
            tenant = await _dbContext.Tenants.FirstOrDefaultAsync(
                item => item.Id == PreferredTenantId,
                cancellationToken);
        }

        if (tenant == null)
        {
            tenant = new Tenant
            {
                Id = PreferredTenantId,
                Name = TenantName,
                Slug = TenantSlug,
                Status = TenantStatus.Active,
                CreatedAt = now
            };
            _dbContext.Tenants.Add(tenant);
        }
        else
        {
            tenant.Name = TenantName;
            tenant.Slug = TenantSlug;
            tenant.Status = TenantStatus.Active;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    private async Task EnsurePlatformAdminAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var normalizedEmail = PlatformAdminEmail.Trim().ToLowerInvariant();
        var user = await _dbContext.PlatformUsers.FirstOrDefaultAsync(
            item => item.EmailNormalized == normalizedEmail,
            cancellationToken);

        if (user == null)
        {
            user = new PlatformUser
            {
                Name = "Admin SABR Local",
                Email = PlatformAdminEmail,
                EmailNormalized = normalizedEmail,
                PasswordHash = PasswordHasher.HashPassword(DefaultPassword),
                Role = PlatformUserRole.SuperAdmin,
                ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.InternalUserRh, ProtheusOperationType.CREATE),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.PlatformUsers.Add(user);
        }
        else
        {
            user.Name = "Admin SABR Local";
            user.Email = PlatformAdminEmail;
            user.EmailNormalized = normalizedEmail;
            user.PasswordHash = PasswordHasher.HashPassword(DefaultPassword);
            user.Role = PlatformUserRole.SuperAdmin;
            user.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.InternalUserRh, ProtheusOperationType.CREATE);
            user.IsActive = true;
            user.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureTenantOwnerUserAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(
            item => item.Email == TenantOwnerEmail,
            cancellationToken);

        if (user == null)
        {
            user = new User
            {
                TenantId = tenantId,
                Name = "Owner SABR Local",
                Email = TenantOwnerEmail,
                PasswordHash = PasswordHasher.HashPassword(DefaultPassword),
                Role = UserRole.SuperAdmin,
                SectorCode = "RH",
                IsActive = true,
                ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.InternalUserRh, ProtheusOperationType.CREATE),
                ProtheusOperation = ProtheusOperationType.CREATE,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Users.Add(user);
        }
        else
        {
            user.TenantId = tenantId;
            user.Name = "Owner SABR Local";
            user.PasswordHash = PasswordHasher.HashPassword(DefaultPassword);
            user.Role = UserRole.SuperAdmin;
            user.SectorCode = "RH";
            user.IsActive = true;
            user.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Client> EnsureClientAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var client = await _dbContext.Clients.FirstOrDefaultAsync(
            item => item.TenantId == tenantId && item.Email == ClientEmail,
            cancellationToken);

        if (client == null)
        {
            client = await _dbContext.Clients.FirstOrDefaultAsync(
                item => item.TenantId == tenantId && item.Id == PreferredClientId,
                cancellationToken);
        }

        if (client == null)
        {
            client = new Client
            {
                Id = PreferredClientId,
                TenantId = tenantId,
                ProtheusCode = "990200",
                AccountName = "SABR CLIENTE DEV LOCAL",
                Email = ClientEmail,
                PasswordHash = PasswordHasher.HashPassword(DefaultPassword),
                LegalName = "SABR CLIENTE DEV LOCAL LTDA",
                TradeName = "SABR DEV LOCAL",
                Document = "00000000000200",
                ResponsibleName = "RESPONSAVEL DEV LOCAL",
                ResponsibleDocument = "00000000001",
                City = "Sao Paulo",
                State = "SP",
                Status = ClientStatus.Approved,
                MustChangePassword = false,
                ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE),
                ProtheusOperation = ProtheusOperationType.CREATE,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Clients.Add(client);
        }
        else
        {
            client.TenantId = tenantId;
            client.ProtheusCode = "990200";
            client.AccountName = "SABR CLIENTE DEV LOCAL";
            client.Email = ClientEmail;
            client.PasswordHash = PasswordHasher.HashPassword(DefaultPassword);
            client.LegalName = "SABR CLIENTE DEV LOCAL LTDA";
            client.TradeName = "SABR DEV LOCAL";
            client.Document = "00000000000200";
            client.ResponsibleName = "RESPONSAVEL DEV LOCAL";
            client.ResponsibleDocument = "00000000001";
            client.City = "Sao Paulo";
            client.State = "SP";
            client.Status = ClientStatus.Approved;
            client.MustChangePassword = false;
            client.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE);
            client.ProtheusOperation = ProtheusOperationType.CREATE;
            client.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return client;
    }

    private async Task EnsureCategoriesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await UpsertCategoryAsync(
            ProductAdminService.UncategorizedSlug,
            "Sem Categoria",
            UncategorizedCategoryId,
            parentId: null,
            now,
            cancellationToken);

        await UpsertCategoryAsync(
            "esportes-e-fitness",
            "Esportes e Fitness",
            OutdoorsRootCategoryId,
            parentId: null,
            now,
            cancellationToken);

        await UpsertCategoryAsync(
            "geladeiras-termicas",
            "Geladeiras Termicas",
            CoolersCategoryId,
            parentId: OutdoorsRootCategoryId,
            now,
            cancellationToken);

        await UpsertCategoryAsync(
            "bolsas-termicas",
            "Bolsas Termicas",
            ThermalBagsCategoryId,
            parentId: OutdoorsRootCategoryId,
            now,
            cancellationToken);
    }

    private async Task UpsertCategoryAsync(
        string slug,
        string name,
        Guid preferredId,
        Guid? parentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var category = await _dbContext.Categories.FirstOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken);

        if (category == null)
        {
            category = await _dbContext.Categories.FirstOrDefaultAsync(
                item => item.Id == preferredId,
                cancellationToken);
        }

        if (category == null)
        {
            category = new Category
            {
                Id = preferredId,
                Name = name,
                Slug = normalizedSlug,
                ParentId = parentId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Categories.Add(category);
        }
        else
        {
            category.Name = name;
            category.Slug = normalizedSlug;
            category.ParentId = parentId;
            category.IsActive = true;
            category.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsurePlansCatalogsAndSubscriptionAsync(
        string tenantId,
        Guid clientId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans.FirstOrDefaultAsync(
            item => item.Id == ActivePlanId && item.TenantId == tenantId,
            cancellationToken);
        if (plan == null)
        {
            _dbContext.Plans.Add(new Plan
            {
                Id = ActivePlanId,
                TenantId = tenantId,
                Name = "Plano Dev Inicial",
                BillingPeriod = BillingPeriod.Monthly,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            plan.Name = "Plano Dev Inicial";
            plan.BillingPeriod = BillingPeriod.Monthly;
            plan.IsActive = true;
            plan.UpdatedAt = now;
        }

        var catalog = await _dbContext.Catalogs.FirstOrDefaultAsync(
            item => item.Id == ActiveCatalogId && item.TenantId == tenantId,
            cancellationToken);
        if (catalog == null)
        {
            _dbContext.Catalogs.Add(new Catalog
            {
                Id = ActiveCatalogId,
                TenantId = tenantId,
                Name = "Catalogo Dev Inicial",
                Description = "Catalogo ativo para bootstrap local completo",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            catalog.Name = "Catalogo Dev Inicial";
            catalog.Description = "Catalogo ativo para bootstrap local completo";
            catalog.IsActive = true;
            catalog.UpdatedAt = now;
        }

        var relation = await _dbContext.PlanCatalogs.FirstOrDefaultAsync(
            item => item.TenantId == tenantId && item.PlanId == ActivePlanId && item.CatalogId == ActiveCatalogId,
            cancellationToken);
        if (relation == null)
        {
            _dbContext.PlanCatalogs.Add(new PlanCatalog
            {
                TenantId = tenantId,
                PlanId = ActivePlanId,
                CatalogId = ActiveCatalogId,
                CreatedAt = now
            });
        }

        var subscription = await _dbContext.ClientPlanSubscriptions.FirstOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.PlanId == ActivePlanId,
            cancellationToken);
        if (subscription == null)
        {
            _dbContext.ClientPlanSubscriptions.Add(new ClientPlanSubscription
            {
                TenantId = tenantId,
                ClientId = clientId,
                PlanId = ActivePlanId,
                IsActive = true,
                StartsAt = now.AddDays(-30),
                EndsAt = now.AddYears(1),
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            subscription.IsActive = true;
            subscription.StartsAt = now.AddDays(-30);
            subscription.EndsAt = now.AddYears(1);
            subscription.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureProductsVariantsImagesAsync(
        IReadOnlyCollection<ProductSeed> products,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var productSeed in products)
        {
            var normalizedBaseSku = Sku.Normalize(productSeed.BaseSku);
            var product = await _dbContext.Products.FirstOrDefaultAsync(
                item => item.Sku == normalizedBaseSku,
                cancellationToken);

            if (product == null)
            {
                product = new Product
                {
                    Sku = normalizedBaseSku,
                    Name = productSeed.Name,
                    Brand = productSeed.Brand,
                    Ncm = productSeed.Ncm,
                    Ean = productSeed.Ean,
                    Description = productSeed.Description,
                    CategoryId = productSeed.CategorySlug,
                    CostPriceCents = productSeed.CostPriceCents,
                    CatalogPriceCents = productSeed.CatalogPriceCents,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _dbContext.Products.Add(product);
            }
            else
            {
                product.Name = productSeed.Name;
                product.Brand = productSeed.Brand;
                product.Ncm = productSeed.Ncm;
                product.Ean = productSeed.Ean;
                product.Description = productSeed.Description;
                product.CategoryId = productSeed.CategorySlug;
                product.CostPriceCents = productSeed.CostPriceCents;
                product.CatalogPriceCents = productSeed.CatalogPriceCents;
                product.IsActive = true;
                product.UpdatedAt = now;
            }

            foreach (var variantSeed in productSeed.Variants)
            {
                var normalizedVariantSku = Sku.Normalize(variantSeed.VariantSku);
                var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
                    item => item.VariantSku == normalizedVariantSku,
                    cancellationToken);
                var availableStock = Math.Max(0, variantSeed.PhysicalStock - variantSeed.ReservedStock);
                if (variant == null)
                {
                    variant = new ProductVariant
                    {
                        VariantSku = normalizedVariantSku,
                        BaseSku = normalizedBaseSku,
                        Name = variantSeed.Name,
                        CostPriceCents = variantSeed.CostPriceCents,
                        CatalogPriceCents = variantSeed.CatalogPriceCents,
                        PhysicalStock = variantSeed.PhysicalStock,
                        ReservedStock = variantSeed.ReservedStock,
                        AvailableStock = availableStock,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    _dbContext.ProductVariants.Add(variant);
                }
                else
                {
                    variant.BaseSku = normalizedBaseSku;
                    variant.Name = variantSeed.Name;
                    variant.CostPriceCents = variantSeed.CostPriceCents;
                    variant.CatalogPriceCents = variantSeed.CatalogPriceCents;
                    variant.PhysicalStock = variantSeed.PhysicalStock;
                    variant.ReservedStock = variantSeed.ReservedStock;
                    variant.AvailableStock = availableStock;
                    variant.IsActive = true;
                    variant.UpdatedAt = now;
                }
            }

            foreach (var imageEntry in productSeed.ImageUrls.Select((url, index) => new { Url = url, Index = index }))
            {
                var imageUrl = imageEntry.Url;
                var image = await _dbContext.ProductImages.FirstOrDefaultAsync(
                    item => item.ProductSku == normalizedBaseSku && item.Url == imageUrl,
                    cancellationToken);

                if (image == null)
                {
                    image = new ProductImage
                    {
                        Id = Guid.NewGuid(),
                        ProductSku = normalizedBaseSku,
                        Url = imageUrl,
                        MimeType = "image/jpeg",
                        SizeBytes = 1024,
                        SortOrder = imageEntry.Index,
                        IsPrimary = imageEntry.Index == 0,
                        CreatedAt = now
                    };
                    _dbContext.ProductImages.Add(image);
                }
                else
                {
                    image.MimeType = "image/jpeg";
                    image.SizeBytes = Math.Max(image.SizeBytes, 1024);
                    image.SortOrder = imageEntry.Index;
                    image.IsPrimary = imageEntry.Index == 0;
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureProductCatalogLinksAsync(
        string tenantId,
        Guid catalogId,
        IReadOnlyCollection<ProductSeed> products,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var product in products)
        {
            var normalizedSku = Sku.Normalize(product.BaseSku);
            var relation = await _dbContext.ProductCatalogs.FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.CatalogId == catalogId
                        && item.ProductSku == normalizedSku,
                cancellationToken);

            if (relation != null)
            {
                continue;
            }

            _dbContext.ProductCatalogs.Add(new ProductCatalog
            {
                TenantId = tenantId,
                CatalogId = catalogId,
                ProductSku = normalizedSku,
                CreatedAt = now
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<TenantMarketplaceConnection> EnsureMercadoLivreConnectionWithoutTokenAsync(
        string tenantId,
        Guid clientId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == DefaultSellerId,
            cancellationToken);

        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                Id = PreferredConnectionId,
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = DefaultSellerId,
                Nickname = "seller-dev-local",
                AccessToken = string.Empty,
                RefreshToken = string.Empty,
                TokenExpiresAt = now.AddDays(-1),
                LastSyncAt = null,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.TenantMarketplaceConnections.Add(connection);
        }
        else
        {
            connection.Nickname = "seller-dev-local";
            connection.AccessToken = string.Empty;
            connection.RefreshToken = string.Empty;
            connection.TokenExpiresAt = now.AddDays(-1);
            connection.LastSyncAt = null;
            connection.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return connection;
    }

    private async Task EnsureListingDraftsAsync(
        string tenantId,
        Guid clientId,
        TenantMarketplaceConnection connection,
        IReadOnlyCollection<ProductSeed> products,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var imageByBaseSku = products.ToDictionary(
            keySelector: item => Sku.Normalize(item.BaseSku),
            elementSelector: item => item.ImageUrls.FirstOrDefault() ?? $"https://images.example.test/{item.BaseSku}.jpg",
            comparer: StringComparer.OrdinalIgnoreCase);

        var draftSeeds = new[]
        {
            new DraftSeed(
                VariantSku: "SAD13790",
                Title: "Bolsa Termica Lancheira para Marmita com Bolso Lateral - (Cores Sortidas)",
                Description: "Bolsa Termica para uso diario, ideal para manter alimentos conservados durante o transporte.",
                CategoryId: DefaultCategoryId,
                Price: 40.00m,
                Gtin: "7892864163877",
                Ncm: "42029200",
                Status: ListingDraftStatus.Valid),
            new DraftSeed(
                VariantSku: "SAD13790-AZUL",
                Title: "Bolsa Termica Lancheira Azul com Bolso Lateral",
                Description: "Versao azul da bolsa Termica com acabamento resistente e facil higienizacao.",
                CategoryId: DefaultCategoryId,
                Price: 42.90m,
                Gtin: "7892864163877",
                Ncm: "42029200",
                Status: ListingDraftStatus.Draft),
            new DraftSeed(
                VariantSku: "SABR-TERMICA-001-UN",
                Title: "Bolsa Termica Compacta para Lanche",
                Description: "Bolsa Termica compacta para rotina escolar e escritorio.",
                CategoryId: DefaultCategoryId,
                Price: 29.90m,
                Gtin: "7891234567895",
                Ncm: "42029200",
                Status: ListingDraftStatus.Error)
        };

        var draftIds = new List<Guid>();
        foreach (var draftSeed in draftSeeds)
        {
            var variantSku = Sku.Normalize(draftSeed.VariantSku);
            var variant = await _dbContext.ProductVariants.AsNoTracking()
                .FirstOrDefaultAsync(item => item.VariantSku == variantSku, cancellationToken);

            if (variant == null)
            {
                throw new InvalidOperationException($"Variant {variantSku} not found while seeding listing drafts.");
            }

            imageByBaseSku.TryGetValue(variant.BaseSku, out var imageUrl);
            imageUrl ??= $"https://images.example.test/{variant.BaseSku}.jpg";

            var request = new ListingDraftUpsertRequest
            {
                IntegrationId = connection.Id,
                Channel = "mercadolivre",
                SellerId = connection.SellerId.ToString(),
                SiteId = DefaultSiteId,
                SabrVariantSku = variantSku,
                CategoryId = draftSeed.CategoryId,
                ListingTypeId = DefaultListingTypeId,
                Condition = "new",
                Title = draftSeed.Title,
                Description = draftSeed.Description,
                Price = draftSeed.Price,
                CurrencyId = "BRL",
                Gtin = draftSeed.Gtin,
                Ncm = draftSeed.Ncm,
                Origin = DefaultOrigin,
                EmptyGtinReason = null,
                Images = new List<ListingDraftImageRequest>
                {
                    new()
                    {
                        Url = imageUrl,
                        Position = 0
                    }
                },
                Attributes = new List<ListingDraftAttributeRequest>
                {
                    new() { Id = "BRAND", ValueName = "Generico" },
                    new() { Id = "MODEL", ValueName = variant.Name }
                },
                ProductCost = variant.CostPriceCents / 100m,
                OperationalCost = 0m,
                PublishMode = "SingleVariant",
                SelectedVariantSkus = new List<string> { variantSku }
            };

            var upsertResult = await _listingDraftService.UpsertAsync(
                tenantId,
                clientId,
                request,
                cancellationToken,
                traceId: "seed-dev-initial");

            if (!upsertResult.Succeeded || upsertResult.Data == null)
            {
                throw new InvalidOperationException($"Failed to upsert draft {variantSku}: {FormatErrors(upsertResult.Errors)}");
            }

            draftIds.Add(upsertResult.Data.DraftId);
        }

        var drafts = await _dbContext.ListingDrafts
            .Where(item => draftIds.Contains(item.DraftId))
            .ToListAsync(cancellationToken);

        foreach (var draftSeed in draftSeeds)
        {
            var draft = drafts.FirstOrDefault(item =>
                string.Equals(item.SabrVariantSku, Sku.Normalize(draftSeed.VariantSku), StringComparison.OrdinalIgnoreCase));
            if (draft == null)
            {
                continue;
            }

            draft.Status = draftSeed.Status;
            draft.UpdatedAt = now;
            if (draftSeed.Status == ListingDraftStatus.Error)
            {
                draft.LastErrorAt = now;
                draft.LastErrorCode = "SEED_SAMPLE_ERROR";
                draft.LastErrorMessage = "Erro de exemplo para demonstrar estado Error no wizard.";
            }
            else
            {
                draft.LastErrorAt = null;
                draft.LastErrorCode = null;
                draft.LastErrorMessage = null;
                draft.LastErrorRawJson = null;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string FormatErrors(IReadOnlyCollection<Sabr.Application.Validation.ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return "unknown";
        }

        var builder = new StringBuilder();
        foreach (var error in errors)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(error.Field);
            builder.Append('=');
            builder.Append(error.Message);
        }

        return builder.ToString();
    }

    private async Task PrintSummaryAsync(
        string tenantId,
        Guid clientId,
        Guid integrationId,
        IReadOnlyCollection<ProductSeed> products,
        CancellationToken cancellationToken)
    {
        var productSkus = products.Select(item => Sku.Normalize(item.BaseSku)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var variantSkus = products.SelectMany(item => item.Variants).Select(item => Sku.Normalize(item.VariantSku)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var productsCount = await _dbContext.Products.CountAsync(item => productSkus.Contains(item.Sku), cancellationToken);
        var variantsCount = await _dbContext.ProductVariants.CountAsync(item => variantSkus.Contains(item.VariantSku), cancellationToken);
        var draftsCount = await _dbContext.ListingDrafts.CountAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.IntegrationId == integrationId,
            cancellationToken);

        Console.WriteLine("Dev Initial Seed Summary");
        Console.WriteLine($"Tenant slug: {TenantSlug}");
        Console.WriteLine($"Admin platform: {PlatformAdminEmail} / {DefaultPassword}");
        Console.WriteLine($"Owner tenant: {TenantOwnerEmail} / {DefaultPassword}");
        Console.WriteLine($"Client: {ClientEmail} / {DefaultPassword}");
        Console.WriteLine($"SellerId: {DefaultSellerId}");
        Console.WriteLine($"Products touched: {productsCount}");
        Console.WriteLine($"Variants touched: {variantsCount}");
        Console.WriteLine($"Drafts touched: {draftsCount}");
    }

    private static IReadOnlyCollection<ProductSeed> BuildProductSeeds()
    {
        return new[]
        {
            new ProductSeed(
                BaseSku: "SAD13790",
                Name: "Bolsa Termica Lancheira para Marmita com Bolso Lateral - (Cores Sortidas)",
                Brand: "Generico",
                Ncm: "42029200",
                Ean: "7892864163877",
                Description: "Bolsa Termica para marmita com bolso lateral e alca reforcada.",
                CategorySlug: "bolsas-termicas",
                CostPriceCents: 700,
                CatalogPriceCents: 4000,
                ImageUrls: new[]
                {
                    "https://images.example.test/sad13790/1.jpg",
                    "https://images.example.test/sad13790/2.jpg",
                    "https://images.example.test/sad13790/3.jpg"
                },
                Variants: new[]
                {
                    new VariantSeed("SAD13790", "Bolsa Termica Lancheira", 700, 4000, 100, 0),
                    new VariantSeed("SAD13790-AZUL", "Bolsa Termica Lancheira Azul", 720, 4290, 60, 0)
                }),
            new ProductSeed(
                BaseSku: "SABR-TERMICA-001",
                Name: "Bolsa Termica Compacta 10L",
                Brand: "SABR",
                Ncm: "42029200",
                Ean: "7891234567895",
                Description: "Bolsa Termica compacta para transporte diario de alimentos.",
                CategorySlug: "geladeiras-termicas",
                CostPriceCents: 1200,
                CatalogPriceCents: 2990,
                ImageUrls: new[]
                {
                    "https://images.example.test/sabr-termica-001/1.jpg",
                    "https://images.example.test/sabr-termica-001/2.jpg"
                },
                Variants: new[]
                {
                    new VariantSeed("SABR-TERMICA-001-UN", "Bolsa Termica Compacta 10L", 1200, 2990, 45, 5)
                }),
            new ProductSeed(
                BaseSku: "SABR-COOLER-020",
                Name: "Caixa Termica 20L",
                Brand: "SABR",
                Ncm: "39231090",
                Ean: "7891234567009",
                Description: "Caixa Termica para camping e praia com tampa reforcada.",
                CategorySlug: "geladeiras-termicas",
                CostPriceCents: 3500,
                CatalogPriceCents: 8990,
                ImageUrls: new[]
                {
                    "https://images.example.test/sabr-cooler-020/1.jpg",
                    "https://images.example.test/sabr-cooler-020/2.jpg"
                },
                Variants: new[]
                {
                    new VariantSeed("SABR-COOLER-020-UN", "Caixa Termica 20L", 3500, 8990, 20, 2)
                })
        };
    }

    private sealed record ProductSeed(
        string BaseSku,
        string Name,
        string Brand,
        string? Ncm,
        string? Ean,
        string? Description,
        string CategorySlug,
        long CostPriceCents,
        long CatalogPriceCents,
        IReadOnlyCollection<string> ImageUrls,
        IReadOnlyCollection<VariantSeed> Variants);

    private sealed record VariantSeed(
        string VariantSku,
        string Name,
        long CostPriceCents,
        long CatalogPriceCents,
        int PhysicalStock,
        int ReservedStock);

    private sealed record DraftSeed(
        string VariantSku,
        string Title,
        string Description,
        string CategoryId,
        decimal Price,
        string? Gtin,
        string? Ncm,
        ListingDraftStatus Status);
}

