# PR3.5 v2 - Deploy Seguro e Monitoramento

## Escopo do release

- `POST /api/v1/client/marketplaces/fees/estimate`
- `POST /api/v1/client/marketplaces/categories/attributes`
- `MarketplaceFeesEstimateResult.source`: `ml-api` ou `ml-cache-fallback`
- Erros contratuais:
  - `401 ML_AUTH_INVALID`
  - `503 ML_UNAVAILABLE`
  - `traceId` em `ApiError`

## Fase 0 - Pre-deploy

1. Confirmar build verde:
   - `dotnet test tests/Phub.Api.Tests/Phub.Api.Tests.csproj --filter "FullyQualifiedName~MarketplaceResilienceHttpTests|FullyQualifiedName~CategorySuggestHttpTests"`
   - `npm.cmd run build:client`
2. Confirmar feature flags do wizard no ambiente alvo.
3. Registrar versao exata do artefato.

## Fase 1 - Deploy controlado

1. Executar deploy em janela curta.
2. Registrar timestamp inicial.
3. Capturar baseline 30 min:
   - 500 em `fees/estimate`
   - 500 em `categories/attributes`
   - volume `ML_AUTH_INVALID`
   - latencia p95 dos dois endpoints

## Fase 2 - Smoke (5-15 min)

1. Abrir:
   - `/client/publications/new?channel=mercadolivre&variantSku=<sku_real>`
2. Validar network:
   - `status -> drafts/get -> my-products?variantSku -> drafts/upsert`
   - `categories/attributes`
   - `fees/estimate`
3. Validar cenarios:
   - fees normal: `source=ml-api`
   - fees fallback: `source=ml-cache-fallback`
   - indisponibilidade sem cache: `503 ML_UNAVAILABLE` com `traceId`
   - auth invalida: `401 ML_AUTH_INVALID`

## Fase 3 - Estabilizacao (30-120 min)

Monitorar:

- `fees_estimate_fallback_total`
- `fees_estimate_unavailable_total`
- `category_attributes_unavailable_total`
- `ml_auth_invalid_total`

Logs estruturados:

- `tenantId`, `clientId`, `integrationId`, `sellerId`, `categoryId`, `listingTypeId`, `endpoint`, `fallbackUsed`, `traceId`

## Limites de alerta

1. Critico: qualquer 500 em `fees/estimate` ou `categories/attributes`.
2. Alto: `fees_estimate_unavailable_total` > baseline + 100% por 15 min.
3. Alto: `category_attributes_unavailable_total` > baseline + 100% por 15 min.
4. Medio: `ml_auth_invalid_total` > baseline + 50% por 30 min.
5. Medio: fallback ratio fees > 40% por 30 min.

## Rollback

1. Imediato se houver 500 recorrente por 10 min.
2. Imediato se bloqueio funcional do publish > 20% por 15 min.
3. Coletar evidencia:
   - requests com `traceId`
   - contadores antes/depois
   - comparativo de erro por endpoint
