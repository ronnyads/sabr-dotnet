using Sabr.Domain.Enums;

namespace Sabr.Application.Models;

public sealed class UpdateMyProductDraftRequest
{
    public PricingMode PricingMode { get; set; }
    public decimal? MarkupPercent { get; set; }
    public long? FixedPriceCents { get; set; }
    public string? RowVersion { get; set; }
}
