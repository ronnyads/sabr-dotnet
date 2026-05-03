namespace Phub.Domain.Protheus;

public static class ProtheusTag
{
    public static string Build(string prefix, ProtheusOperationType operation)
    {
        return $"{prefix}_{operation}";
    }
}
