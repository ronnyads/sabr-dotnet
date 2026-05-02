# SABR 3.0 (.NET 8)

Minimal start to get the new system online.

## Prerequisites
- .NET 8 SDK

## Run
```bash
cd src/Sabr.Api
dotnet run
```

Swagger: http://localhost:5250/swagger

## Dev Bootstrap (Client)
Local dev padrao usa:
- API: `http://localhost:5250`
- Front proxy client: `http://127.0.0.1:5250` (`sabr-frontend/proxy.client.conf.json`)

Pre-check rapido (PowerShell), antes de subir o Angular:
```powershell
Invoke-WebRequest http://127.0.0.1:5250/health/ready
```

Se falhar, inicie o backend:
```bash
dotnet run --project src/Sabr.Api/Sabr.Api.csproj
```

Comando unico recomendado para client (sobe API se necessario + frontend):
```bash
cd ../sabr-frontend
npm run start:client:stack
```

Matriz rapida de erro em DEV:
- `504` em `/api/v1/auth/csrf` ou `/api/v1/auth/login`: API offline/inacessivel na porta `5250`.
- `401` em login: credencial invalida ou contexto de autenticacao.
- `28P01` no startup da API: credencial de banco invalida para `sabr_user` (runbook: `docs/runbooks/login-local-28p01.md`).

## Build Lock (MSB3026/MSB3027)
Quando o `Sabr.Api` esta em execucao, o processo pode bloquear `Sabr.Application.dll` e `Sabr.Infrastructure.dll` dentro de `src/Sabr.Api/bin/Debug/net8.0`, causando:
- `MSB3026` (copy retry)
- `MSB3027`/`MSB3021` (copy failed: file in use)

Fluxo recomendado:
1. Build com API parada.
2. Run com `--no-build`.

Comandos:
```powershell
# Build seguro (para API em execucao antes de compilar)
../scripts/build-safe.ps1

# Subir API sem rebuild
dotnet run --no-build --project src/Sabr.Api/Sabr.Api.csproj
```

Destravamento manual (se necessario):
```powershell
Get-Process -Name Sabr.Api
../scripts/stop-sabr-api.ps1
dotnet build src/Sabr.Api/Sabr.Api.csproj
```

Testes rapidos com API ligada (evita recompilar referencias em uso):
```powershell
dotnet build tests/Sabr.Api.Tests/Sabr.Api.Tests.csproj -p:BuildProjectReferences=false
dotnet test tests/Sabr.Api.Tests/Sabr.Api.Tests.csproj --no-build --filter "MarketplaceCategoryResolverTests|CategorySuggestHttpTests"
```

## Notes
- Tenant is resolved by subdomain (ex.: cliente.sabr.com).
- Dev fallback: `?tenant=cliente1` or cookie `sabr_tenant`.
- Login payload uses email + password only.
- Protheus tag format: PREFIX_OPERATION (example: SB1_UPDATE)

## Staging Fake Seed (LGPD-safe)
Use fake data only in staging (no real PII). The command below creates deterministic fake tenants, clients, plans, catalogs, products and subscriptions:

```bash
cd src/Sabr.Api
dotnet run -- --seed-staging-fake
```

Seed created:
- Tenant: `stg_sabr` with slug `sabr` (always present for local dev)
- Client fake: `cliente.sabr.staging@example.test` with password `Fake1234!`
- Plans:
  - `Plano Staging Base` (active)
  - `Plano Staging Inativo` (inactive)
- Catalogs:
  - `Catalogo Staging` (active)
  - `Catalogo Staging Inativo` (inactive)
- Fake global SKUs (uppercase): `STG-SKU-001`, `STG-SKU-002`, `STG/SKU-003`, `STG_SKU_004`, `STG-SKU-005`
- Active subscription linking the fake client to the active plan/catalog

Quick local check:
1. Run seed command above.
2. Set frontend dev tenant to `sabr`.
3. Login with fake client credentials.
4. Open `/client/catalog` and `/client/my-products`.

## Dev Initial Seed (Machine Bootstrap)
Use this seed to bootstrap a full local environment in a new machine (admin + client + catalog + marketplace connection without real token + wizard drafts):

```bash
dotnet run --project src/Sabr.Api/Sabr.Api.csproj -- --seed-dev-initial
```

What it creates:
- Tenant (`slug: sabr`) for local bootstrap
- Platform admin user
- Tenant owner user
- Approved client account
- Active plan/catalog/subscription graph
- Product, variant and image set (including `SAD13790`)
- Mercado Livre connection for seller `1979655640` with no real token
- Listing drafts for wizard (`Draft`, `Valid` and `Error` samples)

Default credentials:
- Platform Admin: `admin.sabr.local@example.test` / `SabrDev@123`
- Tenant Owner: `owner.sabr.local@example.test` / `SabrDev@123`
- Client: `cliente.sabr.local@example.test` / `SabrDev@123`

Operational note:
- The ML connection is intentionally created without real access token/refresh token.
- Until OAuth connect is completed, ML flows can return auth degradation (for example `ML_AUTH_INVALID`) by design.

## Catalog + My Products API Status Matrix

Tenant realm endpoints covered in this wave:

- `GET /api/v1/catalog/products`
- `POST /api/v1/my-products`
- `GET /api/v1/my-products`
- `GET /api/v1/my-products/{draftId}`
- `PUT /api/v1/my-products/{draftId}`
- `DELETE /api/v1/my-products/{draftId}`

Expected statuses:

