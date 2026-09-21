using Npgsql;
using System.Data;
using System.Text.Json;

const string oldSku = "MLBU5072958857";
const string canonicalSku = "PH-RN03";
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
    ?? throw new InvalidOperationException("Database connection is not configured.");

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

static async Task PrintRows(NpgsqlConnection connection, string sql, params NpgsqlParameter[] parameters)
{
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddRange(parameters);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount)
            .Select(i => $"{reader.GetName(i)}={reader.GetValue(i)}")));
    }
}

Console.WriteLine("PRODUCTS");
await PrintRows(connection,
    "select sku, name, brand, ean, catalog_price_cents, cost_price_cents, is_active from products where sku = @old or sku = @canonical order by sku",
    new NpgsqlParameter("old", oldSku), new NpgsqlParameter("canonical", canonicalSku));

Console.WriteLine("SKU REFERENCES");
await using (var columns = new NpgsqlCommand("""
    select table_name, column_name from information_schema.columns
    where table_schema = 'public'
      and (column_name like '%sku%' or column_name in ('ml_item_id', 'channel_sku'))
      and data_type in ('character varying', 'text', 'character')
    order by table_name, column_name
    """, connection))
await using (var reader = await columns.ExecuteReaderAsync())
{
    var references = new List<(string Table, string Column)>();
    while (await reader.ReadAsync()) references.Add((reader.GetString(0), reader.GetString(1)));
    await reader.CloseAsync();
    var identifierQuoter = new NpgsqlCommandBuilder();
    foreach (var (table, column) in references)
    {
        var quotedTable = identifierQuoter.QuoteIdentifier(table);
        var quotedColumn = identifierQuoter.QuoteIdentifier(column);
        await using var count = new NpgsqlCommand($"select count(*) from {quotedTable} where {quotedColumn} = @sku", connection);
        count.Parameters.AddWithValue("sku", oldSku);
        var n = Convert.ToInt64(await count.ExecuteScalarAsync());
        if (n > 0) Console.WriteLine($"{table}.{column}={n}");
    }
}

Console.WriteLine("MAPPINGS");
await PrintRows(connection, """
    select id, tenant_id, client_id, seller_id, ml_item_id, ml_variation_id, channel_sku,
           sabr_variant_sku, mapping_version
    from tenant_marketplace_listing_maps
    where sabr_variant_sku in (@old, @canonical) or ml_item_id = @old
    order by client_id, seller_id, ml_item_id
    """, new NpgsqlParameter("old", oldSku), new NpgsqlParameter("canonical", canonicalSku));

Console.WriteLine("VARIANTS");
await PrintRows(connection, """
    select variant_sku, base_sku, physical_stock, reserved_stock, available_stock, is_active
    from product_variants where variant_sku in (@old, @canonical) or base_sku in (@old, @canonical)
    order by variant_sku
    """, new NpgsqlParameter("old", oldSku), new NpgsqlParameter("canonical", canonicalSku));

Console.WriteLine("RESERVATIONS");
await PrintRows(connection, """
    select id, tenant_id, client_id, marketplace_order_id, marketplace_order_item_id, quantity, status, reserved_at
    from stock_reservations where sabr_variant_sku = @old order by reserved_at
    """, new NpgsqlParameter("old", oldSku));

Console.WriteLine("ORDER ITEM STATES");
await PrintRows(connection, """
    select ml_item_id, channel_sku, sabr_variant_sku, mapping_state, count(*) as items,
           sum(quantity) as units, sum(reserved_quantity) as reserved_units,
           count(*) filter (where catalog_unit_price_cents_at_payment is not null) as paid_snapshot_items
    from marketplace_order_items
    where channel_sku = @old or sabr_variant_sku = @old or ml_item_id = @old
    group by ml_item_id, channel_sku, sabr_variant_sku, mapping_state
    order by items desc
    """, new NpgsqlParameter("old", oldSku));

Console.WriteLine("PUBLICATIONS");
await PrintRows(connection, """
    select id, tenant_id, client_id, product_sku, status, catalog_price_cents_snapshot
    from publications where product_sku in (@old, @canonical)
    order by client_id, product_sku
    """, new NpgsqlParameter("old", oldSku), new NpgsqlParameter("canonical", canonicalSku));

if (args.Length == 0) return;
if (args.Length != 1 || args[0] != "--apply-PH-RN03")
    throw new ArgumentException("Unsupported operation. Run without arguments for inspection.");

await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
await using (var lockCommand = new NpgsqlCommand("set local lock_timeout = '5s'", connection, tx))
    await lockCommand.ExecuteNonQueryAsync();

