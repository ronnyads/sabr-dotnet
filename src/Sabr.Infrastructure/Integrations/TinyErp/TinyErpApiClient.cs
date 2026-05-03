using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Microsoft.Extensions.Options;

namespace Phub.Infrastructure.Integrations.TinyErp;

public sealed class TinyErpApiClient : ITinyErpApiClient
{
    private readonly HttpClient _http;
    private readonly TinyErpOptions _options;
    private readonly ILogger<TinyErpApiClient> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public TinyErpApiClient(HttpClient http, IOptions<TinyErpOptions> options, ILogger<TinyErpApiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    // ── Token ─────────────────────────────────────────────────────────────────

    public async Task<TinyTokenResponse> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret
        };
        return await PostTokenAsync(form, ct);
    }

    public async Task<TinyTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret
        };
        return await PostTokenAsync(form, ct);
    }

    private async Task<TinyTokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var url = $"{_options.AuthBaseUrl}/token";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<TinyTokenResponse>(stream, _json)!;
    }

    // ── User ──────────────────────────────────────────────────────────────────

    public async Task<TinyUserInfoResult> GetUserInfoAsync(string accessToken, CancellationToken ct = default)
    {
        var req = BuildGet($"{_options.ApiBaseUrl}/info", accessToken);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<TinyUserInfoResult>(stream, _json)!;
    }

    // ── Stock ─────────────────────────────────────────────────────────────────

    public async Task<TinyStockResult?> GetProductStockAsync(string accessToken, long tinyProductId, CancellationToken ct = default)
    {
        var req = BuildGet($"{_options.ApiBaseUrl}/produtos/{tinyProductId}/estoque", accessToken);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<TinyStockResult>(stream, _json);
    }

    public async Task UpdateProductStockAsync(string accessToken, long tinyProductId, decimal quantity, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { saldo = quantity });
        var req = BuildPost($"{_options.ApiBaseUrl}/estoque/{tinyProductId}", accessToken, body);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("TinyERP UpdateProductStock failed for product {Id}: {Status}", tinyProductId, resp.StatusCode);
    }

    // ── Orders ────────────────────────────────────────────────────────────────

    public async Task<TinyPagedResult<TinyOrderResult>> ListOrdersAsync(string accessToken, int pagina = 1, string? situacao = null, CancellationToken ct = default)
    {
        var url = $"{_options.ApiBaseUrl}/pedidos?pagina={pagina}";
        if (!string.IsNullOrEmpty(situacao)) url += $"&situacao={Uri.EscapeDataString(situacao)}";
        var req = BuildGet(url, accessToken);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<TinyPagedResult<TinyOrderResult>>(stream, _json)!;
    }

    public async Task<TinyOrderResult?> GetOrderAsync(string accessToken, long tinyOrderId, CancellationToken ct = default)
    {
        var req = BuildGet($"{_options.ApiBaseUrl}/pedidos/{tinyOrderId}", accessToken);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<TinyOrderResult>(stream, _json);
    }

    public async Task UpdateOrderSituacaoAsync(string accessToken, long tinyOrderId, string situacao, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { situacao });
        var req = BuildPut($"{_options.ApiBaseUrl}/pedidos/{tinyOrderId}/situacao", accessToken, body);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("TinyERP UpdateOrderSituacao failed for order {Id}: {Status}", tinyOrderId, resp.StatusCode);
    }

    public async Task UpdateOrderDespachoAsync(string accessToken, long tinyOrderId, string trackingCode, string carrier, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { codigoRastreamento = trackingCode, formaEnvio = carrier });
        var req = BuildPut($"{_options.ApiBaseUrl}/pedidos/{tinyOrderId}/despacho", accessToken, body);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("TinyERP UpdateOrderDespacho failed for order {Id}: {Status}", tinyOrderId, resp.StatusCode);
    }

    // ── Invoice ───────────────────────────────────────────────────────────────

    public async Task<TinyNotaResult> GenerateInvoiceAsync(string accessToken, long tinyOrderId, CancellationToken ct = default)
    {
        var req = BuildPost($"{_options.ApiBaseUrl}/pedidos/{tinyOrderId}/gerar-nota-fiscal", accessToken, "{}");
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<TinyNotaResult>(stream, _json)!;
    }

    public async Task<byte[]> GetNoteXmlAsync(string accessToken, long tinyNoteId, CancellationToken ct = default)
    {
        var req = BuildGet($"{_options.ApiBaseUrl}/notas/{tinyNoteId}/xml", accessToken);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string> GetNoteLinkAsync(string accessToken, long tinyNoteId, CancellationToken ct = default)
    {
        var req = BuildGet($"{_options.ApiBaseUrl}/notas/{tinyNoteId}/link", accessToken);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(stream, _json);
        return result.GetProperty("url").GetString() ?? "";
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    public async Task<byte[]> GetExpedicaoLabelsAsync(string accessToken, long idAgrupamento, CancellationToken ct = default)
    {
        var req = BuildGet($"{_options.ApiBaseUrl}/expedicao/{idAgrupamento}/etiquetas", accessToken);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // ── Products ──────────────────────────────────────────────────────────────

    public async Task<TinyPagedResult<TinyProductResult>> ListProductsAsync(string accessToken, int pagina = 1, CancellationToken ct = default)
    {
        var req = BuildGet($"{_options.ApiBaseUrl}/produtos?pagina={pagina}", accessToken);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<TinyPagedResult<TinyProductResult>>(stream, _json)!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildGet(string url, string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    private static HttpRequestMessage BuildPost(string url, string accessToken, string jsonBody)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    private static HttpRequestMessage BuildPut(string url, string accessToken, string jsonBody)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }
}
