---
tags: [prometheushub, pedidos, expedicao]
updated: 2026-09-08
---

# Linha do tempo de pedidos

Sequência comum ao admin e cliente:

**Pedido baixado → Pedido pago → Etiqueta gerada → Etiqueta impressa → Pedido separado → Pedido processado → Pedido despachado**

- Importação, pagamento, obtenção da etiqueta e despacho registram marcos automáticos.
- Impressão, separação e processamento são ações explícitas do admin.
- Uma etapa não pode pular a anterior.
- Pedido pago permanece na Expedição mesmo com etiqueta pendente.
- O cliente acompanha datas, estados e baixa a etiqueta, mas não avança etapas administrativas.
- O despacho remove da fila ativa e preserva o histórico em Pedidos.
- Múltiplos pacotes mantêm marcos por shipment.

Ver também [[01 - Dashboard de Vendas]] para a visão comercial agregada.

## Etiquetas assíncronas

A busca individual continua contextual por pedido/shipment. A busca em lote não mantém uma conexão HTTP longa: `POST /api/v1/client/orders/marketplace/labels/pull` cria um `MarketplaceOperationJob`, responde `202` com `jobId` e o portal acompanha `GET /api/v1/client/orders/marketplace/jobs/{jobId}`.

O worker processa os shipments com reuso da etiqueta armazenada, até três tentativas e estados `PENDING`, `PROCESSING`, `COMPLETED`, `COMPLETED_WITH_ERRORS` ou `FAILED`. Isso evita que timeouts de proxy apareçam no navegador como falso erro de CORS.
