using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class MercadoLivreLegacyListingAdapter : IMarketplaceListingAdapter
{
    public bool CanHandle(MercadoLivreSellerItemDetails source) => string.IsNullOrWhiteSpace(source.UserProductId);

    public ClientMarketplaceListing Normalize(TenantMarketplaceListingMap mapping, MercadoLivreSellerItemDetails source)
        => MarketplaceListingAdapterSupport.Normalize(mapping, source, MarketplaceListingModel.LegacyItem);

    public MarketplaceListingCapabilities Evaluate(ClientMarketplaceListing listing, MercadoLivreSellerItemDetails source, DateTimeOffset now)
    {
        var mutable = MarketplaceListingAdapterSupport.IsMutableStatus(source.Status);
        var titleEditable = mutable && source.SoldQuantity == 0 && !source.IsCatalogListing;
        return MarketplaceListingAdapterSupport.BuildCapabilities(
            listing,
            source,
            now,
            titleEditable,
            titleEditable ? null : source.IsCatalogListing
                ? ("CATALOG_FIELD_MANAGED", "O titulo deste anuncio de catalogo e administrado pelo Mercado Livre.")
                : source.SoldQuantity > 0
                    ? ("LISTING_HAS_SALES", "O titulo nao pode ser alterado depois que o anuncio registra vendas.")
                    : ("LISTING_STATUS_LOCKED", "O status atual bloqueia a edicao do titulo."),
            descriptionEditable: mutable);
    }
}

public sealed class MercadoLivreUserProductListingAdapter : IMarketplaceListingAdapter
{
    public bool CanHandle(MercadoLivreSellerItemDetails source) => !string.IsNullOrWhiteSpace(source.UserProductId);

    public ClientMarketplaceListing Normalize(TenantMarketplaceListingMap mapping, MercadoLivreSellerItemDetails source)
        => MarketplaceListingAdapterSupport.Normalize(mapping, source, MarketplaceListingModel.UserProduct);

    public MarketplaceListingCapabilities Evaluate(ClientMarketplaceListing listing, MercadoLivreSellerItemDetails source, DateTimeOffset now)
        => MarketplaceListingAdapterSupport.BuildCapabilities(
            listing,
            source,
            now,
            titleEditable: false,
            titleBlocked: ("USER_PRODUCT_INHERITED_FIELD", "O titulo e herdado do User Product e nao pode ser editado neste anuncio."),
            descriptionEditable: MarketplaceListingAdapterSupport.IsMutableStatus(source.Status));
}

internal static class MarketplaceListingAdapterSupport
{
    internal static ClientMarketplaceListing Normalize(
        TenantMarketplaceListingMap mapping,
        MercadoLivreSellerItemDetails source,
        MarketplaceListingModel model)
        => new()
        {
            MappingId = mapping.Id,
            MappingVersion = mapping.MappingVersion,
            Model = model,
            MasterSku = mapping.SabrVariantSku,
            ChannelSku = mapping.ChannelSku ?? source.SellerSku,
            Title = source.Title,
            Price = source.Price,
            AvailableQuantity = source.AvailableQuantity,
            SoldQuantity = source.SoldQuantity,
            Status = source.Status,
            Permalink = source.Permalink,
            IsCatalogListing = source.IsCatalogListing,
            Identity = new MarketplaceListingIdentity
            {
                Provider = mapping.Provider,
                IntegrationId = mapping.IntegrationId ?? Guid.Empty,
                SellerId = mapping.SellerId,
                ItemId = mapping.MlItemId,
                VariationId = mapping.MlVariationId,
                UserProductId = mapping.UserProductId ?? source.UserProductId
            }
        };

    internal static MarketplaceListingCapabilities BuildCapabilities(
        ClientMarketplaceListing listing,
        MercadoLivreSellerItemDetails source,
        DateTimeOffset now,
        bool titleEditable,
        (string Code, string Reason)? titleBlocked,
        bool descriptionEditable)
    {
        var mutable = IsMutableStatus(source.Status);
        var isVariation = !string.IsNullOrWhiteSpace(listing.Identity.VariationId);
        var priceEditable = mutable && !source.HasPriceAutomation && !isVariation;
        var capabilities = new MarketplaceListingCapabilities
        {
            MappingId = listing.MappingId,
            MappingVersion = listing.MappingVersion,
            Model = listing.Model,
            EvaluatedAt = now,
            ExpiresAt = now.AddMinutes(5),
            Fields = new Dictionary<string, MarketplaceListingFieldCapability>(StringComparer.OrdinalIgnoreCase)
            {
                ["masterSku"] = Blocked("MASTER_SKU_IMMUTABLE", "A SKU mestre e imutavel; use o fluxo auditado de remapeamento.", listing.MasterSku),
                ["stock"] = Blocked("CENTRAL_INVENTORY_MANAGED", "O estoque e controlado pelo estoque central e suas reservas.", listing.AvailableQuantity),
                ["title"] = titleEditable
                    ? Editable(listing.Title)
                    : Blocked(titleBlocked?.Code ?? "FIELD_NOT_EDITABLE", titleBlocked?.Reason ?? "Campo indisponivel.", listing.Title),
                ["price"] = priceEditable
                    ? Editable(listing.Price)
                    : Blocked(source.HasPriceAutomation ? "PRICE_AUTOMATION_ACTIVE" : isVariation ? "VARIATION_PRICE_REQUIRES_VALUE_SCOPE" : "LISTING_STATUS_LOCKED",
                        source.HasPriceAutomation
                            ? "Desative ou ajuste a automacao de preco no canal antes de editar."
                            : isVariation
                                ? "O preco desta variacao permanece bloqueado ate a homologacao do endpoint de valores por variacao."
                                : "O status atual bloqueia a edicao de preco.",
                        listing.Price),
                ["description"] = descriptionEditable
                    ? Editable(null)
                    : Blocked("LISTING_STATUS_LOCKED", "O status atual bloqueia a edicao da descricao.", null),
                ["images"] = Blocked("NOT_AVAILABLE_IN_THIS_RELEASE", "A edicao de imagens sera liberada apos a homologacao do fluxo normalizado.", null)
            }
        };

        capabilities.EvaluationHash = ComputeHash(listing, source);
        return capabilities;
    }

    internal static bool IsMutableStatus(string? status)
        => string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase);

    private static MarketplaceListingFieldCapability Editable(object? current)
        => new() { Editable = true, CurrentValue = current };

    private static MarketplaceListingFieldCapability Blocked(string code, string reason, object? current)
        => new() { Editable = false, ReasonCode = code, Reason = reason, CurrentValue = current };

    private static string ComputeHash(ClientMarketplaceListing listing, MercadoLivreSellerItemDetails source)
    {
        var value = string.Join('|',
            listing.MappingId.ToString("N"),
            listing.MappingVersion.ToString(CultureInfo.InvariantCulture),
            listing.Model.ToString(),
            source.ItemId,
            source.Status,
            source.Title,
            source.Price.ToString(CultureInfo.InvariantCulture),
            source.SoldQuantity.ToString(CultureInfo.InvariantCulture),
            source.AvailableQuantity.ToString(CultureInfo.InvariantCulture),
            source.IsCatalogListing,
            source.HasPriceAutomation,
            source.UserProductId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
