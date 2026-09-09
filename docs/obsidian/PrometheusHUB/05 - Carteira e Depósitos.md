---
tags: [prometheushub, financeiro, carteira, depósitos, auditoria]
updated: 2026-09-09
---

# Carteira e depósitos

## Objetivo

Começar com depósito manual comprovado e manter o domínio preparado para PIX. Saldo só existe depois da aprovação administrativa; um comprovante enviado nunca credita a própria conta do cliente.

## Fluxo

1. Cliente aprovado informa o valor e anexa PDF, JPG ou PNG de até 10 MB.
2. A solicitação nasce `Pending` e aparece no histórico do cliente e na fila administrativa.
3. Admin, SuperAdmin ou Finance visualiza o comprovante e aprova ou rejeita.
4. A aprovação chama o ledger existente com chave idempotente `wallet-deposit:{id}`.
5. Somente depois do crédito confirmado a solicitação recebe `Approved` e o `ledger_entry_id`.
6. Rejeição exige motivo na interface e não movimenta saldo.

## Persistência

- `wallet_deposit_requests`: metadados, valor em centavos (`bigint`), método, estado e revisão.
- `wallet_deposit_proofs`: conteúdo binário separado 1:1; listas nunca carregam o blob.
- `wallet_accounts`: saldo atual bloqueado durante a movimentação.
- `wallet_ledger`: histórico imutável de créditos e débitos.
- `audit_events`: envio, aprovação e rejeição com ator responsável.

Os índices cobrem o histórico por tenant/cliente/data e a fila por status/data. Há restrições de valor positivo, tamanho do comprovante e unicidade do ledger aprovado.

## Contratos

- `GET /api/v1/client/wallet`
- `POST /api/v1/client/wallet/deposits` (`multipart/form-data`)
- `GET /api/v1/client/wallet/deposits/{id}/proof`
- `GET /api/v1/admin/wallet/deposits`
- `POST /api/v1/admin/wallet/deposits/{id}/approve`
- `POST /api/v1/admin/wallet/deposits/{id}/reject`
- `GET /api/v1/admin/wallet/deposits/{id}/proof`

Os endpoints internos antigos de crédito/débito agora exigem papel administrativo/financeiro, fechando a possibilidade de auto-crédito por cliente.

## Pagamento de pedidos do marketplace

O pagamento interno é um checkout transacional, separado do estado `paid` informado pelo canal:

1. `GET /api/v1/client/orders/{orderId}/payment-quote` calcula os itens pelo preço vigente do catálogo, saldo atual e bloqueadores operacionais.
2. O portal apresenta os itens, quantidades, subtotal, total e saldo projetado antes da confirmação.
3. `POST /api/v1/client/orders/{orderId}/mark-paid` envia o `quoteHash`; uma cotação alterada é recusada com `PAYMENT_QUOTE_CHANGED`.
4. Pedido, variantes e carteira são bloqueados no PostgreSQL. Débito, snapshots de preço, consumo da reserva e confirmação do pagamento são salvos na mesma transação.
5. Saldo insuficiente retorna `INSUFFICIENT_WALLET_BALANCE` sem confirmar o pedido, consumir estoque ou criar ledger.

Cada pedido aceita no máximo um débito (`OrderId` + `Debit`). Repetir a confirmação devolve o resultado original sem movimentar carteira ou reserva novamente. O ledger referencia o pedido e o pedido referencia o ledger para auditoria bidirecional.

O snapshot financeiro preserva preço unitário de catálogo, custo unitário, total da linha, subtotal, adicionais, descontos e total cobrado. Frete, adicionais e descontos permanecem zero enquanto não existir uma política comercial explícita; valores de frete do marketplace não são convertidos silenciosamente em cobrança interna.

## Evolução para PIX

`WalletDepositMethod` já contém `Pix`. O futuro webhook deve criar/identificar a solicitação com referência externa única e reutilizar o mesmo ledger idempotente; a fonte de verdade continua sendo o ledger, não o retorno visual do provedor.
