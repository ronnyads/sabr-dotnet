namespace Sabr.Domain.Protheus;

/// <summary>
/// Central catalogue of mock Protheus tables available in the system.
/// Keeps the full DA0...SF5 catalog plus explicit SIGA modules.
/// </summary>
public static class ProtheusTableRegistry
{
    private static readonly HashSet<string> Tables = BuildCatalog();

    public static IReadOnlyCollection<string> All => Tables;

    public static bool Contains(string? table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return false;
        }

        return Tables.Contains(table.Trim().ToUpperInvariant());
    }

    private static HashSet<string> BuildCatalog()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProtheusPrefixes.Client,
            ProtheusPrefixes.Product,
            ProtheusPrefixes.Order,
            ProtheusPrefixes.Financial,
            ProtheusPrefixes.SigaAcd,
            ProtheusPrefixes.SigaAps,
            ProtheusPrefixes.SigaAtf,
            ProtheusPrefixes.SigaCom,
            ProtheusPrefixes.SigaCtb,
            ProtheusPrefixes.SigaEst,
            ProtheusPrefixes.SigaExp,
            ProtheusPrefixes.SigaFat,
            ProtheusPrefixes.SigaGpe,
            ProtheusPrefixes.SigaMnt,
            ProtheusPrefixes.SigaPcp,
            ProtheusPrefixes.SigaRhs,
            ProtheusPrefixes.SigaOpr
        };

        // Catalog range requested by product plan: DA0...SF5 (mock registry only).
        for (var first = 'D'; first <= 'S'; first++)
        {
            for (var second = 'A'; second <= 'Z'; second++)
            {
                for (var digit = 0; digit <= 5; digit++)
                {
                    result.Add($"{first}{second}{digit}");
                }
            }
        }

        return result;
    }
}
