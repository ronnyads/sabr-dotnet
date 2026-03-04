-- Objective:
--   Clear stale listing_drafts.category_id when it diverges from
--   product_marketplace_category_lock.approved_category_id for the same
--   tenant/client/base sku/site.
--
-- Notes:
--   - Idempotent: rows already cleared/updated are skipped.
--   - Keeps audit-friendly BEFORE/AFTER snapshots.
--   - Site resolution fallback: provider_draft_json.siteId -> MLB.

-- BEFORE snapshot
with draft_scope as (
    select
        ld.draft_id,
        ld.tenant_id,
        ld.client_id,
        ld.base_product_sku,
        upper(trim(coalesce(ld.category_id, ''))) as draft_category_id,
        upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB'))) as resolved_site_id
    from listing_drafts ld
),
stale_candidates as (
    select
        ds.draft_id,
        ds.tenant_id,
        ds.client_id,
        ds.base_product_sku,
        ds.resolved_site_id,
        ds.draft_category_id,
        upper(trim(coalesce(pmcl.approved_category_id, ''))) as lock_category_id
    from draft_scope ds
    join product_marketplace_category_lock pmcl
      on pmcl.tenant_id = ds.tenant_id
     and pmcl.client_id = ds.client_id
     and pmcl.base_product_sku = ds.base_product_sku
     and upper(trim(pmcl.site_id)) = ds.resolved_site_id
    where ds.draft_category_id <> ''
      and upper(trim(coalesce(pmcl.approved_category_id, ''))) <> ''
      and ds.draft_category_id <> upper(trim(coalesce(pmcl.approved_category_id, '')))
)
select
    tenant_id,
    resolved_site_id as site_id,
    count(*) as stale_draft_rows
from stale_candidates
group by tenant_id, resolved_site_id
order by tenant_id, resolved_site_id;

-- UPDATE stale rows (clear category_id)
with draft_scope as (
    select
        ld.draft_id,
        ld.tenant_id,
        ld.client_id,
        ld.base_product_sku,
        upper(trim(coalesce(ld.category_id, ''))) as draft_category_id,
        upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB'))) as resolved_site_id
    from listing_drafts ld
),
stale_candidates as (
    select ds.draft_id
    from draft_scope ds
    join product_marketplace_category_lock pmcl
      on pmcl.tenant_id = ds.tenant_id
     and pmcl.client_id = ds.client_id
     and pmcl.base_product_sku = ds.base_product_sku
     and upper(trim(pmcl.site_id)) = ds.resolved_site_id
    where ds.draft_category_id <> ''
      and upper(trim(coalesce(pmcl.approved_category_id, ''))) <> ''
      and ds.draft_category_id <> upper(trim(coalesce(pmcl.approved_category_id, '')))
)
update listing_drafts ld
set
    category_id = null,
    updated_at = now()
where ld.draft_id in (select draft_id from stale_candidates);

-- AFTER snapshot
with draft_scope as (
    select
        ld.draft_id,
        ld.tenant_id,
        ld.client_id,
        ld.base_product_sku,
        upper(trim(coalesce(ld.category_id, ''))) as draft_category_id,
        upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB'))) as resolved_site_id
    from listing_drafts ld
),
stale_candidates as (
    select
        ds.draft_id,
        ds.tenant_id,
        ds.client_id,
        ds.base_product_sku,
        ds.resolved_site_id,
        ds.draft_category_id,
        upper(trim(coalesce(pmcl.approved_category_id, ''))) as lock_category_id
    from draft_scope ds
    join product_marketplace_category_lock pmcl
      on pmcl.tenant_id = ds.tenant_id
     and pmcl.client_id = ds.client_id
     and pmcl.base_product_sku = ds.base_product_sku
     and upper(trim(pmcl.site_id)) = ds.resolved_site_id
    where ds.draft_category_id <> ''
      and upper(trim(coalesce(pmcl.approved_category_id, ''))) <> ''
      and ds.draft_category_id <> upper(trim(coalesce(pmcl.approved_category_id, '')))
)
select
    tenant_id,
    resolved_site_id as site_id,
    count(*) as stale_draft_rows
from stale_candidates
group by tenant_id, resolved_site_id
order by tenant_id, resolved_site_id;
