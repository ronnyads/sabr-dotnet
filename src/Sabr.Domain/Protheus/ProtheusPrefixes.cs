namespace Sabr.Domain.Protheus;

public static class ProtheusPrefixes
{
    // Base business tables currently mapped by entities.
    public const string Client = "SA1";
    public const string Product = "SB1";
    public const string Order = "SC5";
    public const string Financial = "SE1";

    // Full mock catalogue bounds (DA0...SF5).
    public const string MockTableRangeStart = "DA0";
    public const string MockTableRangeEnd = "SF5";

    // SIGA modules.
    public const string SigaAcd = "SIGAACD";
    public const string SigaAps = "SIGAAPS";
    public const string SigaAtf = "SIGAATF";
    public const string SigaCom = "SIGACOM";
    public const string SigaCtb = "SIGACTB";
    public const string SigaEst = "SIGAEST";
    public const string SigaExp = "SIGAEXP";
    public const string SigaFat = "SIGAFAT";
    public const string SigaGpe = "SIGAGPE";
    public const string SigaMnt = "SIGAMNT";
    public const string SigaPcp = "SIGAPCP";
    public const string SigaRhs = "SIGARHS";
    public const string SigaOpr = "SIGAOPR";

    public const string InternalUserRh = "SIGAGPE";
    public const string InternalUserPurchasing = "SIGACOM";
    public const string InternalUserStock = "SIGAEST";
    public const string InternalUserAccounting = "SIGACTB";

    // Future internal modules (placeholders)
    public const string InternalUserExpedition = "SIGAEXP";
    public const string InternalUserOperator = "SIGAOPR";
}