async Task<long> Scalar(string sql)
{
    await using var command = new NpgsqlCommand(sql, connection, tx);
    command.Parameters.AddWithValue("old", oldSku);
    command.Parameters.AddWithValue("canonical", canonicalSku);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

async Task<int> Change(string sql)
{
    await using var command = new NpgsqlCommand(sql, connection, tx);
    command.Parameters.AddWithValue("old", oldSku);
    command.Parameters.AddWithValue("canonical", canonicalSku);
    return await command.ExecuteNonQueryAsync();
}

if (await Scalar("""
    select count(*) from products a cross join products b
    where a.sku = @old and b.sku = @canonical
      and a.ean is not null and a.ean = b.ean
      and a.catalog_price_cents = b.catalog_price_cents
      and a.cost_price_cents = b.cost_price_cents
      and a.is_active and b.is_active
    """) != 1)
    throw new InvalidOperationException("Product identity, price, or active state changed; no updates applied.");
if (await Scalar("select count(*) from stock_reservations where sabr_variant_sku = @old and status = 1") != 0
    || await Scalar("select count(*) from marketplace_order_items where sabr_variant_sku = @old") != 0
    || await Scalar("select coalesce(sum(reserved_stock), 0) from product_variants where variant_sku = @old") != 0)
    throw new InvalidOperationException("Old SKU still has active reservations, mapped order items, or reserved stock; no updates applied.");
if (await Scalar("select count(*) from tenant_marketplace_listing_maps where sabr_variant_sku = @old") != 1
    || await Scalar("select count(*) from publications where product_sku = @old and status = 0") != 1
    || await Scalar("select count(*) from publications where product_sku = @old and status <> 0") != 0)
    throw new InvalidOperationException("Unexpected active references; no updates applied.");
if (await Scalar("""
    select count(*) from publications old_draft
    where old_draft.product_sku = @old and old_draft.status = 0
      and exists (select 1 from publications canonical_draft
                  where canonical_draft.tenant_id = old_draft.tenant_id
                    and canonical_draft.client_id = old_draft.client_id
                    and canonical_draft.product_sku = @canonical
                    and canonical_draft.status = 0)
    """) != 1)
    throw new InvalidOperationException("Canonical client draft missing; no updates applied.");

var mappingCount = await Change("""
    update tenant_marketplace_listing_maps
    set sabr_variant_sku = @canonical, mapping_version = mapping_version + 1, updated_at = now()
    where sabr_variant_sku = @old
    """);
var pausedDraftCount = await Change("""
    update publications set status = 4, updated_at = now()
    where product_sku = @old and status = 0
    """);
var addedCatalogLinks = await Change("""
    insert into product_catalogs (id, catalog_id, product_sku, created_at)
    select gen_random_uuid(), catalog_id, @canonical, now()
    from product_catalogs where product_sku = @old
    on conflict (catalog_id, product_sku) do nothing
    """);
var removedLegacyCatalogLinks = await Change("delete from product_catalogs where product_sku = @old");
var deactivatedVariantCount = await Change("""
    update product_variants set is_active = false, updated_at = now()
    where variant_sku = @old and base_sku = @old and reserved_stock = 0
    """);
var deactivatedProductCount = await Change("""
    update products set is_active = false, updated_at = now()
    where sku = @old
    """);
if (mappingCount != 1 || pausedDraftCount != 1 || removedLegacyCatalogLinks != 2
    || deactivatedVariantCount != 1 || deactivatedProductCount != 1)
    throw new InvalidOperationException("Change counts did not match the inspected production state; no updates applied.");

var requestId = Guid.NewGuid();
var metadata = JsonSerializer.Serialize(new
{
    oldSku,
    canonicalSku,
    mappingCount,
    pausedDraftCount,
    addedCatalogLinks,
    removedLegacyCatalogLinks,
    deactivatedVariantCount,
    deactivatedProductCount,
    preservedReleasedReservations = 4,
    preservedChannelSkuOrderItems = 93,
    stockWasNotSummed = true,
    historicalFinancialSnapshotsUnchanged = true,
    reason = "User-authorized consolidation of duplicate Mercado Livre user-product identifier into PH-RN03"
});
await using (var audit = new NpgsqlCommand("""
    insert into audit_events ("Id", tenant_id, actor_type, action, entity, request_id, metadata_json, created_at)
    select gen_random_uuid(), tenant_id, 'Maintenance', 'Catalog.ConsolidateDuplicateSku', 'Product', @requestId,
           @metadata::jsonb, now()
    from (select distinct tenant_id from publications where product_sku in (@old, @canonical)) t
    """, connection, tx))
{
    audit.Parameters.AddWithValue("requestId", requestId);
    audit.Parameters.AddWithValue("metadata", metadata);
    audit.Parameters.AddWithValue("old", oldSku);
    audit.Parameters.AddWithValue("canonical", canonicalSku);
    await audit.ExecuteNonQueryAsync();
}

await tx.CommitAsync();
Console.WriteLine($"APPLIED requestId={requestId} mapping={mappingCount} pausedDraft={pausedDraftCount} catalogLinksAdded={addedCatalogLinks} catalogLinksRemoved={removedLegacyCatalogLinks} variantDeactivated={deactivatedVariantCount} productDeactivated={deactivatedProductCount}");
