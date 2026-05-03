using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class AddMyProductRequest
{
    public string ProductSku { get; set; } = string.Empty;
    public PricingMode? PricingMode { get; set; }
    public decimal? MarkupPercent { get; set; }
    public long? FixedPriceCents { get; set; }
}
