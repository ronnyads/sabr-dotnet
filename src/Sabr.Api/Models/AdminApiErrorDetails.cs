using Sabr.Application.Validation;

namespace Sabr.Api.Models;

public sealed class AdminApiErrorDetails
{
    public List<string>? InvalidSkus { get; init; }
    public List<string>? InvalidPlanIds { get; init; }
    public List<string>? InactivePlanIds { get; init; }
    public List<string>? InvalidCatalogIds { get; init; }
    public List<ValidationError> Details { get; init; } = new();
}

public static class AdminApiErrorDetailsBuilder
{
    public static AdminApiErrorDetails Build(IReadOnlyCollection<ValidationError> errors)
    {
        var invalidSkus = errors
            .Where(error => string.Equals(error.Field, "invalidSkus", StringComparison.OrdinalIgnoreCase))
            .Select(error => error.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToList();

        var invalidPlanIds = errors
            .Where(error => string.Equals(error.Field, "invalidPlanIds", StringComparison.OrdinalIgnoreCase))
            .Select(error => error.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(message => message, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inactivePlanIds = errors
            .Where(error => string.Equals(error.Field, "inactivePlanIds", StringComparison.OrdinalIgnoreCase))
            .Select(error => error.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(message => message, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var invalidCatalogIds = errors
            .Where(error => string.Equals(error.Field, "invalidCatalogIds", StringComparison.OrdinalIgnoreCase))
            .Select(error => error.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(message => message, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AdminApiErrorDetails
        {
            InvalidSkus = invalidSkus.Count == 0 ? null : invalidSkus,
            InvalidPlanIds = invalidPlanIds.Count == 0 ? null : invalidPlanIds,
            InactivePlanIds = inactivePlanIds.Count == 0 ? null : inactivePlanIds,
            InvalidCatalogIds = invalidCatalogIds.Count == 0 ? null : invalidCatalogIds,
            Details = errors.ToList()
        };
    }
}
