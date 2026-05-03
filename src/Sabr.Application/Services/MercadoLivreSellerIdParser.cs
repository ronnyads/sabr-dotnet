using System.Globalization;

namespace Phub.Application.Services;

internal static class MercadoLivreSellerIdParser
{
    public static bool TryParseRequired(string? rawSellerId, out long sellerId)
    {
        sellerId = 0;
        if (string.IsNullOrWhiteSpace(rawSellerId))
        {
            return false;
        }

        return long.TryParse(rawSellerId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sellerId)
               && sellerId > 0;
    }

    public static bool TryParseOptional(string? rawSellerId, out long? sellerId)
    {
        sellerId = null;
        if (string.IsNullOrWhiteSpace(rawSellerId))
        {
            return true;
        }

        if (!long.TryParse(rawSellerId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        sellerId = parsed;
        return true;
    }

    public static string ToApiString(long sellerId)
    {
        return sellerId.ToString(CultureInfo.InvariantCulture);
    }
}
