namespace Phub.Application.Models;

// ── OAuth ──────────────────────────────────────────────────────────────────────

public sealed class TinyTokenResponse
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public int ExpiresIn { get; set; }
    public string TokenType { get; set; } = "";
}

public sealed class TinyUserInfoResult
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
}

// ── Paged ──────────────────────────────────────────────────────────────────────

public sealed class TinyPagedResult<T>
{
    public int Itens { get; set; }
    public int Paginas { get; set; }
    public List<T> Dados { get; set; } = new();
}

// ── Orders ─────────────────────────────────────────────────────────────────────

public sealed class TinyOrderResult
{
    public long Id { get; set; }
    public string Numero { get; set; } = "";
    public string Situacao { get; set; } = "";
    public DateTime? DataPedido { get; set; }
    public DateTime? DataPrevista { get; set; }
    public TinyOrderClientResult? Cliente { get; set; }
    public List<TinyOrderItemResult> Itens { get; set; } = new();
    public TinyOrderShippingResult? Envio { get; set; }
}

public sealed class TinyOrderClientResult
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string? Email { get; set; }
}

public sealed class TinyOrderItemResult
{
    public long Id { get; set; }
    public string Descricao { get; set; } = "";
    public string? Codigo { get; set; }
    public decimal Quantidade { get; set; }
    public decimal Valor { get; set; }
    public long? IdProduto { get; set; }
}

public sealed class TinyOrderShippingResult
{
    public string? TipoEnvio { get; set; }
    public string? FormaEnvio { get; set; }
    public string? CodigoRastreamento { get; set; }
    public long? IdAgrupamento { get; set; }
}

// ── Products ───────────────────────────────────────────────────────────────────

public sealed class TinyProductResult
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string? Codigo { get; set; }
    public decimal? Preco { get; set; }
    public string Situacao { get; set; } = "";
    public TinyStockResult? Estoque { get; set; }
}

public sealed class TinyStockResult
{
    public long IdProduto { get; set; }
    public decimal Saldo { get; set; }
    public decimal? SaldoReservado { get; set; }
}

// ── Invoice ────────────────────────────────────────────────────────────────────

public sealed class TinyNotaResult
{
    public long Id { get; set; }
    public string? Numero { get; set; }
    public string? ChaveAcesso { get; set; }
    public string Situacao { get; set; } = "";
}

// ── Integration status / results ───────────────────────────────────────────────

public sealed class TinyIntegrationStatusResult
{
    public bool IsConnected { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyEmail { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? LastSyncAt { get; set; }
}

public sealed class TinySyncResult
{
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class TinyCatalogSyncResult
{
    public int Linked { get; set; }
    public int Unlinked { get; set; }
    public int Skipped { get; set; }
}

public sealed class TinyInvoiceResult
{
    public long NoteId { get; set; }
    public string? Numero { get; set; }
    public string? ChaveAcesso { get; set; }
    public string? XmlUrl { get; set; }
    public string? DanfeUrl { get; set; }
    public string Situacao { get; set; } = "";
}
