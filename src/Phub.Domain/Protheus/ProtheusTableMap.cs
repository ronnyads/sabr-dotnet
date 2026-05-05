using Phub.Domain.Common;
using Phub.Domain.Entities;

namespace Phub.Domain.Protheus;

/// <summary>
/// Centraliza o mapeamento Entidade -> Sigla Protheus (tabela).
/// Caso uma entidade não tenha sigla definida aqui, ela não deve ser persistida.
/// </summary>
public static class ProtheusTableMap
{
    private static readonly Dictionary<Type, string> EntityPrefixMap = new()
    {
        { typeof(Client), ProtheusPrefixes.Client },          // SA1
        { typeof(ClientStore), ProtheusPrefixes.Client },     // SA1 (A1_LOJA)
        { typeof(ClientDocument), ProtheusPrefixes.Client },  // SA1 documentos
        // Futuras entidades devem ser registradas aqui antes de salvar.
    };

    static ProtheusTableMap()
    {
        foreach (var prefix in EntityPrefixMap.Values)
        {
            if (!ProtheusTableRegistry.Contains(prefix))
            {
                throw new InvalidOperationException($"Prefixo Protheus nao catalogado: {prefix}");
            }
        }
    }

    public static bool TryGetPrefix(EntityBase entity, out string prefix)
    {
        if (EntityPrefixMap.TryGetValue(entity.GetType(), out prefix!))
        {
            return true;
        }

        prefix = string.Empty;
        return false;
    }
}
