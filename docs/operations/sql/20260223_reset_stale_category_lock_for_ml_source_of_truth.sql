-- Objective:
--   Reset legacy category locks that can keep stale categories active when
--   ML is the source of truth.
--
-- Scope:
--   - Locks with status approved (1/2) and source manual/lock (3/4).
--   - Marks these locks as ReviewRequired (3).
--   - Clears listing_drafts.category_id for related tenant/client/base_sku/site
--     so the wizard forces a new ML-based category resolution.
--
-- Safety:
--   - Idempotent: reruns only affect still-approved manual/lock rows.
--   - Includes BEFORE/AFTER snapshots.

-- =========================================================
-- BEFORE SNAPSHOT
-- =========================================================
with lock_scope as (
    select
        pmcl.id,
        pmcl.tenant_id,
        pmcl.client_id,
        pmcl.base_product_sku,
        upper(trim(coalesce(pmcl.site_id, 'MLB'))) as site_id,
        upper(trim(coalesce(pmcl.approved_category_id, ''))) as approved_category_id,
        pmcl.status,
        pmcl.source
    from product_marketplace_category_lock pmcl
),
affected_locks as (
    select *
    from lock_scope
    where status in (1, 2)
      and source in (3, 4)
      and approved_category_id <> ''
)
select
    tenant_id,
    site_id,
    count(*) as locks_to_review
from affected_locks
group by tenant_id, site_id
order by tenant_id, site_id;

with lock_scope as (
    select
        pmcl.id,
        pmcl.tenant_id,
        pmcl.client_id,
        pmcl.base_product_sku,
        upper(trim(coalesce(pmcl.site_id, 'MLB'))) as site_id,
        upper(trim(coalesce(pmcl.approved_category_id, ''))) as approved_category_id,
        pmcl.status,
        pmcl.source
    from product_marketplace_category_lock pmcl
),
affected_locks as (
    select *
    from lock_scope
    where status in (1, 2)
      and source in (3, 4)
      and approved_category_id <> ''
),
affected_drafts as (
    select ld.draft_id, ld.tenant_id, upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB'))) as site_id
    from listing_drafts ld
    join affected_locks al
      on al.tenant_id = ld.tenant_id
     and al.client_id = ld.client_id
     and al.base_product_sku = ld.base_product_sku
     and al.site_id = upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB')))
    where upper(trim(coalesce(ld.category_id, ''))) <> ''
)
select
    tenant_id,
    site_id,
    count(*) as drafts_to_clear
from affected_drafts
group by tenant_id, site_id
order by tenant_id, site_id;

-- =========================================================
-- UPDATE LOCKS
-- =========================================================
with affected_locks as (
    select pmcl.id
    from product_marketplace_category_lock pmcl
    where pmcl.status in (1, 2)
      and pmcl.source in (3, 4)
      and upper(trim(coalesce(pmcl.approved_category_id, ''))) <> ''
)
update product_marketplace_category_lock pmcl
set
    status = 3,
    updated_at = now()
where pmcl.id in (select id from affected_locks);

-- =========================================================
-- CLEAR DRAFT CATEGORY
-- =========================================================
with affected_locks as (
    select
        pmcl.tenant_id,
        pmcl.client_id,
        pmcl.base_product_sku,
        upper(trim(coalesce(pmcl.site_id, 'MLB'))) as site_id
    from product_marketplace_category_lock pmcl
    where pmcl.status = 3
      and pmcl.source in (3, 4)
),
affected_drafts as (
    select ld.draft_id
    from listing_drafts ld
    join affected_locks al
      on al.tenant_id = ld.tenant_id
     and al.client_id = ld.client_id
     and al.base_product_sku = ld.base_product_sku
     and al.site_id = upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB')))
    where upper(trim(coalesce(ld.category_id, ''))) <> ''
)
update listing_drafts ld
set
    category_id = null,
    updated_at = now()
where ld.draft_id in (select draft_id from affected_drafts);

-- =========================================================
-- AFTER SNAPSHOT
-- =========================================================
with lock_scope as (
    select
        pmcl.id,
        pmcl.tenant_id,
        pmcl.client_id,
        pmcl.base_product_sku,
        upper(trim(coalesce(pmcl.site_id, 'MLB'))) as site_id,
        upper(trim(coalesce(pmcl.approved_category_id, ''))) as approved_category_id,
        pmcl.status,
        pmcl.source
    from product_marketplace_category_lock pmcl
),
affected_locks as (
    select *
    from lock_scope
    where status in (1, 2)
      and source in (3, 4)
      and approved_category_id <> ''
)
select
    tenant_id,
    site_id,
    count(*) as locks_to_review
from affected_locks
group by tenant_id, site_id
order by tenant_id, site_id;

with lock_scope as (
    select
        pmcl.id,
        pmcl.tenant_id,
        pmcl.client_id,
        pmcl.base_product_sku,
        upper(trim(coalesce(pmcl.site_id, 'MLB'))) as site_id,
        upper(trim(coalesce(pmcl.approved_category_id, ''))) as approved_category_id,
        pmcl.status,
        pmcl.source
    from product_marketplace_category_lock pmcl
),
affected_locks as (
    select *
    from lock_scope
    where status in (1, 2)
      and source in (3, 4)
      and approved_category_id <> ''
),
affected_drafts as (
    select ld.draft_id, ld.tenant_id, upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB'))) as site_id
    from listing_drafts ld
    join affected_locks al
      on al.tenant_id = ld.tenant_id
     and al.client_id = ld.client_id
     and al.base_product_sku = ld.base_product_sku
     and al.site_id = upper(trim(coalesce(nullif(ld.provider_draft_json ->> 'siteId', ''), 'MLB')))
    where upper(trim(coalesce(ld.category_id, ''))) <> ''
)
select
    tenant_id,
    site_id,
    count(*) as drafts_to_clear
from affected_drafts
group by tenant_id, site_id
order by tenant_id, site_id;
