-- Objective:
--   Clear listing_drafts.category_id when:
--   1) the related lock is already in ReviewRequired, or
--   2) draft category diverges from lock approved category.
--
-- Scope:
--   Mercado Livre drafts only (provider = 1).
--   Site is resolved from provider_draft_json.siteId with fallback MLB.
--
-- Safety:
--   Idempotent; running multiple times is safe.
--   Includes BEFORE/AFTER snapshots by tenant/client/site.

-- BEFORE snapshot
with lock_scope as (
    select
        l.tenant_id,
        l.client_id,
        l.base_product_sku,
        upper(trim(l.site_id)) as site_id,
        upper(trim(coalesce(l.approved_category_id, ''))) as approved_category_id,
        l.status
    from product_marketplace_category_lock l
),
draft_scope as (
    select
        d.draft_id,
        d.tenant_id,
        d.client_id,
        d.base_product_sku,
        upper(trim(coalesce(d.category_id, ''))) as draft_category_id,
        upper(trim(coalesce(nullif(d.provider_draft_json ->> 'siteId', ''), 'MLB'))) as site_id
    from listing_drafts d
    where d.provider = 1
),
to_clear as (
    select
        ds.draft_id,
        ds.tenant_id,
        ds.client_id,
        ds.site_id
    from draft_scope ds
    join lock_scope ls
      on ls.tenant_id = ds.tenant_id
     and ls.client_id = ds.client_id
     and ls.base_product_sku = ds.base_product_sku
     and ls.site_id = ds.site_id
    where ds.draft_category_id <> ''
      and (
            ls.status = 3
            or (
                ls.approved_category_id <> ''
                and ds.draft_category_id <> ls.approved_category_id
            )
          )
)
select
    tenant_id,
    client_id,
    site_id,
    count(*) as drafts_to_clear
from to_clear
group by tenant_id, client_id, site_id
order by tenant_id, client_id, site_id;

-- APPLY: clear stale categories
with lock_scope as (
    select
        l.tenant_id,
        l.client_id,
        l.base_product_sku,
        upper(trim(l.site_id)) as site_id,
        upper(trim(coalesce(l.approved_category_id, ''))) as approved_category_id,
        l.status
    from product_marketplace_category_lock l
),
draft_scope as (
    select
        d.draft_id,
        d.tenant_id,
        d.client_id,
        d.base_product_sku,
        upper(trim(coalesce(d.category_id, ''))) as draft_category_id,
        upper(trim(coalesce(nullif(d.provider_draft_json ->> 'siteId', ''), 'MLB'))) as site_id
    from listing_drafts d
    where d.provider = 1
),
to_clear as (
    select ds.draft_id
    from draft_scope ds
    join lock_scope ls
      on ls.tenant_id = ds.tenant_id
     and ls.client_id = ds.client_id
     and ls.base_product_sku = ds.base_product_sku
     and ls.site_id = ds.site_id
    where ds.draft_category_id <> ''
      and (
            ls.status = 3
            or (
                ls.approved_category_id <> ''
                and ds.draft_category_id <> ls.approved_category_id
            )
          )
)
update listing_drafts d
set
    category_id = null,
    updated_at = now()
where d.draft_id in (select draft_id from to_clear);

-- AFTER snapshot
with lock_scope as (
    select
        l.tenant_id,
        l.client_id,
        l.base_product_sku,
        upper(trim(l.site_id)) as site_id,
        upper(trim(coalesce(l.approved_category_id, ''))) as approved_category_id,
        l.status
    from product_marketplace_category_lock l
),
draft_scope as (
    select
        d.draft_id,
        d.tenant_id,
        d.client_id,
        d.base_product_sku,
        upper(trim(coalesce(d.category_id, ''))) as draft_category_id,
        upper(trim(coalesce(nullif(d.provider_draft_json ->> 'siteId', ''), 'MLB'))) as site_id
    from listing_drafts d
    where d.provider = 1
),
to_clear as (
    select
        ds.draft_id,
        ds.tenant_id,
        ds.client_id,
        ds.site_id
    from draft_scope ds
    join lock_scope ls
      on ls.tenant_id = ds.tenant_id
     and ls.client_id = ds.client_id
     and ls.base_product_sku = ds.base_product_sku
     and ls.site_id = ds.site_id
    where ds.draft_category_id <> ''
      and (
            ls.status = 3
            or (
                ls.approved_category_id <> ''
                and ds.draft_category_id <> ls.approved_category_id
            )
          )
)
select
    tenant_id,
    client_id,
    site_id,
    count(*) as drafts_to_clear
from to_clear
group by tenant_id, client_id, site_id
order by tenant_id, client_id, site_id;
