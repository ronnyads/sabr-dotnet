using System.Buffers.Binary;
using System.Globalization;

namespace Phub.Application.Services;

internal static class ListingDraftHelpers
{
    private const int MaxRawErrorLength = 32 * 1024;
    private const string TruncatedSuffix = "...TRUNCATED";

    public static string EncodeRowVersion(uint xmin, DateTimeOffset updatedAt, bool isNpgsqlProvider)
    {
        if (isNpgsqlProvider)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, xmin);
            return Convert.ToBase64String(bytes);
        }

        Span<byte> fallback = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(fallback, updatedAt.UtcTicks);
        return Convert.ToBase64String(fallback);
    }

    public static bool TryDecodeNpgsqlRowVersion(string? encoded, out uint xmin)
    {
        xmin = 0;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(encoded.Trim());
            if (bytes.Length != 4)
            {
                return false;
            }

            xmin = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static long ToCents(decimal amount)
    {
        var normalized = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return (long)Math.Round(normalized * 100m, 0, MidpointRounding.AwayFromZero);
    }

    public static decimal ToDecimal(long cents)
    {
        return cents / 100m;
    }

    public static string NormalizeCurrency(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "BRL"
            : value.Trim().ToUpperInvariant();
    }

    public static string TrimTo32Kb(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.Trim();
        if (normalized.Length <= MaxRawErrorLength)
        {
            return normalized;
        }

        var take = Math.Max(0, MaxRawErrorLength - TruncatedSuffix.Length);
        return normalized[..take] + TruncatedSuffix;
    }

    public static string NormalizeListingTypeId(string? listingTypeId)
    {
        return string.IsNullOrWhiteSpace(listingTypeId)
            ? string.Empty
            : listingTypeId.Trim().ToLowerInvariant();
    }

    public static bool IsValidListingType(string? listingTypeId)
    {
        var normalized = NormalizeListingTypeId(listingTypeId);
        return normalized is "gold_special" or "gold_pro";
    }
}
