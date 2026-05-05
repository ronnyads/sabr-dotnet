using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Phub.Domain.Entities;

namespace Phub.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Client> Clients { get; }
    DbSet<User> Users { get; }
    DbSet<PlatformUser> PlatformUsers { get; }
    DbSet<ClientDocument> ClientDocuments { get; }
    DbSet<ClientStore> ClientStores { get; }
    DbSet<ClientRefreshToken> ClientRefreshTokens { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<PlatformRefreshToken> PlatformRefreshTokens { get; }
    DbSet<ProtheusOutboxEvent> ProtheusOutboxEvents { get; }
    DbSet<WalletAccount> WalletAccounts { get; }
    DbSet<WalletLedgerEntry> WalletLedgerEntries { get; }
    DbSet<IdempotencyKey> IdempotencyKeys { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductImage> ProductImages { get; }
    DbSet<ProductVariant> ProductVariants { get; }
    DbSet<Category> Categories { get; }
    DbSet<Plan> Plans { get; }
    DbSet<Catalog> Catalogs { get; }
    DbSet<PlanCatalog> PlanCatalogs { get; }
    DbSet<ProductCatalog> ProductCatalogs { get; }
    DbSet<ClientPlanSubscription> ClientPlanSubscriptions { get; }
    DbSet<Publication> Publications { get; }
    DbSet<ListingDraft> ListingDrafts { get; }
    DbSet<ProductPriceHistory> ProductPriceHistories { get; }
    DbSet<TenantMarketplaceConnection> TenantMarketplaceConnections { get; }
    DbSet<TenantMarketplaceListingMap> TenantMarketplaceListingMaps { get; }
    DbSet<ProductMarketplaceCategoryLock> ProductMarketplaceCategoryLocks { get; }
    DbSet<MarketplaceOrder> MarketplaceOrders { get; }
    DbSet<MarketplaceOrderNumberSequence> MarketplaceOrderNumberSequences { get; }
    DbSet<MarketplaceOrderItem> MarketplaceOrderItems { get; }
    DbSet<MarketplaceShipment> MarketplaceShipments { get; }
    DbSet<StockReservation> StockReservations { get; }
    DbSet<MarketplaceEventLog> MarketplaceEventLogs { get; }
    DbSet<TenantMarketplaceSlaRule> TenantMarketplaceSlaRules { get; }
    DbSet<AiPromptConfig> AiPromptConfigs { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<SupplierRefreshToken> SupplierRefreshTokens { get; }
    DbSet<SupplierProduct> SupplierProducts { get; }
    DbSet<SupplierWalletAccount> SupplierWalletAccounts { get; }
    DbSet<SupplierWalletEntry> SupplierWalletEntries { get; }
    DbSet<SupplierWithdrawal> SupplierWithdrawals { get; }
    DbSet<PlatformFinancialConfig> PlatformFinancialConfigs { get; }
    DatabaseFacade Database { get; }
    Task<long> NextClientProtheusCodeAsync(CancellationToken cancellationToken = default);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
