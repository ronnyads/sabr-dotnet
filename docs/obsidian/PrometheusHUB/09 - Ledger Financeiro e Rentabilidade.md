# Ledger Financeiro e Rentabilidade

## Objetivo

Explicar, sem sobrescrita de fatos, quanto o seller faturou, quais débitos incidiram, qual foi o custo operacional e qual é a maturidade de cada resultado.

## Camadas

- `OPERATIONAL`: projeção imediata de Orders, Discounts, Shipments, Packs e catálogo.
- `RECONCILED`: confirmação posterior pelos recursos oficiais de Billing.
- `INTERNAL_CONFIRMED`: fatos confirmados dentro do PrometheusHUB, como o débito do custo de produto.

Billing confirma e concilia; nunca substitui Orders e Shipments como fonte da operação.

## Invariantes

1. Receita/crédito/recuperação são positivos; custo/tarifa/débito/reembolso são negativos.
2. O ledger é append-only. Uma correção cria uma nova entrada e avança `FinancialEconomicHead`.
3. Há exatamente uma cabeça ativa por tenant, cliente, provider, seller e `economicKey`.
4. Somente a cabeça ativa participa dos agregados.
5. `economicOccurredAt`, `financialConfirmedAt` e `observedAt` são datas distintas.
6. `sellerId` é dimensão relacional explícita em fatos, projeções, cursores, jobs e índices.
7. Refund ou chargeback sem alocação oficial permanece no grão fornecido; nunca há rateio inventado.
8. Entrega de devolução não recupera custo. A recuperação exige confirmação administrativa de item vendável.
9. Preço de catálogo é fotografado no pedido e não muda retroativamente.
10. Ausência de dado não equivale a zero.

## Maturidade

- `INCOMPLETO`: falta SKU, custo, frete, alocação ou componente obrigatório.
- `ESTIMADO`: componentes operacionais resolvidos, ainda sem confirmação suficiente.
- `PARCIALMENTE_CONFIRMADO`: confirmação parcial.
- `CONFIRMADO`: todos os componentes esperados confirmados ou não aplicáveis.
- `REABERTO`: fato tardio alterou um pedido antes confirmado.

## Sincronização

- Webhook permanece primário.
- O catch-up operacional de até 365 dias é particionado em chunks de no máximo 31 dias.
- Cada chunk possui checkpoint, dedupe key, tentativas, lease e retomada durável.
- A execução de um chunk avança no máximo uma hora por tentativa. O checkpoint só avança após persistir a sincronização dessa hora; uma interrupção repete apenas a hora incompleta. Timeout HTTP sem cancelamento do worker gera `RETRY`, não deixa o chunk em `RUNNING`.
- O catch-up nunca reserva estoque de pedidos com envio externo terminal (`shipped`, `delivered`, `returned`, `cancelled`, `not_delivered`) ou pedido cancelado/reembolsado. A sincronização usa o reconciliador transacional de reservas; mapeamentos legados sem `integrationId` são fallback somente dentro do mesmo tenant, cliente, provider e seller.
- Incidente de 19/09/2026: o worker foi pausado e 172 reservas indevidas de pedidos terminais (183 unidades, sellers 2496573592 e 3630972063) foram liberadas transacionalmente, preservando as linhas históricas com status `Released`. `reservedStock`, `availableStock` e `inventoryVersion` foram atualizados por SKU; verificação posterior encontrou zero reservas terminais e zero disponibilidades inconsistentes. Retomar o worker somente após deploy da regra preventiva.
- Regra preventiva implementada em 20/09/2026 (achado 2.1 da auditoria `mercado-livre-360-auditoria.md`): `MercadoLivreSyncService.ExpireReservationsAsync` deixou de liberar por TTL qualquer `StockReservation` vencida; agora só libera reservas órfãs (pedido inexistente) ou de pedidos já terminais, via `MarketplaceOrderInventoryService.IsReservationTerminalAsync` (extraído do reconciliador transacional, reaproveitando `MarketplaceOrderWorkflow.IsTerminalOrderStatus`). Reservas de pedido ainda ativo (ex.: `paid`, `created`) são mantidas e contadas em log; só cancelamento confirmado ou resolução administrativa auditada as encerra. Cobertura: teste de integração `ExpireReservations_KeepsReservedStock_ForActiveOrder` (novo) e `ExpireReservations_ReleasesReservedStock_ForTerminalOrder_AndSyncsAvailability` (renomeado do teste anterior). Build/teste local não pôde ser executado neste ambiente por bloqueio de rede a `api.nuget.org`; pendente confirmação de `dotnet test` local antes de considerar 100% verificado.
- O lock de variantes no reconciliador usa um parâmetro JSON convertido em conjunto de SKUs no PostgreSQL; não passar `string[]` diretamente ao `ANY` via `FromSqlRaw`, pois esse caminho gerou `42809` em produção. A consulta parametrizada foi validada contra PostgreSQL com `PREPARE`/`EXECUTE` e transação revertida.
- O mesmo padrão `ANY({0})`/`string[]` cru existia em `MarketplaceOrderCheckoutService.LoadVariantsAsync` (lock de variantes na confirmação de pagamento) e foi corrigido em 20/09/2026 com a mesma técnica (`jsonb_array_elements_text`). Validado com `PREPARE`/`EXECUTE` contra PostgreSQL 16 real (instância isolada), cobrindo lista populada, lista vazia e `FOR UPDATE`; não coberto por teste .NET porque a suíte usa `UseInMemoryDatabase`.
- Corrigido em 20/09/2026 (achado 2.5, item b): `MercadoLivreWebhookService.ProcessPendingEventsAsync` não reclamava um evento travado em `PROCESSING` (worker morto no meio do processamento) porque só `Pending`/`Failed` eram elegíveis para reclaim. Adicionada janela de staleness de 20 minutos (`ProcessingStaleAfter`), mesmo padrão de lease de `FinancialSyncJob`/`SentinelReconciliationService`. Validado com `PREPARE`/`EXECUTE` contra PostgreSQL real: `PROCESSING` velho é reclamado, `PROCESSING` recente não é. Item (a) do mesmo achado — sincronizar só o recurso notificado em vez do seller inteiro — segue pendente (mudança maior, sem risco de corretude, só de eficiência).
- Um `RUNNING` com lease vencido pode ser reclamado por outro worker. `lockedBy` e `leaseUntil` identificam a tentativa ativa; nunca limpar manualmente o lease de uma tentativa ainda viva.
- Corrigido em 20/09/2026: a liberação final do lease em `FinancialSyncJobService.ProcessNextAsync` agora exige `WHERE Id = jobId AND LockedBy = workerId` (via `ExecuteUpdateAsync` em PostgreSQL, com fallback equivalente para teste). Antes, um worker atrasado (a chamada de sync fica fora da transação e pode passar dos 30 minutos do lease) podia sobrescrever todo o estado de um job já reclamado por outro worker, não só o lease. Validado com UPDATE guardado contra PostgreSQL real (escrita tardia = 0 linhas afetadas, dono legítimo = 1 linha). O mesmo padrão ainda existe, com risco bem menor, em `SentinelReconciliationService.EnqueueDueAsync` (achado 2.8 da auditoria) — pendente.
- A aquisição PostgreSQL usa `FOR UPDATE SKIP LOCKED`; a chamada HTTP ocorre fora da transação.
- Retentativas usam backoff exponencial com full jitter.
- Billing é processado sequencialmente por seller, grupo e período, preservando `from_id`; respostas parciais e rate limit não viram zero confirmado.

