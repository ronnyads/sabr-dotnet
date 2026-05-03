using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class PriceCalculator
{
    public const decimal MaxMarkupPercent = 500m;

    public ServiceResult<long> ComputeFinalPrice(long baseCatalogCents, PricingMode pricingMode, decimal? markupPercent, long? fixedPriceCents)
    {
        var errors = new List<ValidationError>();

        if (baseCatalogCents < 0)
        {
            errors.Add(new ValidationError("baseCatalogCents", "Base catalog price cannot be negative"));
        }

        switch (pricingMode)
        {
            case PricingMode.CatalogPrice:
                if (markupPercent.HasValue || fixedPriceCents.HasValue)
                {
                    errors.Add(new ValidationError("pricingMode", "CatalogPrice mode cannot include markup or fixed price"));
                }

                break;

            case PricingMode.MarkupPercent:
                if (!markupPercent.HasValue)
                {
                    errors.Add(new ValidationError("markupPercent", "MarkupPercent mode requires markupPercent"));
                    break;
                }

                if (markupPercent.Value < 0 || markupPercent.Value > MaxMarkupPercent)
                {
                    errors.Add(new ValidationError("markupPercent", $"MarkupPercent must be between 0 and {MaxMarkupPercent}"));
                }

                if (fixedPriceCents.HasValue)
                {
                    errors.Add(new ValidationError("fixedPriceCents", "MarkupPercent mode cannot include fixed price"));
                }

                break;

            case PricingMode.FixedPrice:
                if (!fixedPriceCents.HasValue)
                {
                    errors.Add(new ValidationError("fixedPriceCents", "FixedPrice mode requires fixedPriceCents"));
                    break;
                }

                if (fixedPriceCents.Value < 0)
                {
                    errors.Add(new ValidationError("fixedPriceCents", "Fixed price cannot be negative"));
                }

                if (markupPercent.HasValue)
                {
                    errors.Add(new ValidationError("markupPercent", "FixedPrice mode cannot include markupPercent"));
                }

                break;

            default:
                errors.Add(new ValidationError("pricingMode", "Invalid pricing mode"));
                break;
        }

        if (errors.Count > 0)
        {
            return ServiceResult<long>.Failure(errors);
        }

        try
        {
            var result = pricingMode switch
            {
                PricingMode.CatalogPrice => baseCatalogCents,
                PricingMode.MarkupPercent => ComputeMarkup(baseCatalogCents, markupPercent!.Value),
                PricingMode.FixedPrice => fixedPriceCents!.Value,
                _ => baseCatalogCents
            };

            return ServiceResult<long>.Success(Math.Max(result, 0));
        }
        catch (OverflowException)
        {
            return ServiceResult<long>.Failure(new[]
            {
                new ValidationError("pricing", "Computed price is out of range")
            });
        }
    }

    private static long ComputeMarkup(long baseCatalogCents, decimal markupPercent)
    {
        var multiplier = 1m + (markupPercent / 100m);
        var computed = decimal.Round(baseCatalogCents * multiplier, 0, MidpointRounding.AwayFromZero);
        if (computed > long.MaxValue)
        {
            throw new OverflowException("Computed value exceeds Int64 range.");
        }

        return (long)computed;
    }
}