- `200 OK`: successful reads/updates and idempotent POST returning existing draft
- `201 Created`: POST created a new draft
- `204 No Content`: idempotent delete (even when draft does not exist)
- `403 Forbidden`: `SKU_NOT_AUTHORIZED`
- `404 Not Found`: `DRAFT_NOT_FOUND`
- `409 Conflict`: `CONCURRENCY_CONFLICT`
- `422 Unprocessable Entity`: `PRICING_INVALID`
- `428 Precondition Required`: `PRECONDITION_REQUIRED` when both `If-Match` and `rowVersion` are missing on PUT

## AWS Deployment Baseline

This repository now includes:
- `Dockerfile` for ECS Fargate
- Terraform stack in `infra/terraform`
- GitHub Actions workflow: `.github/workflows/backend-deploy.yml`

Required GitHub repository variables:
- `AWS_REGION` (default `sa-east-1` if omitted)
- `ECS_CLUSTER_NAME`
- `ECS_SERVICE_DEV`
- `ECS_SERVICE_PROD`
- `ECR_REPOSITORY_DEV`
- `ECR_REPOSITORY_PROD`
- `ECS_CONTAINER_NAME` (optional, default `sabr-api`)

Required GitHub repository secrets:
- `AWS_ROLE_ARN_DEV`
- `AWS_ROLE_ARN_PROD`

Runtime secrets are sourced from AWS Secrets Manager and injected via ECS task definition (`ConnectionStrings__Default`, `Jwt__Secret`, `Ml__ApiKey`).

## Smoke Checklist (Local)

1. Start API:
   - `cd src/Sabr.Api`
   - `dotnet run -- --seed-staging-fake`
   - `dotnet run`
2. Start frontend client:
   - `cd ../sabr-frontend`
   - `npm run start:client:stack` (recomendado)
   - ou `npm run start:client` se a API ja estiver online em `localhost:5250`
3. Start frontend admin:
   - `npm run start:admin`
4. Validate client flow:
   - open `/client/catalog`
   - add SKU to my products (`201` on first, `200` on repeat)
   - open `/client/my-products`
   - update draft with pricing mode and verify preview
   - force stale rowVersion to verify `409`
   - send update without precondition to verify `428`
   - delete draft and repeat delete (`204` both times)
5. Validate isolation:
   - tenant/client B cannot access draft from tenant/client A
6. Validate admin flow:
   - menu shows Dashboard, Clientes, Usuarios do Sistema, Produtos, Catalogos, Planos
   - both root and `/admin/...` aliases work

## Wave Deltas (Locked)

- `tenant_id` remains `string` in this module.
- Cost snapshot is persisted internally but not exposed in client DTO responses.
- Concurrency contract uses `ETag/If-Match` with `rowVersion` fallback.
- Test host note: `rowVersion` uses Postgres `xmin` only on Npgsql provider; non-Npgsql (InMemory tests) uses `UpdatedAt.Ticks` fallback strictly for tests.

## Admin Products Hardening v2.4

Backend additions:
- Product marketplace fields: `brand`, `ncm`, `ean`, `description`, `categoryId`, `widthCm`, `heightCm`, `lengthCm`, `weightKg`, `requiresAnatel`, `anatelHomologationNumber`, `anatelDocumentId`.
- Product images endpoints:
  - `POST /api/v1/admin/products/{sku}/images` (multipart)
  - `DELETE /api/v1/admin/products/{sku}/images/{imageId}`
  - `PUT /api/v1/admin/products/{sku}/images/{imageId}/primary`
- Product-catalog tenant links:
  - `GET /api/v1/admin/tenants/{tenantSlug}/products/{sku}/catalogs`
  - `PUT /api/v1/admin/tenants/{tenantSlug}/products/{sku}/catalogs`

Admin error/status matrix (v2.4):
- `422 PRODUCT_MISSING_CATALOG_LINKS`: active product without catalog link in tenant context.
- `422 ANATEL_REQUIRED`: `requiresAnatel=true` and missing/invalid homologation.
- `422 INVALID_IMAGE_TYPE`: MIME/extension not in `SVG/PNG/JPEG`.
- `422 IMAGE_LIMIT_EXCEEDED`: more than 10 images.
- `413 PAYLOAD_TOO_LARGE`: image exceeds 5MB.

Validation rules:
- SKU remains global and normalized uppercase.
- Active product requires at least one catalog link in selected tenant.
- `RequiresAnatel=true` requires homologation number.
- Image allowlist is strict: `image/svg+xml`, `image/png`, `image/jpeg`.

## Categories + Variants v2.5.3 (Locked)

Pagination policy (centralized):
- All list endpoints in Admin and Client realms enforce `limit` in `1..200`.
- `limit=201` returns `400 VALIDATION_ERROR` (`ApiError.traceId` always set).

Category codes/status:
- `422 CATEGORY_NOT_FOUND`: provided category slug does not exist.
- `422 CATEGORY_INACTIVE`: provided category slug exists but is inactive.
- `422 CATEGORY_HAS_ACTIVE_CHILDREN`: deactivating a parent category with active children.
- `422 CATEGORY_CYCLE_DETECTED`: category tree update would create a cycle.

Product update category semantics (`PUT /api/v1/admin/products/{sku}`):
1. `categoryId` field absent in payload: keep current category.
2. `categoryId` present with `null` or empty string: fallback to `uncategorized`.
3. `categoryId` present with slug:
   - slug missing -> `422 CATEGORY_NOT_FOUND`
   - slug inactive -> `422 CATEGORY_INACTIVE`
   - slug active -> update category.

Variants:
- `409 VARIANT_ALREADY_EXISTS` when creating a variant with an existing `variantSku`.
