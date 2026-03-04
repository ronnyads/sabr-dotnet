using Sabr.Domain.Enums;
using Sabr.Domain.ValueObjects;

namespace Sabr.Domain.Entities;

public sealed class ProductMarketplaceCategoryLock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }

    private string _baseProductSku = string.Empty;
    public string BaseProductSku
    {
        get => _baseProductSku;
        set => _baseProductSku = Sku.Normalize(value);
    }

    public string SiteId { get; set; } = "MLB";
    public string ApprovedCategoryId { get; set; } = string.Empty;
    public string ApprovedCategoryName { get; set; } = string.Empty;
    public string? ApprovedCategoryPath { get; set; }
    public MarketplaceCategoryLockStatus Status { get; set; } = MarketplaceCategoryLockStatus.PendingReview;
    public MarketplaceCategoryLockSource Source { get; set; } = MarketplaceCategoryLockSource.Manual;
    public string? InternalCategorySlugSnapshot { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
