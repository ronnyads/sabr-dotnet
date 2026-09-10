---
tags: [prometheushub, pedidos, expedicao]
updated: 2026-09-10
---

# Linha do tempo de pedidos

Sequência comum ao admin e cliente:

**Recebido → Pago → Etiqueta disponível → Etiqueta impressa → Separação iniciada → Separado → Embalado → Aguardando postagem/coleta → Enviado confirmado pelo marketplace → Em trânsito → Entregue/Devolvido/Cancelado**

- Importação, pagamento, obtenção da etiqueta e confirmação externa registram marcos automáticos.
- Impressão, início da separação, separação e embalagem são ações explícitas do admin.
- Uma etapa não pode pular a anterior.
- Pedido pago permanece na Expedição mesmo com etiqueta pendente.
- O cliente acompanha datas, estados e baixa a etiqueta, mas não avança etapas administrativas.
- A bipagem final significa `Embalado e pronto`; o shipment permanece vigiado até o marketplace confirmar o envio.
- Múltiplos pacotes mantêm marcos por shipment.

## Confirmação interna de pagamento

`Pedido pago` só é concluído depois do checkout da carteira. Antes da confirmação, cliente e admin recebem a mesma cotação com produtos, SKU mestre, quantidade, valor unitário, total e saldo projetado. A confirmação é atômica com o débito e o consumo das reservas de estoque.

O hash da cotação impede confirmar valores que mudaram entre revisão e clique. Repetições são idempotentes e não recriam reservas nem geram um segundo débito. Pedidos sem vínculo, sem preço, sem estoque ou sem saldo continuam pendentes com bloqueador explícito.

Ver também [[01 - Dashboard de Vendas]] para a visão comercial agregada.

Ver também [[08 - SENTINEL Torre de Expedicao]] para estados separados, deadline oficial e risco de SLA.

## Etiquetas assíncronas

A busca individual continua contextual por pedido/shipment. A busca em lote não mantém uma conexão HTTP longa: `POST /api/v1/client/orders/marketplace/labels/pull` cria um `MarketplaceOperationJob`, responde `202` com `jobId` e o portal acompanha `GET /api/v1/client/orders/marketplace/jobs/{jobId}`.

O worker processa os shipments com reuso da etiqueta armazenada, até três tentativas e estados `PENDING`, `PROCESSING`, `COMPLETED`, `COMPLETED_WITH_ERRORS` ou `FAILED`. Isso evita que timeouts de proxy apareçam no navegador como falso erro de CORS.

## Etiqueta e declaração de conteúdo

- A etiqueta oficial de transporte continua sendo obtida do marketplace e armazenada por shipment.
- O cliente pode baixar, no mesmo pedido, uma declaração operacional de conteúdo separada contendo pedido HUB, pedido do canal, shipment, tracking, produto, SKU mestre e quantidade.
- A declaração é gerada pelo backend com escopo obrigatório de tenant e cliente. Ela não substitui a etiqueta oficial; deve ser impressa junto dela.
- O código de bipagem PHUB do shipment é incluído na declaração para manter a leitura administrativa e a linha do tempo auditável.
