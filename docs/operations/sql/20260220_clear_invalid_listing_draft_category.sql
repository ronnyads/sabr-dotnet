-- Purpose:
--   Clear legacy invalid listing_drafts.category_id values that do not match
--   the Mercado Livre site-specific format ^{siteId}\d+$.
--
-- Safety:
--   - Idempotent: running multiple times only clears still-invalid rows.
--   - Scope: only non-empty category_id values that fail the normalized rule.
--   - Site fallback: defaults to MLB when provider_draft_json.siteId is absent or invalid.
--
-- Usage:
--   1) Run the BEFORE snapshot.
--   2) Run the UPDATE block.
--   3) Run the AFTER snapshot.

-- =========================================================
-- BEFORE SNAPSHOT (count invalid rows by tenant/site)
-- =========================================================
with normalized as (
    select
        ld.draft_id,
        ld.tenant_id,
        coalesce(
            nullif(
                case
                    when upper(trim(coalesce(ld.provider_draft_json ->> 'siteId', ''))) ~ '^ML[A-Z]$'
                        then upper(trim(ld.provider_draft_json ->> 'siteId'))
                    else 'MLB'
                end,
                ''
            ),
            'MLB'
        ) as resolved_site_id,
        upper(trim(coalesce(ld.category_id, ''))) as normalized_category_id
    from listing_drafts ld
)
select
    tenant_id,
    resolved_site_id as site_id,
    count(*) as invalid_count
from normalized
where normalized_category_id <> ''
  and normalized_category_id !~ ('^' || resolved_site_id || '\d+$')
group by tenant_id, resolved_site_id
order by invalid_count desc, tenant_id, resolved_site_id;

-- =========================================================
-- UPDATE INVALID ROWS
-- =========================================================
with normalized as (
    select
        ld.draft_id,
        coalesce(
            nullif(
                case
                    when upper(trim(coalesce(ld.provider_draft_json ->> 'siteId', ''))) ~ '^ML[A-Z]$'
                        then upper(trim(ld.provider_draft_json ->> 'siteId'))
                    else 'MLB'
                end,
                ''
            ),
            'MLB'
        ) as resolved_site_id,
        upper(trim(coalesce(ld.category_id, ''))) as normalized_category_id
    from listing_drafts ld
),
invalid_rows as (
    select draft_id
    from normalized
    where normalized_category_id <> ''
      and normalized_category_id !~ ('^' || resolved_site_id || '\d+$')
)
update listing_drafts ld
set
    category_id = null,
    updated_at = now()
from invalid_rows ir
where ld.draft_id = ir.draft_id;

-- =========================================================
-- AFTER SNAPSHOT
-- =========================================================
with normalized as (
    select
        ld.draft_id,
        ld.tenant_id,
        coalesce(
            nullif(
                case
                    when upper(trim(coalesce(ld.provider_draft_json ->> 'siteId', ''))) ~ '^ML[A-Z]$'
                        then upper(trim(ld.provider_draft_json ->> 'siteId'))
                    else 'MLB'
                end,
                ''
            ),
            'MLB'
        ) as resolved_site_id,
        upper(trim(coalesce(ld.category_id, ''))) as normalized_category_id
    from listing_drafts ld
)
select
    tenant_id,
    resolved_site_id as site_id,
    count(*) as invalid_count
from normalized
where normalized_category_id <> ''
  and normalized_category_id !~ ('^' || resolved_site_id || '\d+$')
group by tenant_id, resolved_site_id
order by invalid_count desc, tenant_id, resolved_site_id;
