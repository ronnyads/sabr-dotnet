using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MarketplaceShipmentLabelService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly MarketplaceAuditLogService _auditLogService;
    private readonly MarketplaceMabangDispatchService _mabangDispatchService;
    private readonly MercadoLivreOptions _options;

    public MarketplaceShipmentLabelService(
        IAppDbContext dbContext,
        IMercadoLivreApiClient mercadoLivreApiClient,
        MercadoLivreOAuthService oauthService,
        MarketplaceAuditLogService auditLogService,
        MarketplaceMabangDispatchService mabangDispatchService,
        IOptions<MercadoLivreOptions> options)
    {
        _dbContext = dbContext;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _oauthService = oauthService;
        _auditLogService = auditLogService;
        _mabangDispatchService = mabangDispatchService;
        _options = options.Value;
    }

    public async Task<ServiceResult<MarketplaceShipmentLabelDownloadResult>> GetOrFetchAsync(
        string tenantId,
        Guid clientId,
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
                    && item.Provider == MarketplaceProvider.MercadoLivre
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

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
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
        var label = await _mercadoLivreApiClient.GetShipmentLabelAsync(normalizedShipmentId, accessToken, cancellationToken);
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
        shipment.LabelInternalUrl = $"/api/v1/admin/tenants/{{tenantSlug}}/clients/{clientId}/integrations/mercadolivre/operations/labels/{shipment.ShipmentId}";
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

    private static MarketplaceShipmentLabelDownloadResult ToDownloadResult(Domain.Entities.MarketplaceShipment shipment)
    {
        return new MarketplaceShipmentLabelDownloadResult
        {
            ShipmentId = shipment.ShipmentId,
            ContentType = string.IsNullOrWhiteSpace(shipment.LabelContentType) ? "application/pdf" : shipment.LabelContentType,
            FileName = $"ml-label-{shipment.ShipmentId}.pdf",
            Content = shipment.LabelContentBytes ?? Array.Empty<byte>(),
            SourceUrl = shipment.LabelSourceUrl,
            Sha256 = shipment.LabelSha256
        };
    }
}
