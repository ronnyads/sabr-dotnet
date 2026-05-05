namespace Phub.Application.Validation;

public static class PaginationGuard
{
    public static IReadOnlyCollection<ValidationError> ValidateOrError(
        int skip,
        int limit,
        int minLimit = 1,
        int maxLimit = 200)
    {
        var errors = new List<ValidationError>();

        if (skip < 0)
        {
            errors.Add(new ValidationError("skip", "Skip must be greater than or equal to zero"));
        }

        if (limit < minLimit || limit > maxLimit)
        {
            errors.Add(new ValidationError("limit", $"Limit must be between {minLimit} and {maxLimit}"));
        }

        return errors;
    }
}
