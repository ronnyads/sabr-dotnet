using System.Text.RegularExpressions;

namespace Sabr.Domain.ValueObjects;

public readonly partial record struct Sku
{
    public const int MaxLength = 64;
    private static readonly Regex ValidationRegex = SkuRegex();

    public string Value { get; }

    private Sku(string value)
    {
        Value = value;
    }

    public static Sku Parse(string? input)
    {
        if (!TryParse(input, out var sku))
        {
            throw new ArgumentException("SKU must match pattern ^[A-Z0-9][A-Z0-9\\-_/]{0,63}$.", nameof(input));
        }

        return sku;
    }

    public static bool TryParse(string? input, out Sku sku)
    {
        sku = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input.Trim().ToUpperInvariant();
        if (!ValidationRegex.IsMatch(normalized))
        {
            return false;
        }

        sku = new Sku(normalized);
        return true;
    }

    public static string Normalize(string? input)
    {
        return Parse(input).Value;
    }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9\\-_/]{0,63}$", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex SkuRegex();
}
