# Mercado Livre Smoke Test (Base GO)

Este roteiro gera as 4 evidencias exigidas para GO da base:

1. Sem credenciais hardcoded no `appsettings.Development.json` (usar env/user-secrets).
2. OAuth real funcionando (print + row em `tenant_marketplace_connections`).
3. `sync-now` real importando pedido (rows em `marketplace_orders/items/reservas` ou `UNMAPPED`).
4. Prova operacional com endpoints e outputs esperados.

## 0) Pre-requisitos

- API local rodando com banco PostgreSQL acessivel.
- URL local oficial da API (padrao atual): `http://localhost:5250`.
- Tenant/client de teste ativo.
- Credenciais reais de app Mercado Livre.
- Token JWT de client (`accountType=client`) para chamar endpoints `/api/v1/client/...`.

## 1) Configurar segredos (obrigatorio)

`appsettings.Development.json` deve conter apenas placeholders (sem segredo real).

Configure segredos reais via user-secrets (recomendado):

```powershell
dotnet user-secrets init --project src/Sabr.Api/Sabr.Api.csproj
dotnet user-secrets set "MercadoLivre:ClientId" "<ML_CLIENT_ID_REAL>" --project src/Sabr.Api/Sabr.Api.csproj
dotnet user-secrets set "MercadoLivre:ClientSecret" "<ML_CLIENT_SECRET_REAL>" --project src/Sabr.Api/Sabr.Api.csproj
dotnet user-secrets set "MercadoLivre:RedirectUri" "http://localhost:5250/api/v1/client/integrations/mercadolivre/callback" --project src/Sabr.Api/Sabr.Api.csproj
dotnet user-secrets set "MercadoLivre:WebhookSecret" "<ML_WEBHOOK_SECRET_REAL>" --project src/Sabr.Api/Sabr.Api.csproj
dotnet user-secrets set "MercadoLivre:Mabang:ApiKey" "<MABANG_API_KEY_REAL>" --project src/Sabr.Api/Sabr.Api.csproj
```

Opcionalmente via variaveis de ambiente:

```powershell
$env:MercadoLivre__ClientId="<ML_CLIENT_ID_REAL>"
$env:MercadoLivre__ClientSecret="<ML_CLIENT_SECRET_REAL>"
$env:MercadoLivre__RedirectUri="http://localhost:5250/api/v1/client/integrations/mercadolivre/callback"
$env:MercadoLivre__WebhookSecret="<ML_WEBHOOK_SECRET_REAL>"
$env:MercadoLivre__Mabang__ApiKey="<MABANG_API_KEY_REAL>"
```

Validacao rapida (sem expor segredo):

```powershell
dotnet user-secrets list --project src/Sabr.Api/Sabr.Api.csproj | findstr /I "MercadoLivre:ClientId MercadoLivre:ClientSecret MercadoLivre:RedirectUri MercadoLivre:WebhookSecret"
```

Output esperado:
- Chaves listadas sem valores placeholder (`CHANGE_ME` / `__SET_VIA...__`).

## 2) Evidencia OAuth real

### 2.1 Gerar connect URL

```bash
curl -X POST "http://localhost:5250/api/v1/client/integrations/mercadolivre/connect-url" \
  -H "Authorization: Bearer <JWT_CLIENT>" \
  -H "Content-Type: application/json" \
  -d "{\"returnUrl\":\"/client/integrations/mercadolivre\"}"
```

Output esperado:
- HTTP `200`
- JSON com campo `url` contendo `response_type=code` e `state=...`.

### 2.1.1 Auditoria frontend (sem XHR no callback)

- No DevTools Network, apos clicar em **Conectar**, deve existir apenas a chamada `POST /connect-url`.
- O callback `/api/v1/client/integrations/mercadolivre/callback` deve acontecer por navegacao do browser (document redirect), nao por XHR/fetch Angular.

### 2.2 Autorizar no ML e concluir callback

- Abrir `url` retornada.
- Logar no Mercado Livre.
- Autorizar.
- Confirmar redirecionamento para `/client/integrations/mercadolivre?ml=connected`.
- Tirar print da URL final no browser.

### 2.3 Provar persistencia no banco

```sql
select
  tenant_id,
  client_id,
  provider,
  seller_id,
  nickname,
  token_expires_at,
  created_at,
  updated_at
from tenant_marketplace_connections
order by updated_at desc
limit 5;
```

Output esperado:
- Nova linha com `provider=1` (MercadoLivre) para o tenant/client usado no teste.

## 3) Evidencia sync-now real

### 3.1 Executar sync-now

```bash
curl -X POST "http://localhost:5250/api/v1/client/integrations/mercadolivre/sync-now" \
  -H "Authorization: Bearer <JWT_CLIENT>" \
  -H "Content-Type: application/json" \
  -d "{}"
```

Output esperado:
- HTTP `200`
- JSON com `ordersUpserted`, `itemsUpserted`, `reservationsCreated`.

### 3.2 Provar importacao no banco