## APIs

- `GET /api/v1/client/dashboard/profitability`
- `GET /api/v1/client/dashboard/profitability/orders`
- `GET /api/v1/client/dashboard/profitability/orders/{orderId}`
- `POST /api/v1/client/dashboard/sync`
- `GET /api/v1/client/dashboard/sync/{jobId}`
- `GET /api/v1/client/dashboard/sync-status`
- `GET|PUT /api/v1/client/financial-settings/tax`
- `GET|POST /api/v1/admin/integrations/{clientId}/financial-capabilities[/probe]`
- `GET /api/v1/admin/financial-reconciliation/runs`

## Rollout e recuperação

As migrações são aditivas. Ativar primeiro em shadow mode, auditar grants separados do Mercado Livre e Mercado Pago e validar manualmente a amostra do seller piloto. Qualquer centavo sem causa identificada bloqueia o rollout. Em incidente, pausar consumidores financeiros; o fluxo operacional de pedidos continua independente e as filas retomam do checkpoint.

## Autorização Mercado Pago (preparação)

- Aplicação separada da integração Mercado Livre; nunca reutilizar o token ML como se fosse MP.
- Configurar `MercadoPago__ClientId`, `MercadoPago__ClientSecret`, `MercadoPago__RedirectUri=https://api.marketplaceonline.site/api/v1/client/integrations/mercadopago/callback` e `MercadoPago__ClientPortalBaseUrl=https://app.marketplaceonline.site` como secrets do backend. Não registrar valores secretos neste vault ou no Git.
- Registrar exatamente o mesmo redirect URI na aplicação Mercado Pago correta. O cliente inicia em Integrações → Mercado Livre → Conectar Mercado Pago.
- O callback exige que o `user_id` autorizado corresponda ao seller de uma conexão ML do mesmo tenant/cliente. O grant é armazenado criptografado em `MarketplaceOAuthGrant` com `AppFamily=MERCADO_PAGO`.
- `Conta autorizada` significa somente que o OAuth concluiu. `billingMercadoPago` continua falso até um probe real dos recursos Billing e a conciliação; o dashboard não deve chamar valores estimados de confirmados.
- `PA_UNAUTHORIZED_RESULT_FROM_POLICIES` no probe indica que o provider negou a consulta de Billing apesar do OAuth válido. Conferir a permissão funcional de Faturamento na aplicação correta, renovar a autorização e repetir o probe. Não converter essa falha em valor confirmado zero.
- Ao trocar o segredo da aplicação, revisar grants e solicitar nova autorização. Não reutilizar grants de outro aplicativo.
