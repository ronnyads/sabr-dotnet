using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Api.Tests;

public sealed class MarketplaceOrderInventoryPaymentTests
{
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
