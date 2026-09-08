---
tags: [prometheushub, arquitetura, onboarding-agentes]
updated: 2026-09-07
---

# PrometheusHUB — mapa do sistema

> [!summary]
> Plataforma B2B de dropshipping. O cliente vende produtos do catálogo em marketplaces; o PrometheusHUB importa o pedido, identifica o SKU, controla pagamento e estoque, obtém a etiqueta e entrega o pedido ao fluxo administrativo de expedição.

## Repositórios

- Backend: `sabr-dotnet` — .NET 8, Entity Framework Core e PostgreSQL.
- Frontend: `sabr-frontend` — Angular 20 + Nebular.
- Branch de produção: `main` nos dois repositórios.

## Produção

- Portal do cliente: `https://app.marketplaceonline.site`
- Portal administrativo: `https://admin.marketplaceonline.site`
- Frontend: Cloudflare Pages (`sabr-frontend` e `sabr-admin`).
- API e worker: Fly.io, app histórico `sabr-api-dev` que atualmente atende produção.
- Banco: PostgreSQL acessado pela API/worker.

## Limites de segurança

- Pedidos carregam `tenant_id` e `client_id`.
- Endpoints do cliente devem exigir `accountType=Client` e sempre filtrar pelos dois identificadores.
- Nunca aceitar `clientId` do frontend para consultas do próprio cliente.
- O painel administrativo pode consultar escopo global; o cliente nunca pode avançar etapas operacionais administrativas.

## Fluxos relacionados

- [[01 - Dashboard de Vendas]]
- [[02 - Linha do Tempo de Pedidos]]
- [[03 - Deploy e Operação]]
- [[04 - Onboarding de Clientes]]
- [[05 - Carteira e Depósitos]]
- [[06 - Importação de Catálogo do Mercado Livre]]

## Regra de atualização deste vault

Ao alterar integrações, esquema de pedidos, etapas de expedição ou deploy, atualizar a nota correspondente na mesma entrega. Registrar a decisão e sua razão, não apenas listar arquivos alterados.
