using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Api.Tests;

public sealed class MarketplaceOrderInventoryPaymentTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Checkout_RequiresFullExistingReservation(int reserved, bool expected)
    {
        var orderId = Guid.NewGuid();
        var item = new MarketplaceOrderItem
        {
            MarketplaceOrderId = orderId, SabrVariantSku = "PH-TEST",
            MappingState = MarketplaceMappingStates.Mapped, Quantity = 2
        };
        var reservations = reserved == 0 ? Array.Empty<StockReservation>() : new[]
        {
            new StockReservation
            {
                MarketplaceOrderId = orderId, MarketplaceOrderItemId = item.Id,
                SabrVariantSku = "PH-TEST", Quantity = reserved,
                Status = StockReservationStatus.Reserved
            }
        };

        Assert.Equal(expected, MarketplaceOrderCheckoutService.HasCompleteReservation(new[] { item }, reservations));
    }

    [Theory]
    [InlineData("pending_payment", false)]
    [InlineData("created", false)]
    [InlineData("paid", true)]
    public void MercadoLivrePayment_RequiresConfirmedChannelStatus(string status, bool canMarkPaid)
    {
        var order = new MarketplaceOrder
        {
            Provider = MarketplaceProvider.MercadoLivre,
            Status = status
        };
        var item = new MarketplaceOrderItemInventorySummary(
            Guid.NewGuid(), "MAPPED", 5, 0, MarketplaceOrderItemStockStatuses.InStock);

        var summary = MarketplaceOrderInventoryService.BuildSummary(
            order, Array.Empty<MarketplaceShipment>(), new[] { item });

        Assert.Equal(canMarkPaid, summary.CanMarkPaid);
        Assert.Equal(!canMarkPaid, summary.PaymentBlockers.Contains(MarketplaceOrderPaymentBlockers.ChannelPaymentPending));
    }
}
