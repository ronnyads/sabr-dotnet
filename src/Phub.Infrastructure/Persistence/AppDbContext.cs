using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Phub.Application.Abstractions;
using Phub.Domain.Common;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;
using Phub.Domain.ValueObjects;

namespace Phub.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IAppDbContext, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<User> Users => Set<User>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
    public DbSet<ClientDocument> ClientDocuments => Set<ClientDocument>();
    public DbSet<ClientStore> ClientStores => Set<ClientStore>();
    public DbSet<ClientRefreshToken> ClientRefreshTokens => Set<ClientRefreshToken>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PlatformRefreshToken> PlatformRefreshTokens => Set<PlatformRefreshToken>();
    public DbSet<ProtheusOutboxEvent> ProtheusOutboxEvents => Set<ProtheusOutboxEvent>();
    public DbSet<WalletAccount> WalletAccounts => Set<WalletAccount>();
    public DbSet<WalletLedgerEntry> WalletLedgerEntries => Set<WalletLedgerEntry>();
    public DbSet<WalletDepositRequest> WalletDepositRequests => Set<WalletDepositRequest>();
    public DbSet<WalletDepositProof> WalletDepositProofs => Set<WalletDepositProof>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Catalog> Catalogs => Set<Catalog>();
    public DbSet<PlanCatalog> PlanCatalogs => Set<PlanCatalog>();
    public DbSet<ProductCatalog> ProductCatalogs => Set<ProductCatalog>();
    public DbSet<ClientPlanSubscription> ClientPlanSubscriptions => Set<ClientPlanSubscription>();
    public DbSet<Publication> Publications => Set<Publication>();
    public DbSet<ListingDraft> ListingDrafts => Set<ListingDraft>();
    public DbSet<ProductPriceHistory> ProductPriceHistories => Set<ProductPriceHistory>();
    public DbSet<ProductPriceVersion> ProductPriceVersions => Set<ProductPriceVersion>();
    public DbSet<TenantMarketplaceConnection> TenantMarketplaceConnections => Set<TenantMarketplaceConnection>();
    public DbSet<TenantMarketplaceListingMap> TenantMarketplaceListingMaps => Set<TenantMarketplaceListingMap>();
    public DbSet<MarketplaceListingClassificationVersion> MarketplaceListingClassificationVersions => Set<MarketplaceListingClassificationVersion>();
    public DbSet<ProductMarketplaceCategoryLock> ProductMarketplaceCategoryLocks => Set<ProductMarketplaceCategoryLock>();
    public DbSet<MarketplaceOrder> MarketplaceOrders => Set<MarketplaceOrder>();
    public DbSet<MarketplaceOrderNumberSequence> MarketplaceOrderNumberSequences => Set<MarketplaceOrderNumberSequence>();
    public DbSet<MarketplaceOrderItem> MarketplaceOrderItems => Set<MarketplaceOrderItem>();
    public DbSet<MarketplaceShipment> MarketplaceShipments => Set<MarketplaceShipment>();
    public DbSet<MarketplaceShipmentExternalState> MarketplaceShipmentExternalStates => Set<MarketplaceShipmentExternalState>();
    public DbSet<MarketplaceShipmentOperationalState> MarketplaceShipmentOperationalStates => Set<MarketplaceShipmentOperationalState>();
    public DbSet<MarketplaceShipmentDispatchDeadlineVersion> MarketplaceShipmentDispatchDeadlineVersions => Set<MarketplaceShipmentDispatchDeadlineVersion>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<StockReservationAllocation> StockReservationAllocations => Set<StockReservationAllocation>();
    public DbSet<SellerOwnedStockLot> SellerOwnedStockLots => Set<SellerOwnedStockLot>();
    public DbSet<MarketplaceEventLog> MarketplaceEventLogs => Set<MarketplaceEventLog>();
    public DbSet<MarketplaceOperationJob> MarketplaceOperationJobs => Set<MarketplaceOperationJob>();
    public DbSet<MarketplaceFinancialEntry> MarketplaceFinancialEntries => Set<MarketplaceFinancialEntry>();
    public DbSet<FinancialEconomicHead> FinancialEconomicHeads => Set<FinancialEconomicHead>();
    public DbSet<MarketplaceOrderFinancialState> MarketplaceOrderFinancialStates => Set<MarketplaceOrderFinancialState>();
    public DbSet<SellerTaxProfileVersion> SellerTaxProfileVersions => Set<SellerTaxProfileVersion>();
    public DbSet<FinancialReconciliationCursor> FinancialReconciliationCursors => Set<FinancialReconciliationCursor>();
    public DbSet<FinancialSyncJob> FinancialSyncJobs => Set<FinancialSyncJob>();
    public DbSet<FinancialCorrectionPlan> FinancialCorrectionPlans => Set<FinancialCorrectionPlan>();
    public DbSet<FinancialCorrectionPlanEntry> FinancialCorrectionPlanEntries => Set<FinancialCorrectionPlanEntry>();
    public DbSet<MarketplaceOAuthGrant> MarketplaceOAuthGrants => Set<MarketplaceOAuthGrant>();
    public DbSet<TenantMarketplaceSlaRule> TenantMarketplaceSlaRules => Set<TenantMarketplaceSlaRule>();
    public DbSet<AiPromptConfig> AiPromptConfigs => Set<AiPromptConfig>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierRefreshToken> SupplierRefreshTokens => Set<SupplierRefreshToken>();
    public DbSet<SupplierProduct> SupplierProducts => Set<SupplierProduct>();
    public DbSet<SupplierWalletAccount> SupplierWalletAccounts => Set<SupplierWalletAccount>();
    public DbSet<SupplierWalletEntry> SupplierWalletEntries => Set<SupplierWalletEntry>();
    public DbSet<SupplierWithdrawal> SupplierWithdrawals => Set<SupplierWithdrawal>();
    public DbSet<PlatformFinancialConfig> PlatformFinancialConfigs => Set<PlatformFinancialConfig>();

    public override int SaveChanges()
    {
        TouchEntities();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TouchEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    public async Task<long> NextClientProtheusCodeAsync(CancellationToken cancellationToken = default)
    {
        if (!Database.IsRelational())
        {
            var lastKnown = await Clients.LongCountAsync(cancellationToken);
            return lastKnown + 1;
        }

        var connection = Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT nextval('client_protheus_code_seq')";

        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (shouldClose)
        {
            await connection.CloseAsync();
        }

        return Convert.ToInt64(result);
    }

    private void TouchEntities()
    {
        var entries = ChangeTracker.Entries<EntityBase>();
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                EnsureProtheusTag(entry.Entity);
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                EnsureProtheusTag(entry.Entity);
            }
        }

        foreach (var entry in ChangeTracker.Entries<PlatformUser>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProtheusOutboxEvent>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Product>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.Sku = Sku.Normalize(entry.Entity.Sku);
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Sku = Sku.Normalize(entry.Entity.Sku);
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                var state = NormalizeCatalogCostState(entry.Entity.CatalogPriceCents,
                    entry.Entity.CatalogCostStatus, entry.Entity.CatalogPriceOrigin, ProductPricingModes.Inherited);
                entry.Entity.CatalogCostStatus = state.Status;
                entry.Entity.CatalogPriceOrigin = state.Origin;
                CatalogCostPolicy.EnsureValid(entry.Entity.CatalogPriceCents, state.Status, state.Origin);
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProductImage>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.ProductSku = Sku.Normalize(entry.Entity.ProductSku);
                entry.Entity.CreatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.ProductSku = Sku.Normalize(entry.Entity.ProductSku);
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProductVariant>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.VariantSku = Sku.Normalize(entry.Entity.VariantSku);
                entry.Entity.BaseSku = Sku.Normalize(entry.Entity.BaseSku);
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                entry.Entity.SafetyBuffer = Math.Max(0, entry.Entity.SafetyBuffer);
                entry.Entity.AvailableStock = Math.Max(0, entry.Entity.PhysicalStock - entry.Entity.ClientOwnedStock - entry.Entity.ReservedStock - entry.Entity.SafetyBuffer);
                entry.Entity.InventoryVersion = Math.Max(1, entry.Entity.InventoryVersion);
            }

            if (entry.State == EntityState.Modified)
            {
                var inventoryChanged = entry.Property(nameof(ProductVariant.PhysicalStock)).IsModified
                                       || entry.Property(nameof(ProductVariant.ReservedStock)).IsModified
                                       || entry.Property(nameof(ProductVariant.SafetyBuffer)).IsModified;
                entry.Entity.VariantSku = Sku.Normalize(entry.Entity.VariantSku);
                entry.Entity.BaseSku = Sku.Normalize(entry.Entity.BaseSku);
                entry.Entity.SafetyBuffer = Math.Max(0, entry.Entity.SafetyBuffer);
                entry.Entity.AvailableStock = Math.Max(0, entry.Entity.PhysicalStock - entry.Entity.ClientOwnedStock - entry.Entity.ReservedStock - entry.Entity.SafetyBuffer);
                if (inventoryChanged)
                {
                    entry.Entity.InventoryVersion = Math.Max(1, entry.Entity.InventoryVersion + 1);
                }
                entry.Entity.UpdatedAt = now;
            }


            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                var state = NormalizeCatalogCostState(entry.Entity.CatalogPriceCents,
                    entry.Entity.CatalogCostStatus, entry.Entity.CatalogPriceOrigin, entry.Entity.PricingMode);
                entry.Entity.CatalogCostStatus = state.Status;
                entry.Entity.CatalogPriceOrigin = state.Origin;
                CatalogCostPolicy.EnsureValid(entry.Entity.CatalogPriceCents, state.Status, state.Origin);
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProductPriceVersion>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var state = NormalizeCatalogCostState(entry.Entity.CatalogPriceCents,
                entry.Entity.CatalogCostStatus, entry.Entity.CatalogPriceOrigin, entry.Entity.PricingMode);
            entry.Entity.CatalogCostStatus = state.Status;
            entry.Entity.CatalogPriceOrigin = state.Origin;
            CatalogCostPolicy.EnsureValid(entry.Entity.CatalogPriceCents, state.Status, state.Origin);
        }

        foreach (var entry in ChangeTracker.Entries<Category>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.Slug = NormalizeCategorySlug(entry.Entity.Slug);
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Slug = NormalizeCategorySlug(entry.Entity.Slug);
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Plan>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Catalog>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ClientPlanSubscription>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Publication>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.ProductSku = Sku.Normalize(entry.Entity.ProductSku);
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                if (entry.Entity.PriceSnapshotTakenAt == default)
                {
                    entry.Entity.PriceSnapshotTakenAt = now;
                }
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.ProductSku = Sku.Normalize(entry.Entity.ProductSku);
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ListingDraft>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.BaseProductSku = Sku.Normalize(entry.Entity.BaseProductSku);
                entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                entry.Entity.CurrencyId = string.IsNullOrWhiteSpace(entry.Entity.CurrencyId)
                    ? "BRL"
                    : entry.Entity.CurrencyId.Trim().ToUpperInvariant();
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.BaseProductSku = Sku.Normalize(entry.Entity.BaseProductSku);
                entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                entry.Entity.CurrencyId = string.IsNullOrWhiteSpace(entry.Entity.CurrencyId)
                    ? "BRL"
                    : entry.Entity.CurrencyId.Trim().ToUpperInvariant();
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProductCatalog>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.ProductSku = Sku.Normalize(entry.Entity.ProductSku);
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProductPriceHistory>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.ProductSku = Sku.Normalize(entry.Entity.ProductSku);
            }
        }

        foreach (var entry in ChangeTracker.Entries<TenantMarketplaceConnection>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<TenantMarketplaceListingMap>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProductMarketplaceCategoryLock>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.BaseProductSku = Sku.Normalize(entry.Entity.BaseProductSku);
                entry.Entity.SiteId = string.IsNullOrWhiteSpace(entry.Entity.SiteId)
                    ? "MLB"
                    : entry.Entity.SiteId.Trim().ToUpperInvariant();
                entry.Entity.ApprovedCategoryId = string.IsNullOrWhiteSpace(entry.Entity.ApprovedCategoryId)
                    ? string.Empty
                    : entry.Entity.ApprovedCategoryId.Trim().ToUpperInvariant();
                entry.Entity.ApprovedCategoryName = string.IsNullOrWhiteSpace(entry.Entity.ApprovedCategoryName)
                    ? string.Empty
                    : entry.Entity.ApprovedCategoryName.Trim();
                entry.Entity.ApprovedCategoryPath = string.IsNullOrWhiteSpace(entry.Entity.ApprovedCategoryPath)
                    ? null
                    : entry.Entity.ApprovedCategoryPath.Trim();
                entry.Entity.InternalCategorySlugSnapshot = string.IsNullOrWhiteSpace(entry.Entity.InternalCategorySlugSnapshot)
                    ? null
                    : entry.Entity.InternalCategorySlugSnapshot.Trim().ToLowerInvariant();
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.BaseProductSku = Sku.Normalize(entry.Entity.BaseProductSku);
                entry.Entity.SiteId = string.IsNullOrWhiteSpace(entry.Entity.SiteId)
                    ? "MLB"
                    : entry.Entity.SiteId.Trim().ToUpperInvariant();
                entry.Entity.ApprovedCategoryId = string.IsNullOrWhiteSpace(entry.Entity.ApprovedCategoryId)
                    ? string.Empty
                    : entry.Entity.ApprovedCategoryId.Trim().ToUpperInvariant();
                entry.Entity.ApprovedCategoryName = string.IsNullOrWhiteSpace(entry.Entity.ApprovedCategoryName)
                    ? string.Empty
                    : entry.Entity.ApprovedCategoryName.Trim();
                entry.Entity.ApprovedCategoryPath = string.IsNullOrWhiteSpace(entry.Entity.ApprovedCategoryPath)
                    ? null
                    : entry.Entity.ApprovedCategoryPath.Trim();
                entry.Entity.InternalCategorySlugSnapshot = string.IsNullOrWhiteSpace(entry.Entity.InternalCategorySlugSnapshot)
                    ? null
                    : entry.Entity.InternalCategorySlugSnapshot.Trim().ToLowerInvariant();
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<MarketplaceOrder>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                if (entry.Entity.ImportedAt == default)
                {
                    entry.Entity.ImportedAt = now;
                }
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<MarketplaceOrderItem>())
        {
            if (entry.State == EntityState.Added)
            {
                if (!string.IsNullOrWhiteSpace(entry.Entity.SabrVariantSku))
                {
                    entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                }

                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                if (!string.IsNullOrWhiteSpace(entry.Entity.SabrVariantSku))
                {
                    entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                }

                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<MarketplaceShipment>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<StockReservation>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                if (entry.Entity.ReservedAt == default)
                {
                    entry.Entity.ReservedAt = now;
                }
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.SabrVariantSku = Sku.Normalize(entry.Entity.SabrVariantSku);
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<MarketplaceEventLog>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<MarketplaceOperationJob>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = now;
        }

        foreach (var entry in ChangeTracker.Entries<TenantMarketplaceSlaRule>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Supplier>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                entry.Entity.EmailNormalized = entry.Entity.Email.Trim().ToLowerInvariant();
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.EmailNormalized = entry.Entity.Email.Trim().ToLowerInvariant();
            }
        }

        foreach (var entry in ChangeTracker.Entries<SupplierProduct>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<SupplierWalletAccount>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<SupplierWalletEntry>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<PlatformFinancialConfig>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }

    private static string NormalizeCategorySlug(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Category slug is required.");
        }

        return value.Trim().ToLowerInvariant();
    }

    private static (string Status, string Origin) NormalizeCatalogCostState(
        long catalogPriceCents, string? status, string? origin, string pricingMode)
    {
        if (catalogPriceCents <= 0)
            return (CatalogCostStatuses.Pending, CatalogPriceOrigins.None);

        var normalizedStatus = CatalogCostStatuses.Resolved;
        var normalizedOrigin = string.IsNullOrWhiteSpace(origin) || origin == CatalogPriceOrigins.None
            ? pricingMode == ProductPricingModes.Override
                ? CatalogPriceOrigins.VariantOverride
                : CatalogPriceOrigins.MasterProduct
            : origin.Trim().ToUpperInvariant();
        return (normalizedStatus, normalizedOrigin);
    }

    private static void EnsureProtheusTag(EntityBase entity)
    {
        // User entities are tenant-scoped and do not require SIGA/sector mapping.
        if (entity is User user)
        {
            // Tenant users no longer depend on SIGA/sector for Protheus tags.
            // Platform users do not inherit EntityBase, so they do not pass here.
            user.SectorCode = null;

            if (string.IsNullOrWhiteSpace(user.ProtheusTag))
            {
                user.ProtheusTag = string.Empty;
            }

            return;
        }

        // Demais entidades: precisam de sigla mapeada.
        if (!ProtheusTableMap.TryGetPrefix(entity, out var prefix) || string.IsNullOrWhiteSpace(prefix))
        {
            throw new InvalidOperationException($"Tabela Protheus nao definida para {entity.GetType().Name}. Cadastre a sigla antes de salvar.");
        }

        var requiredPrefix = $"{prefix}_";

        if (string.IsNullOrWhiteSpace(entity.ProtheusTag))
        {
            entity.ProtheusTag = ProtheusTag.Build(prefix, ProtheusOperationType.CREATE);
            entity.ProtheusOperation = ProtheusOperationType.CREATE;
        }

        if (!entity.ProtheusTag.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ProtheusTag prefix mismatch para {entity.GetType().Name}. Esperado prefixo {requiredPrefix}.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("clients");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Document).IsUnique();
            entity.HasIndex(e => e.ProtheusCode).IsUnique();
            entity.Property(e => e.ProtheusCode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.AccountName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(200).IsRequired();
            entity.Property(e => e.LegalName).HasMaxLength(200);
            entity.Property(e => e.TradeName).HasMaxLength(200);
            entity.Property(e => e.Document).HasMaxLength(20);
            entity.Property(e => e.StateRegistration).HasMaxLength(30);
            entity.Property(e => e.CnpjUf).HasMaxLength(2);
            entity.Property(e => e.Whatsapp).HasMaxLength(30);
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.ZipCode).HasMaxLength(10);
            entity.Property(e => e.Street).HasMaxLength(200);
            entity.Property(e => e.Number).HasMaxLength(20);
            entity.Property(e => e.District).HasMaxLength(100);
            entity.Property(e => e.City).HasMaxLength(100);
            entity.Property(e => e.State).HasMaxLength(2);
            entity.Property(e => e.Complement).HasMaxLength(100);
            entity.Property(e => e.ResponsibleName).HasMaxLength(200);
            entity.Property(e => e.ResponsibleDocument).HasMaxLength(20);
            entity.Property(e => e.ProtheusTag).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProtheusRef).HasMaxLength(50);

            entity.HasMany(e => e.Documents)
                .WithOne(d => d.Client)
                .HasForeignKey(d => d.ClientId);

            entity.HasMany(e => e.Stores)
                .WithOne(s => s.Client)
                .HasForeignKey(s => s.ClientId);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(e => e.SectorCode).HasMaxLength(20);
            entity.Property(e => e.ProtheusTag).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProtheusRef).HasMaxLength(50);
        });

        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.ToTable("platform_users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(200).IsRequired();
            entity.Property(e => e.EmailNormalized).HasColumnName("email_normalized").HasMaxLength(200).IsRequired();
            entity.HasIndex(e => e.EmailNormalized).IsUnique();
            entity.Property(e => e.PasswordHash).HasColumnName("password_hash").HasMaxLength(500).IsRequired();
            entity.Property(e => e.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.ProtheusTag).HasColumnName("protheus_tag").HasMaxLength(50).IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });

        modelBuilder.Entity<ClientDocument>(entity =>
        {
            entity.ToTable("client_documents");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.ClientId, e.DocumentType }).IsUnique();
            entity.Property(e => e.FileName).HasMaxLength(255).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.FileUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ReviewReason).HasMaxLength(500);
            entity.Property(e => e.ProtheusTag).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProtheusRef).HasMaxLength(50);
        });

        modelBuilder.Entity<ClientStore>(entity =>
        {
            entity.ToTable("client_stores");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.ClientId, e.StoreCode }).IsUnique();
            entity.Property(e => e.StoreCode).HasMaxLength(10).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.ProtheusTag).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProtheusRef).HasMaxLength(50);
        });

        modelBuilder.Entity<ClientRefreshToken>(entity =>
        {
            entity.ToTable("client_refresh_tokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.TenantId);
            entity.Property(e => e.TokenHash).HasMaxLength(500).IsRequired();
            entity.Property(e => e.TenantId).HasMaxLength(40).IsRequired();
            entity.Property(e => e.CreatedByIp).HasMaxLength(100);
            entity.Property(e => e.CreatedByUserAgent).HasMaxLength(300);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.Property(e => e.Id).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Slug).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.TenantId);
            entity.Property(e => e.TokenHash).HasMaxLength(500).IsRequired();
            entity.Property(e => e.TenantId).HasMaxLength(40).IsRequired();
            entity.Property(e => e.CreatedByIp).HasMaxLength(100);
            entity.Property(e => e.CreatedByUserAgent).HasMaxLength(300);
        });

        modelBuilder.Entity<PlatformRefreshToken>(entity =>
        {
            entity.ToTable("platform_refresh_tokens");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.PlatformUserId);
            entity.Property(e => e.TokenHash).HasMaxLength(500).IsRequired();
            entity.Property(e => e.CreatedByIp).HasMaxLength(100);
            entity.Property(e => e.CreatedByUserAgent).HasMaxLength(300);
        });

        modelBuilder.Entity<ProtheusOutboxEvent>(entity =>
        {
            entity.ToTable("protheus_outbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.AggregateType).HasColumnName("aggregate_type").HasMaxLength(60).IsRequired();
            entity.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
            entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(80).IsRequired();
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(e => e.NextRetryAt).HasColumnName("next_retry_at");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id").IsRequired();
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
            entity.HasIndex(e => new { e.Status, e.NextRetryAt }).HasDatabaseName("ix_outbox_status_retry");
            entity.HasIndex(e => new { e.TenantId, e.CorrelationId, e.EventType }).IsUnique().HasDatabaseName("ux_outbox_idem");
        });

        modelBuilder.Entity<WalletAccount>(entity =>
        {
            entity.ToTable("wallet_accounts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.BalanceCents).HasColumnName("balance_cents").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId }).IsUnique();
        });

        modelBuilder.Entity<WalletLedgerEntry>(entity =>
        {
            entity.ToTable("wallet_ledger");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.AmountCents).HasColumnName("amount_cents").IsRequired();
            entity.Property(e => e.BalanceAfterCents).HasColumnName("balance_after_cents").IsRequired();
            entity.Property(e => e.RequestId).HasColumnName("request_id").IsRequired();
            entity.Property(e => e.ReferenceType).HasColumnName("reference_type").HasMaxLength(40);
            entity.Property(e => e.ReferenceId).HasColumnName("reference_id").HasMaxLength(80);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.CreatedAt }).HasDatabaseName("ix_ledger_client");
            entity.HasIndex(e => new { e.OrderId, e.Type })
                .IsUnique()
                .HasFilter("order_id IS NOT NULL AND type = 'Debit'")
                .HasDatabaseName("ux_wallet_ledger_order_debit");
        });

        modelBuilder.Entity<IdempotencyKey>(entity =>
        {
            entity.ToTable("idempotency_keys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.Scope).HasColumnName("scope").HasMaxLength(60).IsRequired();
            entity.Property(e => e.Key).HasColumnName("key").HasMaxLength(120).IsRequired();
            entity.Property(e => e.RequestHash).HasColumnName("request_hash").HasMaxLength(128).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.ResponseJson).HasColumnName("response_json").HasColumnType("jsonb");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.Scope, e.Key }).IsUnique();
            entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_idem_exp");
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40);
            entity.Property(e => e.ActorType).HasColumnName("actor_type").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.Action).HasColumnName("action").HasMaxLength(120).IsRequired();
            entity.Property(e => e.Entity).HasColumnName("entity").HasMaxLength(80).IsRequired();
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.RequestId).HasColumnName("request_id").IsRequired();
            entity.Property(e => e.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => e.RequestId).HasDatabaseName("ix_audit_request");
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt }).HasDatabaseName("ix_audit_tenant");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products");
            entity.HasKey(e => e.Sku);
            entity.Property(e => e.Sku).HasColumnName("sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(250).IsRequired();
            entity.Property(e => e.Brand).HasColumnName("brand").HasMaxLength(120).IsRequired();
            entity.Property(e => e.Ncm).HasColumnName("ncm").HasMaxLength(8);
            entity.Property(e => e.Ean).HasColumnName("ean").HasMaxLength(14);
            entity.Property(e => e.Description).HasColumnName("description").HasMaxLength(4000);
            entity.Property(e => e.CategoryId).HasColumnName("category_id").HasMaxLength(120);
            entity.Property(e => e.ThumbnailUrl).HasColumnName("thumbnail_url").HasMaxLength(500);
            entity.Property(e => e.WidthCm).HasColumnName("width_cm").HasPrecision(10, 3);
            entity.Property(e => e.HeightCm).HasColumnName("height_cm").HasPrecision(10, 3);
            entity.Property(e => e.LengthCm).HasColumnName("length_cm").HasPrecision(10, 3);
            entity.Property(e => e.WeightKg).HasColumnName("weight_kg").HasPrecision(10, 3);
            entity.Property(e => e.RequiresAnatel).HasColumnName("requires_anatel").IsRequired();
            entity.Property(e => e.AnatelHomologationNumber).HasColumnName("anatel_homologation_number").HasMaxLength(32);
            entity.Property(e => e.AnatelDocumentId).HasColumnName("anatel_document_id");
            entity.Property(e => e.CostPriceCents).HasColumnName("cost_price_cents").IsRequired();
            entity.Property(e => e.CatalogPriceCents).HasColumnName("catalog_price_cents").IsRequired();
            entity.Property(e => e.CatalogCostStatus).HasColumnName("catalog_cost_status").HasMaxLength(40).IsRequired();
            entity.Property(e => e.CatalogPriceOrigin).HasColumnName("catalog_price_origin").HasMaxLength(40).IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_products_sku_format", "\"sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_products_cost_non_negative", "\"cost_price_cents\" >= 0");
            entity.HasCheckConstraint("ck_products_catalog_non_negative", "\"catalog_price_cents\" >= 0");
            entity.HasCheckConstraint("ck_products_catalog_cost_state", "(\"catalog_cost_status\" = 'CATALOG_COST_PENDING' AND \"catalog_price_cents\" = 0 AND \"catalog_price_origin\" = 'NONE') OR (\"catalog_cost_status\" = 'RESOLVED' AND \"catalog_price_cents\" > 0 AND \"catalog_price_origin\" IN ('MASTER_PRODUCT','VARIANT_OVERRIDE','EXTERNAL_SUPPLIER','PREPURCHASED_LOT'))");
            entity.HasCheckConstraint("ck_products_width_non_negative", "\"width_cm\" IS NULL OR \"width_cm\" >= 0");
            entity.HasCheckConstraint("ck_products_height_non_negative", "\"height_cm\" IS NULL OR \"height_cm\" >= 0");
            entity.HasCheckConstraint("ck_products_length_non_negative", "\"length_cm\" IS NULL OR \"length_cm\" >= 0");
            entity.HasCheckConstraint("ck_products_weight_non_negative", "\"weight_kg\" IS NULL OR \"weight_kg\" >= 0");
            entity.HasIndex(e => e.Name).HasDatabaseName("ix_products_name");
        });

        modelBuilder.Entity<ProductImage>(entity =>
        {
            entity.ToTable("product_images");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.ProductSku).HasColumnName("product_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.Url).HasColumnName("url").HasMaxLength(1000).IsRequired();
            entity.Property(e => e.MimeType).HasColumnName("mime_type").HasMaxLength(120).IsRequired();
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes").IsRequired();
            entity.Property(e => e.SortOrder).HasColumnName("sort_order").IsRequired();
            entity.Property(e => e.IsPrimary).HasColumnName("is_primary").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasCheckConstraint("ck_product_images_sku_format", "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_product_images_size_non_negative", "\"size_bytes\" >= 0");
            entity.HasCheckConstraint("ck_product_images_sort_non_negative", "\"sort_order\" >= 0");
            entity.HasIndex(e => new { e.ProductSku, e.SortOrder }).HasDatabaseName("ix_product_images_sku_sort");
            entity.HasIndex(e => new { e.ProductSku, e.IsPrimary }).HasDatabaseName("ix_product_images_sku_primary");
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("categories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Slug).HasColumnName("slug").HasMaxLength(120).IsRequired();
            entity.Property(e => e.ParentId).HasColumnName("parent_id");
            entity.Property(e => e.Icon).HasColumnName("icon").HasMaxLength(120);
            entity.Property(e => e.Description).HasColumnName("description").HasMaxLength(600);
            entity.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_categories_slug_format", "\"slug\" ~ '^[a-z0-9][a-z0-9_/-]{0,119}$'");
            entity.HasIndex(e => e.Slug).IsUnique().HasDatabaseName("ux_categories_slug");
            entity.HasIndex(e => e.ParentId).HasDatabaseName("ix_categories_parent");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("ix_categories_active");
            entity.HasOne<Category>()
                .WithMany()
                .HasForeignKey(e => e.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductVariant>(entity =>
        {
            entity.ToTable("product_variants");
            entity.HasKey(e => e.VariantSku);
            entity.Property(e => e.VariantSku).HasColumnName("variant_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.BaseSku).HasColumnName("base_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(250).IsRequired();
            entity.Property(e => e.CostPriceCents).HasColumnName("cost_price_cents").IsRequired();
            entity.Property(e => e.CatalogPriceCents).HasColumnName("catalog_price_cents").IsRequired();
            entity.Property(e => e.PricingMode).HasColumnName("pricing_mode").HasMaxLength(20).IsRequired();
            entity.Property(e => e.CatalogCostStatus).HasColumnName("catalog_cost_status").HasMaxLength(40).IsRequired();
            entity.Property(e => e.CatalogPriceOrigin).HasColumnName("catalog_price_origin").HasMaxLength(40).IsRequired();
            entity.Property(e => e.PhysicalStock).HasColumnName("physical_stock").IsRequired();
            entity.Property(e => e.ReservedStock).HasColumnName("reserved_stock").IsRequired();
            entity.Property(e => e.AvailableStock).HasColumnName("available_stock").IsRequired();
            entity.Property(e => e.ClientOwnedStock).HasColumnName("client_owned_stock").IsRequired();
            entity.Property(e => e.SafetyBuffer).HasColumnName("safety_buffer").HasDefaultValue(2).IsRequired();
            entity.Property(e => e.InventoryVersion).HasColumnName("inventory_version").HasDefaultValue(1L).IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_product_variants_variant_sku_format", "\"variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_product_variants_base_sku_format", "\"base_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_product_variants_cost_non_negative", "\"cost_price_cents\" >= 0");
            entity.HasCheckConstraint("ck_product_variants_catalog_non_negative", "\"catalog_price_cents\" >= 0");
            entity.HasCheckConstraint("ck_product_variants_catalog_cost_state", "(\"catalog_cost_status\" = 'CATALOG_COST_PENDING' AND \"catalog_price_cents\" = 0 AND \"catalog_price_origin\" = 'NONE') OR (\"catalog_cost_status\" = 'RESOLVED' AND \"catalog_price_cents\" > 0 AND \"catalog_price_origin\" IN ('MASTER_PRODUCT','VARIANT_OVERRIDE','EXTERNAL_SUPPLIER','PREPURCHASED_LOT'))");
            entity.HasCheckConstraint("ck_product_variants_physical_non_negative", "\"physical_stock\" >= 0");
            entity.HasCheckConstraint("ck_product_variants_reserved_non_negative", "\"reserved_stock\" >= 0");
            entity.HasCheckConstraint("ck_product_variants_available_non_negative", "\"available_stock\" >= 0");
            entity.HasCheckConstraint("ck_product_variants_buffer_non_negative", "\"safety_buffer\" >= 0");
            entity.HasCheckConstraint("ck_product_variants_inventory_version_positive", "\"inventory_version\" > 0");
            entity.HasCheckConstraint("ck_product_variants_client_owned_non_negative", "\"client_owned_stock\" >= 0");
            entity.HasCheckConstraint("ck_product_variants_owned_not_above_physical", "\"client_owned_stock\" <= \"physical_stock\"");
            entity.HasCheckConstraint("ck_product_variants_available_consistency", "\"available_stock\" = GREATEST(0, \"physical_stock\" - \"client_owned_stock\" - \"reserved_stock\" - \"safety_buffer\")");
            entity.HasIndex(e => e.BaseSku).HasDatabaseName("ix_product_variants_base_sku");
            entity.HasIndex(e => new { e.BaseSku, e.IsActive }).HasDatabaseName("ix_product_variants_base_sku_active");
            entity.HasOne<Product>()
                .WithMany()
                .HasForeignKey(e => e.BaseSku)
                .HasPrincipalKey(e => e.Sku)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Plan>(entity =>
        {
            entity.ToTable("plans");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.BillingPeriod).HasColumnName("billing_period").IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_plans_billing_period_valid", "\"billing_period\" IN (0, 1, 2, 3)");
            entity.HasIndex(e => e.Name).IsUnique().HasDatabaseName("ux_plans_name");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("ix_plans_active");
        });

        modelBuilder.Entity<Catalog>(entity =>
        {
            entity.ToTable("catalogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasColumnName("description").HasMaxLength(600);
            entity.Property(e => e.AccessMode).HasColumnName("access_mode").HasDefaultValue(CatalogAccessMode.Public).IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => e.Name).IsUnique().HasDatabaseName("ux_catalogs_name");
            entity.HasIndex(e => e.IsActive).HasDatabaseName("ix_catalogs_active");
        });

        modelBuilder.Entity<PlanCatalog>(entity =>
        {
            entity.ToTable("plan_catalogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.PlanId).HasColumnName("plan_id").IsRequired();
            entity.Property(e => e.CatalogId).HasColumnName("catalog_id").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.PlanId, e.CatalogId })
                .IsUnique()
                .HasDatabaseName("ux_plan_catalogs_plan_catalog");
            entity.HasIndex(e => e.PlanId).HasDatabaseName("ix_plan_catalogs_plan");
            entity.HasIndex(e => e.CatalogId).HasDatabaseName("ix_plan_catalogs_catalog");
        });

        modelBuilder.Entity<ProductCatalog>(entity =>
        {
            entity.ToTable("product_catalogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.CatalogId).HasColumnName("catalog_id").IsRequired();
            entity.Property(e => e.ProductSku).HasColumnName("product_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasCheckConstraint("ck_product_catalogs_sku_format", "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasIndex(e => new { e.CatalogId, e.ProductSku })
                .IsUnique()
                .HasDatabaseName("ux_product_catalogs_catalog_sku");
            entity.HasIndex(e => e.ProductSku).HasDatabaseName("ix_product_catalogs_sku");
        });

        modelBuilder.Entity<ClientPlanSubscription>(entity =>
        {
            entity.ToTable("client_plan_subscriptions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.PlanId).HasColumnName("plan_id").IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(e => e.StartsAt).HasColumnName("starts_at").IsRequired();
            entity.Property(e => e.EndsAt).HasColumnName("ends_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.IsActive }).HasDatabaseName("ix_client_plan_subscriptions_lookup");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.PlanId })
                .HasFilter("\"is_active\" = true")
                .IsUnique()
                .HasDatabaseName("ux_client_plan_subscriptions_active_tenant_client_plan");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.PlanId, e.StartsAt })
                .IsUnique()
                .HasDatabaseName("ux_client_plan_subscriptions_tenant_client_plan_starts");
        });

        modelBuilder.Entity<Publication>(entity =>
        {
            entity.ToTable("publications");
            entity.HasKey(e => e.Id);
            entity.UseXminAsConcurrencyToken();
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.ProductSku).HasColumnName("product_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").IsRequired();
            entity.Property(e => e.PricingMode).HasColumnName("pricing_mode").IsRequired();
            entity.Property(e => e.MarkupPercent).HasColumnName("markup_percent").HasPrecision(9, 4);
            entity.Property(e => e.FixedPriceCents).HasColumnName("fixed_price_cents");
            entity.Property(e => e.CostPriceCentsSnapshot).HasColumnName("cost_price_cents_snapshot").IsRequired();
            entity.Property(e => e.CatalogPriceCentsSnapshot).HasColumnName("catalog_price_cents_snapshot").IsRequired();
            entity.Property(e => e.FinalPriceCentsSnapshot).HasColumnName("final_price_cents_snapshot").IsRequired();
            entity.Property(e => e.PriceSnapshotTakenAt).HasColumnName("price_snapshot_taken_at").IsRequired();
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            entity.Property(e => e.UpdatedByUserId).HasColumnName("updated_by_user_id").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_publications_sku_format", "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_publications_cost_non_negative", "\"cost_price_cents_snapshot\" >= 0");
            entity.HasCheckConstraint("ck_publications_catalog_non_negative", "\"catalog_price_cents_snapshot\" >= 0");
            entity.HasCheckConstraint("ck_publications_final_non_negative", "\"final_price_cents_snapshot\" >= 0");
            entity.HasCheckConstraint("ck_publications_fixed_non_negative", "\"fixed_price_cents\" IS NULL OR \"fixed_price_cents\" >= 0");
            entity.HasCheckConstraint("ck_publications_pricing_coherence", "(\"pricing_mode\" <> 2 OR \"fixed_price_cents\" IS NOT NULL) AND (\"pricing_mode\" <> 1 OR \"markup_percent\" IS NOT NULL)");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Status, e.UpdatedAt }).HasDatabaseName("ix_publications_tenant_client_status_updated");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.ProductSku }).HasDatabaseName("ix_publications_tenant_client_sku");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.ProductSku })
                .HasFilter("\"status\" = 0")
                .IsUnique()
                .HasDatabaseName("ux_publications_draft_tenant_client_sku");
        });

        modelBuilder.Entity<ListingDraft>(entity =>
        {
            entity.ToTable("listing_drafts");
            entity.HasKey(e => e.DraftId);
            entity.UseXminAsConcurrencyToken();
            entity.Property(e => e.DraftId).HasColumnName("draft_id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.IntegrationId).HasColumnName("integration_id").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.BaseProductSku).HasColumnName("base_product_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.SabrVariantSku).HasColumnName("sabr_variant_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.CategoryId).HasColumnName("category_id").HasMaxLength(120);
            entity.Property(e => e.ListingTypeId).HasColumnName("listing_type_id").HasMaxLength(40);
            entity.Property(e => e.PriceCents).HasColumnName("price_cents");
            entity.Property(e => e.CurrencyId).HasColumnName("currency_id").HasMaxLength(8).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").IsRequired();
            entity.Property(e => e.ProviderDraftJson).HasColumnName("provider_draft_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.PublishedItemId).HasColumnName("published_item_id").HasMaxLength(80);
            entity.Property(e => e.PublishedVariationId).HasColumnName("published_variation_id").HasMaxLength(80);
            entity.Property(e => e.PublishedPermalink).HasColumnName("published_permalink").HasMaxLength(1000);
            entity.Property(e => e.PublishedApiUrl).HasColumnName("published_api_url").HasMaxLength(1000);
            entity.Property(e => e.LastErrorAt).HasColumnName("last_error_at");
            entity.Property(e => e.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(120);
            entity.Property(e => e.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(2000);
            entity.Property(e => e.LastErrorRawJson).HasColumnName("last_error_raw_json");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_listing_drafts_base_sku_format", "\"base_product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_listing_drafts_variant_sku_format", "\"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_listing_drafts_price_non_negative", "\"price_cents\" IS NULL OR \"price_cents\" >= 0");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.IntegrationId, e.SabrVariantSku })
                .IsUnique()
                .HasDatabaseName("ux_listing_drafts_scope_integration_variant");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.Status, e.UpdatedAt })
                .HasDatabaseName("ix_listing_drafts_scope_status_updated");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.BaseProductSku })
                .HasDatabaseName("ix_listing_drafts_scope_base_sku");
        });

        modelBuilder.Entity<ProductPriceHistory>(entity =>
        {
            entity.ToTable("product_price_history");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40);
            entity.Property(e => e.ProductSku).HasColumnName("product_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.OldCostPriceCents).HasColumnName("old_cost_price_cents").IsRequired();
            entity.Property(e => e.NewCostPriceCents).HasColumnName("new_cost_price_cents").IsRequired();
            entity.Property(e => e.OldCatalogPriceCents).HasColumnName("old_catalog_price_cents").IsRequired();
            entity.Property(e => e.NewCatalogPriceCents).HasColumnName("new_catalog_price_cents").IsRequired();
            entity.Property(e => e.ChangedAt).HasColumnName("changed_at").IsRequired();
            entity.Property(e => e.ChangedByUserId).HasColumnName("changed_by_user_id").IsRequired();
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(200);
            entity.HasCheckConstraint("ck_product_price_history_sku_format", "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasIndex(e => new { e.ProductSku, e.ChangedAt }).HasDatabaseName("ix_product_price_history_sku_changed");
        });

        modelBuilder.Entity<TenantMarketplaceConnection>(entity =>
        {
            entity.ToTable("tenant_marketplace_connections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.Nickname).HasColumnName("nickname").HasMaxLength(120);
            entity.Property(e => e.AccessToken).HasColumnName("access_token").HasMaxLength(4000).IsRequired();
            entity.Property(e => e.RefreshToken).HasColumnName("refresh_token").HasMaxLength(4000).IsRequired();
            entity.Property(e => e.TokenExpiresAt).HasColumnName("token_expires_at").IsRequired();
            entity.Property(e => e.ShopCipher).HasColumnName("shop_cipher").HasMaxLength(500);
            entity.Property(e => e.LastSyncAt).HasColumnName("last_sync_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId })
                .IsUnique()
                .HasDatabaseName("ux_tenant_marketplace_connections_scope_seller");
        });

        modelBuilder.Entity<TenantMarketplaceListingMap>(entity =>
        {
            entity.ToTable("tenant_marketplace_listing_maps");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.IntegrationId).HasColumnName("integration_id");
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.MlItemId).HasColumnName("ml_item_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.MlVariationId).HasColumnName("ml_variation_id").HasMaxLength(80);
            entity.Property(e => e.UserProductId).HasColumnName("user_product_id").HasMaxLength(80);
            entity.Property(e => e.ChannelSku).HasColumnName("channel_sku").HasMaxLength(120);
            entity.Property(e => e.SabrVariantSku).HasColumnName("sabr_variant_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.MappingVersion).HasColumnName("mapping_version").HasDefaultValue(1L).IsRequired().IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_tenant_marketplace_listing_maps_sku_format", "\"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_tenant_marketplace_listing_maps_version_positive", "\"mapping_version\" > 0");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.IntegrationId, e.MlItemId, e.MlVariationId })
                .HasFilter("\"ml_variation_id\" IS NOT NULL")
                .IsUnique()
                .HasDatabaseName("ux_tenant_marketplace_listing_maps_scope_item_var");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.IntegrationId, e.MlItemId })
                .HasFilter("\"ml_variation_id\" IS NULL")
                .IsUnique()
                .HasDatabaseName("ux_tenant_marketplace_listing_maps_scope_item_no_var");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.SabrVariantSku })
                .HasDatabaseName("ix_tenant_marketplace_listing_maps_scope_sku");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.IntegrationId, e.SabrVariantSku })
                .HasDatabaseName("ix_tenant_marketplace_listing_maps_scope_integration_sku");
        });

        modelBuilder.Entity<ProductMarketplaceCategoryLock>(entity =>
        {
            entity.ToTable("product_marketplace_category_lock");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.BaseProductSku).HasColumnName("base_product_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.SiteId).HasColumnName("site_id").HasMaxLength(6).IsRequired();
            entity.Property(e => e.ApprovedCategoryId).HasColumnName("approved_category_id").HasMaxLength(120).IsRequired();
            entity.Property(e => e.ApprovedCategoryName).HasColumnName("approved_category_name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.ApprovedCategoryPath).HasColumnName("approved_category_path").HasMaxLength(600);
            entity.Property(e => e.Status).HasColumnName("status").IsRequired();
            entity.Property(e => e.Source).HasColumnName("source").IsRequired();
            entity.Property(e => e.InternalCategorySlugSnapshot).HasColumnName("internal_category_slug_snapshot").HasMaxLength(120);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_product_marketplace_category_lock_sku_format", "\"base_product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasCheckConstraint("ck_product_marketplace_category_lock_site_format", "\"site_id\" ~ '^ML[A-Z]$'");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.BaseProductSku, e.SiteId })
                .IsUnique()
                .HasDatabaseName("ux_product_marketplace_category_lock_scope_product_site");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.SiteId, e.Status })
                .HasDatabaseName("ix_product_marketplace_category_lock_scope_site_status");
        });

        modelBuilder.Entity<MarketplaceOrder>(entity =>
        {
            entity.ToTable("marketplace_orders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.InternalOrderNumber).HasColumnName("internal_order_number").HasMaxLength(32);
            entity.Property(e => e.MlOrderId).HasColumnName("ml_order_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(80).IsRequired();
            entity.Property(e => e.ChannelCreatedAt).HasColumnName("channel_created_at");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.CurrencyId).HasColumnName("currency_id").HasMaxLength(3);
            entity.Property(e => e.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2);
            entity.Property(e => e.PaidAmount).HasColumnName("paid_amount").HasPrecision(18, 2);
            entity.Property(e => e.ShipmentId).HasColumnName("shipment_id").HasMaxLength(80);
            entity.Property(e => e.ShippingMode).HasColumnName("shipping_mode").HasMaxLength(80);
            entity.Property(e => e.LogisticType).HasColumnName("logistic_type").HasMaxLength(80);
            entity.Property(e => e.ShipByDeadlineAt).HasColumnName("ship_by_deadline_at");
            entity.Property(e => e.ImportedAt).HasColumnName("imported_at").IsRequired();
            entity.Property(e => e.SabrPaymentConfirmedAt).HasColumnName("sabr_payment_confirmed_at");
            entity.Property(e => e.ProductSubtotalCentsAtPayment).HasColumnName("product_subtotal_cents_at_payment");
            entity.Property(e => e.FreightCentsAtPayment).HasColumnName("freight_cents_at_payment");
            entity.Property(e => e.AdditionalCentsAtPayment).HasColumnName("additional_cents_at_payment");
            entity.Property(e => e.DiscountCentsAtPayment).HasColumnName("discount_cents_at_payment");
            entity.Property(e => e.TotalChargeCentsAtPayment).HasColumnName("total_charge_cents_at_payment");
            entity.Property(e => e.WalletLedgerEntryId).HasColumnName("wallet_ledger_entry_id");
            entity.Property(e => e.PaymentQuoteHash).HasColumnName("payment_quote_hash").HasMaxLength(64);
            entity.Property(e => e.CancellationRequestStatus).HasColumnName("cancellation_request_status").HasMaxLength(40);
            entity.Property(e => e.CancellationRequestedAt).HasColumnName("cancellation_requested_at");
            entity.Property(e => e.CancellationRequestedBy).HasColumnName("cancellation_requested_by").HasMaxLength(120);
            entity.Property(e => e.CancellationRequestReason).HasColumnName("cancellation_request_reason").HasMaxLength(1000);
            entity.Property(e => e.CancellationReviewedAt).HasColumnName("cancellation_reviewed_at");
            entity.Property(e => e.CancellationReviewedBy).HasColumnName("cancellation_reviewed_by").HasMaxLength(120);
            entity.Property(e => e.RiskFlagsJson).HasColumnName("risk_flags_json").HasColumnType("jsonb");
            entity.Property(e => e.RawJson).HasColumnName("raw_json").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.MlOrderId })
                .IsUnique()
                .HasDatabaseName("ux_marketplace_orders_scope_provider_ml_order");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.Status, e.ImportedAt })
                .HasDatabaseName("ix_marketplace_orders_scope_status_imported");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.ChannelCreatedAt })
                .HasDatabaseName("ix_marketplace_orders_scope_channel_created");
            entity.HasIndex(e => e.InternalOrderNumber)
                .IsUnique()
                .HasDatabaseName("ux_marketplace_orders_internal_order_number");
            entity.HasIndex(e => e.WalletLedgerEntryId)
                .IsUnique()
                .HasFilter("wallet_ledger_entry_id IS NOT NULL")
                .HasDatabaseName("ux_marketplace_orders_wallet_ledger_entry");
            entity.HasOne<WalletLedgerEntry>()
                .WithMany()
                .HasForeignKey(e => e.WalletLedgerEntryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MarketplaceListingClassificationVersion>(entity =>
        {
            entity.ToTable("marketplace_listing_classification_versions", table =>
            {
                table.HasCheckConstraint("ck_marketplace_listing_classification", "classification IN ('EXTERNAL_SUPPLIER','PENDING')");
                table.HasCheckConstraint("ck_marketplace_listing_external_supplier", "classification <> 'EXTERNAL_SUPPLIER' OR (supplier_name IS NOT NULL AND length(trim(supplier_name)) > 0)");
                table.HasCheckConstraint("ck_marketplace_listing_external_cost", "external_unit_cost_cents IS NULL OR external_unit_cost_cents >= 0");
                table.HasCheckConstraint("ck_marketplace_listing_external_currency", "external_unit_cost_cents IS NULL OR currency_id IS NOT NULL");
                table.HasCheckConstraint("ck_marketplace_listing_classification_version", "version > 0");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.IntegrationId).HasColumnName("integration_id");
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.ExternalItemId).HasColumnName("external_item_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.ExternalVariationKey).HasColumnName("external_variation_key").HasMaxLength(80).IsRequired();
            entity.Property(e => e.Classification).HasColumnName("classification").HasMaxLength(30).IsRequired();
            entity.Property(e => e.SupplierName).HasColumnName("supplier_name").HasMaxLength(200);
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(1000);
            entity.Property(e => e.ExternalUnitCostCents).HasColumnName("external_unit_cost_cents");
            entity.Property(e => e.CurrencyId).HasColumnName("currency_id").HasMaxLength(3);
            entity.Property(e => e.EffectiveAt).HasColumnName("effective_at").IsRequired();
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.IsCurrent).HasColumnName("is_current").IsRequired();
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.ExternalItemId, e.ExternalVariationKey, e.Version })
                .IsUnique().HasDatabaseName("ux_marketplace_listing_classification_scope_version");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.ExternalItemId, e.ExternalVariationKey })
                .HasFilter("is_current = true").IsUnique().HasDatabaseName("ux_marketplace_listing_classification_current");
        });

        modelBuilder.Entity<ProductPriceVersion>(entity =>
        {
            entity.ToTable("product_price_versions", table =>
            {
                table.HasCheckConstraint("ck_product_price_version_mode", "pricing_mode IN ('INHERITED','OVERRIDE')");
                table.HasCheckConstraint("ck_product_price_version_change", "change_type IN ('CHANGE','CORRECTION')");
                table.HasCheckConstraint("ck_product_price_version_range", "valid_to IS NULL OR valid_to > valid_from");
                table.HasCheckConstraint("ck_product_price_version_amounts", "cost_price_cents >= 0 AND catalog_price_cents >= 0");
                table.HasCheckConstraint("ck_product_price_version_catalog_cost_state", "(catalog_cost_status = 'CATALOG_COST_PENDING' AND catalog_price_cents = 0 AND catalog_price_origin = 'NONE') OR (catalog_cost_status = 'RESOLVED' AND catalog_price_cents > 0 AND catalog_price_origin IN ('MASTER_PRODUCT','VARIANT_OVERRIDE','EXTERNAL_SUPPLIER','PREPURCHASED_LOT'))");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ProductSku).HasColumnName("product_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.VariantSku).HasColumnName("variant_sku").HasMaxLength(Sku.MaxLength);
            entity.Property(e => e.PricingMode).HasColumnName("pricing_mode").HasMaxLength(20).IsRequired();
            entity.Property(e => e.CostPriceCents).HasColumnName("cost_price_cents").IsRequired();
            entity.Property(e => e.CatalogPriceCents).HasColumnName("catalog_price_cents").IsRequired();
            entity.Property(e => e.CatalogCostStatus).HasColumnName("catalog_cost_status").HasMaxLength(40).IsRequired();
            entity.Property(e => e.CatalogPriceOrigin).HasColumnName("catalog_price_origin").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from").IsRequired();
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.ChangeType).HasColumnName("change_type").HasMaxLength(20).IsRequired();
            entity.Property(e => e.ChangedByUserId).HasColumnName("changed_by_user_id").IsRequired();
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.ProductSku, e.VariantSku, e.Version }).IsUnique().HasDatabaseName("ux_product_price_versions_scope_version");
            entity.HasIndex(e => new { e.ProductSku, e.VariantSku, e.ValidFrom }).HasDatabaseName("ix_product_price_versions_scope_valid_from");
        });

        modelBuilder.Entity<WalletDepositRequest>(entity =>
        {
            entity.ToTable("wallet_deposit_requests", table =>
            {
                table.HasCheckConstraint("ck_wallet_deposit_amount_positive", "amount_cents > 0");
                table.HasCheckConstraint("ck_wallet_deposit_proof_size", "proof_size_bytes > 0 AND proof_size_bytes <= 10485760");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.AmountCents).HasColumnName("amount_cents").IsRequired();
            entity.Property(e => e.Method).HasColumnName("method").HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.ProofFileName).HasColumnName("proof_file_name").HasMaxLength(180).IsRequired();
            entity.Property(e => e.ProofContentType).HasColumnName("proof_content_type").HasMaxLength(80).IsRequired();
            entity.Property(e => e.ProofSizeBytes).HasColumnName("proof_size_bytes").IsRequired();
            entity.Property(e => e.ClientNote).HasColumnName("client_note").HasMaxLength(500);
            entity.Property(e => e.ReviewNote).HasColumnName("review_note").HasMaxLength(500);
            entity.Property(e => e.ReviewedByUserId).HasColumnName("reviewed_by_user_id");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.LedgerEntryId).HasColumnName("ledger_entry_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.CreatedAt }).HasDatabaseName("ix_wallet_deposit_client_history");
            entity.HasIndex(e => new { e.Status, e.CreatedAt }).HasDatabaseName("ix_wallet_deposit_review_queue");
            entity.HasIndex(e => e.LedgerEntryId).IsUnique().HasFilter("ledger_entry_id IS NOT NULL");
            entity.HasOne<Client>().WithMany().HasForeignKey(e => e.ClientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WalletLedgerEntry>().WithMany().HasForeignKey(e => e.LedgerEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WalletDepositProof>(entity =>
        {
            entity.ToTable("wallet_deposit_proofs");
            entity.HasKey(e => e.DepositRequestId);
            entity.Property(e => e.DepositRequestId).HasColumnName("deposit_request_id");
            entity.Property(e => e.Content).HasColumnName("content").HasColumnType("bytea").IsRequired();
            entity.HasOne(e => e.DepositRequest).WithOne(e => e.Proof).HasForeignKey<WalletDepositProof>(e => e.DepositRequestId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarketplaceOrderNumberSequence>(entity =>
        {
            entity.ToTable("marketplace_order_number_sequences");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.NextNumber).HasColumnName("next_number").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });

        modelBuilder.Entity<MarketplaceOrderItem>(entity =>
        {
            entity.ToTable("marketplace_order_items");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.MarketplaceOrderId).HasColumnName("marketplace_order_id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.MlItemId).HasColumnName("ml_item_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.MlVariationId).HasColumnName("ml_variation_id").HasMaxLength(80);
            entity.Property(e => e.ChannelSku).HasColumnName("channel_sku").HasMaxLength(120);
            entity.Property(e => e.SabrVariantSku).HasColumnName("sabr_variant_sku").HasMaxLength(Sku.MaxLength);
            entity.Property(e => e.ProductName).HasColumnName("product_name").HasMaxLength(300);
            entity.Property(e => e.Quantity).HasColumnName("quantity").IsRequired();
            entity.Property(e => e.CurrencyId).HasColumnName("currency_id").HasMaxLength(3);
            entity.Property(e => e.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2);
            entity.Property(e => e.FullUnitPrice).HasColumnName("full_unit_price").HasPrecision(18, 2);
            entity.Property(e => e.GrossPrice).HasColumnName("gross_price").HasPrecision(18, 2);
            entity.Property(e => e.SaleFee).HasColumnName("sale_fee").HasPrecision(18, 2);
            entity.Property(e => e.CatalogUnitPriceCentsAtPayment).HasColumnName("catalog_unit_price_cents_at_payment");
            entity.Property(e => e.CostUnitPriceCentsAtPayment).HasColumnName("cost_unit_price_cents_at_payment");
            entity.Property(e => e.EconomicAt).HasColumnName("economic_at");
            entity.Property(e => e.EconomicAtSource).HasColumnName("economic_at_source").HasMaxLength(40);
            entity.Property(e => e.CostSource).HasColumnName("cost_source").HasMaxLength(30);
            entity.Property(e => e.CatalogPriceVersionId).HasColumnName("catalog_price_version_id");
            entity.Property(e => e.CostReferencesJson).HasColumnName("cost_references_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.InternalCostStatus).HasColumnName("internal_cost_status").HasMaxLength(20).IsRequired();
            entity.Property(e => e.ProductCostEntryId).HasColumnName("product_cost_entry_id");
            entity.Property(e => e.InternalWalletEntryId).HasColumnName("internal_wallet_entry_id");
            entity.Property(e => e.CostAccruedAt).HasColumnName("cost_accrued_at");
            entity.Property(e => e.CostSettledAt).HasColumnName("cost_settled_at");
            entity.Property(e => e.ChargeLineTotalCentsAtPayment).HasColumnName("charge_line_total_cents_at_payment");
            entity.Property(e => e.ReservedQuantity).HasColumnName("reserved_quantity").IsRequired();
            entity.Property(e => e.MappingState).HasColumnName("mapping_state").HasMaxLength(40).IsRequired();
            entity.Property(e => e.MappingSnapshotId).HasColumnName("mapping_snapshot_id");
            entity.Property(e => e.MappingSnapshotVersion).HasColumnName("mapping_snapshot_version");
            entity.Property(e => e.MappingResolutionReason).HasColumnName("mapping_resolution_reason").HasMaxLength(80);
            entity.Property(e => e.MappingResolvedAt).HasColumnName("mapping_resolved_at");
            entity.Property(e => e.ExternalSupplierName).HasColumnName("external_supplier_name").HasMaxLength(200);
            entity.Property(e => e.ExternalUnitCostCentsSnapshot).HasColumnName("external_unit_cost_cents_snapshot");
            entity.Property(e => e.ExternalCostCurrencyId).HasColumnName("external_cost_currency_id").HasMaxLength(3);
            entity.Property(e => e.ExternalCostVersionId).HasColumnName("external_cost_version_id");
            entity.Property(e => e.RawJson).HasColumnName("raw_json").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_marketplace_order_items_quantity_positive", "\"quantity\" > 0");
            entity.HasCheckConstraint("ck_marketplace_order_items_reserved_non_negative", "\"reserved_quantity\" >= 0");
            entity.HasCheckConstraint("ck_marketplace_order_items_prices_non_negative", "(\"unit_price\" IS NULL OR \"unit_price\" >= 0) AND (\"full_unit_price\" IS NULL OR \"full_unit_price\" >= 0) AND (\"gross_price\" IS NULL OR \"gross_price\" >= 0) AND (\"sale_fee\" IS NULL OR \"sale_fee\" >= 0)");
            entity.HasCheckConstraint("ck_marketplace_order_items_payment_prices_non_negative", "(\"catalog_unit_price_cents_at_payment\" IS NULL OR \"catalog_unit_price_cents_at_payment\" >= 0) AND (\"cost_unit_price_cents_at_payment\" IS NULL OR \"cost_unit_price_cents_at_payment\" >= 0) AND (\"charge_line_total_cents_at_payment\" IS NULL OR \"charge_line_total_cents_at_payment\" >= 0)");
            entity.HasCheckConstraint("ck_marketplace_order_items_external_cost_non_negative", "\"external_unit_cost_cents_snapshot\" IS NULL OR \"external_unit_cost_cents_snapshot\" >= 0");
            entity.HasCheckConstraint("ck_marketplace_order_items_sku_format", "\"sabr_variant_sku\" IS NULL OR \"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasIndex(e => new { e.MarketplaceOrderId, e.MlItemId, e.MlVariationId })
                .IsUnique()
                .HasDatabaseName("ux_marketplace_order_items_order_item_variation");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.MappingState })
                .HasDatabaseName("ix_marketplace_order_items_scope_mapping_state");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.SabrVariantSku })
                .HasDatabaseName("ix_marketplace_order_items_scope_sku");
            entity.HasOne(e => e.MarketplaceOrder)
                .WithMany(o => o.Items)
                .HasForeignKey(e => e.MarketplaceOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MarketplaceShipment>(entity =>
        {
            entity.ToTable("marketplace_shipments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.ShipmentId).HasColumnName("shipment_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.ShipmentScanCode).HasColumnName("shipment_scan_code").HasMaxLength(160);
            entity.Property(e => e.MlOrderId).HasColumnName("ml_order_id").HasMaxLength(80);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(80);
            entity.Property(e => e.Substatus).HasColumnName("substatus").HasMaxLength(120);
            entity.Property(e => e.ShippingMode).HasColumnName("shipping_mode").HasMaxLength(80);
            entity.Property(e => e.LogisticType).HasColumnName("logistic_type").HasMaxLength(80);
            entity.Property(e => e.TrackingNumber).HasColumnName("tracking_number").HasMaxLength(120);
            entity.Property(e => e.TrackingMethod).HasColumnName("tracking_method").HasMaxLength(80);
            entity.Property(e => e.TrackingUrl).HasColumnName("tracking_url").HasMaxLength(1000);
            entity.Property(e => e.ShippedAt).HasColumnName("shipped_at");
            entity.Property(e => e.ShipByDeadlineAt).HasColumnName("ship_by_deadline_at");
            entity.Property(e => e.LabelInternalUrl).HasColumnName("label_internal_url").HasMaxLength(500);
            entity.Property(e => e.LabelSourceUrl).HasColumnName("label_source_url").HasMaxLength(1000);
            entity.Property(e => e.LabelSha256).HasColumnName("label_sha256").HasMaxLength(64);
            entity.Property(e => e.LabelContentType).HasColumnName("label_content_type").HasMaxLength(120);
            entity.Property(e => e.LabelContentBytes).HasColumnName("label_content_bytes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.ShipmentId })
                .IsUnique()
                .HasDatabaseName("ux_marketplace_shipments_scope_provider_shipment");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.MlOrderId })
                .HasDatabaseName("ix_marketplace_shipments_scope_order");
            entity.HasIndex(e => e.ShipmentScanCode)
                .HasDatabaseName("ix_marketplace_shipments_scan_code");
        });

        modelBuilder.Entity<MarketplaceShipmentExternalState>(entity =>
        {
            entity.ToTable("marketplace_shipment_external_states");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.ShipmentId).HasColumnName("shipment_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.MlOrderId).HasColumnName("ml_order_id").HasMaxLength(80);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(80);
            entity.Property(e => e.Substatus).HasColumnName("substatus").HasMaxLength(120);
            entity.Property(e => e.ShippingMode).HasColumnName("shipping_mode").HasMaxLength(80);
            entity.Property(e => e.LogisticType).HasColumnName("logistic_type").HasMaxLength(80);
            entity.Property(e => e.HandlingAt).HasColumnName("handling_at");
            entity.Property(e => e.ReadyToShipAt).HasColumnName("ready_to_ship_at");
            entity.Property(e => e.FirstPrintedAt).HasColumnName("first_printed_at");
            entity.Property(e => e.ShippedAt).HasColumnName("shipped_at");
            entity.Property(e => e.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(e => e.NotDeliveredAt).HasColumnName("not_delivered_at");
            entity.Property(e => e.ReturnedAt).HasColumnName("returned_at");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.DeliveryExpectedAt).HasColumnName("delivery_expected_at");
            entity.Property(e => e.DelayType).HasColumnName("delay_type").HasMaxLength(120);
            entity.Property(e => e.TrackingNumber).HasColumnName("tracking_number").HasMaxLength(120);
            entity.Property(e => e.TrackingMethod).HasColumnName("tracking_method").HasMaxLength(80);
            entity.Property(e => e.TrackingUrl).HasColumnName("tracking_url").HasMaxLength(1000);
            entity.Property(e => e.ProviderUpdatedAt).HasColumnName("provider_updated_at");
            entity.Property(e => e.LastMarketplaceSyncAt).HasColumnName("last_marketplace_sync_at").IsRequired();
            entity.Property(e => e.PayloadHash).HasColumnName("payload_hash").HasMaxLength(64).IsRequired();
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.NextReconciliationAt).HasColumnName("next_reconciliation_at");
            entity.Property(e => e.ReconciliationAttempts).HasColumnName("reconciliation_attempts").IsRequired();
            entity.Property(e => e.LockedBy).HasColumnName("locked_by").HasMaxLength(120);
            entity.Property(e => e.LeaseUntil).HasColumnName("lease_until");
            entity.Property(e => e.FreshnessState).HasColumnName("freshness_state").HasMaxLength(30).IsRequired();
            entity.Property(e => e.LastSyncError).HasColumnName("last_sync_error").HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.ShipmentId }).IsUnique()
                .HasDatabaseName("ux_shipment_external_scope_provider_shipment");
            entity.HasIndex(e => new { e.NextReconciliationAt, e.LeaseUntil })
                .HasDatabaseName("ix_shipment_external_reconciliation");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.FreshnessState, e.LastMarketplaceSyncAt })
                .HasDatabaseName("ix_shipment_external_scope_freshness");
            entity.HasCheckConstraint("ck_shipment_external_version", "\"version\" > 0");
        });

        modelBuilder.Entity<MarketplaceShipmentOperationalState>(entity =>
        {
            entity.ToTable("marketplace_shipment_operational_states");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.ShipmentId).HasColumnName("shipment_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.LabelPrintedAt).HasColumnName("label_printed_at");
            entity.Property(e => e.LabelPrintedBy).HasColumnName("label_printed_by").HasMaxLength(160);
            entity.Property(e => e.PickingStartedAt).HasColumnName("picking_started_at");
            entity.Property(e => e.PickingStartedBy).HasColumnName("picking_started_by").HasMaxLength(160);
            entity.Property(e => e.SeparatedAt).HasColumnName("separated_at");
            entity.Property(e => e.SeparatedBy).HasColumnName("separated_by").HasMaxLength(160);
            entity.Property(e => e.PackedAt).HasColumnName("packed_at");
            entity.Property(e => e.PackedBy).HasColumnName("packed_by").HasMaxLength(160);
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.ShipmentId }).IsUnique()
                .HasDatabaseName("ux_shipment_operational_scope_provider_shipment");
            entity.HasCheckConstraint("ck_shipment_operational_version", "\"version\" > 0");
        });

        modelBuilder.Entity<MarketplaceShipmentDispatchDeadlineVersion>(entity =>
        {
            entity.ToTable("marketplace_shipment_dispatch_deadline_versions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.ShipmentId).HasColumnName("shipment_id").HasMaxLength(80).IsRequired();
            entity.Property(e => e.DispatchDeadline).HasColumnName("dispatch_deadline").IsRequired();
            entity.Property(e => e.Source).HasColumnName("source").HasMaxLength(500).IsRequired();
            entity.Property(e => e.ProviderLastUpdatedAt).HasColumnName("provider_last_updated_at");
            entity.Property(e => e.QueriedAt).HasColumnName("queried_at").IsRequired();
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.PayloadHash).HasColumnName("payload_hash").HasMaxLength(64).IsRequired();
            entity.Property(e => e.IsCurrent).HasColumnName("is_current").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.ShipmentId, e.Version }).IsUnique()
                .HasDatabaseName("ux_dispatch_deadline_scope_version");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.ShipmentId, e.PayloadHash }).IsUnique()
                .HasDatabaseName("ux_dispatch_deadline_scope_hash");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.ShipmentId }).IsUnique()
                .HasFilter("\"is_current\" = TRUE")
                .HasDatabaseName("ux_dispatch_deadline_current");
            entity.HasIndex(e => new { e.IsCurrent, e.DispatchDeadline })
                .HasDatabaseName("ix_dispatch_deadline_current_deadline");
            entity.HasCheckConstraint("ck_dispatch_deadline_version", "\"version\" > 0");
        });

        modelBuilder.Entity<StockReservation>(entity =>
        {
            entity.ToTable("stock_reservations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.SabrVariantSku).HasColumnName("sabr_variant_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.MarketplaceOrderId).HasColumnName("marketplace_order_id").IsRequired();
            entity.Property(e => e.MarketplaceOrderItemId).HasColumnName("marketplace_order_item_id").IsRequired();
            entity.Property(e => e.Quantity).HasColumnName("quantity").IsRequired();
            entity.Property(e => e.Source).HasColumnName("source").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").IsRequired();
            entity.Property(e => e.ReservedAt).HasColumnName("reserved_at").IsRequired();
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasCheckConstraint("ck_stock_reservations_quantity_positive", "\"quantity\" > 0");
            entity.HasCheckConstraint("ck_stock_reservations_sku_format", "\"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.SabrVariantSku, e.Status, e.ReservedAt })
                .HasDatabaseName("ix_stock_reservations_scope_sku_status_reserved_at");
            entity.HasIndex(e => new { e.MarketplaceOrderId, e.MarketplaceOrderItemId })
                .HasDatabaseName("ix_stock_reservations_order_item");
            entity.HasOne<MarketplaceOrder>()
                .WithMany()
                .HasForeignKey(e => e.MarketplaceOrderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<MarketplaceOrderItem>()
                .WithMany()
                .HasForeignKey(e => e.MarketplaceOrderItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProductVariant>()
                .WithMany()
                .HasForeignKey(e => e.SabrVariantSku)
                .HasPrincipalKey(e => e.VariantSku)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MarketplaceEventLog>(entity =>
        {
            entity.ToTable("marketplace_event_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.Topic).HasColumnName("topic").HasMaxLength(120).IsRequired();
            entity.Property(e => e.ResourceId).HasColumnName("resource_id").HasMaxLength(200).IsRequired();
            entity.Property(e => e.NotificationId).HasColumnName("notification_id").HasMaxLength(120);
            entity.Property(e => e.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(400).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(40).IsRequired();
            entity.Property(e => e.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
            entity.Property(e => e.LastErrorAt).HasColumnName("last_error_at");
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(1000);
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => e.DedupeKey)
                .IsUnique()
                .HasDatabaseName("ux_marketplace_event_logs_dedupe_key");
            entity.HasIndex(e => new { e.Status, e.CreatedAt })
                .HasDatabaseName("ix_marketplace_event_logs_status_created");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId })
                .HasDatabaseName("ix_marketplace_event_logs_scope_seller");
        });

        modelBuilder.Entity<SellerOwnedStockLot>(entity =>
        {
            entity.ToTable("seller_owned_stock_lots", table =>
            {
                table.HasCheckConstraint("ck_seller_owned_stock_lot_quantities", "original_quantity > 0 AND available_quantity >= 0 AND reserved_quantity >= 0 AND consumed_quantity >= 0 AND available_quantity + reserved_quantity + consumed_quantity = original_quantity");
                table.HasCheckConstraint("ck_seller_owned_stock_lot_cost", "unit_cost_cents > 0");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.VariantSku).HasColumnName("variant_sku").HasMaxLength(Sku.MaxLength).IsRequired();
            entity.Property(e => e.OriginalQuantity).HasColumnName("original_quantity").IsRequired();
            entity.Property(e => e.AvailableQuantity).HasColumnName("available_quantity").IsRequired();
            entity.Property(e => e.ReservedQuantity).HasColumnName("reserved_quantity").IsRequired();
            entity.Property(e => e.ConsumedQuantity).HasColumnName("consumed_quantity").IsRequired();
            entity.Property(e => e.UnitCostCents).HasColumnName("unit_cost_cents").IsRequired();
            entity.Property(e => e.CurrencyId).HasColumnName("currency_id").HasMaxLength(3).IsRequired();
            entity.Property(e => e.SourceType).HasColumnName("source_type").HasMaxLength(40).IsRequired();
            entity.Property(e => e.SourceId).HasColumnName("source_id").HasMaxLength(200).IsRequired();
            entity.Property(e => e.EvidenceReference).HasColumnName("evidence_reference").HasMaxLength(1000);
            entity.Property(e => e.AcquiredAt).HasColumnName("acquired_at").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.SellerId, e.SourceType, e.SourceId }).IsUnique().HasDatabaseName("ux_seller_owned_stock_lots_source");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.SellerId, e.VariantSku, e.AcquiredAt }).HasDatabaseName("ix_seller_owned_stock_lots_fifo");
            entity.HasOne<ProductVariant>().WithMany().HasForeignKey(e => e.VariantSku).HasPrincipalKey(e => e.VariantSku).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockReservationAllocation>(entity =>
        {
            entity.ToTable("stock_reservation_allocations", table => table.HasCheckConstraint("ck_stock_reservation_allocation_quantity", "quantity > 0"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.StockReservationId).HasColumnName("stock_reservation_id").IsRequired();
            entity.Property(e => e.SellerOwnedStockLotId).HasColumnName("seller_owned_stock_lot_id");
            entity.Property(e => e.Source).HasColumnName("source").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Quantity).HasColumnName("quantity").IsRequired();
            entity.Property(e => e.UnitCostCents).HasColumnName("unit_cost_cents");
            entity.Property(e => e.CurrencyId).HasColumnName("currency_id").HasMaxLength(3).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.ConsumedAt).HasColumnName("consumed_at");
            entity.HasIndex(e => new { e.StockReservationId, e.SellerOwnedStockLotId, e.Source }).IsUnique().HasDatabaseName("ux_stock_reservation_allocations_origin");
            entity.HasOne<StockReservation>().WithMany().HasForeignKey(e => e.StockReservationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SellerOwnedStockLot>().WithMany().HasForeignKey(e => e.SellerOwnedStockLotId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MarketplaceOperationJob>(entity =>
        {
            entity.ToTable("marketplace_operation_jobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.OperationType).HasColumnName("operation_type").HasMaxLength(60).IsRequired();
            entity.Property(e => e.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(300);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(40).IsRequired();
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.ResultJson).HasColumnName("result_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Total).HasColumnName("total").IsRequired();
            entity.Property(e => e.Processed).HasColumnName("processed").IsRequired();
            entity.Property(e => e.Succeeded).HasColumnName("succeeded").IsRequired();
            entity.Property(e => e.Failed).HasColumnName("failed").IsRequired();
            entity.Property(e => e.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(e => e.InventoryVersion).HasColumnName("inventory_version");
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(2000);
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.Status, e.CreatedAt }).HasDatabaseName("ix_marketplace_operation_jobs_status_created");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.CreatedAt }).HasDatabaseName("ix_marketplace_operation_jobs_scope_created");
            entity.HasIndex(e => e.DedupeKey)
                .IsUnique()
                .HasFilter("\"dedupe_key\" IS NOT NULL")
                .HasDatabaseName("ux_marketplace_operation_jobs_dedupe_key");
        });

        modelBuilder.Entity<MarketplaceFinancialEntry>(entity =>
        {
            entity.ToTable("marketplace_financial_entries", table =>
            {
                table.HasCheckConstraint("ck_financial_entry_amount_non_zero", "amount_cents <> 0");
                table.HasCheckConstraint("ck_financial_entry_layer", "layer IN ('OPERATIONAL','RECONCILED','INTERNAL_CONFIRMED')");
                table.HasCheckConstraint("ck_financial_entry_status", "status IN ('ESTIMATED','CONFIRMED','VOIDED')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.EntryType).HasColumnName("entry_type").HasMaxLength(60).IsRequired();
            entity.Property(e => e.Layer).HasColumnName("layer").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            entity.Property(e => e.AmountCents).HasColumnName("amount_cents").IsRequired();
            entity.Property(e => e.CurrencyId).HasColumnName("currency_id").HasMaxLength(3).IsRequired();
            entity.Property(e => e.EconomicKey).HasColumnName("economic_key").HasMaxLength(500).IsRequired();
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(500).IsRequired();
            entity.Property(e => e.SupersedesEntryId).HasColumnName("supersedes_entry_id");
            entity.Property(e => e.MarketplaceOrderId).HasColumnName("marketplace_order_id");
            entity.Property(e => e.MarketplaceOrderItemId).HasColumnName("marketplace_order_item_id");
            entity.Property(e => e.ExternalOrderId).HasColumnName("external_order_id").HasMaxLength(120);
            entity.Property(e => e.ExternalPaymentId).HasColumnName("external_payment_id").HasMaxLength(120);
            entity.Property(e => e.ExternalShipmentId).HasColumnName("external_shipment_id").HasMaxLength(120);
            entity.Property(e => e.ExternalPackId).HasColumnName("external_pack_id").HasMaxLength(120);
            entity.Property(e => e.ExternalClaimId).HasColumnName("external_claim_id").HasMaxLength(120);
            entity.Property(e => e.ExternalReturnId).HasColumnName("external_return_id").HasMaxLength(120);
            entity.Property(e => e.EconomicOccurredAt).HasColumnName("economic_occurred_at").IsRequired();
            entity.Property(e => e.FinancialConfirmedAt).HasColumnName("financial_confirmed_at");
            entity.Property(e => e.ProviderUpdatedAt).HasColumnName("provider_updated_at");
            entity.Property(e => e.ObservedAt).HasColumnName("observed_at").IsRequired();
            entity.Property(e => e.SourceEndpoint).HasColumnName("source_endpoint").HasMaxLength(500).IsRequired();
            entity.Property(e => e.SourceRecordId).HasColumnName("source_record_id").HasMaxLength(200);
            entity.Property(e => e.CanonicalPayloadHash).HasColumnName("canonical_payload_hash").HasMaxLength(64).IsRequired();
            entity.Property(e => e.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.Provider, e.SellerId, e.IdempotencyKey }).IsUnique()
                .HasDatabaseName("ux_financial_entries_scope_idempotency");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.EconomicOccurredAt })
                .HasDatabaseName("ix_financial_entries_scope_economic_at");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.FinancialConfirmedAt })
                .HasDatabaseName("ix_financial_entries_scope_confirmed_at");
            entity.HasIndex(e => new { e.SellerId, e.MarketplaceOrderId }).HasDatabaseName("ix_financial_entries_seller_order");
            entity.HasIndex(e => new { e.SellerId, e.ExternalPaymentId }).HasDatabaseName("ix_financial_entries_seller_payment");
            entity.HasIndex(e => new { e.SellerId, e.ExternalShipmentId }).HasDatabaseName("ix_financial_entries_seller_shipment");
            entity.HasIndex(e => e.SupersedesEntryId).HasDatabaseName("ix_financial_entries_supersedes");
            entity.HasOne<MarketplaceFinancialEntry>().WithMany().HasForeignKey(e => e.SupersedesEntryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MarketplaceOrder>().WithMany().HasForeignKey(e => e.MarketplaceOrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MarketplaceOrderItem>().WithMany().HasForeignKey(e => e.MarketplaceOrderItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialCorrectionPlan>(entity =>
        {
            entity.ToTable("financial_correction_plans");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.PlanHash).HasColumnName("plan_hash").HasMaxLength(64).IsRequired();
            entity.Property(e => e.ScopeJson).HasColumnName("scope_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.ReportJson).HasColumnName("report_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
            entity.Property(e => e.TotalEntries).HasColumnName("total_entries").IsRequired();
            entity.Property(e => e.ProcessedEntries).HasColumnName("processed_entries").IsRequired();
            entity.Property(e => e.LastProcessedEntryId).HasColumnName("last_processed_entry_id");
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(2000);
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.SellerId, e.PlanHash }).IsUnique().HasDatabaseName("ux_financial_correction_plans_hash");
            entity.HasIndex(e => new { e.Status, e.UpdatedAt }).HasDatabaseName("ix_financial_correction_plans_status_updated");
        });

        modelBuilder.Entity<FinancialCorrectionPlanEntry>(entity =>
        {
            entity.ToTable("financial_correction_plan_entries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.PlanId).HasColumnName("plan_id").IsRequired();
            entity.Property(e => e.OriginalEntryId).HasColumnName("original_entry_id").IsRequired();
            entity.Property(e => e.ReplacementEntryId).HasColumnName("replacement_entry_id");
            entity.Property(e => e.MarketplaceOrderId).HasColumnName("marketplace_order_id");
            entity.Property(e => e.EconomicKey).HasColumnName("economic_key").HasMaxLength(500).IsRequired();
            entity.Property(e => e.State).HasColumnName("state").HasMaxLength(30).IsRequired();
            entity.Property(e => e.ExpectedHeadId).HasColumnName("expected_head_id").IsRequired();
            entity.Property(e => e.ExpectedActiveEntryId).HasColumnName("expected_active_entry_id").IsRequired();
            entity.Property(e => e.ExpectedHeadVersion).HasColumnName("expected_head_version").IsRequired();
            entity.Property(e => e.ExpectedActiveEntryHash).HasColumnName("expected_active_entry_hash").HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExpectedPriceVersionId).HasColumnName("expected_price_version_id");
            entity.Property(e => e.ExpectedCostReferencesHash).HasColumnName("expected_cost_references_hash").HasMaxLength(64).IsRequired();
            entity.Property(e => e.CurrentAmountCents).HasColumnName("current_amount_cents").IsRequired();
            entity.Property(e => e.ReplacementAmountCents).HasColumnName("replacement_amount_cents").IsRequired();
            entity.Property(e => e.ReplacementBreakdownJson).HasColumnName("replacement_breakdown_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.PlanId, e.OriginalEntryId }).IsUnique().HasDatabaseName("ux_financial_correction_plan_entries_original");
            entity.HasIndex(e => e.ReplacementEntryId).IsUnique().HasFilter("\"replacement_entry_id\" IS NOT NULL")
                .HasDatabaseName("ux_financial_correction_plan_entries_replacement");
            entity.HasOne<FinancialCorrectionPlan>().WithMany().HasForeignKey(e => e.PlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<MarketplaceFinancialEntry>().WithMany().HasForeignKey(e => e.OriginalEntryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MarketplaceFinancialEntry>().WithMany().HasForeignKey(e => e.ReplacementEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialEconomicHead>(entity =>
        {
            entity.ToTable("financial_economic_heads", table =>
                table.HasCheckConstraint("ck_financial_head_version", "version > 0"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.EconomicKey).HasColumnName("economic_key").HasMaxLength(500).IsRequired();
            entity.Property(e => e.ActiveEntryId).HasColumnName("active_entry_id").IsRequired();
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.EconomicKey }).IsUnique()
                .HasDatabaseName("ux_financial_heads_scope_economic_key");
            entity.HasIndex(e => e.ActiveEntryId).IsUnique().HasDatabaseName("ux_financial_heads_active_entry");
            entity.HasOne<MarketplaceFinancialEntry>().WithMany().HasForeignKey(e => e.ActiveEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MarketplaceOrderFinancialState>(entity =>
        {
            entity.ToTable("marketplace_order_financial_states", table =>
            {
                table.HasCheckConstraint("ck_order_financial_state_version", "version > 0");
                table.HasCheckConstraint("ck_order_financial_state_maturity", "maturity IN ('INCOMPLETO','ESTIMADO','PARCIALMENTE_CONFIRMADO','CONFIRMADO','REABERTO')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.MarketplaceOrderId).HasColumnName("marketplace_order_id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.Maturity).HasColumnName("maturity").HasMaxLength(40).IsRequired();
            entity.Property(e => e.GrossRevenueCents).HasColumnName("gross_revenue_cents");
            entity.Property(e => e.EstimatedEconomicNetCents).HasColumnName("estimated_economic_net_cents");
            entity.Property(e => e.ConfirmedValueCents).HasColumnName("confirmed_value_cents");
            entity.Property(e => e.OperationalProfitCents).HasColumnName("operational_profit_cents");
            entity.Property(e => e.UnallocatedCents).HasColumnName("unallocated_cents");
            entity.Property(e => e.SkuResolved).HasColumnName("sku_resolved");
            entity.Property(e => e.CostResolved).HasColumnName("cost_resolved");
            entity.Property(e => e.FreightResolved).HasColumnName("freight_resolved");
            entity.Property(e => e.OperationalComponentsResolved).HasColumnName("operational_components_resolved");
            entity.Property(e => e.ConfirmedComponentsResolved).HasColumnName("confirmed_components_resolved");
            entity.Property(e => e.ItemAllocationResolved).HasColumnName("item_allocation_resolved");
            entity.Property(e => e.IncompleteReasonsJson).HasColumnName("incomplete_reasons_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.DivergenceJson).HasColumnName("divergence_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.WasConfirmedAt).HasColumnName("was_confirmed_at");
            entity.Property(e => e.ReopenedAt).HasColumnName("reopened_at");
            entity.Property(e => e.LastProjectedAt).HasColumnName("last_projected_at").IsRequired();
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.HasIndex(e => e.MarketplaceOrderId).IsUnique().HasDatabaseName("ux_order_financial_states_order");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.Maturity })
                .HasDatabaseName("ix_order_financial_states_scope_maturity");
            entity.HasOne<MarketplaceOrder>().WithMany().HasForeignKey(e => e.MarketplaceOrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SellerTaxProfileVersion>(entity =>
        {
            entity.ToTable("seller_tax_profile_versions", table =>
            {
                table.HasCheckConstraint("ck_seller_tax_rate", "rate_basis_points >= 0 AND rate_basis_points <= 10000");
                table.HasCheckConstraint("ck_seller_tax_version", "version > 0");
                table.HasCheckConstraint("ck_seller_tax_period", "effective_to IS NULL OR effective_to > effective_from");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.RateBasisPoints).HasColumnName("rate_basis_points").IsRequired();
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from").IsRequired();
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(160).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.Version }).IsUnique()
                .HasDatabaseName("ux_seller_tax_scope_version");
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.EffectiveFrom })
                .HasDatabaseName("ix_seller_tax_scope_effective_from");
        });

        modelBuilder.Entity<FinancialReconciliationCursor>(entity =>
        {
            entity.ToTable("financial_reconciliation_cursors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.BillingGroup).HasColumnName("billing_group").HasMaxLength(20).IsRequired();
            entity.Property(e => e.PeriodKey).HasColumnName("period_key").HasMaxLength(80).IsRequired();
            entity.Property(e => e.FromId).HasColumnName("from_id").HasMaxLength(200);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.LastPayloadHash).HasColumnName("last_payload_hash").HasMaxLength(64);
            entity.Property(e => e.LastSucceededAt).HasColumnName("last_succeeded_at");
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(e => e.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.BillingGroup, e.PeriodKey }).IsUnique()
                .HasDatabaseName("ux_financial_cursor_scope_period");
            entity.HasIndex(e => new { e.SellerId, e.Status, e.NextAttemptAt }).HasDatabaseName("ix_financial_cursor_seller_retry");
        });

        modelBuilder.Entity<FinancialSyncJob>(entity =>
        {
            entity.ToTable("financial_sync_jobs", table =>
                table.HasCheckConstraint("ck_financial_sync_range", "range_to > range_from"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ParentJobId).HasColumnName("parent_job_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.JobType).HasColumnName("job_type").HasMaxLength(50).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.RangeFrom).HasColumnName("range_from").IsRequired();
            entity.Property(e => e.RangeTo).HasColumnName("range_to").IsRequired();
            entity.Property(e => e.Checkpoint).HasColumnName("checkpoint").HasMaxLength(300);
            entity.Property(e => e.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(500).IsRequired();
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.ResultJson).HasColumnName("result_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Total).HasColumnName("total").IsRequired();
            entity.Property(e => e.Processed).HasColumnName("processed").IsRequired();
            entity.Property(e => e.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(e => e.LockedBy).HasColumnName("locked_by").HasMaxLength(160);
            entity.Property(e => e.LeaseUntil).HasColumnName("lease_until");
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(e => new { e.TenantId, e.Provider, e.SellerId, e.DedupeKey }).IsUnique()
                .HasDatabaseName("ux_financial_sync_jobs_scope_dedupe");
            entity.HasIndex(e => new { e.SellerId, e.NextAttemptAt, e.CreatedAt })
                .HasFilter("\"status\" IN ('PENDING','RETRY')")
                .HasDatabaseName("ix_financial_sync_jobs_pending");
            entity.HasIndex(e => e.ParentJobId).HasDatabaseName("ix_financial_sync_jobs_parent");
            entity.HasOne<FinancialSyncJob>().WithMany().HasForeignKey(e => e.ParentJobId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MarketplaceOAuthGrant>(entity =>
        {
            entity.ToTable("marketplace_oauth_grants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.SellerId).HasColumnName("seller_id").IsRequired();
            entity.Property(e => e.AppFamily).HasColumnName("app_family").HasMaxLength(30).IsRequired();
            entity.Property(e => e.ClientIdFingerprint).HasColumnName("client_id_fingerprint").HasMaxLength(64).IsRequired();
            entity.Property(e => e.AccessTokenProtected).HasColumnName("access_token_protected").IsRequired();
            entity.Property(e => e.RefreshTokenProtected).HasColumnName("refresh_token_protected").IsRequired();
            entity.Property(e => e.TokenExpiresAt).HasColumnName("token_expires_at").IsRequired();
            entity.Property(e => e.ScopesJson).HasColumnName("scopes_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.CapabilitiesJson).HasColumnName("capabilities_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.LastCapabilityVerifiedAt).HasColumnName("last_capability_verified_at");
            entity.Property(e => e.CapabilityError).HasColumnName("capability_error").HasMaxLength(2000);
            entity.Property(e => e.RequiresReauthorization).HasColumnName("requires_reauthorization").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.SellerId, e.AppFamily }).IsUnique()
                .HasDatabaseName("ux_oauth_grants_scope_app_family");
            entity.HasIndex(e => new { e.SellerId, e.LastCapabilityVerifiedAt }).HasDatabaseName("ix_oauth_grants_seller_verified");
        });

        modelBuilder.Entity<TenantMarketplaceSlaRule>(entity =>
        {
            entity.ToTable("tenant_marketplace_sla_rules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(40).IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider").IsRequired();
            entity.Property(e => e.LogisticType).HasColumnName("logistic_type").HasMaxLength(80).IsRequired();
            entity.Property(e => e.ShippingMode).HasColumnName("shipping_mode").HasMaxLength(80);
            entity.Property(e => e.CutoffLocalTime).HasColumnName("cutoff_local_time").HasMaxLength(5).IsRequired();
            entity.Property(e => e.MonitorMinutes).HasColumnName("monitor_minutes").IsRequired();
            entity.Property(e => e.AttentionMinutes).HasColumnName("attention_minutes").IsRequired();
            entity.Property(e => e.UrgentMinutes).HasColumnName("urgent_minutes").IsRequired();
            entity.Property(e => e.CriticalMinutes).HasColumnName("critical_minutes").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ClientId, e.Provider, e.LogisticType, e.ShippingMode })
                .IsUnique()
                .HasDatabaseName("ux_tenant_marketplace_sla_rules_scope_mode");
            entity.HasCheckConstraint("ck_sla_risk_thresholds", "\"monitor_minutes\" > \"attention_minutes\" AND \"attention_minutes\" > \"urgent_minutes\" AND \"urgent_minutes\" > \"critical_minutes\" AND \"critical_minutes\" > 0");
        });

        base.OnModelCreating(modelBuilder);
    }
}
