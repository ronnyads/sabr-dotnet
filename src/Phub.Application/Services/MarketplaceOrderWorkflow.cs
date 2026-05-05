using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

internal static class MarketplaceOrderWorkflow
{
    public static bool RequiresLabelForPayment(MarketplaceProvider provider)
        => provider == MarketplaceProvider.TikTokShop;

    public static string ResolveLabelAvailability(MarketplaceShipment shipment)
    {
        if (shipment.LabelContentBytes != null && shipment.LabelContentBytes.Length > 0)
        {
            return MarketplaceLabelAvailabilities.AvailableCached;
        }

        if (!string.IsNullOrWhiteSpace(shipment.LabelSourceUrl))
        {
            return MarketplaceLabelAvailabilities.AvailableRemote;
        }

        return MarketplaceLabelAvailabilities.Pending;
    }

    public static bool HasOperationalLabel(MarketplaceShipment shipment)
        => ResolveLabelAvailability(shipment) != MarketplaceLabelAvailabilities.Pending;

    public static bool HasOperationalLabel(IEnumerable<MarketplaceShipment> shipments)
        => shipments.Any(HasOperationalLabel);

    public static bool CanMarkPaid(MarketplaceOrder order, IEnumerable<MarketplaceShipment> shipments)
        => !RequiresLabelForPayment(order.Provider) || HasOperationalLabel(shipments);

    public static bool CanAutoCancel(string? internalStage)
        => internalStage is MarketplaceInternalStages.Pending
            or MarketplaceInternalStages.Received
            or MarketplaceInternalStages.Paid
            or MarketplaceInternalStages.ProcessingStarted;

    public static MarketplaceCancellationRequestResult BuildCancellationRequest(MarketplaceOrder order)
    {
        var status = string.IsNullOrWhiteSpace(order.CancellationRequestStatus)
            ? MarketplaceCancellationRequestStatuses.None
            : order.CancellationRequestStatus.Trim().ToLowerInvariant();

        return new MarketplaceCancellationRequestResult
        {
            Status = status,
            Label = status switch
            {
                MarketplaceCancellationRequestStatuses.Requested => "Cancelamento solicitado",
                MarketplaceCancellationRequestStatuses.Approved => "Cancelamento aprovado",
                MarketplaceCancellationRequestStatuses.Rejected => "Cancelamento recusado",
                _ => "Sem solicitação"
            },
            RequestedAt = order.CancellationRequestedAt,
            RequestedBy = order.CancellationRequestedBy,
            Reason = order.CancellationRequestReason,
            ReviewedAt = order.CancellationReviewedAt,
            ReviewedBy = order.CancellationReviewedBy,
            IsPending = status == MarketplaceCancellationRequestStatuses.Requested
        };
    }

    public static bool IsExternalDispatched(MarketplaceShipment shipment)
    {
        if (shipment.ShippedAt.HasValue)
        {
            return true;
        }

        var rawStatus = shipment.Status?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return false;
        }

        return rawStatus.Contains("ship", StringComparison.Ordinal)
               || rawStatus.Contains("transit", StringComparison.Ordinal)
               || rawStatus.Contains("dispatch", StringComparison.Ordinal)
               || rawStatus.Contains("handover", StringComparison.Ordinal);
    }

    public static bool IsExternalDelivered(string? rawStatus)
    {
        rawStatus = rawStatus?.Trim().ToLowerInvariant();
        return rawStatus is "delivered" or "completed" or "complete";
    }

    public static bool IsExternalCancelled(string? rawStatus)
    {
        rawStatus = rawStatus?.Trim().ToLowerInvariant();
        return rawStatus is "cancelled" or "canceled";
    }

    public static string ToChannelLabel(string? stage, string? rawStatus)
        => stage switch
        {
            MarketplaceChannelStages.Dispatched => "Despachado no canal",
            MarketplaceChannelStages.Delivered => "Entregue no canal",
            MarketplaceChannelStages.Cancelled => "Cancelado no canal",
            MarketplaceChannelStages.RefundRequested => "Estorno solicitado",
            MarketplaceChannelStages.Refunded => "Estornado",
            MarketplaceChannelStages.AwaitingShipment => "Aguardando envio no canal",
            _ => NormalizeRawStatus(rawStatus) ?? "Aguardando atualização do canal"
        };

    public static string? NormalizeRawStatus(string? rawStatus)
    {
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return null;
        }

        return rawStatus.Trim() switch
        {
            "pending_payment" => "Aguardando pagamento",
            "paid" => "Pago no canal",
            "payment_confirmed" => "Pagamento confirmado",
            "label_generated" => "Etiqueta gerada",
            "dispatched" => "Despachado",
            "delivered" => "Entregue",
            "cancelled" => "Cancelado",
            "refund_requested" => "Estorno solicitado",
            "refunded" => "Estornado",
            "AWAITING_SHIPMENT" => "Aguardando envio",
            "AWAITING_COLLECTION" => "Aguardando coleta",
            "IN_TRANSIT" => "Em trânsito",
            "COMPLETED" => "Concluído",
            var value => value.Replace('_', ' ')
        };
    }
}
