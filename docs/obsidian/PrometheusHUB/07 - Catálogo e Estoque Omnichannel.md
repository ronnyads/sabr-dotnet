---
tags: [prometheushub, catalogo, estoque, marketplace, invariantes]
updated: 2026-09-08
---

# Catálogo e estoque omnichannel

## Separação do domínio

- `Product` e `ProductVariant`: produto e SKU mestre do HUB.
- `Publication`/rascunho: produto preparado pelo cliente.
- `TenantMarketplaceListingMap`: vínculo entre SKU mestre e identidade externa do anúncio.
- `MarketplaceOrderItem`: snapshot imutável do vínculo resolvido na entrada do pedido.
- `MarketplaceOperationJob`: trabalho remoto persistente e rastreável.

## Invariantes

1. SKU mestre não é editável pelo cliente; trocar significa remapear anúncios futuros.
2. Auto-mapping exige uma única correspondência normalizada, ativa e autorizada.
3. Remapeamento não altera pedidos, reservas ou auditoria históricos.
4. Estoque anunciado é `max(0, physicalStock - reservedStock - safetyBuffer)`.
5. Reserva é atômica: bloqueia as variantes afetadas, valida disponibilidade e salva saldo/versão na mesma transação.
6. Toda mudança de saldo incrementa `inventoryVersion`.
7. Antes de cada escrita remota o processo relê a versão; trabalho antigo é superseded.
8. Tenant, cliente, seller e integração são sempre validados em conjunto.

## Acesso ao catálogo

`Public` fica disponível para qualquer cliente aprovado. `PlanRestricted` exige catálogo ligado a plano e assinatura válida. A migration cria o Catálogo Público padrão e associa produtos ativos.

## Rollback

As migrations são aditivas. Em incidente, interromper workers/escrita remota antes de reverter a aplicação. Não apagar snapshots históricos nem diminuir `inventoryVersion`; manter colunas novas até a versão anterior voltar a operar com segurança.