```sql
select tenant_id, client_id, seller_id, ml_order_id, status, imported_at
from marketplace_orders
order by imported_at desc
limit 10;
```

```sql
select marketplace_order_id, ml_item_id, ml_variation_id, sabr_variant_sku, quantity, reserved_quantity, mapping_state
from marketplace_order_items
order by updated_at desc
limit 20;
```

```sql
select marketplace_order_id, marketplace_order_item_id, sabr_variant_sku, quantity, status, reserved_at, expires_at
from stock_reservations
order by reserved_at desc
limit 20;
```

Output esperado:
- Pedido e itens novos.
- Itens mapeados com `mapping_state='MAPPED'` e reserva criada.
- Itens sem mapping com `mapping_state='UNMAPPED'` e sem reserva correspondente.

## 4) Provas de regra de estoque e SLA (base)

### 4.1 Provar que sync nao baixa estoque fisico

Antes do sync:

```sql
select variant_sku, physical_stock, reserved_stock, available_stock
from product_variants
where variant_sku = '<SKU_TESTE>';
```

Depois do sync:
- `physical_stock` deve permanecer igual.
- `reserved_stock` pode aumentar.
- `available_stock = physical_stock - reserved_stock`.

### 4.2 Provar baixa apenas no mark-paid

```bash
curl -X POST "http://localhost:5250/api/v1/client/orders/<ORDER_ID>/mark-paid" \
  -H "Authorization: Bearer <JWT_CLIENT>" \
  -H "Content-Type: application/json" \
  -d "{\"force\":false}"
```

Output esperado (cenario no prazo):
- HTTP `200`

Output esperado (fora de prazo):
- HTTP `409`
- `code = PAYMENT_CONFIRMATION_REQUIRED`

Reenvio forcado:

```bash
curl -X POST "http://localhost:5250/api/v1/client/orders/<ORDER_ID>/mark-paid" \
  -H "Authorization: Bearer <JWT_CLIENT>" \
  -H "Content-Type: application/json" \
  -d "{\"force\":true}"
```

Validacao no banco:

```sql
select ml_order_id, sabr_payment_confirmed_at, risk_flags_json
from marketplace_orders
where id = '<ORDER_GUID>';
```

Output esperado:
- `sabr_payment_confirmed_at` preenchido.
- Em caso de forcar fora de prazo: `risk_flags_json` contendo `PAID_AFTER_DEADLINE`.

## 5) Prova de zeragem em todas as listings quando available=0

### 5.1 Validar mapeamentos do SKU

```sql
select seller_id, ml_item_id, ml_variation_id, sabr_variant_sku
from tenant_marketplace_listing_maps
where sabr_variant_sku = '<SKU_TESTE>'
order by seller_id, ml_item_id;
```

### 5.2 Levar `available` para zero

Condicao alvo:
- `physical_stock - reserved_stock = 0`.

Depois acionar caminho de sync de estoque (por exemplo: `mark-paid`/expiracao de reserva/sync operacional).

### 5.3 Confirmar no ML (cada listing mapeada)

Consultar cada item/variacao na API ML com token do seller:

```bash
curl -H "Authorization: Bearer <ML_ACCESS_TOKEN>" "https://api.mercadolibre.com/items/<ML_ITEM_ID>"
```

Output esperado:
- `available_quantity = 0` para todos os `ml_item_id`/`ml_variation_id` mapeados daquele SKU.

## 6) Diagnostico /status com traceId

```bash
curl -i "http://localhost:5250/api/v1/client/integrations/mercadolivre/status" \
  -H "Authorization: Bearer <JWT_CLIENT>"
```

Output esperado:
- Em sucesso: HTTP `200` e header `X-Correlation-Id`.
- Em falha: `ApiError` com `traceId` no body e o mesmo valor no header `X-Correlation-Id`.
- UI Client ML deve exibir mensagem com `(traceId: ...)`.

## 7) Painel Dev ML (config externa)

Cadastrar no app do Mercado Livre:

1. **Redirect URI** exatamente igual ao backend configurado (protocolo, host, porta e path):
   - `http://localhost:5250/api/v1/client/integrations/mercadolivre/callback`
2. Tratar mismatch de Redirect URI como bloqueador de OAuth.
3. Quando habilitar notificacoes:
   - cadastrar URL publica do webhook;
   - configurar o mesmo `WebhookSecret` no backend;
   - manter dedupe + validacao seller/resource.

## 8) Pacote minimo de evidencias para GO

Anexar:

1. Print do OAuth final (`?ml=connected`) + row SQL de `tenant_marketplace_connections`.
2. Resposta `sync-now` + rows SQL de `marketplace_orders`, `marketplace_order_items`, `stock_reservations`.
3. Before/after SQL de `product_variants` provando que fisico so cai no `mark-paid`.
4. Resposta `409 PAYMENT_CONFIRMATION_REQUIRED` e resposta `200` com `force=true` + `risk_flags_json`.
5. Evidencia de `available_quantity=0` em todas as listings mapeadas para um SKU com disponivel zero.
