# Legacy CategoryId Cleanup Runbook

## Goal
Remove legacy invalid `listing_drafts.category_id` values (for example `MASSAG`) that block the Mercado Livre wizard flow.

## Scope
- Table: `listing_drafts`
- Field: `category_id`
- Validation rule:
  - Resolve site with fallback:
    - `provider_draft_json.siteId` if valid `ML[A-Z]`
    - otherwise `MLB`
  - Category format must match: `^{siteId}\d+$`

## Script
Use:
- `docs/operations/sql/20260220_clear_invalid_listing_draft_category.sql`

The script contains:
1. BEFORE snapshot by tenant/site.
2. Idempotent cleanup update.
3. AFTER snapshot by tenant/site.

## Execution Order
1. **Homologation**
   - Run BEFORE snapshot.
   - Run update.
   - Run AFTER snapshot.
   - Validate wizard behavior with a known legacy draft.
2. **Production**
   - Repeat the same 3 SQL steps.
   - Store snapshot outputs with timestamp for audit.

## Runtime Validation Checklist
After deploy/restart of frontend dev runtime:
1. Open wizard with DevTools > Network > Disable cache.
2. Hard reload page.
3. Confirm console event:
   - `[WIZARD][CLIENT] runtime_signature { signature: 'wizard-legacy-category-clear-20260220' }`
4. Open a draft that used to return `MASSAG`.
5. Confirm:
   - `state.categoryId` stays empty until valid suggestion is selected.
   - autosave sends `clearFields: ["categoryId"]`.
   - after reload, `MASSAG` no longer returns.

## Notes
- Websocket/HMR errors like `ws://localhost:4200/ng-cli-ws` are not the root cause of legacy category data.
- If websocket remains unstable, keep manual hard reload during verification.
