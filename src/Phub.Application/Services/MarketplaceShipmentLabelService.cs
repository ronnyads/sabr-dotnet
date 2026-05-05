using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MarketplaceShipmentLabelService
{
    private static readonly HttpClient TikTokLabelHttpClient = new();

    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly ITikTokShopApiClient _tikTokShopApiClient;
    private readonly TikTokShopOAuthService _tikTokShopOAuthService;
    private readonly MarketplaceAuditLogService _auditLogService;
    private readonly MarketplaceMabangDispatchService _mabangDispatchService;
    private readonly MercadoLivreOptions _options;
    private readonly TikTokShopOptions _tikTokShopOptions;
    private readonly ILogger<MarketplaceShipmentLabelService> _logger;

    public MarketplaceShipmentLabelService(
        IAppDbContext dbContext,
        IMercadoLivreApiClient mercadoLivreApiClient,
        MercadoLivreOAuthService oauthService,
        ITikTokShopApiClient tikTokShopApiClient,
        TikTokShopOAuthService tikTokShopOAuthService,
        MarketplaceAuditLogService auditLogService,
        MarketplaceMabangDispatchService mabangDispatchService,
        IOptions<MercadoLivreOptions> options,
        IOptions<TikTokShopOptions> tikTokShopOptions,
        ILogger<MarketplaceShipmentLabelService> logger)
    {
        _dbContext = dbContext;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _oauthService = oauthService;
        _tikTokShopApiClient = tikTokShopApiClient;
        _tikTokShopOAuthService = tikTokShopOAuthService;
        _auditLogService = auditLogService;
        _mabangDispatchService = mabangDispatchService;
        _options = options.Value;
        _tikTokShopOptions = tikTokShopOptions.Value;
        _logger = logger;
    }

    public async Task<ServiceResult<MarketplaceShipmentLabelDownloadResult>> GetOrFetchAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        string shipmentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty || string.IsNullOrWhiteSpace(shipmentId))
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client/shipment context")
            });
        }

        var normalizedShipmentId = shipmentId.Trim();
        var shipment = await _dbContext.MarketplaceShipments.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == provider
                    && item.ShipmentId == normalizedShipmentId,
            cancellationToken);
        if (shipment == null)
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("shipmentId", "Shipment not found")
            });
        }

        if (shipment.LabelContentBytes != null && shipment.LabelContentBytes.Length > 0)
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Success(ToDownloadResult(shipment));
        }

        return shipment.Provider switch
        {
            MarketplaceProvider.MercadoLivre => await GetOrFetchMercadoLivreLabelAsync(shipment, cancellationToken),
            MarketplaceProvider.TikTokShop => await GetOrFetchTikTokLabelAsync(shipment, cancellationToken),
            _ => ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("provider", "Shipment label not supported for this marketplace")
            })
        };
    }

    private async Task<ServiceResult<MarketplaceShipmentLabelDownloadResult>> GetOrFetchMercadoLivreLabelAsync(
        Domain.Entities.MarketplaceShipment shipment,
        CancellationToken cancellationToken)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == shipment.TenantId
                    && item.ClientId == shipment.ClientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == shipment.SellerId,
            cancellationToken);
        if (connection == null)
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("sellerId", "Mercado Livre connection not found for shipment seller")
            });
        }

        var accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        var label = await _mercadoLivreApiClient.GetShipmentLabelAsync(shipment.ShipmentId, accessToken, cancellationToken);
        if (label == null || label.Content.Length == 0)
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("label", "Shipment label not available in Mercado Livre")
            });
        }

        shipment.LabelContentBytes = label.Content;
        shipment.LabelContentType = label.ContentType;
        shipment.LabelSourceUrl = label.SourceUrl;
        shipment.LabelSha256 = label.Sha256;
        shipment.LabelInternalUrl = $"/api/v1/admin/tenants/{{tenantSlug}}/clients/{shipment.ClientId}/integrations/mercadolivre/operations/labels/{shipment.ShipmentId}";
        shipment.UpdatedAt = DateTimeOffset.UtcNow;

        await _auditLogService.RecordAsync(
            shipment.TenantId,
            shipment.ClientId,
            shipment.Provider,
            shipment.SellerId,
            MarketplaceEventTopics.AuditLabelGenerated,
            shipment.ShipmentId,
            new
            {
                shipmentId = shipment.ShipmentId,
                sourceUrl = shipment.LabelSourceUrl,
                sha256 = shipment.LabelSha256
            },
            "v1",
            cancellationToken);
        if (_options.Features.Mabang)
        {
            await _mabangDispatchService.EnqueueLabelDispatchAsync(shipment, cancellationToken);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Success(ToDownloadResult(shipment));
    }

    private async Task<ServiceResult<MarketplaceShipmentLabelDownloadResult>> GetOrFetchTikTokLabelAsync(
        Domain.Entities.MarketplaceShipment shipment,
        CancellationToken cancellationToken)
    {
        var tokenResult = await _tikTokShopOAuthService.GetValidAccessTokenAsync(
            shipment.TenantId,
            shipment.ClientId,
            cancellationToken);
        if (!tokenResult.Succeeded || string.IsNullOrWhiteSpace(tokenResult.Data))
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(tokenResult.Errors);
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == shipment.TenantId
                    && item.ClientId == shipment.ClientId
                    && item.Provider == MarketplaceProvider.TikTokShop
                    && item.SellerId == shipment.SellerId,
            cancellationToken);
        if (connection == null)
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("sellerId", "TikTok Shop connection not found for shipment seller")
            });
        }

        if (string.IsNullOrWhiteSpace(connection.ShopCipher))
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "Sessao TikTok Shop sem shop cipher para gerar etiqueta. Reconecte a conta.");
        }

        var documentResponse = await _tikTokShopApiClient.GetPackageShippingDocumentAsync(
            tokenResult.Data,
            _tikTokShopOptions.AppKey,
            _tikTokShopOptions.AppSecret,
            shipment.ShipmentId,
            connection.ShopCipher,
            cancellationToken: cancellationToken);
        if (!documentResponse.IsSuccess || string.IsNullOrWhiteSpace(documentResponse.Data?.DocUrl))
        {
            _logger.LogWarning(
                "TikTok Shop shipping document not available yet. tenantId={TenantId} clientId={ClientId} shipmentId={ShipmentId} sellerId={SellerId} code={Code} message={Message} hasDocUrl={HasDocUrl}",
                shipment.TenantId,
                shipment.ClientId,
                shipment.ShipmentId,
                shipment.SellerId,
                documentResponse.Code,
                documentResponse.Message,
                !string.IsNullOrWhiteSpace(documentResponse.Data?.DocUrl));
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("label", "Shipment label not available in TikTok Shop")
            });
        }

        shipment.LabelSourceUrl = documentResponse.Data.DocUrl;
        shipment.UpdatedAt = DateTimeOffset.UtcNow;

        using var response = await TikTokLabelHttpClient.GetAsync(documentResponse.Data.DocUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var labelBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (labelBytes.Length == 0)
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(new[]
            {
                new ValidationError("label", "TikTok Shop returned an empty shipping document")
            });
        }

        shipment.LabelContentBytes = labelBytes;
        shipment.LabelContentType = response.Content.Headers.ContentType?.MediaType ?? "application/pdf";
        shipment.LabelSourceUrl = documentResponse.Data.DocUrl;
        shipment.LabelSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(labelBytes)).ToLowerInvariant();
        shipment.LabelInternalUrl = $"/api/v1/client/orders/marketplace/{{orderId}}/labels/{shipment.ShipmentId}";
        shipment.UpdatedAt = DateTimeOffset.UtcNow;

        await _auditLogService.RecordAsync(
            shipment.TenantId,
            shipment.ClientId,
            shipment.Provider,
            shipment.SellerId,
            MarketplaceEventTopics.AuditLabelGenerated,
            shipment.ShipmentId,
            new
            {
                shipmentId = shipment.ShipmentId,
                sourceUrl = shipment.LabelSourceUrl,
                sha256 = shipment.LabelSha256
            },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Success(ToDownloadResult(shipment));
    }

    private static MarketplaceShipmentLabelDownloadResult ToDownloadResult(Domain.Entities.MarketplaceShipment shipment)
    {
        return new MarketplaceShipmentLabelDownloadResult
        {
            ShipmentId = shipment.ShipmentId,
            ContentType = string.IsNullOrWhiteSpace(shipment.LabelContentType) ? "application/pdf" : shipment.LabelContentType,
            FileName = $"{shipment.Provider.ToString().ToLowerInvariant()}-label-{shipment.ShipmentId}.pdf",
            Content = shipment.LabelContentBytes ?? Array.Empty<byte>(),
            SourceUrl = shipment.LabelSourceUrl,
            Sha256 = shipment.LabelSha256
        };
    }
}
