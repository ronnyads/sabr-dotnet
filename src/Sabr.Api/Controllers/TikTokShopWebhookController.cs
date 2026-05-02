using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;
using Sabr.Application.Services;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/tiktokshop")]
public sealed class TikTokShopWebhookController : ControllerBase
{
    private readonly TikTokShopSyncService _syncService;
    private readonly IAppDbContext _dbContext;
    private readonly TikTokShopOptions _options;
    private readonly ILogger<TikTokShopWebhookController> _logger;

    public TikTokShopWebhookController(
        TikTokShopSyncService syncService,
        IAppDbContext dbContext,
        IOptions<TikTokShopOptions> options,
        ILogger<TikTokShopWebhookController> logger)
    {
        _syncService = syncService;
        _dbContext = dbContext;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        if (!_options.Features.Webhook)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError
            {
                Code = "TIKTOK_WEBHOOK_DISABLED",
                Message = "TikTok Shop webhook está desabilitado",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        Request.EnableBuffering();
        var body = await ReadBodyAsync(cancellationToken);
        Request.Body.Position = 0;

        if (!ValidateSignature(body))
        {
            _logger.LogWarning(
                "TikTok Shop webhook signature validation failed. traceId={TraceId}",
                HttpContext.TraceIdentifier);

            return Unauthorized(new ApiError
            {
                Code = "TIKTOK_WEBHOOK_SIGNATURE_INVALID",
                Message = "Assinatura do webhook inválida",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        TikTokShopWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TikTokShopWebhookPayload>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "TikTok Shop webhook invalid JSON. traceId={TraceId}", HttpContext.TraceIdentifier);
            return BadRequest(new ApiError
            {
                Code = "TIKTOK_WEBHOOK_INVALID_PAYLOAD",
                Message = "Payload inválido",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.ShopId))
        {
            return Accepted();
        }

        _logger.LogInformation(
            "TikTok Shop webhook received. type={Type} shopId={ShopId} traceId={TraceId}",
            payload.Type, payload.ShopId, HttpContext.TraceIdentifier);

        // Resolve tenant/client from shopId (SellerId)
        if (!long.TryParse(payload.ShopId, out var sellerId))
        {
            return Accepted();
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            c => c.Provider == MarketplaceProvider.TikTokShop && c.SellerId == sellerId,
            cancellationToken);

        if (connection == null)
        {
            _logger.LogWarning(
                "TikTok Shop webhook: no connection found for shopId={ShopId}", payload.ShopId);
            return Accepted();
        }

        // Fire-and-forget para não bloquear resposta ao TikTok
        _ = Task.Run(async () =>
        {
            try
            {
                await _syncService.SyncOrdersAsync(connection.TenantId, connection.ClientId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "TikTok Shop webhook sync failed. tenantId={TenantId} clientId={ClientId}",
                    connection.TenantId, connection.ClientId);
            }
        }, CancellationToken.None);

        return Accepted();
    }

    private bool ValidateSignature(string body)
    {
        // TikTok Shop assina o webhook com HMAC-SHA256
        // Header: x-tts-signature = HMAC-SHA256(appSecret, timestamp + nonce + body)
        if (!Request.Headers.TryGetValue("x-tts-timestamp", out var timestamp) ||
            !Request.Headers.TryGetValue("x-tts-nonce", out var nonce) ||
            !Request.Headers.TryGetValue("x-tts-signature", out var signature))
        {
            return false;
        }

        var message = $"{timestamp}{nonce}{body}";
        var keyBytes = Encoding.UTF8.GetBytes(_options.AppSecret);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var expected = Convert.ToHexString(HMACSHA256.HashData(keyBytes, messageBytes)).ToLowerInvariant();

        return string.Equals(expected, signature.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

public sealed class TikTokShopWebhookPayload
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("shop_id")]
    public string? ShopId { get; set; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}
