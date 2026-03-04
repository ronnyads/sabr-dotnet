# Category Lock Stale Cleanup Runbook

## Goal
Clear stale `listing_drafts.category_id` values when they diverge from the persisted product/site category lock.

## Scope
- Table: `listing_drafts`
- Table: `product_marketplace_category_lock`
- Site source:
  - `provider_draft_json.siteId` when present
  - fallback `MLB`

## SQL script
Use:
- `docs/operations/sql/20260222_clear_stale_listing_draft_category_by_lock.sql`

The script includes:
1. BEFORE snapshot (stale rows grouped by tenant/site)
2. Idempotent update (`category_id = null` for stale rows)
3. AFTER snapshot

## Execution order
1. Homologation
2. Production

Always save BEFORE/AFTER outputs with timestamp for audit.

## Post-check
1. Open wizard with hard reload.
2. Confirm stale category was cleared and user is forced to pick or apply recommended category.
3. Confirm no unexpected publish with outdated category.
