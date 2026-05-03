using Phub.Application.Models;

namespace Phub.Application.Abstractions;

public interface ITinyErpApiClient
{
    Task<TinyTokenResponse> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);
    Task<TinyTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);
    Task<TinyUserInfoResult> GetUserInfoAsync(string accessToken, CancellationToken ct = default);

    Task<TinyStockResult?> GetProductStockAsync(string accessToken, long tinyProductId, CancellationToken ct = default);
    Task UpdateProductStockAsync(string accessToken, long tinyProductId, decimal quantity, CancellationToken ct = default);

    Task<TinyPagedResult<TinyOrderResult>> ListOrdersAsync(string accessToken, int pagina = 1, string? situacao = null, CancellationToken ct = default);
    Task<TinyOrderResult?> GetOrderAsync(string accessToken, long tinyOrderId, CancellationToken ct = default);
    Task UpdateOrderSituacaoAsync(string accessToken, long tinyOrderId, string situacao, CancellationToken ct = default);
    Task UpdateOrderDespachoAsync(string accessToken, long tinyOrderId, string trackingCode, string carrier, CancellationToken ct = default);

    Task<TinyNotaResult> GenerateInvoiceAsync(string accessToken, long tinyOrderId, CancellationToken ct = default);
    Task<byte[]> GetNoteXmlAsync(string accessToken, long tinyNoteId, CancellationToken ct = default);
    Task<string> GetNoteLinkAsync(string accessToken, long tinyNoteId, CancellationToken ct = default);

    Task<byte[]> GetExpedicaoLabelsAsync(string accessToken, long idAgrupamento, CancellationToken ct = default);

    Task<TinyPagedResult<TinyProductResult>> ListProductsAsync(string accessToken, int pagina = 1, CancellationToken ct = default);
}
