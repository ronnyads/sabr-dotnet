using Phub.Application.Models;
using Phub.Domain.Entities;

namespace Phub.Application.Abstractions;

public interface IMarketplaceListingAdapter
{
    bool CanHandle(MercadoLivreSellerItemDetails source);
    ClientMarketplaceListing Normalize(TenantMarketplaceListingMap mapping, MercadoLivreSellerItemDetails source);
    MarketplaceListingCapabilities Evaluate(ClientMarketplaceListing listing, MercadoLivreSellerItemDetails source, DateTimeOffset now);
}
